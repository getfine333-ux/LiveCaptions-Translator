namespace LiveCaptionsTranslator.models;

// Presentation consumes complete semantic units; it never asks ASR or MT to cut
// text. Audio time is the production rate, whereas target-language word count
// can grow without the speaker getting any faster.
internal static class CaptionPresentationPolicy
{
    public static TimeSpan Hold(IReadOnlyList<TranslationResult> page, TimeSpan queueWait, bool comfortable)
    {
        double estimate = Estimate(page);
        if (comfortable) return TimeSpan.FromSeconds(Math.Max(4, estimate));
        bool hasAudio = TryAudioSpan(page, out var duration);
        double seconds = hasAudio ? duration : estimate;
        // Consume known end-to-end age as well as the display queue. A late page
        // gets a protected 4s slot and remains as the previous page afterwards;
        // word count must not turn existing ASR latency into further waiting.
        // This budget is fixed before showing the page, never changed on arrival.
        double sourceAge = page.Max(r => (r.SourceToReadyMs ?? 0)/1000) + queueWait.TotalSeconds;
        if (hasAudio && (queueWait.TotalSeconds > 4 || sourceAge > 8)) seconds = 4;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 4, 12));
    }

    public static bool CanCombine(IReadOnlyList<TranslationResult> page, TranslationResult next)
    {
        if (page.Count >= 4 || page.Any(r => !GoodFinal(r)) || !GoodFinal(next)) return false;
        var candidate = page.Append(next).ToArray();
        // Adjacent, fully translated sentences only. Both an audio budget and a
        // bilingual text budget prevent catch-up from producing giant pages.
        return next.Segment.Id == page[^1].Segment.Id + 1 &&
            TryAudioSpan(candidate, out double seconds) && seconds <= 10 && Estimate(candidate) <= 12;
    }

    private static bool GoodFinal(TranslationResult r) => r.Segment.FinalAsr && !r.Segment.IsIncomplete && !r.IsPending && !r.IsError;
    private static double Estimate(IReadOnlyList<TranslationResult> page) => .8 + Math.Max(
        page.Sum(r => CaptionReader.TextSeconds(r.CorrectedSource ?? r.Segment.Text)),
        page.Sum(r => r.IsError || r.IsPending ? 0 : CaptionReader.TextSeconds(r.Text)));

    private static bool TryAudioSpan(IReadOnlyList<TranslationResult> page, out double seconds)
    {
        seconds = 0;
        if (page.Count == 0 || string.IsNullOrEmpty(page[0].Segment.AudioStreamId)) return false;
        long? end = null;
        foreach (var r in page)
        {
            var s = r.Segment;
            if (s.AudioStreamId != page[0].Segment.AudioStreamId ||
                s.AudioStartSample is not long start || s.AudioEndSample is not long stop || start < 0 || stop <= start ||
                end.HasValue && (start < end.Value || start - end.Value > 16000)) return false;
            end = stop;
        }
        seconds = (end!.Value - page[0].Segment.AudioStartSample!.Value) / 16000d;
        return true;
    }
}
