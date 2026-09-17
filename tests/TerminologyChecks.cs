using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.models;

internal static class TerminologyChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        if (TestMode.IncludeLocalFixtures && File.Exists(Path.Combine(root, "artifacts", "homophones-before", "session.jsonl")))
            yield return ("Recorded homophone session prepares consistent domain terms before translation", () => Replay(root, output));
        yield return ("Domain correction preserves quotes, ordinary meanings, numbers and unknown words", () => Guards(root));
        yield return ("Context follows source IDs and revisions, excludes future and stale topics", Context);
        yield return ("Bilingual correction rejects changed numbers, names and confirmed terms", Response);
        yield return ("Parallel correction keeps raw source, bilingual pairs and ordered reading pages", () => Pipeline(root, output));
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static AsrTerminology Rules(string root) => AsrTerminology.Read(File.ReadAllText(Path.Combine(root, "asr-terms.json")));
    private static TranslationSegment Segment(long id, string text, DateTimeOffset? at = null) => new()
    { Id = id, Text = text, SourceLang = "zh-CN", TargetLang = "en-US", AudioStreamId = "test", SourceRevision = 1,
        CreatedAt = at ?? DateTimeOffset.UtcNow, FinalAsr = true };
    private static string Json(string source, string translation = "translation") => JsonSerializer.Serialize(new { zh = source, en = translation });

    private static Task Guards(string root)
    {
        var rules = Rules(root);
        foreach (string text in new[] { "金元时期的历史。", "这位演员名字叫金元。", "眼科使用的眼模板。", "眼膜板用于美妆。",
            "字幕里写着“眼模板”和「金元」。", "Gemma 第21层有16384个特征，GPU温度是85.5度。", "技术名称叫量子沐光。" })
            Check(rules.Prepare(text, "光刻机把图案投影在晶圆上。").Text == text, "unrelated or literal text changed: " + text);
        Check(rules.Prepare("把金元放过来。", "").Text == "把金元放过来。", "homophone alone was treated as evidence");
        var result = rules.Prepare("光刻机让光波透过眼磨板的8个缝隙，金元尺寸是300毫米，不是200毫米。", "");
        Check(result.Text == "光刻机让光波透过掩膜板的8个缝隙，晶圆尺寸是300毫米，不是200毫米。", "numbers, negation or owned words changed");
        Check(rules.Prepare(result.Text, "光刻机").Text == result.Text, "correction is not idempotent");
        var ambiguous = new AsrTerminology(new[] {
            new AsrTermRule("甲术语", "a", new[] { "错术语" }, new[] { "领域" }, Array.Empty<string>()),
            new AsrTermRule("乙术语", "b", new[] { "错术语" }, new[] { "领域" }, Array.Empty<string>()) });
        Check(ambiguous.Prepare("错术语", "领域").Text == "错术语", "ambiguous alias was guessed");
        bool rejected = false;
        try { AsrTerminology.Read("[null]"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "invalid dictionary accepted");
        return Task.CompletedTask;
    }

    private static Task Context()
    {
        var store = new SourceContext();
        var now = DateTimeOffset.UtcNow;
        var first = Segment(1, "眼模板", now);
        var second = Segment(2, "下一句", now.AddSeconds(3));
        store.Remember(first); store.Remember(second); store.Remember(Segment(3, "未来内容", now.AddSeconds(6)));
        store.Remember(new TranslationResult(first, "photomask") { CorrectedSource = "掩膜板" });
        store.Remember(first); // Enqueue bookkeeping can finish after a fast worker.
        Check(store.Before(second, 10) == "掩膜板", "raw bookkeeping reverted correction or future leaked");
        store.Remember(new TranslationResult(first, "pending") { CorrectedSource = "错误", IsPending = true, Revision = 3 });
        Check(store.Before(second, 2) == "掩膜板", "failed correction polluted context");
        store.Remember(first with { SourceRevision = 2, Text = "新识别" });
        store.Remember(new TranslationResult(first, "old") { CorrectedSource = "过期补译", Revision = 5 });
        Check(store.Before(second, 2) == "新识别", "late old ASR revision replaced current context");
        store.Remember(new TranslationResult(first with { SourceRevision = 2 }, "new") { CorrectedSource = "新校正", Revision = 1 });
        Check(store.Before(second, 2) == "新校正", "latest corrected context not used");
        Check(store.Before(second with { AudioStreamId = "other" }, 10) == "", "context leaked across audio streams");
        Check(store.Before(second with { CreatedAt = now.AddMinutes(2) }, 10) == "", "stale topic survived time bound");
        Check(store.Before(second, 0) == "", "disabled context was still used");
        store.Clear(); Check(store.Before(second, 10) == "", "session context not cleared");
        return Task.CompletedTask;
    }

    private static Task Response()
    {
        string source = "Gemma第21层有16384个特征，掩膜板有8条缝隙。";
        var valid = TranslationResponse.Parse(Json(source.Replace("16384", "16,384")), source, "", true, new[] { "掩膜板" });
        Check(valid.Source.Contains("16,384"), "thousands format was rejected");
        foreach (string changed in new[] { source.Replace("21", "22"), source.Replace("Gemma", "Gamma"),
            source.Replace("掩膜板", "眼模板"), source.Replace("16384", "16,385") })
        {
            bool rejected = false;
            try { TranslationResponse.Parse(Json(changed), source, "", true, new[] { "掩膜板" }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "unsafe correction accepted: " + changed);
        }
        using var payload = JsonDocument.Parse(TranslationResponse.CorrectionInput("当前句", "上文\n只读"));
        Check(payload.RootElement.GetProperty("source").GetString() == "当前句" &&
            payload.RootElement.GetProperty("context").GetString() == "上文\n只读", "request scope lost");
        return Task.CompletedTask;
    }

    private static async Task Replay(string root, string output)
    {
        string file = Path.Combine(root, "artifacts", "homophones-before", "session.jsonl");
        if (!File.Exists(file)) throw new FileNotFoundException("Real homophone fixture missing", file);
        var rules = Rules(root);
        var store = new SourceContext();
        var rows = new List<object>();
        var prepared = new Dictionary<long, string>();
        var watch = Stopwatch.StartNew();
        int edits = 0;
        foreach (string line in File.ReadLines(file))
        {
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            var segment = Segment(row.GetProperty("segment_id").GetInt64(), row.GetProperty("raw_source_text").GetString()!,
                DateTimeOffset.Parse(row.GetProperty("timestamp").GetString()!));
            string context = store.Before(segment, 2);
            var next = rules.Prepare(segment.Text, store.Before(segment, 10));
            edits += next.Edits.Length;
            prepared[segment.Id] = next.Text;
            rows.Add(new { id = segment.Id, raw = segment.Text, oldDisplayed = row.GetProperty("source_text").GetString(),
                input = next.Text, context, next.Hints, next.Edits });
            // No model call: feed forward only deterministic local corrections.
            store.Remember(new TranslationResult(segment, "local replay only") { CorrectedSource = next.Text });
        }
        double elapsedMs = watch.Elapsed.TotalMilliseconds;
        foreach (long id in new long[] { 6, 7, 8, 11, 13, 16, 18, 19, 20, 21, 25, 28, 29, 36, 37 })
            Check(prepared[id].Contains("掩膜板"), "mask term not recovered in actual segment " + id);
        foreach (long id in new long[] { 15, 20, 21, 25, 26 })
            Check(prepared[id].Contains("晶圆"), "wafer term not recovered in actual segment " + id);
        Check(prepared[35].Contains("零级衍射") && prepared[38].Contains("一级衍射"), "diffraction order not recovered");
        Check(prepared[17].Contains("8个") && prepared[17].Contains("6个") && prepared[33].Contains("1000赫兹"), "real numeric details changed");
        await File.WriteAllTextAsync(Path.Combine(output, "homophone-replay.json"), JsonSerializer.Serialize(new { count = rows.Count,
            edits, elapsedMs, apiCalls = 0, note = "Prepared source only; no new live translation or ASR accuracy measurement", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Pipeline(string root, string output)
    {
        var rules = Rules(root);
        var context = new SourceContext();
        int calls = 0;
        var results = new List<TranslationResult>();
        await using var queue = new TranslationTaskQueue(async (s, ct) =>
        {
            var fixedSource = rules.Prepare(s.Text, context.Before(s, 2));
            var request = s with { TranslationSource = fixedSource.Text, TermCorrections = fixedSource.Edits, TerminologyHints = fixedSource.Hints };
            Interlocked.Increment(ref calls);
            await Task.Delay(s.Id == 1 ? 30 : 1, ct);
            // Simulate one bilingual provider call using the production payload and parser.
            using var payload = JsonDocument.Parse(TranslationResponse.CorrectionInput(request.TranslationSource, ""));
            string zh = payload.RootElement.GetProperty("source").GetString()!;
            var parsed = TranslationResponse.Parse(Json(zh, "Light passes through the photomask."), request.TranslationSource, "", true,
                request.TermCorrections.Select(e => e.Term));
            return new TranslationResult(request, parsed.Translation) { CorrectedSource = parsed.Source };
        }, r => { results.Add(r); context.Remember(r); return Task.CompletedTask; }, maxRetries: 0,
            spoolDirectory: Path.Combine(output, "terminology-pipeline"));
        string raw = "光刻机让光波透过眼膜板。";
        for (int i = 0; i < 8; i++) queue.Enqueue(Segment(0, raw));
        await queue.CompleteAsync();
        Check(calls == 8 && results.Count == 8 && results.All(r => !r.IsError), "correction added a serial model call or lost a result");
        Check(results.Select(r => r.Segment.Id).SequenceEqual(Enumerable.Range(1, 8).Select(i => (long)i)), "correction reordered output");
        Check(results.All(r => r.Segment.Text == raw && r.CorrectedSource == "光刻机让光波透过掩膜板。" &&
            r.Text.Contains("photomask") && r.Segment.TermCorrections.Length == 1), "raw source or bilingual pair lost");
        var reader = new CaptionReader();
        foreach (var r in results) reader.Accept(r, TimeSpan.Zero);
        Check(reader.Current != null && reader.Current.SourceText.Contains("掩膜板"), "reader displayed raw instead of corrected source");
    }
}
