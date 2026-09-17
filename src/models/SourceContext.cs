namespace LiveCaptionsTranslator.models;

// Recognition and translation finish on different workers. Context is selected by
// source order, never by completion order or by the page currently on screen.
public sealed class SourceContext
{
    private sealed record Entry(TranslationSegment Segment, string Text, int Revision);
    private readonly SortedDictionary<long, Entry> entries = new();
    private readonly object gate = new();
    public void Clear() { lock (gate) entries.Clear(); }

    public void Remember(TranslationSegment segment)
    {
        lock (gate)
        {
            if (entries.TryGetValue(segment.Id, out var prior) && prior.Segment.SourceRevision >= segment.SourceRevision) return;
            entries[segment.Id] = new(segment, segment.Text, -1);
            Trim();
        }
    }

    public void Remember(TranslationResult result)
    {
        if (result.IsError || result.IsPending || string.IsNullOrWhiteSpace(result.CorrectedSource)) return;
        lock (gate)
        {
            if (entries.TryGetValue(result.Segment.Id, out var prior) &&
                (prior.Segment.SourceRevision > result.Segment.SourceRevision ||
                 (prior.Segment.SourceRevision == result.Segment.SourceRevision && prior.Revision >= result.Revision))) return;
            entries[result.Segment.Id] = new(result.Segment, result.CorrectedSource, result.Revision);
            Trim();
        }
    }

    public string Before(TranslationSegment segment, int count)
    {
        lock (gate)
        {
            var lines = entries.Values.Where(e => e.Segment.Id < segment.Id &&
                e.Segment.AudioStreamId == segment.AudioStreamId && e.Segment.SourceLang == segment.SourceLang &&
                e.Segment.CreatedAt <= segment.CreatedAt && segment.CreatedAt - e.Segment.CreatedAt <= TimeSpan.FromSeconds(60))
                .TakeLast(Math.Clamp(count, 0, 10)).Select(e => e.Text).ToArray();
            // Drop whole oldest lines; never truncate a term inside a context line.
            while (lines.Sum(x => x.Length + 1) > 1200 && lines.Length > 0) lines = lines.Skip(1).ToArray();
            return string.Join("\n", lines);
        }
    }
    private void Trim() { while (entries.Count > 64) entries.Remove(entries.First().Key); }
}
