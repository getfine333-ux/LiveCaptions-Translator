using System.IO;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models;

// A glossary supplies canonical terms, not blanket permission to replace similar
// words. Candidates need lexical anchors. Deterministic repair additionally needs
// known sound confusions AND the same explicit meaning in the returned English.
public static class GlossaryConsistency
{
    private static readonly string[] confusionGroups = ["浸静近进晋", "没墨默莫膜模", "值直脂植", "衍演眼颜", "锤垂捶", "轴州洲", "幅符福", "像象相", "晶金津", "圆元园"];
    private sealed record Candidate(int Start, string Heard, GlossaryTerm Term, bool SoundMatch);

    private static IReadOnlyList<GlossaryTerm> Terms(string glossary)
    {
        try { return GlossaryDocument.Parse(glossary); }
        catch (InvalidDataException) { return Array.Empty<GlossaryTerm>(); }
    }

    private static IEnumerable<Candidate> Candidates(string source, IReadOnlyList<GlossaryTerm> terms)
    {
        bool[] quoted = new bool[source.Length];
        char closing = '\0';
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (closing != '\0') { quoted[i] = true; if (c == closing) closing = '\0'; }
            else if ((closing = c switch { '“' => '”', '「' => '」', '『' => '』', '"' => '"', _ => '\0' }) != '\0') quoted[i] = true;
        }
        // Literal spelling/name discussions are not domain-term correction.
        if (Regex.IsMatch(source, @"(?:人名|名字|原文写着|原话是|错别字|这个词|这个字|不要改)")) yield break;
        foreach (var term in terms)
        {
            string canonical = term.Source;
            if (canonical.Length is < 4 or > 16 || canonical.Any(c => c is < '\u3400' or > '\u9fff')) continue;
            for (int start = 0; start + canonical.Length <= source.Length; start++)
            {
                string heard = source.Substring(start, canonical.Length);
                if (heard == canonical || heard.Any(c => c is < '\u3400' or > '\u9fff') ||
                    quoted.AsSpan(start, canonical.Length).Contains(true)) continue;
                var changed = Enumerable.Range(0, canonical.Length).Where(i => canonical[i] != heard[i]).ToArray();
                // At least three matching characters; two errors need a longer term.
                if (changed.Length is < 1 or > 2 || canonical.Length - changed.Length < 3) continue;
                bool sounds = changed.All(i => confusionGroups.Any(group => group.Contains(heard[i]) && group.Contains(canonical[i])));
                yield return new(start, heard, term, sounds);
            }
        }
    }

    public static string Hints(string source, string glossary) => string.Join("\n", Candidates(source, Terms(glossary))
        .GroupBy(c => (c.Start, c.Heard)).Where(group => group.Select(c => c.Term.Source).Distinct().Count() == 1)
        .Select(group => group.First()).Take(6)
        .Select(c => $"核对候选：{c.Heard} → {c.Term.Source} = {c.Term.Target}；仅在本句含义吻合时纠正，并保持中英术语一致。"));

    public static string Reconcile(string source, string translation, string glossary)
    {
        var terms = Terms(glossary);
        var candidates = Candidates(source, terms).GroupBy(c => (c.Start, c.Heard))
            .Where(group => group.Select(c => c.Term.Source).Distinct().Count() == 1)
            .Select(group => group.First()).Where(c => c.SoundMatch && TargetPresent(translation, c.Term.Target))
            .OrderBy(c => c.Start).ToArray();
        int until = -1;
        char[] repaired = source.ToCharArray();
        foreach (var candidate in candidates)
        {
            if (candidate.Start < until) continue;
            candidate.Term.Source.CopyTo(0, repaired, candidate.Start, candidate.Term.Source.Length);
            until = candidate.Start + candidate.Term.Source.Length;
        }
        return new string(repaired);
    }

    private static bool TargetPresent(string translation, string target)
    {
        if (target.Length < 4 || !target.Any(char.IsAsciiLetter)) return false;
        string pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(target).Replace(@"\ ", @"\s+") + @"(?![A-Za-z0-9])";
        return Regex.IsMatch(translation, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
