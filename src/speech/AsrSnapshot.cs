namespace LiveCaptionsTranslator.speech;

// Timestamps are CTC alignment estimates, not exact word ends. Durations from
// the current native wrapper are deliberately not consumed (probe found invalid
// values). Audio before an anchor is kept as context on every window rollover.
public sealed record AsrSnapshot(string Text, string[] Tokens, float[] Timestamps)
{
    public AsrSnapshot(string text) : this(text, Array.Empty<string>(), Array.Empty<float>()) { }

    public AlignedText? Align(long audioStart, int sampleCount)
    {
        if (Tokens.Length == 0 || Tokens.Length != Timestamps.Length) return null;
        var positions = new List<TimedCharacter>();
        int cursor = 0;
        float previous = -1;
        for (int i = 0; i < Tokens.Length; i++)
        {
            float time = Timestamps[i];
            if (!float.IsFinite(time) || time < 0 || time < previous || time * 16000 > sampleCount + 512) return null;
            previous = time;
            foreach (char ch in Tokens[i].Replace('▁', ' '))
            {
                if (char.IsWhiteSpace(ch)) continue;
                while (cursor < Text.Length && char.IsWhiteSpace(Text[cursor])) cursor++;
                if (cursor >= Text.Length || Text[cursor] != ch) return null;
                positions.Add(new(cursor++, audioStart + (long)(time * 16000), i));
            }
        }
        while (cursor < Text.Length && char.IsWhiteSpace(Text[cursor])) cursor++;
        return cursor == Text.Length ? new(Text, positions.ToArray()) : null;
    }
}

public sealed record TimedCharacter(int Index, long Sample, int Token);
public sealed record TextAnchor(string Value, long FirstSample, long LastSample)
{
    public long[] Samples { get; init; } = Array.Empty<long>();
    public bool IncludesBoundaryPunctuation { get; init; }
}

public sealed record AlignedText(string Text, TimedCharacter[] Characters)
{
    public TextAnchor? AnchorBefore(int end)
    {
        var content = Characters.Where(c => c.Index < end && char.IsLetterOrDigit(Text[c.Index])).TakeLast(16).ToArray();
        if (content.Length < 4) return null;
        return new(new string(content.Select(c => Text[c.Index]).ToArray()), content[0].Sample, content[^1].Sample)
        {
            Samples = content.Select(c => c.Sample).ToArray(),
            IncludesBoundaryPunctuation = Text[(content[^1].Index + 1)..end].Any(c => "。！？!?.,，;；".Contains(c))
        };
    }

    public bool Resolve(TextAnchor anchor, out int end, bool allowRevisions = true)
    {
        end = 0;
        var content = Characters.Where(c => char.IsLetterOrDigit(Text[c.Index])).ToArray();
        string value = new(content.Select(c => Text[c.Index]).ToArray());
        // A crop can change the leading word of the left context. Prefer the
        // entire anchor, then a shorter EXACT suffix with both endpoint times.
        // This never edits the uncommitted source or removes a fuzzy text match.
        var lengths = new[] { anchor.Value.Length, 12, 8, 6 }.Where(n => n <= anchor.Value.Length).Distinct();
        foreach (int length in lengths)
        {
            if (length < anchor.Value.Length && anchor.Samples.Length != anchor.Value.Length) continue;
            int matches = 0;
            long firstTime = length == anchor.Value.Length ? anchor.FirstSample :
                anchor.LastSample - (anchor.Samples[^1] - anchor.Samples[^length]);
            for (int i = 0; i + length <= value.Length; i++)
            {
                if (!value.AsSpan(i, length).Equals(anchor.Value.AsSpan(anchor.Value.Length - length), StringComparison.Ordinal)) continue;
                int last = i + length - 1;
                if (Math.Abs(content[last].Sample - anchor.LastSample) > 9600 ||
                    Math.Abs(content[i].Sample - firstTime) > 9600) continue;
                matches++;
                end = content[last].Index + 1;
            }
            if (matches > 1) return false;
            if (matches == 0) continue;
            while (end < Text.Length && (char.IsWhiteSpace(Text[end]) ||
                anchor.IncludesBoundaryPunctuation && "。！？!?.,，;；".Contains(Text[end]))) end++;
            return true;
        }
        if (!allowRevisions || anchor.Samples.Length != anchor.Value.Length) return false;
        // ASR can correct a Chinese homophone in already committed context
        // (e.g. 基术瓶颈 -> 技术瓶颈). Match the same timed token sequence, not an
        // arbitrary similar phrase. Numbers/Latin text and the final two
        // characters must stay exact; ambiguous locations are rejected.
        foreach (int length in new[] { 16, 12, 8 }.Where(n => n <= anchor.Value.Length))
        {
            int matches = 0;
            int anchorStart = anchor.Value.Length - length;
            for (int i = 0; i + length <= value.Length; i++)
            {
                int last = i + length - 1;
                if (Math.Abs(content[last].Sample - anchor.LastSample) > 3840) continue;
                int corrections = 0;
                bool valid = true;
                for (int k = 0; k < length; k++)
                {
                    long expectedTime = anchor.LastSample - (anchor.Samples[^1] - anchor.Samples[anchorStart + k]);
                    if (Math.Abs(content[i + k].Sample - expectedTime) > 6400) { valid = false; break; }
                    char oldChar = anchor.Value[anchorStart + k], newChar = value[i + k];
                    if (oldChar == newChar) continue;
                    if (k >= length - 2 || !IsCjk(oldChar) || !IsCjk(newChar) || ++corrections > length / 8)
                    { valid = false; break; }
                }
                if (!valid) continue;
                matches++;
                end = content[last].Index + 1;
            }
            if (matches > 1) return false;
            if (matches == 0) continue;
            while (end < Text.Length && (char.IsWhiteSpace(Text[end]) ||
                anchor.IncludesBoundaryPunctuation && "。！？!?.,，;；".Contains(Text[end]))) end++;
            return true;
        }
        return false;
    }

    private static bool IsCjk(char value) => value is >= '\u4e00' and <= '\u9fff';

    public long SampleAt(int index) => Characters.FirstOrDefault(c => c.Index >= index)?.Sample ?? Characters.LastOrDefault()?.Sample ?? 0;

    public bool IsTokenInterior(int index)
    {
        var left = Characters.LastOrDefault(c => c.Index < index);
        var right = Characters.FirstOrDefault(c => c.Index >= index);
        return left != null && right != null && left.Token == right.Token;
    }
}
