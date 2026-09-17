using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.speech;
using LiveCaptionsTranslator.utils;
using Microsoft.Data.Sqlite;
using NAudio.Wave;

internal static class ArchitectureChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Forced windows re-decode complete audio instead of translating fragments", ForcedWindows);
        yield return ("An unfinished phrase waits for its continuation without fabricated punctuation", IncompletePause);
        yield return ("Unaligned snapshots wait for the complete source instead of publishing fragments", PrefixRevision);
        yield return ("EOF, gaps and capacity preserve pending speech without pretending capacity is a sentence", Boundaries);
        yield return ("Superseded translations coalesce without deleting later utterances", VersionQueue);
        yield return ("Source versions outrank retry attempts and keep valid bilingual pairs", VersionTimeline);
        yield return ("SQLite source revisions replace one row and reject old retries", VersionHistory);
        yield return ("Malformed, truncated and context-expanded responses are rejected", ResponseValidation);
        if (TestMode.IncludeLocalFixtures)
        {
            yield return ("Forced split and whole-window SenseVoice agree on a real Chinese recording", () => RealAudio(root, output));
            yield return ("Original binary and candidate agree on the normal-speed ASR fixture", () => BaselineInSeparateProcess(output));
        }
        yield return ("Bilingual layout renders offline with stable reading area", () => Layout(output));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static AudioSegment Audio(long start, int count, bool forced = false, bool continues = false) =>
        new(start, Enumerable.Range((int)start, count).Select(x => (float)x).ToArray(), DateTimeOffset.UtcNow)
        { ForcedEnd = forced, ContinuesPrevious = continues };

    private static Task ForcedWindows()
    {
        var decoded = new List<float[]>();
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(audio =>
        {
            decoded.Add(audio);
            return audio.Length == 40 ? "这个模型有1。" : "这个模型有16384个特征。";
        }, "digits");
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 40, forced: true));
        Check(emitted.Count == 0, "artificial endpoint published a broken number");
        engine.Accept(Audio(40, 30, continues: true));
        Check(decoded[1].SequenceEqual(Enumerable.Range(0, 70).Select(x => (float)x)), "the second pass lost the first window's audio");
        Check(emitted.Count == 1 && emitted[0].Text == "这个模型有16384个特征。", "independent fragments leaked into translation");
        Check(emitted[0].StartSample == 0 && emitted[0].EndSample == 70 && emitted[0].IsFinal, "utterance ownership is incorrect");
        return Task.CompletedTask;
    }

    private static Task IncompletePause()
    {
        Check(!UtteranceRecognizer.LooksIncomplete("是的。") && !UtteranceRecognizer.LooksIncomplete("这就是我们需要的。"),
            "a complete Chinese answer gained an unnecessary continuation wait");
        int calls = 0;
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(audio => ++calls == 1
            ? "我们把示例文本输入模型，再把。" : "我们把示例文本输入模型，再把输出送入编码器。", "pause");
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 40));
        engine.ObserveSilence(40, 500);
        Check(emitted.Count == 0, "a short pause finalized the incomplete phrase");
        engine.Accept(Audio(100, 40));
        Check(emitted.Count == 1 && emitted[0].IsFinal && !emitted[0].IsIncomplete, "the continuation was not repaired as a whole sentence");
        Check(emitted[0].EndSample == 140, "silence was omitted from the sample clock");
        var stopped = new UtteranceRecognizer(_ => "然后把。", "stopped");
        stopped.Ready += emitted.Add;
        stopped.Accept(Audio(0, 40));
        stopped.ObserveSilence(40, 40000);
        Check(emitted[^1].IsIncomplete && emitted[^1].Text == "然后把。", "timeout invented or dropped unfinished words");
        return Task.CompletedTask;
    }

    private static Task PrefixRevision()
    {
        var outputs = new Queue<string>(new[]
        {
            "今天介绍编码器。它们分别在模。",
            "今天介绍编码器。它们分别在模型的不同位置上训。",
            "今天介绍编码器。它们分别在模型的不同位置上训练而成。"
        });
        var engine = new UtteranceRecognizer(_ => outputs.Dequeue(), "prefix");
        var emitted = new List<RecognizedSpeech>();
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 40, true));
        engine.Accept(Audio(40, 40, true, true));
        Check(emitted.Count == 0, "an unaligned prefix was prematurely published");
        engine.Accept(Audio(80, 20, continues: true));
        Check(emitted.Count == 1 && emitted[0].IsFinal && emitted[0].Text.EndsWith("训练而成。"), "complete final snapshot was not preserved");
        var timeline = new CaptionTimeline();
        foreach (var speech in emitted)
            timeline.Upsert(new(new TranslationSegment { Id = 1, Text = speech.Text,
                UtteranceKey = speech.UtteranceKey, SourceRevision = speech.SourceRevision }, speech.Text));
        Check(CaptionParagraph.Build(timeline.Snapshot()).Single().SourceText == emitted[^1].Text,
            "whole-sentence revisions were concatenated");
        return Task.CompletedTask;
    }

    private static Task Boundaries()
    {
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(_ => "然后把。", "boundary", maximumSamples: 80);
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 50, true));
        bool held = false;
        try { engine.Accept(Audio(50, 40, true, true)); }
        catch (InvalidOperationException) { held = true; }
        Check(held && emitted.Count == 0 && engine.PendingRecovery!.Audio.Samples.SequenceEqual(
            Enumerable.Range(0, 90).Select(x => (float)x)), "capacity fabricated a sentence or lost incoming audio");
        engine.Reset();
        engine.Accept(Audio(50000, 40));
        engine.Finish();
        Check(emitted.Single().StartSample == 50000 && emitted[0].IsIncomplete, "a later session gap reused old speech");
        var edge = new UtteranceRecognizer(_ => "这句话刚好说完。", "edge");
        edge.Ready += emitted.Add;
        edge.Accept(Audio(0, 40, true));
        edge.ObserveSilence(40, 40000);
        Check(emitted[^1].Text == "这句话刚好说完。" && emitted[^1].IsFinal && !edge.HasPending,
            "speech ending exactly on a forced edge waited forever");
        return Task.CompletedTask;
    }

    private static async Task VersionQueue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new List<int>();
        var rows = new List<TranslationResult>();
        await using var queue = new TranslationTaskQueue(async (s, token) =>
        {
            called.Add(s.SourceRevision);
            if (s.SourceRevision == 1) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return new(s, s.Text);
        }, r => { rows.Add(r); return Task.CompletedTask; }, concurrency: 1, maxRetries: 0);
        var original = new TranslationSegment { Text = "草稿", UtteranceKey = "one", SourceRevision = 1 };
        long id = queue.Enqueue(original);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(original with { SourceRevision = 2, Text = "已过时的中间版本" });
        long other = queue.Enqueue(new TranslationSegment { Text = "不同的下一句", UtteranceKey = "two", SourceRevision = 4 });
        Check(queue.Enqueue(original with { SourceRevision = 3, Text = "完整最终句子", FinalAsr = true }) == id, "revision allocated a second caption");
        release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.CompleteAsync(timeout.Token);
        Check(!called.Contains(2), "obsolete queued request consumed a provider call");
        Check(rows.Count == 2 && rows.Any(r => r.Segment.Id == other) && rows.Single(r => r.Segment.Id == id).Text == "完整最终句子",
            "old result overwrote the final version or an unrelated sentence disappeared");
        Check(queue.OutstandingCount == 0, "superseded requests blocked ordered draining");
    }

    private static Task VersionTimeline()
    {
        var timeline = new CaptionTimeline();
        var first = new TranslationSegment { Id = 1, UtteranceKey = "a", SourceRevision = 1, Text = "原文一" };
        timeline.Upsert(new(first, "one") { Revision = 9 });
        var next = first with { SourceRevision = 2, Text = "原文二" };
        timeline.Upsert(new(next, "pending") { IsError = true, IsPending = true });
        Check(timeline.ReadingSnapshot().Single().Text == "one" && timeline.ReadingSnapshot().Single().Segment.Text == "原文一",
            "new source was paired with an old translation");
        timeline.Upsert(new(next, "two") { Revision = 1 });
        timeline.Upsert(new(first, "stale") { Revision = 99 });
        Check(timeline.ReadingSnapshot().Single().Text == "two", "retry count outranked source revision");
        return Task.CompletedTask;
    }

    private static async Task VersionHistory()
    {
        string session = Guid.NewGuid().ToString();
        var source = new TranslationSegment { Id = 1, SourceRevision = 1, Text = "原文一", FinalAsr = true };
        await SQLiteHistoryLogger.UpsertSegment(session, new(source, "old") { Revision = 8 });
        await SQLiteHistoryLogger.UpsertSegment(session, new(source with { SourceRevision = 2, Text = "原文二" }, "new"));
        await SQLiteHistoryLogger.UpsertSegment(session, new(source, "stale") { Revision = 99 });
        using var connection = new SqliteConnection(SQLiteHistoryLogger.CONNECTION_STRING);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT h.SourceText,h.TranslatedText FROM TranslationHistory h JOIN SegmentHistory s ON s.HistoryId=h.Id WHERE s.SessionId=@Session";
        command.Parameters.AddWithValue("@Session", session);
        using var reader = await command.ExecuteReaderAsync();
        Check(await reader.ReadAsync() && reader.GetString(0) == "原文二" && reader.GetString(1) == "new", "persisted source revision is stale");
        Check(!await reader.ReadAsync(), "revision created duplicate history");
    }

    private static Task ResponseValidation()
    {
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
            throw new Exception("invalid provider output was accepted");
        }
        Reject(() => TranslationResponse.Parse("{\"zh\":\"内容\",\"en\":\"unfinished", "内容", "", true));
        Reject(() => TranslationResponse.Parse("{\"zh\":\"内容\",\"en\":7}", "内容", "", true));
        Reject(() => TranslationResponse.Parse("{\"zh\":\"内容\"}", "内容", "", true));
        Reject(() => TranslationResponse.Parse("{\"zh\":\"内容\",\"en\":\"\"}", "内容", "", true));
        string prior = "谷歌DeepMind团队最近发布了一个名为Gemma Scope的项目，里面包含400多个彼此独立的稀疏自编码器。";
        Reject(() => TranslationResponse.Parse(JsonSerializer.Serialize(new { zh = prior + "它们分别在模型的不同位置训练。", en = "Full previous paragraph" }),
            "型的不同位置，以及不同版本。", prior, true));
        string response = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "length", message = new { content = "{\"zh\":\"内容\",\"en\":\"text\"}" } } } });
        Reject(() => ProviderReply.FromChatCompletion(response));
        var valid = TranslationResponse.Parse("```json\n{\"zh\":\"模型有16384个特征。\",\"en\":\"The model has 16384 features.\"}\n```", "模型有16384个特征。", "", true);
        Check(valid.Translation.Contains("16384"), "valid numeric translation was rejected");
        Check(PipelineTools.ContainsFeatureCount("16,384 features") && PipelineTools.ContainsFeatureCount("16384 features") &&
            !PipelineTools.ContainsFeatureCount("116384 features") && !PipelineTools.ContainsFeatureCount("16 features and 384 layers"),
            "numeric smoke validation conflated formatting with missing content");
        Check(TranslationResponse.OutputTokenBudget(new string('中', 300)) >= 1456 && TranslationResponse.OutputTokenBudget(new string('中', 5000)) == 4096,
            "output budget is truncated or unbounded");
        return Task.CompletedTask;
    }

    private static async Task RealAudio(string root, string output)
    {
        string model = Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        string path = Path.Combine(model, "test_wavs", "zh.wav");
        if (!File.Exists(path)) { Console.WriteLine("SKIP real utterance recognition: model/audio absent"); return; }
        using var reader = new WaveFileReader(path);
        Check(reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.Channels == 1,
            "reference audio format changed");
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        var comparisons = new List<object>();
        string? reference = null;
        foreach (int maximum in new[] { 8, 4 })
        {
            using var recognizer = new VadOfflineAsrClient(model, Path.Combine(root, "models", "silero_vad.onnx"), 8, "zh", 0.6, maximum);
            var speech = new List<RecognizedSpeech>();
            var failures = new List<string>();
            recognizer.SegmentRecognized += speech.Add;
            recognizer.OnError += failures.Add;
            await recognizer.ConnectAsync();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < bytes.Length; i += 1280)
                await recognizer.SendAudioAsync(bytes.AsSpan(i, Math.Min(1280, bytes.Length - i)).ToArray());
            await recognizer.SendEndAsync();
            Check(failures.Count == 0 && speech.Count == 1 && speech[0].IsFinal, "real audio still produced isolated translated fragments");
            reference ??= speech[0].Text;
            Check(reference == speech[0].Text, "forced window changed the final whole-audio recognition");
            comparisons.Add(new { windowSeconds = maximum, speech, elapsedMs = watch.Elapsed.TotalMilliseconds });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "utterance-real-audio.json"), JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task BaselineInSeparateProcess(string output)
    {
        // WPF's pack-URI cache is process-global even across collectible assembly
        // contexts. Keep the old assembly out of the UI test process entirely.
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--baseline-asr");
        start.ArgumentList.Add("--result-directory");
        start.ArgumentList.Add(output);
        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        var text = child.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw; }
        Check(child.ExitCode == 0, "isolated baseline comparison failed: " + await errors + await text);
    }

    public static async Task BaselineAudio(string root, string output)
    {
        string baseline = Path.Combine(root, "bin", "ProgressReview", "LiveCaptionsTranslator.dll");
        string model = Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        string path = Path.Combine(model, "test_wavs", "zh.wav");
        if (!File.Exists(baseline) || !File.Exists(path))
        { Console.WriteLine("SKIP original binary comparison: local baseline/model absent"); return; }
        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length + 32000]; // identical trailing silence for both VAD implementations
        reader.ReadExactly(bytes.AsSpan(0, (int)reader.Length));
        var context = new System.Runtime.Loader.AssemblyLoadContext("original-asr", isCollectible: true);
        var original = new List<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var assembly = context.LoadFromAssemblyPath(baseline);
            var type = assembly.GetType("LiveCaptionsTranslator.speech.VadOfflineAsrClient", throwOnError: true)!;
            using var instance = (IDisposable)Activator.CreateInstance(type, model, Path.Combine(root, "models", "silero_vad.onnx"), 8, "zh", 0.6, 10.0)!;
            Action<string, bool> handler = (text, final) => { if (final) { original.Add(text); ready.TrySetResult(); } };
            type.GetEvent("OnResult")!.AddEventHandler(instance, handler);
            await (Task)type.GetMethod("ConnectAsync")!.Invoke(instance, new object[] { CancellationToken.None })!;
            for (int i = 0; i < bytes.Length; i += 1280)
                await (Task)type.GetMethod("SendAudioAsync")!.Invoke(instance, new object[] {
                    bytes.AsSpan(i, Math.Min(1280, bytes.Length - i)).ToArray(), CancellationToken.None })!;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { context.Unload(); }
        var candidate = new List<RecognizedSpeech>();
        using var current = new VadOfflineAsrClient(model, Path.Combine(root, "models", "silero_vad.onnx"), 8, "zh", 0.6, 8);
        current.SegmentRecognized += candidate.Add;
        await current.ConnectAsync();
        for (int i = 0; i < bytes.Length; i += 1280)
            await current.SendAudioAsync(bytes.AsSpan(i, Math.Min(1280, bytes.Length - i)).ToArray());
        await current.SendEndAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "original-vs-candidate-asr.json"), JsonSerializer.Serialize(new {
            baselineDllSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baseline))),
            audioPcmSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            original, candidate, limitation = "Single normal-speed ASR fixture only; no video, translation adequacy or end-to-end latency claim."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(original.Count == 1 && candidate.Count == 1 && original[0] == candidate[0].Text,
            "candidate did not retain the original binary's whole-sentence ASR result");
    }

    private static Task Layout(string output)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var sample = new { SourceText = "谷歌 DeepMind 团队最近发布了一个名为 Gemma Scope 的项目，里面包含 400 多个彼此独立的稀疏自编码器，它们分别在模型的不同位置训练而成。",
                    TranslatedText = "Google DeepMind recently released Gemma Scope, a project with more than 400 independent sparse autoencoders trained at different positions in the model." };
                var context = new { DisplayReadingCards = new[] { sample }, DisplayLogCards = new[] {
                    new { SourceText = "编码器如何应用在这个例子中？", TranslatedText = "How is the encoder applied in this example?" } },
                    StatusText = "界面布局示例（非实时翻译）", ReadingTitle = "第 2 段", ReadingPauseLabel = "停留",
                    ReadingStatus = "按顺序阅读 · 待阅读 2 段", CanReadNext = true, CanJumpToLatest = true };
                var page = new CaptionPage(context, true, true) { Width = 1000, Height = 560, Background = Brushes.White,
                    Foreground = new SolidColorBrush(Color.FromRgb(38, 43, 49)), FontFamily = new FontFamily("Microsoft YaHei UI") };
                page.Measure(new Size(page.Width, page.Height));
                page.Arrange(new Rect(0, 0, page.Width, page.Height));
                page.UpdateLayout();
                var reading = (System.Windows.Controls.ScrollViewer)page.FindName("ReadingScroll");
                Check(reading.ActualHeight > 250, "history crowded out the reading area");
                var bitmap = new RenderTargetBitmap(1000, 560, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "caption-layout.png"));
                encoder.Save(file);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
