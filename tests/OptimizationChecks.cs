using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.speech;

internal static class OptimizationChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Complement-dependent clauses retain their continuation across ASR pauses", Complements);
        yield return ("False internal stops are repaired without changing words, numbers or quotations", InternalStops);
        yield return ("Repaired placeholders share a bounded reading page without disturbing protected dwell", Placeholder);
        yield return ("Glossary candidates reconcile bilingual terms with lexical and meaning evidence", Glossary);
        yield return ("Glossary reconciliation preserves ambiguous meanings, literal quotes and numbers", GlossaryGuards);
        yield return ("Confirmed formula idiom corrects only with technical context and outside quotations", () => Idiom(root));
        if (TestMode.IncludeLocalFixtures && File.Exists(Path.Combine(root, "artifacts/retry-reading-before/session.jsonl")) &&
            File.Exists(Path.Combine(root, "artifacts/optimization-verification/baseline/LiveCaptionsTranslator.dll")))
            yield return ("Actual 95-segment session retains every result and reduces retry-induced reading delay", () => Replay(root, output));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static AsrSnapshot Timed(string value) => new(value, value.Select(c => c.ToString()).ToArray(),
        Enumerable.Range(0, value.Length).Select(i => i * .1f).ToArray());
    private static Task Complements()
    {
        const string prefix = "所有输入都被预先改造成了。";
        const string continuation = "经过这台不完美的机器之后，恰好输出正确结果的样子。";
        int calls = 0;
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(_ => Timed(++calls == 1 ? prefix : prefix + continuation + "下面继续说明。"), "complements");
        engine.Ready += emitted.Add;
        engine.Accept(new(0, new float[64000], DateTimeOffset.UtcNow) { ForcedEnd = false });
        Check(emitted.Count == 0 && engine.HasPending, "false final punctuation published an unfinished complement");
        engine.Accept(new(64000, new float[64000], DateTimeOffset.UtcNow) { ForcedEnd = false, ContinuesPrevious = true });
        Check(emitted[0].Text == prefix.TrimEnd('。') + continuation && !emitted[0].IsIncomplete,
            "continuation lost or still emitted with a false sentence stop");
        Check(string.Concat(emitted.Select(r => r.Text).SelectMany(t => t.Where(char.IsLetterOrDigit))) ==
            string.Concat((prefix + continuation + "下面继续说明。").Where(char.IsLetterOrDigit)), "words lost while repairing boundaries");
        foreach (string complete in new[] { "我们已经完成了。", "图案已经改变了。", "图像转换完成了。", "这就是最终结果。" })
            Check(!SemanticBoundary.LooksIncomplete(complete), "ordinary complete sentence delayed: " + complete);
        foreach (string incomplete in new[] { "可以将这些信息转换为。", "我们把它定义为。", "接下来取决于。", "数据会被送到。" })
            Check(SemanticBoundary.LooksIncomplete(incomplete), "missing structural complement accepted: " + incomplete);
        return Task.CompletedTask;
    }
    private static Task InternalStops()
    {
        string text = "把16384个特征转换为了。可以识别的图案。随后继续。";
        string repaired = SemanticCompleteness.RepairInternalStops(text);
        Check(repaired == "把16384个特征转换为了可以识别的图案。随后继续。", "wrong punctuation repaired");
        const string quoted = "原文是“改造成了。经过这一步”。值是0.25。";
        Check(SemanticCompleteness.RepairInternalStops(quoted) == quoted, "literal quotation or decimal changed");
        return Task.CompletedTask;
    }
    private static TranslationResult Result(long id, bool pending = false, int sourceRevision = 1) => new(new TranslationSegment
    { Id = id, Text = "这是一个完整的句子。", SourceRevision = sourceRevision, FinalAsr = true, AudioStreamId = "test",
        AudioStartSample = (id - 1) * 64000, AudioEndSample = id * 64000, UtteranceKey = $"test:{id}" }, pending ? "" : "This is a complete sentence.")
    { IsPending = pending, IsError = pending, Revision = pending ? 0 : 1, SourceToReadyMs = 4000 };
    private static Task Placeholder()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1, true), TimeSpan.Zero);
        var visible = reader.Current;
        reader.Accept(Result(2), TimeSpan.FromSeconds(.1));
        reader.Accept(Result(1), TimeSpan.FromSeconds(.6));
        reader.Tick(TimeSpan.FromSeconds(3.9));
        Check(ReferenceEquals(visible, reader.Current), "repair interrupted protected reading");
        reader.Tick(TimeSpan.FromSeconds(4));
        Check(reader.Current!.Results.Select(r => r.Segment.Id).SequenceEqual(new long[] { 1, 2 }) &&
            reader.Current.Results.All(r => !r.IsPending) && reader.PendingCount == 0, "repair still consumes an exclusive extra page");
        reader.Accept(Result(3), TimeSpan.FromSeconds(4.1));
        reader.Tick(TimeSpan.FromSeconds(12));
        Check(reader.Current.Id == 3 && reader.History.Select(r => r.Id).SequenceEqual(new long[] { 1, 2 }), "repair merge lost ordering/history");
        reader = new CaptionReader(); reader.SetComfortMode(true);
        reader.Accept(Result(1, true), TimeSpan.Zero); reader.Accept(Result(2), TimeSpan.Zero); reader.Accept(Result(1), TimeSpan.FromSeconds(1));
        reader.Advance(TimeSpan.FromSeconds(4));
        Check(reader.Current!.LastId == 1, "comfort reading merged a repair");
        reader = new CaptionReader(); reader.Accept(Result(1, true), TimeSpan.Zero); reader.Accept(Result(2), TimeSpan.Zero);
        reader.Accept(Result(1, sourceRevision: 2), TimeSpan.FromSeconds(1)); reader.Advance(TimeSpan.FromSeconds(4));
        Check(reader.Current!.LastId == 1, "actual ASR revision lost its own protected slot");
        return Task.CompletedTask;
    }
    private const string Terms = "浸没式光刻 = immersion lithography\n数值孔径 = numerical aperture";
    private static Task Idiom(string root)
    {
        var rules = AsrTerminology.Read(File.ReadAllText(Path.Combine(root, "asr-terms.json")));
        Check(rules.Prepare("阿斯麦也有一个被分为龟镍的公式。", "").Text == "阿斯麦也有一个被奉为圭臬的公式。", "formula idiom left nonsensical");
        foreach (string literal in new[] { "原文写着被分为龟镍。", "这里有“被分为龟镍”的字样。", "被分为龟镍。" })
            Check(rules.Prepare(literal, "").Text == literal, "quoted or context-free idiom guessed");
        return Task.CompletedTask;
    }
    private static Task Glossary()
    {
        const string heard = "这是DUV里的静墨式光刻，数直孔径更大。";
        string hints = GlossaryConsistency.Hints(heard, Terms);
        Check(hints.Contains("浸没式光刻") && hints.Contains("数值孔径"), "saved glossary not used for term candidates");
        string raw = JsonSerializer.Serialize(new { zh = heard, en = "DUV immersion lithography has a larger numerical aperture." });
        var result = TranslationResponse.Parse(raw, heard, "", true, glossary: Terms);
        Check(result.Source == "这是DUV里的浸没式光刻，数值孔径更大。" && result.Translation.Contains("immersion lithography"), "bilingual known term mismatch survived");
        Check(GlossaryConsistency.Reconcile(result.Source, result.Translation, Terms) == result.Source, "repair is not idempotent");
        Check(GlossaryConsistency.Hints(heard, "") == "" && GlossaryConsistency.Reconcile(heard, result.Translation, "") == heard,
            "removing glossary did not remove its term corrections");
        return Task.CompletedTask;
    }
    private static Task GlossaryGuards()
    {
        foreach (var text in new[] { "她的名字是静墨式光刻。", "原文写着静墨式光刻。", "“静墨式光刻”是引文。", "传统式光刻并非浸没式光刻。", "我们使用16384个特征和GPU。" })
            Check(GlossaryConsistency.Reconcile(text, "immersion lithography", Terms) == text, "literal/unsupported term changed: " + text);
        Check(GlossaryConsistency.Reconcile("这里有静墨式光刻。", "dry lithography", Terms) == "这里有静墨式光刻。", "translation meaning not required");
        string conflict = Terms + "\n近没式光刻 = immersion lithography";
        Check(GlossaryConsistency.Reconcile("静墨式光刻", "immersion lithography", conflict) == "静墨式光刻", "ambiguous candidate chosen");
        Check(GlossaryConsistency.Reconcile("静墨式光刻", "immersion lithography", "bad glossary") == "静墨式光刻", "invalid glossary broke fallback");
        return Task.CompletedTask;
    }

    private static Task Replay(string root, string output)
    {
        var rows = File.ReadLines(Path.Combine(root, "artifacts/retry-reading-before/session.jsonl")).Select(line =>
        {
            using var doc = JsonDocument.Parse(line); var x = doc.RootElement;
            var created = DateTimeOffset.Parse(x.GetProperty("timestamp").GetString()!);
            var r = new TranslationResult(new TranslationSegment { Id = x.GetProperty("segment_id").GetInt64(),
                Text = x.GetProperty("raw_source_text").GetString()!, CreatedAt = created,
                SourceRevision = x.GetProperty("source_revision").GetInt32(), FinalAsr = x.GetProperty("is_final").GetBoolean(),
                IsIncomplete = x.GetProperty("is_incomplete").GetBoolean(), UtteranceKey = x.GetProperty("utterance_key").GetString()!,
                AudioStreamId = x.GetProperty("audio_stream_id").GetString()!, AudioStartSample = x.GetProperty("audio_start_sample").GetInt64(),
                AudioEndSample = x.GetProperty("audio_end_sample").GetInt64()
            }, x.GetProperty("translated_text").GetString()!) { CorrectedSource = x.GetProperty("source_text").GetString(),
                IsPending = x.GetProperty("is_pending").GetBoolean(), IsError = x.GetProperty("is_error").GetBoolean(),
                Revision = x.GetProperty("revision").GetInt32(), SourceToReadyMs = x.GetProperty("source_to_ready_ms").GetDouble() };
            return (at: created.AddMilliseconds(x.GetProperty("ready_ms").GetDouble()), result: r);
        }).OrderBy(x => x.at).ToArray();
        var oldAssembly = Assembly.LoadFile(Path.Combine(root, "artifacts/optimization-verification/baseline/LiveCaptionsTranslator.dll"));
        Dictionary<long, double> Run(bool legacy)
        {
            var type = legacy ? oldAssembly.GetType(typeof(CaptionReader).FullName!)! : typeof(CaptionReader);
            var resultType = legacy ? oldAssembly.GetType(typeof(TranslationResult).FullName!)! : typeof(TranslationResult);
            object reader = Activator.CreateInstance(type, 200)!;
            var translated = rows.Select(row => JsonSerializer.Deserialize(JsonSerializer.Serialize(row.result), resultType)!).ToArray();
            var shown = new Dictionary<long, double>();
            var order = new List<long>();
            Action<string> trace = line =>
            {
                var match = Regex.Match(line, @"show=(\d+).*source_to_show_ms=(\d+)");
                if (match.Success) { long id = long.Parse(match.Groups[1].Value); if (!shown.ContainsKey(id)) order.Add(id); shown[id] = double.Parse(match.Groups[2].Value); }
            };
            type.GetEvent("Trace")!.AddEventHandler(reader, trace);
            int cursor = 0;
            for (int tick = 0; tick < 20000; tick++)
            {
                var now = TimeSpan.FromMilliseconds(tick * 50);
                while (cursor < rows.Length && rows[cursor].at - rows[0].at <= now)
                { type.GetMethod("Accept")!.Invoke(reader, [translated[cursor], now]); cursor++; }
                type.GetMethod("Tick")!.Invoke(reader, [now]);
                if (cursor == rows.Length && (int)type.GetProperty("PendingCount")!.GetValue(reader)! == 0 &&
                    !(bool)type.GetProperty("HasRevision")!.GetValue(reader)!) break;
            }
            Check(order.SequenceEqual(Enumerable.Range(1, 95).Select(i => (long)i)), "recorded sentences disappeared or reordered");
            return shown;
        }
        var before = Run(true); var after = Run(false);
        Check(after[80] < before[80] - 3500 && after.Values.Average() <= before.Values.Average(), "retry queue delay did not improve");
        File.WriteAllText(Path.Combine(output, "retry-reading-replay.json"), JsonSerializer.Serialize(new
        { note = "Fixed actual ASR/MT results, 50 ms simulated reader clock; not a new audio or API run.",
            segments = after.Count, before = new { id80_ms = before[80], average_ms = before.Values.Average(), max_ms = before.Values.Max() },
            after = new { id80_ms = after[80], average_ms = after.Values.Average(), max_ms = after.Values.Max() } }, new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }
}
