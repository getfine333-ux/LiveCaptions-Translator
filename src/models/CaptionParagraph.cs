using System.Text;

namespace LiveCaptionsTranslator.models;

// Presentation groups retain the original segment IDs. A retry only edits its
// existing paragraph; it never creates a new caption or changes the saved text.
public sealed record CaptionParagraph(IReadOnlyList<TranslationResult> Segments)
{
    public long Id => Segments[0].Segment.Id;
    public string SourceText => Join(Segments.Select(r => r.CorrectedSource ?? r.Segment.Text));
    public string TranslatedText => Join(Segments.Select(r => r.Text));

    public static CaptionParagraph[] Build(IEnumerable<TranslationResult> results)
    {
        var paragraphs = new List<CaptionParagraph>();
        var current = new List<TranslationResult>();
        foreach (var result in results.OrderBy(r => r.Segment.Id))
        {
            if (current.Count > 0 && !CanContinue(current, result))
            {
                paragraphs.Add(new(current.ToArray()));
                current.Clear();
            }
            current.Add(result);
        }
        if (current.Count > 0) paragraphs.Add(new(current.ToArray()));
        return paragraphs.ToArray();
    }

    private static bool CanContinue(List<TranslationResult> current, TranslationResult next)
    {
        // Coordinated utterances already own their complete source and translation.
        // They are replaced by version, never concatenated with another result.
        if (current[^1].Segment.UtteranceKey.Length > 0 || next.Segment.UtteranceKey.Length > 0) return false;
        var first = current[0].Segment;
        var previous = current[^1].Segment;
        var segment = next.Segment;
        return segment.ContinuesPrevious && previous.ForcedEnd &&
            segment.Id == previous.Id + 1 &&
            segment.AudioStreamId.Length > 0 && segment.AudioStreamId == previous.AudioStreamId &&
            segment.SourceLang == previous.SourceLang && segment.TargetLang == previous.TargetLang &&
            segment.Provider == previous.Provider &&
            segment.AudioStartSample - previous.AudioEndSample is >= 0 and <= 1024 &&
            segment.AudioEndSample - first.AudioStartSample is > 0 and <= 256000;
    }

    private static string Join(IEnumerable<string> texts)
    {
        var result = new StringBuilder();
        foreach (var text in texts.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            // CJK runs wrap naturally. Separate Latin words without inventing
            // punctuation or removing model output at a forced boundary.
            if (result.Length > 0 && !IsCjk(result[^1]) && !IsCjk(text[0])) result.Append(' ');
            result.Append(text);
        }
        return result.ToString();
    }

    private static bool IsCjk(char ch) => ch is >= '\u3000' and <= '\u9fff' or >= '\uff00' and <= '\uffef';
}
