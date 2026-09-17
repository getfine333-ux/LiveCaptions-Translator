using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models;

// UI-thread owned. Translation arrival and reading progression are independent.
// Time is supplied by the caller so pause/dwell/order can be replayed offline.
public sealed class CaptionReader
{
    private readonly SortedDictionary<long, TranslationResult> pending = new();
    private readonly Dictionary<long, TranslationResult> versions = new();
    private readonly Dictionary<long, TimeSpan> readyAt = new();
    private readonly int historyCapacity;
    private TimeSpan shownAt, holdUntil, pausedAt;
    private long nextId = 1;
    private long discardedThrough;
    public CaptionReader(int historyCapacity = 200) => this.historyCapacity = Math.Max(1, historyCapacity);
    public ObservableCollection<ReadingCard> Cards { get; } = new();
    public ObservableCollection<ReadingCard> History { get; } = new();
    public ReadingCard? Current => Cards.FirstOrDefault();
    public ReadingCard? Previous { get; private set; }
    public bool IsPaused { get; private set; }
    public bool ComfortMode { get; private set; }
    public int PendingCount => pending.Keys.Count(id => !IsCurrent(id));
    public bool HasRevision => Current != null && Current.Results.Any(r => pending.ContainsKey(r.Segment.Id));
    public bool CanAdvance => HasRevision || (Current == null || Current.Results.All(IsComplete)) && pending.ContainsKey(nextId);
    public TimeSpan LastDwell { get; private set; }
    public TimeSpan DisplayQueueWait { get; private set; }
    public double? SourceToShowSeconds { get; private set; }
    public double OldestPendingSeconds(TimeSpan now) => readyAt.Count == 0 ? 0 : Math.Max(0, (now - readyAt.Values.Min()).TotalSeconds);
    public long PresentationVersion { get; private set; }
    public event Action<string>? Trace;

    public bool Accept(TranslationResult result, TimeSpan now)
    {
        long id = result.Segment.Id;
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(result));
        if (versions.TryGetValue(id, out var old) && !IsNewer(result, old)) return false;
        // IDs that have already left retained history cannot re-enter the reader.
        if (id <= discardedThrough) return false;
        versions[id] = result;
        if (id < nextId && !IsCurrent(id))
        {
            Archive(result); // Late retries repair history without rewinding reading.
            return true;
        }
        // A failed replacement must not erase a valid bilingual pair. Prefer a
        // queued newer pair over the currently visible older pair; also propagate
        // finality so a failed final attempt cannot strand a stable-prefix card.
        if (!IsGood(result))
        {
            var ready = pending.GetValueOrDefault(id);
            var visible = Current?.Results.FirstOrDefault(r => r.Segment.Id == id);
            var good = ready != null && IsGood(ready) ? ready : visible != null && IsGood(visible) ? visible : null;
            if (good != null) result = good with { Segment = good.Segment with { FinalAsr = IsComplete(result) || IsComplete(good) } };
        }
        var currentResult = Current?.Results.FirstOrDefault(r => r.Segment.Id == id);
        if (currentResult != null && SameText(currentResult, result))
        {
            Current!.SetResult(result); // Finality-only updates do not flash or restart dwell.
            pending.Remove(id);
            readyAt.Remove(id);
        }
        else
        {
            if (!pending.TryGetValue(id, out var queued) || !SameText(queued, result)) readyAt[id] = now;
            pending[id] = result;
        }
        // Apart from the first card, only the presentation clock advances UI.
        // A burst of high-priority translation callbacks cannot skip render frames.
        if (Current == null) Tick(now);
        return true;
    }

    public bool Tick(TimeSpan now)
    {
        if (IsPaused || Current != null && now < holdUntil) return false;
        return Advance(now);
    }

    // Explicit Next bypasses the dwell, but cannot bypass an unfinished utterance
    // or a missing earlier ID. A revision always precedes the following utterance.
    public bool Advance(TimeSpan now)
    {
        if (HasRevision)
        {
            var original = Current!.Results;
            var revised = original.Select(r => pending.Remove(r.Segment.Id, out var update) ? update : r).ToList();
            // A repaired placeholder need not occupy another exclusive page.
            // At the protected page boundary it may share space with the next
            // complete unit, under the same audio/text budget as normal paging.
            // Actual source revisions keep their own reading slot.
            bool repairedPlaceholder = original.Any(r => !IsGood(r)) && revised.All(IsGood) &&
                original.Zip(revised).All(pair => pair.First.Segment.SourceRevision == pair.Second.Segment.SourceRevision &&
                    pair.First.Segment.Text == pair.Second.Segment.Text && IsComplete(pair.First) && IsComplete(pair.Second));
            if (repairedPlaceholder) CombineAdjacent(revised);
            nextId = Math.Max(nextId, revised[^1].Segment.Id + 1);
            Show(revised, now, true);
            return true;
        }
        if (Current != null && !Current.Results.All(IsComplete)) return false;
        if (!pending.Remove(nextId, out var next)) return false;
        ArchiveCurrent();
        var page = new List<TranslationResult> { next };
        // Release one stable page per clock tick. Coalescing happens before it
        // becomes visible; arriving results cannot append to or move this page.
        CombineAdjacent(page);
        nextId = page[^1].Segment.Id + 1;
        Show(page, now, false);
        return true;
    }

    private void CombineAdjacent(List<TranslationResult> page)
    {
        while (!ComfortMode && !IsPaused && pending.TryGetValue(page[^1].Segment.Id + 1, out var adjacent) &&
            CaptionPresentationPolicy.CanCombine(page, adjacent))
        {
            page.Add(adjacent);
            pending.Remove(adjacent.Segment.Id);
        }
    }

    public void SetPaused(bool paused, TimeSpan now)
    {
        if (paused == IsPaused) return;
        if (paused) pausedAt = now;
        else if (Current != null)
        {
            var duration = now - pausedAt;
            shownAt += duration;
            holdUntil += duration;
        }
        IsPaused = paused;
        Trace?.Invoke($"pause={paused} pending={PendingCount}");
    }

    public void SetComfortMode(bool enabled)
    {
        ComfortMode = enabled;
        if (Current != null) holdUntil = shownAt + CaptionPresentationPolicy.Hold(Current.Results, DisplayQueueWait, enabled);
    }

    // Only an explicit user action may skip unread cards. Every available skipped
    // result stays in chronological history (and the existing durable transcript).
    public bool JumpToLatest(TimeSpan now)
    {
        if (pending.Count == 0) return false;
        var latest = pending.Last().Value;
        if (IsCurrent(latest.Segment.Id))
        {
            // A retry for an earlier member must not rewind nextId into a page
            // whose other members have already been shown.
            SetPaused(false, now);
            return Advance(now);
        }
        ArchiveCurrent(latest.Segment.Id);
        foreach (var result in pending.Values.Where(r => r.Segment.Id != latest.Segment.Id).ToArray()) Archive(result);
        pending.Clear();
        IsPaused = false;
        Show(new[] { latest }, now, IsCurrent(latest.Segment.Id));
        readyAt.Clear();
        nextId = latest.Segment.Id + 1;
        Trace?.Invoke($"jump_to_latest={latest.Segment.Id}");
        return true;
    }

    public double RemainingSeconds(TimeSpan now) => Current == null ? 0 :
        Math.Max(0, (holdUntil - (IsPaused ? pausedAt : now)).TotalSeconds);

    private bool IsCurrent(long id) => Current?.Results.Any(r => r.Segment.Id == id) == true;
    private void ArchiveCurrent(long except = 0)
    {
        if (Current != null)
            foreach (var r in Current.Results.Where(r => r.Segment.Id != except)) Archive(r);
    }

    private void Show(IReadOnlyList<TranslationResult> page, TimeSpan now, bool revision)
    {
        if (Current != null) LastDwell = (IsPaused ? pausedAt : now) - shownAt;
        DisplayQueueWait = TimeSpan.FromSeconds(page.Max(r => readyAt.TryGetValue(r.Segment.Id, out var ready) ? Math.Max(0, (now-ready).TotalSeconds) : 0));
        var sourceAges = page.Where(r => r.SourceToReadyMs.HasValue).Select(r => r.SourceToReadyMs!.Value/1000 +
            (readyAt.TryGetValue(r.Segment.Id,out var ready) ? Math.Max(0,(now-ready).TotalSeconds) : 0)).ToArray();
        SourceToShowSeconds = sourceAges.Length == 0 ? null : sourceAges.Max();
        shownAt = now;
        holdUntil = now + CaptionPresentationPolicy.Hold(page, DisplayQueueWait, ComfortMode);
        if (IsPaused) pausedAt = now;
        var card = new ReadingCard(page, revision);
        if (!revision && Current != null) Previous = Current;
        if (Cards.Count == 0) Cards.Add(card); else Cards[0] = card;
        PresentationVersion++;
        foreach (var result in page)
        {
            double wait = readyAt.Remove(result.Segment.Id, out var ready) ? Math.Max(0, (now-ready).TotalMilliseconds) : 0;
            Trace?.Invoke($"show={result.Segment.Id} page={card.Id}-{card.LastId} source_revision={result.Segment.SourceRevision} retry={result.Revision} final={result.Segment.FinalAsr} display_queue_ms={wait:F0} source_to_show_ms={(result.SourceToReadyMs.HasValue ? (result.SourceToReadyMs.Value+wait).ToString("F0") : "unknown")} hold_ms={(holdUntil-now).TotalMilliseconds:F0} previous_dwell_ms={LastDwell.TotalMilliseconds:F0} pending={PendingCount}");
        }
    }

    private void Archive(TranslationResult result)
    {
        int index = 0;
        while (index < History.Count && History[index].Id < result.Segment.Id) index++;
        if (index < History.Count && History[index].Id == result.Segment.Id)
        {
            if (IsGood(result) || !IsGood(History[index].Result)) History[index].Update(result);
        }
        else History.Insert(index, new ReadingCard(result));
        while (History.Count > historyCapacity)
        {
            discardedThrough = Math.Max(discardedThrough, History[0].Id);
            versions.Remove(History[0].Id);
            History.RemoveAt(0);
        }
    }

    public static TimeSpan ReadingTime(TranslationResult result, bool comfortable = false)
    {
        // Text-only estimate retained for sources without audio timing and for
        // comparison with the previous reader. Live audio uses the page policy.
        double source = TextSeconds(result.CorrectedSource ?? result.Segment.Text);
        double target = IsGood(result) ? TextSeconds(result.Text) : 0;
        double seconds = Math.Max(4, 0.8 + Math.Max(source, target));
        return TimeSpan.FromSeconds(comfortable ? seconds : Math.Min(12, seconds));
    }
    internal static double TextSeconds(string text)
    {
        int cjk = text.Count(c => c is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af');
        // Explicit Latin/number tokens avoid counting punctuation as words.
        int words = Regex.Matches(text, @"[A-Za-zÀ-ÖØ-öø-ÿ0-9]+(?:['’.,-][A-Za-zÀ-ÖØ-öø-ÿ0-9]+)*").Count;
        return cjk / 7.0 + words / 4.0;
    }
    private static bool IsNewer(TranslationResult result, TranslationResult old) =>
        result.Segment.SourceRevision > old.Segment.SourceRevision ||
        result.Segment.SourceRevision == old.Segment.SourceRevision && result.Revision > old.Revision;
    private static bool IsGood(TranslationResult r) => !r.IsPending && !r.IsError;
    private static bool IsComplete(TranslationResult r) => r.Segment.FinalAsr || r.Segment.UtteranceKey.Length == 0;
    private static bool SameText(TranslationResult a, TranslationResult b) =>
        (a.CorrectedSource ?? a.Segment.Text) == (b.CorrectedSource ?? b.Segment.Text) && a.Text == b.Text;
}

public sealed class ReadingCard : INotifyPropertyChanged
{
    private readonly List<TranslationResult> results;
    public ReadingCard(TranslationResult result, bool revision = false) : this(new[] { result }, revision) { }
    public ReadingCard(IEnumerable<TranslationResult> page, bool revision = false)
    {
        results = page.ToList();
        if (results.Count == 0) throw new ArgumentException("Empty reading page", nameof(page));
        Results = results.AsReadOnly();
        IsRevision = revision;
    }
    public IReadOnlyList<TranslationResult> Results { get; }
    public TranslationResult Result => results[0];
    public long Id => Result.Segment.Id;
    public long LastId => results[^1].Segment.Id;
    public bool IsRevision { get; }
    public string Label => (Id == LastId ? $"第 {Id} 段" : $"第 {Id}–{LastId} 段") + (IsRevision ? " · 内容补全" : "");
    public string Timestamp => Result.Segment.CreatedAt.LocalDateTime.ToString("HH:mm:ss");
    public string SourceText => string.Join(" ", results.Select(r => r.CorrectedSource ?? r.Segment.Text));
    public string TranslatedText => string.Join(" ", results.Select(r => r.IsPending ? "译文待补全，原文已保留" : r.IsError ? "翻译失败，原文已保留" : r.Text));
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(TranslationResult result)
    {
        SetResult(result);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    internal void SetResult(TranslationResult result)
    {
        int index = results.FindIndex(r => r.Segment.Id == result.Segment.Id);
        if (index >= 0) results[index] = result;
    }
}
