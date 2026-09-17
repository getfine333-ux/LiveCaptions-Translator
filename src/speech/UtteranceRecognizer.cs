using System.Diagnostics;

namespace LiveCaptionsTranslator.speech;

public sealed record PendingRecognition(AudioSegment Audio, string CarriedText, TextAnchor? Anchor, string Text)
{
    public string AudioStreamId { get; init; } = "";
    public string UtteranceKey { get; init; } = "";
    public long UnitStartSample { get; init; }
}

// One worker owns this state. An inference window never finalizes a caption.
// Only confirmed semantic units leave Ready; unresolved tails retain their audio.
public sealed class UtteranceRecognizer
{
    private readonly Func<float[], AsrSnapshot> decode;
    private readonly string streamId;
    private readonly int maximumSamples, softSamples;
    private readonly List<float> audio = new();
    private AudioSegment? last;
    private long start, unitStart, sequence;
    private string text = "", tail = "", carried = "";
    private AlignedText? aligned;
    private TextAnchor? anchor;
    private int offset, agreed;
    private bool unresolved;
    private double inferenceMs, queueMs;
    public event Action<RecognizedSpeech>? Ready;
    public bool HasPending => audio.Count > 0;

    public UtteranceRecognizer(Func<float[], string> decode, string streamId, int maximumSamples = 480000)
        : this(samples => new AsrSnapshot(decode(samples)), streamId, maximumSamples) { }

    public UtteranceRecognizer(Func<float[], AsrSnapshot> decode, string streamId,
        int maximumSamples = 480000, int softSamples = 128000)
    {
        this.decode = decode;
        this.streamId = streamId;
        this.maximumSamples = maximumSamples;
        this.softSamples = softSamples;
    }

    public void Accept(AudioSegment segment)
    {
        long gap = last == null ? 0 : segment.StartSample - last.EndSample;
        bool continuation = last != null && gap >= 0 &&
            ((last.ForcedEnd && gap <= 32000) ||
             (!last.ForcedEnd && LooksIncomplete(text) && gap <= 32000));
        if (HasPending && !continuation) Finish("capture_gap", incomplete: true);
        if (!HasPending)
        {
            start = unitStart = segment.StartSample;
            sequence++;
        }
        else
        {
            if (audio.Count + gap + segment.Samples.Length > maximumSamples) CarryStablePrefix();
            int silence = checked((int)(segment.StartSample - (start + audio.Count)));
            if (silence > 0) audio.AddRange(new float[silence]);
        }
        audio.AddRange(segment.Samples);
        last = segment;
        // Keep arriving audio in PendingRecovery even if no safe rollover exists.
        // The caller persists it and reports the failure; capacity is not a sentence.
        if (audio.Count > maximumSamples)
            throw new InvalidOperationException("No reliable alignment for long-speech rollover; pending audio requires recovery.");
        Recognize(segment);
    }

    private void Recognize(AudioSegment segment)
    {
        queueMs += Math.Max(0, (DateTimeOffset.UtcNow - segment.QueuedAt).TotalMilliseconds);
        var watch = Stopwatch.StartNew();
        var snapshot = decode(audio.ToArray());
        inferenceMs += watch.Elapsed.TotalMilliseconds;
        var previousAlignment = aligned;
        aligned = snapshot.Align(start, audio.Count);
        offset = 0;
        if (anchor != null && (aligned == null || !aligned.Resolve(anchor, out offset)))
        {
            // Already published text is context, not ownership of the remaining
            // speech. If ASR rewrites that context, decode the still-uncommitted
            // audio directly from the last confirmed semantic boundary instead
            // of making all subsequent speech depend on a fragile text match.
            if (TryRebasePendingAudio(out var rebased))
            {
                snapshot = rebased;
                aligned = snapshot.Align(start, audio.Count);
                previousAlignment = null;
                offset = 0;
            }
            else
            {
                unresolved = true;
                agreed = 0;
                if (!segment.ForcedEnd)
                    throw new InvalidOperationException("Continuation alignment changed; pending audio requires recovery.");
                return;
            }
        }
        unresolved = false;
        string nextTail = snapshot.Text[offset..];
        agreed = SemanticBoundary.StableLength(tail, nextTail);
        tail = nextTail;
        text = Join(carried, tail);
        LiveCaptionsTranslator.utils.DiagLog.Write($"[Segmentation] aligned={aligned != null} agreed={agreed} chars={tail.Length} retained={audio.Count}");

        // Natural pauses still release complete short utterances immediately.
        // Continuous speech requires agreement across decoding snapshots.
        bool natural = !segment.ForcedEnd && !LooksIncomplete(text);
        if (aligned != null)
        {
            int confirmed = natural ? tail.Length : agreed;
            while (SemanticBoundary.Find(aligned, offset, confirmed, carried,
                unitStart, segment.EndSample, softSamples, previousAlignment, requirePrevious: !natural,
                pauseEvidence: HasBoundaryPause) is { } boundary)
            {
                int consumed = boundary.End - offset;
                var nextAnchor = aligned.AnchorBefore(boundary.End)!;
                Emit(Join(carried, tail[..consumed]), boundary.Sample, false, boundary.Reason);
                sequence++;
                unitStart = boundary.Sample;
                carried = "";
                tail = tail[consumed..];
                text = tail;
                offset = boundary.End;
                confirmed = Math.Max(0, confirmed - consumed);
                agreed = Math.Max(0, agreed - consumed);
                anchor = nextAnchor;
                Compact(nextAnchor);
            }
        }
        if (natural) Finish("speech_pause", incomplete: false);
    }

    private bool TryRebasePendingAudio(out AsrSnapshot snapshot)
    {
        snapshot = null!;
        // Private carry is not a completed semantic boundary: never discard it
        // using this path. The committed cursor must still be inside retained
        // audio, and rebasing must not consume any uncommitted samples.
        long cut = unitStart - start;
        if (carried.Length != 0 || cut <= 0 || cut >= audio.Count ||
            anchor?.IncludesBoundaryPunctuation != true) return false;
        var samples = audio.GetRange((int)cut, audio.Count-(int)cut).ToArray();
        var watch = Stopwatch.StartNew();
        var candidate = decode(samples);
        inferenceMs += watch.Elapsed.TotalMilliseconds;
        if (string.IsNullOrWhiteSpace(candidate.Text)) return false;
        audio.RemoveRange(0,(int)cut);
        start = unitStart;
        anchor = null;
        snapshot = candidate;
        LiveCaptionsTranslator.utils.DiagLog.Write($"[SeamRecovery] key={streamId}:{sequence} committed_until={unitStart} removed_context={cut} retained={audio.Count}");
        return true;
    }

    private bool HasBoundaryPause(long from,long to)
    {
        // An earlier snapshot may end at a real sentence boundary. Confirm that
        // boundary with actual quiet audio plus new spoken right context, rather
        // than paying for yet another full recognition window. A forced Flush
        // or an empty worker queue is never accepted as pause evidence.
        const int frame=320; // 20ms at 16kHz
        int begin=(int)Math.Max(0,from-start), end=(int)Math.Min(audio.Count,to-start);
        if (end-begin<3520) return false;
        double Energy(int at)
        {
            double sum=0;for(int i=at;i<at+frame;i++)sum+=audio[i]*audio[i];
            return Math.Sqrt(sum/frame);
        }
        double peak=0;
        for(int i=Math.Max(0,begin-16000);i+frame<=end;i+=frame)peak=Math.Max(peak,Energy(i));
        if(peak<.003) return false; // No reliable local speech reference.
        double threshold=Math.Max(.0008,peak*.06);
        int quiet=0;
        for(int i=begin;i+frame<=end;i+=frame)
        {
            quiet=Energy(i)<threshold?quiet+frame:0;
            if(quiet>=3520)return true;
        }
        return false;
    }

    // Rebuild from retained audio with short chronological snapshots. This is a
    // bounded retry, not a mode that suppresses all future continuous speech.
    // Existing committed IDs stay closed; the restored unit keeps its identity.
    public void Recover(PendingRecognition checkpoint)
    {
        if (checkpoint.Audio.Samples.Length == 0) return;
        if (!long.TryParse(checkpoint.UtteranceKey.Split(':').Last(), out long restoredSequence))
            throw new InvalidOperationException("Recovery checkpoint has no utterance identity.");
        Reset();
        start = checkpoint.Audio.StartSample;
        unitStart = checkpoint.UnitStartSample;
        sequence = restoredSequence;
        anchor = checkpoint.Anchor;
        carried = checkpoint.CarriedText;
        int initial = Math.Min(checkpoint.Audio.Samples.Length, Math.Min(maximumSamples, Math.Max(128000, (int)(unitStart - start) + 32000)));
        audio.AddRange(checkpoint.Audio.Samples.AsSpan(0, initial).ToArray());
        last = checkpoint.Audio with { Samples = audio.ToArray(), ForcedEnd = initial < checkpoint.Audio.Samples.Length || checkpoint.Audio.ForcedEnd };
        Recognize(last);
        for (int cursor = initial; cursor < checkpoint.Audio.Samples.Length;)
        {
            int count = Math.Min(32000, checkpoint.Audio.Samples.Length - cursor);
            Accept(new(checkpoint.Audio.StartSample + cursor, checkpoint.Audio.Samples.AsSpan(cursor, count).ToArray(), checkpoint.Audio.QueuedAt)
            { ContinuesPrevious = true, ForcedEnd = cursor + count < checkpoint.Audio.Samples.Length || checkpoint.Audio.ForcedEnd });
            cursor += count;
        }
    }

    public void ObserveSilence(long silenceStart, long silenceEnd)
    {
        // Only ordered, contiguous audio-clock silence may release a window tail.
        if (!HasPending || last == null || silenceEnd - silenceStart < 32000 ||
            silenceStart < last.EndSample) return;
        if (last.ForcedEnd) last = last with { ForcedEnd = false };
        Finish("observed_silence", incomplete: true);
    }

    private void CarryStablePrefix()
    {
        if (unresolved || aligned == null || agreed <= 24) return;
        int end = offset + Math.Min(agreed, tail.Length) - 24;
        // Private storage rotation keeps one visible unit and an unresolved suffix.
        // Do not bisect a token or Latin/numeric run.
        while (end > offset && end < aligned.Text.Length &&
            (aligned.IsTokenInterior(end) ||
             IsAsciiWord(aligned.Text[end - 1]) && IsAsciiWord(aligned.Text[end]))) end--;
        if (end - offset < 16 || aligned.AnchorBefore(end) is not { } nextAnchor) return;
        if (RetainFrom(nextAnchor) <= start) return;
        carried = Join(carried, tail[..(end - offset)]);
        tail = tail[(end - offset)..];
        agreed = Math.Max(0, agreed - (end - offset));
        offset = end;
        anchor = nextAnchor;
        Compact(nextAnchor);
        text = Join(carried, tail);
    }

    private long RetainFrom(TextAnchor value) => Math.Max(start,
        Math.Min(value.FirstSample - 6400, value.LastSample - 32000));

    private void Compact(TextAnchor value)
    {
        long retain = RetainFrom(value);
        int count = (int)Math.Min(audio.Count, Math.Max(0, retain - start));
        if (count == 0) return;
        audio.RemoveRange(0, count);
        start += count;
    }

    public void Finish(string reason = "input_end", bool incomplete = true)
    {
        if (!HasPending) return;
        if (unresolved) throw new InvalidOperationException("Unresolved continuation alignment; pending audio requires recovery.");
        Emit(text, last!.EndSample,
            incomplete && (last.ForcedEnd || LooksIncomplete(text)), reason);
        Reset();
    }

    private void Emit(string value, long end, bool incomplete, string reason)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        Ready?.Invoke(new RecognizedSpeech(SemanticCompleteness.RepairInternalStops(value.Trim()), unitStart, end, queueMs, inferenceMs)
        {
            AudioStreamId = streamId, UtteranceKey = streamId + ":" + sequence,
            SourceRevision = 1, IsFinal = true, IsIncomplete = incomplete,
            EndReason = reason, ForcedEnd = last?.ForcedEnd ?? false,
            SourceEndedAt = last!.QueuedAt - TimeSpan.FromSeconds(((last.ObservedThroughSample ?? last.EndSample)-end)/16000d)
        });
        inferenceMs = queueMs = 0;
    }

    public void Reset()
    {
        audio.Clear();
        last = null;
        text = tail = carried = "";
        aligned = null;
        anchor = null;
        offset = agreed = 0;
        unresolved = false;
        inferenceMs = queueMs = 0;
    }

    private static bool IsAsciiWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static string Join(string left, string right) => left.Length > 0 && right.Length > 0 &&
        IsAsciiWord(left[^1]) && IsAsciiWord(right[0]) ? left + " " + right : left + right;
    public static bool LooksIncomplete(string value) => SemanticBoundary.LooksIncomplete(value);
    public AudioSegment? PendingAudio => !HasPending ? null : new(start, audio.ToArray(), last?.QueuedAt ?? DateTimeOffset.UtcNow)
    { ForcedEnd = last?.ForcedEnd ?? false, ObservedThroughSample = last?.ObservedThroughSample };
    public PendingRecognition? PendingRecovery => PendingAudio is { } pending ? new(pending, carried, anchor, text)
    { AudioStreamId = streamId, UtteranceKey = streamId + ":" + sequence, UnitStartSample = unitStart } : null;
}
