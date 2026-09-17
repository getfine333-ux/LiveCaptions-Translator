using System.IO;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models;

public sealed record AsrTermRule(string Term, string Translation, string[] Aliases, string[] RequireAny, string[] ExcludeAny);
public sealed record SourceTermEdit(int Start, string Heard, string Term);
public sealed record PreparedSource(string Text, string Hints, SourceTermEdit[] Edits);

// Explicit, domain-gated aliases, not global phonetic substitution. The original
// ASR string remains immutable for audio ownership, recovery and diagnostics.
public sealed class AsrTerminology
{
    private readonly AsrTermRule[] rules;
    public AsrTerminology(IEnumerable<AsrTermRule> rules)
    {
        this.rules = rules.ToArray();
        if (this.rules.Any(r => r is null || string.IsNullOrWhiteSpace(r.Term) || string.IsNullOrWhiteSpace(r.Translation) ||
            r.Aliases is null || r.RequireAny is null || r.ExcludeAny is null || r.RequireAny.Length == 0 ||
            r.Aliases.Concat(r.RequireAny).Concat(r.ExcludeAny).Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("Terminology rules require a term, aliases and domain evidence.");
    }
    public static AsrTerminology Read(string json) => new(JsonSerializer.Deserialize<AsrTermRule[]>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty terminology file."));

    public PreparedSource Prepare(string source, string context)
    {
        string evidence = source + "\n" + context;
        var active = rules.Where(r => r.RequireAny.Any(c => evidence.Contains(c, StringComparison.Ordinal)) &&
            !r.ExcludeAny.Any(c => source.Contains(c, StringComparison.Ordinal))).ToArray();
        var aliases = active.SelectMany(r => r.Aliases.Select(a => (Alias: a, Rule: r)))
            .OrderByDescending(x => x.Alias.Length).ToArray();
        var output = new StringBuilder();
        var edits = new List<SourceTermEdit>();
        char quoteEnd = '\0';
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (quoteEnd != '\0')
            {
                output.Append(c); i++;
                if (c == quoteEnd) quoteEnd = '\0';
                continue;
            }
            quoteEnd = c switch { '“' => '”', '「' => '」', '『' => '』', '"' => '"', _ => '\0' };
            if (quoteEnd != '\0') { output.Append(c); i++; continue; }
            var matches = aliases.Where(a => source.AsSpan(i).StartsWith(a.Alias, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) { output.Append(c); i++; continue; }
            int length = matches[0].Alias.Length;
            var longest = matches.Where(a => a.Alias.Length == length).ToArray();
            // Conflicting dictionary entries cannot choose the meaning for a speaker.
            if (longest.Select(a => a.Rule.Term).Distinct().Count() != 1)
            { output.Append(source.AsSpan(i, length)); i += length; continue; }
            var match = longest[0];
            output.Append(match.Rule.Term);
            if (match.Alias != match.Rule.Term) edits.Add(new(i, match.Alias, match.Rule.Term));
            i += length;
        }
        string prepared = output.ToString();
        string hints = string.Join("\n", active.Where(r => prepared.Contains(r.Term, StringComparison.Ordinal))
            .Select(r => r.Term + " = " + r.Translation).Distinct());
        return new(prepared, hints, edits.ToArray());
    }

    private static readonly object fileGate = new();
    private static string? cachedPath;
    private static DateTime cachedStamp;
    private static AsrTerminology cached = new(Array.Empty<AsrTermRule>());
    public static AsrTerminology LoadDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "asr-terms.json");
        lock (fileGate)
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (cachedPath == path && cachedStamp == stamp) return cached;
                cached = File.Exists(path) ? Read(File.ReadAllText(path)) : new(Array.Empty<AsrTermRule>());
                cachedPath = path; cachedStamp = stamp;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
            {
                cached = new(Array.Empty<AsrTermRule>());
                DiagLog.Write("[Terminology] rules unavailable: " + ex.Message);
            }
            return cached;
        }
    }
}
