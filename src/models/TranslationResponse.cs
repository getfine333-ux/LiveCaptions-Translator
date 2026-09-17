using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models;

public sealed record ProviderReply(string Content, string? FinishReason = null)
{
    public static implicit operator ProviderReply(string content) => new(content);

    public static ProviderReply FromChatCompletion(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() != 1) throw new InvalidDataException("Unexpected translation choice count.");
        var choice = choices[0];
        string? reason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
            ? finish.GetString() : null;
        if (reason is not null && reason != "stop")
            throw new InvalidDataException("Translation did not complete: finish_reason=" + reason);
        string content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        return new(content, reason);
    }
}

public sealed record TranslationContent(string Source, string Translation);

public static class TranslationResponse
{
    // Budget both corrected source and translation; the old constant 128 also
    // constrained long bilingual JSON. Validation remains mandatory at any budget.
    public static int OutputTokenBudget(string source) => (int)Math.Clamp(256L + source.Length * 4L, 512, 4096);

    public static TranslationContent Parse(string raw, string source, string context, bool correction, IEnumerable<string>? protectedTerms = null, string glossary = "")
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("[ERROR]"))
            throw new InvalidDataException("Empty or failed translation response.");
        if (!correction) return new(source, raw.Replace("🔤", "").Trim());

        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int line = json.IndexOf('\n');
            if (line < 0 || !json.EndsWith("```", StringComparison.Ordinal))
                throw new InvalidDataException("Incomplete correction JSON fence.");
            json = json[(line + 1)..^3].Trim();
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("zh", out var zh) || zh.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("en", out var en) || en.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Correction requires exactly two string fields: zh and en.");
            string corrected = zh.GetString()!.Trim(), translated = en.GetString()!.Trim();
            if (corrected.Length == 0 || translated.Length == 0)
                throw new InvalidDataException("Correction contains an empty source or translation.");
            corrected = GlossaryConsistency.Reconcile(corrected, translated, glossary);
            if (!ProtectedTokens(source).SequenceEqual(ProtectedTokens(corrected), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Correction changed a number or Latin term in its source.");
            foreach (string term in protectedTerms?.Distinct() ?? Enumerable.Empty<string>())
                if (source.Split(term, StringSplitOptions.None).Length != corrected.Split(term, StringSplitOptions.None).Length)
                    throw new InvalidDataException("Correction changed a confirmed domain term: " + term);
            if (!string.IsNullOrWhiteSpace(context))
            {
                // Reject obvious scope expansion, never delete overlapping words.
                // This is a conservative guard, not a semantic correctness metric.
                string owned = Normalize(source), revised = Normalize(corrected);
                if (revised.Length > owned.Length * 1.5 + 20)
                    throw new InvalidDataException("Correction expanded beyond its owned source.");
                foreach (var line in context.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string prior = Normalize(line);
                    string prefix = prior[..Math.Min(prior.Length, 12)];
                    if (prefix.Length >= 8 && revised.StartsWith(prefix, StringComparison.Ordinal) &&
                        !owned.Contains(prefix, StringComparison.Ordinal))
                        throw new InvalidDataException("Correction repeated read-only context.");
                }
            }
            return new(corrected, translated);
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed or truncated correction JSON.", ex); }
    }

    private static string Normalize(string text) => Regex.Replace(text, @"[\p{P}\p{Z}\s]", "");

    private static string[] ProtectedTokens(string text)
    {
        string normalized = text.Normalize(System.Text.NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)", m => m.Value.Replace(",", ""));
        return Regex.Matches(normalized, @"[A-Za-z][A-Za-z0-9]*(?:[._+-][A-Za-z0-9]+)*|[-+]?\d+(?:\.\d+)?")
            .Select(m => m.Value).ToArray();
    }

    public static string CorrectionInput(string source, string context) => JsonSerializer.Serialize(new { source, context });
    public const string CorrectionInstruction = "\n纠错顺序：先依据本句语义、领域术语和上文消歧中文，再严格按该中文生成英文。上文也可能残留识别错误，同一个错词多次出现不代表正确。只改有依据的同音词，不凭读音猜人名；不确定则保留。数字（可保留千位逗号）、英文名称、否定词和引号内原词不得任意改动。不补造未完成的话。";

    public const string ScopeInstruction = "\n\n输出协议：只处理本次 source 的完整内容；context 仅用于术语消歧，禁止把上文追加或重述到本次结果中。禁止替未完成的话补造内容。返回且只返回包含 zh 与 en 两个非空字符串字段的完整 JSON 对象，不要 Markdown 或说明。";
}
