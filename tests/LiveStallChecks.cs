using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.speech;
using SherpaOnnx;

internal static class LiveStallChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Timed Chinese anchor corrections retain numbers, endpoints and repeated speech", AnchorCorrection);
        yield return ("Automatic replay rebuilds a failed pending unit without duplicating committed IDs", Retry);
        string directory = Path.Combine(root, "artifacts", "semantic-stall-before", "pending");
        if (TestMode.IncludeLocalFixtures && Directory.Exists(directory))
            yield return ("A local stalled-audio fixture produces continuous units through the final tail", () => Replay(root, output, directory));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static AlignedText Timed(string text, long start = 0) => new(text,
        text.Select((c, i) => new TimedCharacter(i, start + i * 2400, i)).ToArray());

    private static Task AnchorCorrection()
    {
        const string old = "不停实验却始终遇到同一个基术瓶颈。";
        const string corrected = "不停实验却始终遇到同一个技术瓶颈。";
        var anchor = Timed(old).AnchorBefore(old.Length)!;
        var current = Timed(corrected + "要看清药物引发的生化反应。");
        Check(!current.Resolve(anchor, out _, allowRevisions: false), "fixture no longer reproduces the rigid-anchor failure");
        Check(current.Resolve(anchor, out int end) && end == corrected.Length, "same timed corrected Chinese words cannot hand over the tail");
        Check(current.Text[end..].StartsWith("要看清"), "uncommitted source was consumed by correction matching");
        Check(!current.Resolve(anchor with { LastSample = anchor.LastSample + 16000, FirstSample = anchor.FirstSample + 16000 }, out _), "similar text at another time was accepted");
        var repeated = Timed(corrected + corrected + "后文继续。");
        Check(repeated.Resolve(anchor, out end) && end == corrected.Length, "real repetition was removed as overlap");
        var ambiguous = new AlignedText(corrected + corrected, Timed(corrected).Characters.Concat(
            Timed(corrected).Characters.Select(c => c with { Index = c.Index + corrected.Length })).ToArray());
        Check(!ambiguous.Resolve(anchor, out _), "ambiguous timed corrections were accepted");
        foreach (var pair in new[] { ("模型的第21层含16384个特征。", "模型的第22层含16385个特征。"),
            ("我们实际通过研究讨论模型Gemma。", "我们实际通过研究讨论模型Gamma。") })
            Check(!Timed(pair.Item2).Resolve(Timed(pair.Item1).AnchorBefore(pair.Item1.Length)!, out _), "numbers or Latin names were treated as Chinese corrections");
        return Task.CompletedTask;
    }

    private static Task Retry()
    {
        int calls = 0;
        var engine = new UtteranceRecognizer(_ => ++calls == 1 ? throw new IOException("transient decoder error") : "这是恢复以后完整识别的一句话。", "retry");
        var ready = new List<RecognizedSpeech>();
        engine.Ready += ready.Add;
        try { engine.Accept(new(0, new float[16000], DateTimeOffset.UtcNow)); }
        catch (IOException) { }
        var checkpoint = engine.PendingRecovery!;
        engine.Recover(checkpoint);
        engine.Accept(new(20000, new float[16000], DateTimeOffset.UtcNow));
        Check(ready.Count == 2 && ready[0].UtteranceKey == checkpoint.UtteranceKey && ready[1].UtteranceKey != ready[0].UtteranceKey,
            "retry duplicated a committed ID or stranded the next utterance");
        Check(ready.All(x => x.IsFinal && !x.IsIncomplete), "a transient decode error fabricated a truncated caption");
        return Task.CompletedTask;
    }

    private static async Task Replay(string root, string output, string directory)
    {
        var records = Directory.GetFiles(directory, "*.pending.jsonl").OrderBy(x => x).SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<PendingRecognition>(line)!).OrderBy(x => x.Audio.StartSample).ToArray();
        var first = records[0];
        string model = Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = 8;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.SenseVoice.Model = Path.Combine(model, "model.onnx");
        config.ModelConfig.SenseVoice.Language = "zh";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = Path.Combine(model, "tokens.txt");
        using var decoder = new OfflineRecognizer(config);
        AsrSnapshot Decode(float[] samples)
        {
            using var stream = decoder.CreateStream();
            stream.AcceptWaveform(16000, samples); decoder.Decode(stream);
            var result = stream.Result;
            return new(result.Text.Trim(), result.Tokens, result.Timestamps);
        }
        // The next physical window following the user's last good commit.
        var probeSamples = first.Audio.Samples.Take(6573568 - (int)first.Audio.StartSample).ToArray();
        var probe = Decode(probeSamples);
        var alignment = probe.Align(first.Audio.StartSample, probeSamples.Length)!;
        bool oldResolved = alignment.Resolve(first.Anchor!, out _, allowRevisions: false);
        bool newResolved = alignment.Resolve(first.Anchor!, out int boundary);
        await File.WriteAllTextAsync(Path.Combine(output, "stall-anchor.json"), JsonSerializer.Serialize(
            new { first.Anchor, probe.Text, oldResolved, newResolved, boundary }, new JsonSerializerOptions { WriteIndented = true }));
        Check(!oldResolved && newResolved, "the live failure was not reproduced and repaired at its original boundary");

        var units = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(Decode, first.AudioStreamId);
        engine.Ready += units.Add;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? error = null;
        try
        {
            engine.Recover(first);
            foreach (var record in records.Skip(1)) engine.Accept(record.Audio);
            engine.Finish();
        }
        catch (Exception ex) { error = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(output, "live-stall-replay.json"), JsonSerializer.Serialize(
            new { seconds = (records[^1].Audio.EndSample - first.Audio.StartSample) / 16000.0,
                elapsedMs = watch.Elapsed.TotalMilliseconds, error, units }, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(output, "recovered-source.txt"), string.Join(Environment.NewLine + Environment.NewLine, units.Select(x => x.Text)));
        Check(error == null, "live replay still stalled: " + error);
        Check(units.Count >= 10 && units[0].StartSample == first.UnitStartSample && units[^1].EndSample == records[^1].Audio.EndSample,
            "replay did not cover the pending source through the final tail");
        Check(units.Select(x => x.UtteranceKey).Distinct().Count() == units.Count, "recovery reused an already committed unit ID");
        Check(units.Zip(units.Skip(1)).All(p => p.First.EndSample == p.Second.StartSample), "recovery omitted a logical source range");
        Check(units.All(x => x.EndSample - x.StartSample < 320000), "continuous output stalled for over twenty audio seconds");
    }
}
