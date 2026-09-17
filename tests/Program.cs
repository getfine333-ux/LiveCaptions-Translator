using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.speech;
using LiveCaptionsTranslator.utils;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

var root = FindRoot();
TestMode.IncludeLocalFixtures = args.Contains("--integration");
if (args.Contains("--ci") && (TestMode.IncludeLocalFixtures || args.Any(a => a is "--capture" or "--provider-smoke" or "--provider-number-smoke" or "--baseline-asr" or "--hardware-benchmark" or "--replay")))
    throw new ArgumentException("CI only runs offline tests with synthetic fixtures.");
var output = Path.Combine(root, "artifacts", "pipeline-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
Directory.SetCurrentDirectory(output);
if (await PipelineTools.TryRun(args, root, output)) return;
var results = new List<object>();
var checks = new (string name, Func<Task> test)[]
{
    ("Disk overflow preserves FIFO", TestSpill),
    ("Concurrent producer/consumer retains all work", TestConcurrentSpill),
    ("Burst translations retain IDs and input order", TestOrdering),
    ("Timeout fallback lets later sentences continue; correction keeps ID", TestTimeout),
    ("Failure does not wedge ordered commits", TestFault),
    ("Timeline retains repeated text and ignores stale corrections", TestTimeline),
    ("SQLite corrections replace the same row", TestHistory),
    ("Capture starts after ASR; overflow and shutdown preserve audio", TestCapture),
    ("Raw capture overflow preserves sample bytes", TestRawCapture),
    ("Resampling drains and resumes after an empty input", TestResampling),
    ("Forced continuations stay in one reading paragraph", TestParagraphs),
    ("Natural pauses, restarts and long speech keep paragraph boundaries", TestParagraphBoundaries),
    ("Session JSONL records flush with segment metadata", TestJsonl)
};
if (TestMode.IncludeLocalFixtures)
    checks = checks.Concat(new (string name, Func<Task> test)[] {
        ("Downloaded model families use the correct recognizer", TestModelRouting),
        ("Real VAD enforces duration and flushes the tail", TestVad),
        ("Reading profile keeps the normal-speed sample together", TestReadingProfile)
    }).ToArray();
var selectedChecks = args.Contains("--optimization-only") ? OptimizationChecks.All(root,output) : args.Contains("--settings-only") ? SettingsChecks.All(root,output) : args.Contains("--glossary-only") ? GlossaryChecks.All(root,output) : args.Contains("--models-only") ? ModelCatalogChecks.All(root,output) : args.Contains("--terminology-only") ? TerminologyChecks.All(root,output) : args.Contains("--reading-only") ? ReadingChecks.All(root,output) : args.Contains("--source-latency-only") ? SourceLatencyChecks.All(root,output).Concat(MissingSentenceChecks.All(root,output)) : args.Contains("--missing-probe") ? MissingSentenceChecks.Probe(root,output) : args.Contains("--latency-only") ? LatencyChecks.All(root, output) : args.Contains("--stall-only") ? LiveStallChecks.All(root, output) : args.Contains("--semantic-only") ? SemanticChecks.All(root, output) :
    checks.Concat(SettingsChecks.All(root,output)).Concat(ArchitectureChecks.All(root, output)).Concat(ModelCatalogChecks.All(root,output)).Concat(TerminologyChecks.All(root,output)).Concat(GlossaryChecks.All(root,output)).Concat(ReadingChecks.All(root, output)).Concat(SemanticChecks.All(root, output)).Concat(LiveStallChecks.All(root, output)).Concat(LatencyChecks.All(root, output)).Concat(MissingSentenceChecks.All(root,output)).Concat(SourceLatencyChecks.All(root,output)).Concat(OptimizationChecks.All(root,output));
foreach (var (name, test) in selectedChecks)
{
    var watch = Stopwatch.StartNew();
    try { await test(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true, ms = watch.ElapsedMilliseconds }); }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL " + name + ": " + ex);
        results.Add(new { name, passed = false, error = ex.ToString() });
        Environment.ExitCode = 1;
    }
}
if (args.Contains("--replay")) await Replay();
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("RESULTS " + output);

static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LiveCaptionsTranslator.csproj"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static TranslationSegment Segment(string text = "相同的开头，接着说明不同的结论。") => new()
{ Text = text, FinalAsr = true, SourceLang = "zh-CN", TargetLang = "en-US", Provider = "test", Model = "fake" };

Task TestSpill()
{
    using var queue = new SpillQueue<string>(2, "fifo");
    for (int i = 0; i < 500; i++) queue.Enqueue(i + " 中文\n两行");
    Check(queue.SpilledItems == 498, "overflow path was not exercised");
    queue.Complete();
    for (int i = 0; i < 500; i++)
        Check(queue.TryDequeue(out var value) && value == i + " 中文\n两行", "FIFO changed or item lost at " + i);
    Check(queue.IsCompleted && queue.Count == 0, "queue did not drain");
    Check(!Directory.EnumerateFiles("fifo").Any(), "drained spool file was not removed");
    return Task.CompletedTask;
}
async Task TestConcurrentSpill()
{
    using var queue = new SpillQueue<int>(3, "concurrent");
    var producer = Task.Run(() => { for (int i = 0; i < 1000; i++) queue.Enqueue(i); queue.Complete(); });
    int expected = 0;
    while (!queue.IsCompleted)
    {
        if (queue.TryDequeue(out var value)) Check(value == expected++, "concurrent FIFO mismatch");
        else await Task.Delay(1);
    }
    await producer;
    Check(expected == 1000, "concurrent records lost");
}
async Task TestOrdering()
{
    var rows = new List<TranslationResult>();
    int active = 0, peak = 0;
    await using var pipeline = new TranslationTaskQueue(async (s, token) =>
    {
        int now = Interlocked.Increment(ref active);
        int prior;
        do { prior = Volatile.Read(ref peak); } while (now > prior && Interlocked.CompareExchange(ref peak, now, prior) != prior);
        try { await Task.Delay(2 + (int)(s.Id % 7) * 2, token); return new(s, "重复译文"); }
        finally { Interlocked.Decrement(ref active); }
    }, r => { rows.Add(r); return Task.CompletedTask; }, maxRetries: 0,
        realtimeTimeout: TimeSpan.FromSeconds(2), spoolDirectory: "translation");
    for (int i = 0; i < 300; i++) pipeline.Enqueue(Segment());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipeline.CompleteAsync(timeout.Token);
    Check(rows.Count == 300, "burst lost translations");
    Check(rows.Select(r => r.Segment.Id).SequenceEqual(Enumerable.Range(1, 300).Select(i => (long)i)), "commit order changed");
    Check(peak <= 3 && peak > 1, "concurrency is not bounded at three");
    Check(pipeline.OutstandingCount == 0, "outstanding accounting incorrect");
}
async Task TestTimeout()
{
    var attempts = new ConcurrentDictionary<long, int>();
    var rows = new List<TranslationResult>();
    var laterDisplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var pipeline = new TranslationTaskQueue(async (s, token) =>
    {
        int attempt = attempts.AddOrUpdate(s.Id, 1, (_, n) => n + 1);
        if (s.Id == 1 && attempt == 1) await Task.Delay(Timeout.Infinite, token);
        if (s.Id == 1) await laterDisplayed.Task.WaitAsync(token);
        return new(s, "translated-" + s.Id);
    }, r =>
    {
        rows.Add(r);
        if (r.Segment.Id == 2 && r.Revision == 0) laterDisplayed.TrySetResult();
        return Task.CompletedTask;
    }, maxRetries: 1, realtimeTimeout: TimeSpan.FromMilliseconds(80), retryTimeout: TimeSpan.FromSeconds(2));
    pipeline.Enqueue(Segment("first"));
    pipeline.Enqueue(Segment("second"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await pipeline.CompleteAsync(timeout.Token);
    Check(rows.Count == 3, "expected two initial rows and one correction");
    Check(rows[0].Segment.Id == 1 && rows[0].IsPending, "missing first-segment placeholder");
    Check(rows[1].Segment.Id == 2 && !rows[1].IsError, "slow first result blocked second");
    Check(rows[2].Segment.Id == 1 && rows[2].Revision == 1 && !rows[2].IsError, "correction not associated with original ID");
}
async Task TestFault()
{
    var rows = new List<TranslationResult>();
    await using var pipeline = new TranslationTaskQueue((s, _) =>
    {
        if (s.Id == 1) throw new IOException("simulated provider error");
        return Task.FromResult(new TranslationResult(s, "ok"));
    }, r => { rows.Add(r); return Task.CompletedTask; }, maxRetries: 0);
    pipeline.Enqueue(Segment()); pipeline.Enqueue(Segment());
    await pipeline.CompleteAsync();
    Check(rows.Count == 2 && rows[0].IsError && rows[1].Segment.Id == 2, "fault wedged ordering");
}
Task TestTimeline()
{
    var timeline = new CaptionTimeline(3);
    for (int i = 1; i <= 3; i++) timeline.Upsert(new(Segment("same") with { Id = i }, "same"));
    timeline.Upsert(new(Segment("same") with { Id = 1 }, "corrected") { Revision = 1 });
    timeline.Upsert(new(Segment("same") with { Id = 1 }, "stale"));
    Check(timeline.Snapshot().Select(r => r.Segment.Id).SequenceEqual(new long[] { 1, 2, 3 }), "correction moved row");
    Check(timeline.Snapshot()[0].Text == "corrected", "stale revision replaced final");
    timeline.Upsert(new(Segment() with { Id = 4 }, "four"));
    timeline.Upsert(new(Segment() with { Id = 1 }, "late") { Revision = 2 });
    Check(timeline.Snapshot().Select(r => r.Segment.Id).SequenceEqual(new long[] { 2, 3, 4 }), "evicted row reappeared");
    return Task.CompletedTask;
}
async Task TestHistory()
{
    string session = Guid.NewGuid().ToString();
    var first = new TranslationResult(Segment() with { Id = 1 }, "pending") { IsPending = true };
    await SQLiteHistoryLogger.UpsertSegment(session, first);
    await SQLiteHistoryLogger.UpsertSegment(session, new(Segment() with { Id = 2 }, "second"));
    await SQLiteHistoryLogger.UpsertSegment(session, first with { Text = "corrected", Revision = 1, IsPending = false });
    await SQLiteHistoryLogger.UpsertSegment(session, first);
    using var connection = new SqliteConnection(SQLiteHistoryLogger.CONNECTION_STRING);
    await connection.OpenAsync();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT h.TranslatedText FROM TranslationHistory h JOIN SegmentHistory s ON s.HistoryId=h.Id WHERE s.SessionId=@Session ORDER BY s.SegmentId";
    command.Parameters.AddWithValue("@Session", session);
    var texts = new List<string>();
    using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) texts.Add(reader.GetString(0));
    Check(texts.SequenceEqual(new[] { "corrected", "second" }), "history duplicated or overwritten by similar text");
}
async Task TestCapture()
{
    byte[] expected = Enumerable.Range(0, 1280 * 500 + 1234).Select(i => (byte)(i % 251)).ToArray();
    var fakeAsr = new FakeAsr();
    var capture = new FakeCapture(expected, () => fakeAsr.Connected);
    using var loop = new AudioSpeechLoop(() => fakeAsr, () => capture);
    loop.Start();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await loop.StopAsync(timeout.Token);
    Check(fakeAsr.Ended, "end of audio not sent");
    Check(SHA256.HashData(expected).SequenceEqual(SHA256.HashData(fakeAsr.Data.ToArray())), "audio samples lost or reordered");
}
Task TestRawCapture()
{
    var bytes = Enumerable.Range(0, 400000).Select(i => (byte)(i % 239)).ToArray();
    using var source = new QueuedWaveProvider(new WaveFormat(48000, 16, 2));
    for (int i = 0; i < bytes.Length; i += 1000) source.AddSamples(bytes, i, Math.Min(1000, bytes.Length-i));
    using var actual = new MemoryStream();
    var read = new byte[716];
    int n;
    while ((n=source.Read(read, 0, read.Length)) > 0) actual.Write(read, 0, n);
    Check(actual.ToArray().SequenceEqual(bytes), "raw device packets were lost before resampling");
    Check(source.BufferedBytes == 0, "raw capture byte accounting incorrect");
    return Task.CompletedTask;
}
Task TestResampling()
{
    using var source = new QueuedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    var outputPcm = new SampleToWaveProvider16(new WdlResamplingSampleProvider(source.ToSampleProvider().ToMono(), 16000));
    var wave = new float[48000 * 2];
    for (int i = 0; i < 48000; i++) wave[i * 2] = wave[i * 2 + 1] = (float)(0.4 * Math.Sin(i * 2 * Math.PI * 440 / 48000));
    var bytes = new byte[wave.Length * sizeof(float)];
    Buffer.BlockCopy(wave, 0, bytes, 0, bytes.Length);
    for (int burst = 0; burst < 2; burst++)
    {
        source.AddSamples(bytes, 0, bytes.Length);
        var frame = new byte[1280];
        int total = 0, read;
        long energy = 0;
        while ((read = outputPcm.Read(frame, 0, frame.Length)) > 0)
        {
            total += read;
            Check(total <= 32016, "resampler emitted endless padding");
            for (int i = 0; i < read; i += 2) energy += Math.Abs((int)BitConverter.ToInt16(frame, i));
        }
        Check(Math.Abs(total - 32000) <= 16, "resampler lost audio around an empty input: " + total);
        Check(energy > 1000000, "resampler produced silence instead of resumed audio");
        Check(source.BufferedBytes == 0, "resampler left input packets behind");
    }
    return Task.CompletedTask;
}
Task TestModelRouting()
{
    var basePath = Path.Combine(root, "models");
    if (!Directory.Exists(basePath)) return Task.CompletedTask;
    foreach (var name in new[] { "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20", "sherpa-onnx-streaming-paraformer-bilingual-zh-en" })
        if (Directory.Exists(Path.Combine(basePath,name)))
            Check(!VadOfflineAsrClient.IsSupportedModel(Path.Combine(basePath,name)), name+" misclassified as Whisper");
    return Task.CompletedTask;
}
Task TestVad()
{
    string path = Path.Combine(root, "models", "silero_vad.onnx");
    if (!File.Exists(path)) { Console.WriteLine("SKIP native VAD: model not present"); return Task.CompletedTask; }
    float[] samples = LoadSamples(Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "test_wavs", "zh.wav"));
    var segments = new List<AudioSegment>();
    using var vad = new BoundedVadSegmenter(path, 0.4, 4);
    vad.SegmentReady += segments.Add;
    // Repeat without real-time pacing: deadlines must depend on sample count.
    for (int repeat = 0; repeat < 6; repeat++)
        for (int i = 0; i < samples.Length; i += 640)
            vad.Accept(samples.AsSpan(i, Math.Min(640, samples.Length - i)).ToArray());
    vad.Finish();
    Check(segments.Count > 0, "no speech segments detected");
    Check(segments.All(s => s.Samples.Length <= 64000), "hard duration limit exceeded");
    Check(segments.All(s => s.StartSample >= 0 && s.EndSample <= samples.Length * 6L), "padding leaked into sample timestamps");
    Check(segments.Zip(segments.Skip(1)).All(pair => pair.Second.StartSample >= pair.First.StartSample), "sample order changed");
    using var tail = new BoundedVadSegmenter(path, 0.4, 4);
    var tails = new List<AudioSegment>();
    tail.SegmentReady += tails.Add;
    tail.Accept(samples.Take(Math.Min(samples.Length, 32000)).ToArray());
    tail.Finish();
    Check(tails.Count > 0, "short final utterance disappeared at shutdown");
    // Compare with an unforced VAD pass over uninterrupted speech. A duration cap
    // must not punch holes into audio that the same detector considered speech.
    var continuous = Enumerable.Range(0, 6).SelectMany(_ => samples.Skip(18000).Take(54000)).ToArray();
    var referenceSegments = new List<AudioSegment>();
    var cappedSegments = new List<AudioSegment>();
    using var referenceVad = new BoundedVadSegmenter(path, 1.0, 30);
    using var cappedVad = new BoundedVadSegmenter(path, 1.0, 4);
    referenceVad.SegmentReady += referenceSegments.Add;
    cappedVad.SegmentReady += cappedSegments.Add;
    referenceVad.Accept(continuous); referenceVad.Finish();
    cappedVad.Accept(continuous); cappedVad.Finish();
    var covered = new bool[continuous.Length];
    foreach (var segment in cappedSegments)
    {
        Check(segment.Samples.SequenceEqual(continuous.AsSpan((int)segment.StartSample, segment.Samples.Length).ToArray()), "VAD changed or mislocated boundary samples");
        Array.Fill(covered, true, (int)segment.StartSample, segment.Samples.Length);
    }
    foreach (var segment in referenceSegments)
        Check(covered.AsSpan((int)segment.StartSample, segment.Samples.Length).ToArray().All(value => value), "forced split dropped speech samples");
    Console.WriteLine($"VAD segments={segments.Count} forced={vad.ForcedSplits} max={segments.Max(s=>s.Samples.Length)/16000.0:F3}s");
    return Task.CompletedTask;
}
static float[] LoadSamples(string file)
{
    using var reader = new AudioFileReader(file);
    ISampleProvider source = reader;
    if (source.WaveFormat.Channels > 1) source = source.ToMono();
    if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);
    var values = new List<float>();
    var buffer = new float[16000];
    int count;
    while ((count = source.Read(buffer, 0, buffer.Length)) > 0) values.AddRange(buffer.Take(count));
    return values.ToArray();
}
Task TestParagraphs()
{
    var first = Segment("开放时间是早上九点至下午") with
    { Id = 1, AudioStreamId = "stream", AudioStartSample = 0, AudioEndSample = 64000, ForcedEnd = true };
    var second = Segment("五点。") with
    { Id = 2, AudioStreamId = "stream", AudioStartSample = 64000, AudioEndSample = 80000, ContinuesPrevious = true };
    var timeline = new CaptionTimeline();
    timeline.Upsert(new(first, "Open from 9 a.m. to"));
    long originalParagraph = CaptionParagraph.Build(timeline.Snapshot()).Single().Id;
    timeline.Upsert(new(second, "5 p.m."));
    var joined = CaptionParagraph.Build(timeline.Snapshot()).Single();
    Check(joined.Id == originalParagraph && joined.Segments.Count == 2, "continuation moved to a new paragraph");
    Check(joined.SourceText == "开放时间是早上九点至下午五点。", "source continuation gained a hard line break");
    Check(joined.TranslatedText == "Open from 9 a.m. to 5 p.m.", "Latin words lost their separator");
    timeline.Upsert(new(second, "five in the afternoon.") { Revision = 1 });
    joined = CaptionParagraph.Build(timeline.Snapshot()).Single();
    Check(joined.Id == originalParagraph && joined.TranslatedText.EndsWith("five in the afternoon."), "late correction broke paragraph association");
    return Task.CompletedTask;
}
Task TestParagraphBoundaries()
{
    var first = Segment("第一句。") with
    { Id = 1, AudioStreamId = "stream", AudioStartSample = 0, AudioEndSample = 64000 };
    var next = Segment("第二句。") with
    { Id = 2, AudioStreamId = "stream", AudioStartSample = 64000, AudioEndSample = 128000 };
    Check(CaptionParagraph.Build(new[] { new TranslationResult(first, "one"), new(next, "two") }).Length == 2, "natural sentence boundary was removed");
    first = first with { ForcedEnd = true };
    next = next with { ContinuesPrevious = true, AudioStreamId = "restarted" };
    Check(CaptionParagraph.Build(new[] { new TranslationResult(first, "one"), new(next, "two") }).Length == 2, "audio restart joined unrelated speech");
    var longSpeech = Enumerable.Range(0, 6).Select(i => new TranslationResult(first with
    { Id = i + 1, AudioStartSample = i * 128000L, AudioEndSample = (i + 1) * 128000L, ContinuesPrevious = i > 0 }, "text"));
    var paragraphs = CaptionParagraph.Build(longSpeech);
    Check(paragraphs.Length == 3 && paragraphs.All(p => p.Segments.Count == 2), "continuous speech grew an unbounded paragraph");
    Check(paragraphs.Sum(p => p.Segments.Count) == 6, "reading groups lost a segment");
    return Task.CompletedTask;
}
Task TestReadingProfile()
{
    string path = Path.Combine(root, "models", "silero_vad.onnx");
    if (!File.Exists(path)) { Console.WriteLine("SKIP reading profile: model not present"); return Task.CompletedTask; }
    var samples = LoadSamples(Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "test_wavs", "zh.wav"));
    var oldSegments = new List<AudioSegment>();
    var readingSegments = new List<AudioSegment>();
    using var oldVad = new BoundedVadSegmenter(path, 0.4, 4);
    using var readingVad = new BoundedVadSegmenter(path, 0.6, 8);
    oldVad.SegmentReady += oldSegments.Add;
    readingVad.SegmentReady += readingSegments.Add;
    oldVad.Accept(samples); oldVad.Finish();
    readingVad.Accept(samples); readingVad.Finish();
    Check(oldSegments.Count > 1 && readingSegments.Count == 1, "normal sentence is still split into fragments");
    Check(oldSegments[0].ForcedEnd && oldSegments[1].ContinuesPrevious, "forced continuation metadata was lost");
    Check(!readingSegments[0].ForcedEnd && readingSegments[0].EndSample <= samples.Length, "reading profile invented a forced boundary");
    Console.WriteLine($"READABILITY segments before={oldSegments.Count} after={readingSegments.Count}; forced before={oldVad.ForcedSplits} after={readingVad.ForcedSplits}");
    return Task.CompletedTask;
}
Task TestJsonl()
{
    string directory = Path.Combine(output, "jsonl");
    using var logger = new JsonlLogger(directory, "test-session");
    logger.Log(new TranslationRecord { SessionId = "test-session", SegmentId = 1, SourceText = "测试完整记录。", AudioStreamId = "stream", ForcedEnd = true });
    logger.Log(new TranslationRecord { SessionId = "test-session", SegmentId = 2, SourceText = "继续。", AudioStreamId = "stream", ContinuesPrevious = true });
    using var file = new FileStream(logger.GetFilePath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(file);
    var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check(lines.Length == 2, "session records were silently lost");
    using var record = JsonDocument.Parse(lines[1]);
    Check(record.RootElement.GetProperty("continues_previous").GetBoolean(), "continuation metadata not saved");
    return Task.CompletedTask;
}
async Task Replay()
{
    string model = Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
    var fileIndex = Array.IndexOf(args, "--audio");
    string file = fileIndex >= 0 ? args[fileIndex + 1] : Path.Combine(model, "test_wavs", "zh.wav");
    float[] samples = LoadSamples(file);
    foreach (double speed in new[] { 1.0, 1.25, 1.5, 2.0 })
    {
        using var asr = new VadOfflineAsrClient(model, Path.Combine(root, "models", "silero_vad.onnx"), 8, "zh-CN", 0.6, 8);
        var recognized = new List<RecognizedSpeech>();
        asr.SegmentRecognized += recognized.Add;
        await asr.ConnectAsync();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < samples.Length; i += 640)
        {
            int count = Math.Min(640, samples.Length - i);
            var pcm = new byte[count * 2];
            for (int j = 0; j < count; j++)
            {
                short value = (short)Math.Clamp(samples[i+j] * 32768, short.MinValue, short.MaxValue);
                pcm[j*2] = (byte)value; pcm[j*2+1] = (byte)(value >> 8);
            }
            await asr.SendAudioAsync(pcm);
            double remaining = (i + count) / 16000.0 / speed * 1000 - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
        }
        await asr.SendEndAsync();
        var summary = new { speed, mode = "accelerated delivery; original pitch and sample timeline", audioSeconds = samples.Length / 16000.0,
            segments = recognized.Count, empty = recognized.Count(r=>r.Text.Length==0),
            maxSegmentSeconds = recognized.Max(r=>(r.EndSample-r.StartSample)/16000.0),
            maxAsrQueueMs = recognized.Max(r=>r.QueueMs), maxAsrMs = recognized.Max(r=>r.InferenceMs),
            wallSeconds = clock.Elapsed.TotalSeconds };
        Console.WriteLine("REPLAY " + JsonSerializer.Serialize(summary));
        await File.WriteAllTextAsync(Path.Combine(output, $"replay-{speed}.json"), JsonSerializer.Serialize(new { summary, recognized }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
sealed class FakeCapture(byte[] bytes, Func<bool> connected) : IAudioCapture
{
    public event Action<byte[]>? DataAvailable;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Start()
    {
        if (!connected()) throw new Exception("capture started before ASR was ready");
        for (int i = 0; i < bytes.Length; i += 1280)
            DataAvailable?.Invoke(bytes.AsSpan(i, Math.Min(1280, bytes.Length-i)).ToArray());
        Started.TrySetResult();
    }
    public void Stop() { }
    public void Dispose() { }
}
sealed class FakeAsr : IAsrClient
{
    public MemoryStream Data { get; } = new();
    public bool Connected, Ended;
    public bool IsOpen => Connected;
    public event Action<string,bool>? OnResult;
    public event Action<string>? OnError;
    public event Action? OnClosed;
    public async Task ConnectAsync(CancellationToken token = default) { await Task.Delay(25, token); Connected = true; }
    public async Task SendAudioAsync(byte[] data, CancellationToken token = default) { await Task.Delay(1, token); Data.Write(data); }
    public Task SendEndAsync(CancellationToken token = default) { Ended = true; return Task.CompletedTask; }
    public void Dispose() { Connected = false; }
}



