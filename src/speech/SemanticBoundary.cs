using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.speech;

public sealed record BoundaryChoice(int End, long Sample, string Reason);

// Conservative punctuation/structure policy. This is not a general syntactic
// parser: uncertain tails remain internal rather than fabricating a completion.
public static class SemanticBoundary
{
    public static BoundaryChoice? Find(AlignedText aligned, int offset, int agreed,
        string carried, long unitStart, long audioEnd, int softSamples,
        AlignedText? previous = null, bool requirePrevious = false,
        Func<long,long,bool>? pauseEvidence = null)
    {
        string text = aligned.Text;
        var candidates = new List<(BoundaryChoice Boundary, bool Strong)>();
        for (int i = offset; i < text.Length - 1; i++)
        {
            bool strong = IsSentenceEnd(text, i);
            bool clause = text[i] is '，' or ',' or '；' or ';';
            if (clause && i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
            if (!strong && !clause) continue;
            // A confirmed full sentence need not wait until the long-clause
            // target. The soft length target applies only to comma/semicolon
            // choices; neither case relaxes agreement or right-context checks.
            if (!strong && audioEnd-unitStart < softSamples) continue;
            // A punctuation mark appended at the right edge is never a confirmed
            // sentence. Require spoken text after it, including at natural ends.
            if (!text[(i + 1)..].Any(char.IsLetterOrDigit)) continue;
            string prefix = carried + text[offset..(i + 1)];
            if (prefix.Count(char.IsLetterOrDigit) < (strong ? 6 : 18) || LooksIncomplete(prefix)) continue;
            if (DependentClause(prefix)) continue;
            long sample = aligned.SampleAt(i);
            if (sample <= unitStart || audioEnd - sample < 2400) continue;
            var anchor = aligned.AnchorBefore(i + 1);
            if (anchor == null) continue;
            if (requirePrevious)
            {
                // Punctuation changes before this boundary need not invalidate
                // every later word. The selected boundary must exist in both
                // snapshots, with earlier right context or a real acoustic pause.
                if (previous == null || !previous.Resolve(anchor, out int priorEnd)) continue;
                bool priorHasRightContext=previous.Text[priorEnd..].Any(char.IsLetterOrDigit);
                if (!priorHasRightContext && (!strong || pauseEvidence?.Invoke(anchor.LastSample,aligned.SampleAt(i+1)) != true)) continue;
                int mark = priorEnd - 1;
                while (mark >= 0 && char.IsWhiteSpace(previous.Text[mark])) mark--;
                if (mark < 0 || (strong ? !IsSentenceEnd(previous.Text, mark) : previous.Text[mark] is not ('，' or ',' or '；' or ';'))) continue;
                if (i-offset >= agreed)
                {
                    // A corrected word near the beginning must not indefinitely
                    // block a later stable boundary. Validate the whole candidate
                    // against the same timed source range, allowing small Chinese
                    // corrections but never silently accepting changed numbers or
                    // Latin terms. The boundary itself still needs prior evidence.
                    int priorStart = previous.Characters.FirstOrDefault(c => c.Sample >= unitStart)?.Index ?? priorEnd;
                    if (priorStart >= priorEnd || !StableCandidate(previous.Text[priorStart..priorEnd],text[offset..(i+1)])) continue;
                }
            }
            else if (i-offset >= agreed) continue;
            // Stable commas need either a small acoustic gap or more lookahead
            // pressure; commas alone are not proof of sentence completeness.
            long next = aligned.SampleAt(i + 1);
            if (!strong && next - anchor.LastSample < 2400 && audioEnd - unitStart < softSamples + 64000) continue;
            candidates.Add((new(i + 1, sample, strong ? "semantic_sentence" : "semantic_clause"), strong));
        }
        var strongCandidates = candidates.Where(c => c.Strong).ToArray();
        if (strongCandidates.Length > 0) return strongCandidates.OrderBy(c => c.Boundary.Sample).First().Boundary;
        return candidates.OrderBy(c => Math.Abs(c.Boundary.Sample - (unitStart + softSamples)))
            .Select(c => c.Boundary).FirstOrDefault();
    }

    public static int StableLength(string previous, string current)
    {
        int oldIndex = 0, newIndex = 0, end = 0;
        while (true)
        {
            while (oldIndex < previous.Length && IsFormatting(previous[oldIndex])) oldIndex++;
            while (newIndex < current.Length && IsFormatting(current[newIndex])) newIndex++;
            if (oldIndex == previous.Length || newIndex == current.Length || previous[oldIndex] != current[newIndex]) break;
            oldIndex++; newIndex++;
            end = newIndex;
        }
        return end;
    }

    private static bool IsFormatting(char c) => char.IsWhiteSpace(c) || "。！？!?.,，;；".Contains(c);

    private static bool StableCandidate(string previous, string current)
    {
        string a = new(previous.Where(char.IsLetterOrDigit).ToArray());
        string b = new(current.Where(char.IsLetterOrDigit).ToArray());
        string Terms(string value) => string.Join("|",Regex.Matches(value,@"[A-Za-z0-9]+(?:[.,-][A-Za-z0-9]+)*").Select(m => m.Value));
        if (Terms(previous) != Terms(current)) return false;
        int limit = Math.Max(1,Math.Min(3,b.Length/12));
        if (a.Length < 6 || Math.Abs(a.Length-b.Length)>limit) return false;
        var row = Enumerable.Range(0,b.Length+1).ToArray();
        for (int i=1;i<=a.Length;i++)
        {
            int diagonal=row[0]; row[0]=i;
            for (int j=1;j<=b.Length;j++)
            {
                int above=row[j];
                row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),diagonal+(a[i-1]==b[j-1]?0:1));
                diagonal=above;
            }
        }
        return row[^1]<=limit;
    }

    public static bool IsSentenceEnd(string text, int index)
    {
        char c = text[index];
        if (c is '。' or '！' or '？' or '!' or '?') return true;
        if (c != '.') return false;
        // Protect decimals, dotted identifiers and common short abbreviations.
        if (index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1])) return false;
        int begin = index - 1;
        while (begin >= 0 && char.IsLetter(text[begin])) begin--;
        string word = text[(begin + 1)..index];
        return word.Length > 3 && !new[] { "Prof", "Mrs", "Mr", "Dr", "etc" }.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    private static bool DependentClause(string value)
    {
        string clause = Regex.Split(value.Trim(), @"[。！？!?]").LastOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        return Regex.IsMatch(clause, @"^(?:因为|由于|如果|假如|虽然|尽管|只要|当|为了)") &&
            !Regex.IsMatch(clause, @"(?:所以|因此|但是|不过|那么|就会|便会)") ||
            Regex.IsMatch(clause, @"^(?:if|because|although|when|while|unless)\b", RegexOptions.IgnoreCase);
    }

    public static bool LooksIncomplete(string value)
    {
        string core = value.Trim().TrimEnd('。', '.', '！', '!', '？', '?', '，', ',', '；', ';', '：', ':', '…');
        if (core.Length == 0) return true;
        if (SemanticCompleteness.RequiresComplement(core)) return true;
        return Regex.IsMatch(core, @"(?:再把|然后把|把|将|然后|以及|和|或者|因为|所以|但是|而且|如果|那么|分别在|映射到|输入到|送入|背后都|有个|所有使用|会自|就能调用|不同的|需要把|需要将)$") ||
            Regex.IsMatch(core, @"\b(?:and then|and|or|because|into|the|to)$", RegexOptions.IgnoreCase);
    }
}
