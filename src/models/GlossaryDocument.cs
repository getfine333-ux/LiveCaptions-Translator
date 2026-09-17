using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.models;

public sealed record GlossaryTerm(string Source, string Target);
public sealed record GlossaryMerge(string Text, int Added, int Kept);

public static class GlossaryDocument
{
    public const int MaxCharacters = 16000;
    public static IReadOnlyList<GlossaryTerm> Parse(string text)
    {
        if (text.Length > MaxCharacters) throw new InvalidDataException("术语表最多支持 16000 字符，请按工作主题精简。");
        var terms = new Dictionary<string, GlossaryTerm>(StringComparer.OrdinalIgnoreCase);
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
                throw new InvalidDataException($"第 {lineNumber} 行格式不正确，请使用「原文 = 译文」。");
            var term = Validate(line[..separator].Trim(), line[(separator + 1)..].Trim());
            if (terms.TryGetValue(term.Source, out var prior) && prior.Target != term.Target)
                throw new InvalidDataException($"第 {lineNumber} 行的「{term.Source}」有不同译法，请保留一个。");
            terms[term.Source] = term;
        }
        return terms.Values.ToArray();
    }

    private static GlossaryTerm Validate(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || source.Length > 100 || target.Length > 200 ||
            source.StartsWith('#') || source.Contains('=') || source.Concat(target).Any(char.IsControl))
            throw new InvalidDataException("术语或译文为空、过长或含换行，请保持一行一条简短术语。");
        return new(source, target);
    }

    public static IReadOnlyList<GlossaryTerm> ParseGenerated(string json)
    {
        json = json.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
        {
            int line = json.IndexOf('\n');
            if (line >= 0) json = json[(line + 1)..^3].Trim();
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("terms", out var terms) ||
                terms.ValueKind != JsonValueKind.Array || terms.GetArrayLength() is < 1 or > 60)
                throw new InvalidDataException("AI 未返回有效术语列表，草稿保持不变。");
            var items = new List<GlossaryTerm>();
            foreach (var item in terms.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("source", out var source) ||
                    !item.TryGetProperty("target", out var target) || source.ValueKind != JsonValueKind.String || target.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("AI 返回的术语格式不完整，草稿保持不变。");
                items.Add(Validate(source.GetString()!.Trim(), target.GetString()!.Trim()));
            }
            return Parse(Format(items));
        }
        catch (JsonException ex) { throw new InvalidDataException("AI 返回了无效或不完整的 JSON，草稿保持不变。", ex); }
    }

    public static string Format(IEnumerable<GlossaryTerm> terms) => string.Join(Environment.NewLine, terms.Select(t => t.Source + " = " + t.Target));
    public static GlossaryMerge Merge(string current, IReadOnlyList<GlossaryTerm> generated, bool preserveExisting)
    {
        var existing = preserveExisting ? Parse(current) : Array.Empty<GlossaryTerm>();
        var keys = existing.Select(t => t.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = generated.Where(t => keys.Add(t.Source)).ToArray();
        string text = preserveExisting ? current.TrimEnd() : "";
        if (additions.Length > 0) text += (text.Length == 0 ? "" : Environment.NewLine + Environment.NewLine) + Format(additions) + Environment.NewLine;
        Parse(text);
        return new(text, additions.Length, generated.Count - additions.Length);
    }
}

// Edits remain in memory until Save. Replacement is atomic and retains a backup;
// a file edited outside this window is never silently overwritten.
public sealed class GlossaryFile
{
    public string Path { get; }
    private string? fingerprint;
    public GlossaryFile(string path) => Path = System.IO.Path.GetFullPath(path);
    public string Load()
    {
        var bytes = ReadBytes();
        fingerprint = Hash(bytes);
        return bytes == null ? "" : new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }
    private byte[]? ReadBytes()
    {
        if (!File.Exists(Path)) return null;
        if (new FileInfo(Path).Length > 1024 * 1024) throw new InvalidDataException("术语文件超过 1 MB，请先精简后再打开。");
        return File.ReadAllBytes(Path);
    }
    private static string? Hash(byte[]? bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    public void Save(string text)
    {
        GlossaryDocument.Parse(text);
        if (Hash(ReadBytes()) != fingerprint)
            throw new IOException("术语文件已在其他地方修改。请先复制当前草稿，重新打开后合并再保存。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (fingerprint == null) File.Move(temporary, Path);
            else File.Replace(temporary, Path, Path + ".bak", ignoreMetadataErrors: true);
            fingerprint = Hash(Encoding.UTF8.GetBytes(text));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
