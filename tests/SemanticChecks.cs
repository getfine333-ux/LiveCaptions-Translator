using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.speech;
using LiveCaptionsTranslator.utils;
using NAudio.Wave;

internal static class SemanticChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Alignment rejects invalid timing and text normalization mismatch", Alignment);
        yield return ("Stable semantic units preserve all text and real repetitions across audio rollover", Units);
        yield return ("Soft target waits through an unfinished number and false trailing period", Lookahead);
        yield return ("Clause choice protects dependent phrases, numeric groups and abbreviations", Clauses);
        yield return ("Punctuation edits do not starve stable boundaries or confirm artificial endpoints", Punctuation);
        yield return ("Over a minute without punctuation rotates storage without displaying cut sentences", PrivateCarry);
        yield return ("Silence requires ordered contiguous samples, never decoder wall time", Silence);
        yield return ("Ambiguous or shifted overlap cannot silently discard spoken words", Anchors);
        if (TestMode.IncludeLocalFixtures)
        {
            yield return ("Native forced windows do not emit false silence and retain FIFO event order", () => NativeEvents(root));
            yield return ("Reset at a pause preserves every sample owned by an unforced VAD pass", () => NativeCoverage(root));
            yield return ("Real continuous Chinese audio completes semantic streaming without recovery failures", () => NativeLong(root, output));
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static AudioSegment Audio(int start, int count, bool forced = true) =>
        new(start, Enumerable.Range(start, count).Select(i => (float)i).ToArray(), DateTimeOffset.UtcNow)
        { ForcedEnd = forced, ContinuesPrevious = start > 0 };

    // Deterministic recognizer with actual absolute token positions, including
    // context redecoding after the coordinator trims its retained audio buffer.
    private static AsrSnapshot Decode(string text, float[] audio, int step = 2400)
    {
        int start = (int)audio[0], end = start + audio.Length;
        var positions = Enumerable.Range(0, text.Length).Where(i => (i + 1) * step >= start && (i + 1) * step < end).ToArray();
        return new(new string(positions.Select(i => text[i]).ToArray()), positions.Select(i => text[i].ToString()).ToArray(),
            positions.Select(i => ((i + 1) * step - start) / 16000f).ToArray());
    }

    private static Task Alignment()
    {
        Check(new AsrSnapshot("第21层有16,384个特征。", new[] { "第", "21", "层有", "16,384", "个特征", "。" },
            new[] { 0f, .1f, .2f, .3f, .4f, .5f }).Align(32000, 16000) != null, "mixed numeric tokens did not align");
        Check(new AsrSnapshot("Hello world", new[] { "▁Hello", "▁world" }, new[] { 0f, .2f }).Align(0, 16000) != null, "word spacing lost alignment");
        foreach (var snapshot in new[] {
            new AsrSnapshot("你好", new[] { "你", "好" }, new[] { .5f, .1f }),
            new AsrSnapshot("你好", new[] { "你", "好" }, new[] { float.NaN, .1f }),
            new AsrSnapshot("你好", new[] { "你", "好" }, new[] { 0f, 2f }),
            new AsrSnapshot("21", new[] { "二", "十", "一" }, new[] { 0f, .1f, .2f }),
            new AsrSnapshot("你好") })
            Check(snapshot.Align(0, 16000) == null, "invalid alignment was accepted");
        return Task.CompletedTask;
    }

    private static Task Units()
    {
        const string sentence = "我们把示例文本输入模型得到完整的结果。";
        string source = string.Concat(Enumerable.Repeat(sentence, 8));
        var emitted = new List<RecognizedSpeech>();
        var starts = new List<int>();
        var engine = new UtteranceRecognizer(samples => { starts.Add((int)samples[0]); return Decode(source, samples); }, "units");
        engine.Ready += emitted.Add;
        int length = (source.Length + 1) * 2400;
        int cursor = 0;
        while (cursor < length)
        {
            int count = Math.Min(cursor == 0 ? 128000 : 32000, length - cursor);
            engine.Accept(Audio(cursor, count, cursor + count < length));
            cursor += count;
        }
        Check(string.Concat(emitted.Select(x => x.Text)) == source, "semantic handoff lost or duplicated source text");
        Check(emitted.Count > 2 && emitted.All(x => x.IsFinal && !x.IsIncomplete && x.SourceRevision == 1), "draft or partial units leaked");
        Check(emitted.Select(x => x.UtteranceKey).Distinct().Count() == emitted.Count, "later text rewrote a read prefix");
        Check(starts.Any(x => x > 0), "rolling decode never exercised audio compaction");
        Check(emitted.Zip(emitted.Skip(1)).All(pair => pair.First.EndSample == pair.Second.StartSample), "logical unit sample boundaries are not contiguous");
        return Task.CompletedTask;
    }

    private static Task Lookahead()
    {
        const string source = "我们通过这个稀疏自编码器分析Gemma第21层的16384个特征，然后得到了完整的实验结果。";
        int calls = 0;
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(samples =>
        {
            var snapshot = Decode(source, samples);
            if (++calls == 1) return snapshot with { Text = snapshot.Text + "。",
                Tokens = snapshot.Tokens.Append("。").ToArray(), Timestamps = snapshot.Timestamps.Append((samples.Length - 1) / 16000f).ToArray() };
            return snapshot;
        }, "lookahead");
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 128000));
        Check(emitted.Count == 0, "target length or artificial edge period became a sentence");
        // Use a slower token clock so the numeric phrase crosses the first target.
        engine.Reset(); calls = 0;
        var slower = new UtteranceRecognizer(samples => Decode(source, samples, 4000), "digits");
        slower.Ready += emitted.Add;
        slower.Accept(Audio(0, 128000));
        Check(emitted.Count == 0, "number prefix appeared at the threshold");
        slower.Accept(Audio(128000, (source.Length + 1) * 4000 - 128000, false));
        Check(string.Concat(emitted.Select(x => x.Text)) == source && emitted.All(x => !x.IsIncomplete), "lookahead lost digits or clipped the phrase");
        return Task.CompletedTask;
    }

    private static Task Clauses()
    {
        static BoundaryChoice? Choose(string source)
        {
            var aligned = Decode(source, Audio(0, (source.Length + 1) * 3200).Samples, 3200).Align(0, (source.Length + 1) * 3200)!;
            return SemanticBoundary.Find(aligned, 0, source.Length, "", 0, Math.Max(240000, (source.Length + 1) * 3200), 128000);
        }
        Check(Choose("我们已经通过这一组完整的实验验证模型能够正常工作，接下来会介绍实际应用")?.Reason == "semantic_clause", "independent clause was ignored");
        Check(Choose("如果我们把所有不同位置上训练得到的稀疏编码器连接起来，就可以观察结果") == null, "a dependent if-clause was shown alone");
        Check(Choose("如果我们把所有不同位置上训练得到的稀疏编码器连接起来。就可以观察结果") == null, "an ASR full stop disguised a dependent if-clause");
        Check(Choose("我们正在讨论这个模型所拥有的特征数量为16,384个并且仍在继续分析") == null, "numeric grouping became a clause boundary");
        string english = "Prof. Smith measured 3.14 in Gemma.v2 today.";
        foreach (int i in Enumerable.Range(0, english.Length).Where(i => english[i] == '.' && i < english.Length - 1))
            Check(!SemanticBoundary.IsSentenceEnd(english, i), "abbreviation/decimal/identifier was cut");
        foreach (string tail in new[] { "其实背后都。", "有，但这里有个。", "就能调用所有使用。", "一个agent接到目标后会自。" })
            Check(UtteranceRecognizer.LooksIncomplete(tail), "known live failure was treated as complete: " + tail);
        return Task.CompletedTask;
    }

    private static Task PrivateCarry()
    {
        // No punctuation for over 60 seconds. Storage pressure must not emit a
        // sentence; only the eventual natural endpoint releases the whole text.
        string source = string.Concat(Enumerable.Repeat("这一段连续说明不同位置的编码特征以及实际应用过程", 14)) + "最后全部说明完毕。";
        var emitted = new List<RecognizedSpeech>();
        var lengths = new List<int>();
        bool carried = false;
        var engine = new UtteranceRecognizer(samples => { lengths.Add(samples.Length); return Decode(source, samples, 3200); }, "carry");
        engine.Ready += emitted.Add;
        int total = (source.Length + 1) * 3200;
        Check(total > 60 * 16000, "long-speech fixture no longer crosses one minute");
        for (int cursor = 0; cursor < total;)
        {
            int count = Math.Min(cursor == 0 ? 128000 : 32000, total - cursor);
            bool forced = cursor + count < total;
            engine.Accept(Audio(cursor, count, forced));
            if (forced) Check(emitted.Count == 0, "storage capacity published an unfinished fragment");
            carried |= engine.PendingRecovery?.CarriedText.Length > 0;
            cursor += count;
        }
        Check(carried && lengths.Max() <= 480000, "private carry was not exercised or native decode exceeded its budget");
        Check(emitted.Single().Text == source && !emitted[0].IsIncomplete, "private rollover lost stable text or duplicated the overlap");
        return Task.CompletedTask;
    }

    private static Task Punctuation()
    {
        const string previous = "首先我们已经验证模型工作正常，接下来再检查处理结果。最后还有一段说明";
        const string current = "首先，我们已经验证模型工作正常，接下来再检查处理结果。最后还有一段说明需要继续";
        int stable = SemanticBoundary.StableLength(previous, current);
        Check(stable > current.IndexOf('。'), "an earlier comma change starved an otherwise stable sentence");
        Check(SemanticBoundary.StableLength("模型有1。", "模型有16384个特征。") == 4, "number continuation was incorrectly stable");
        var oldAligned = new AsrSnapshot("完成了这一段分析。", "完成了这一段分析。".Select(c => c.ToString()).ToArray(),
            Enumerable.Range(0, 9).Select(i => i * .4f).ToArray()).Align(0, 160000)!;
        const string next = "完成了这一段分析。接下来还有具体的说明";
        var aligned = new AsrSnapshot(next, next.Select(c => c.ToString()).ToArray(),
            Enumerable.Range(0, next.Length).Select(i => i * .4f).ToArray()).Align(0, 160000)!;
        Check(SemanticBoundary.Find(aligned, 0, next.Length, "", 0, 160000, 128000, oldAligned, true) == null,
            "a punctuation mark on the previous window edge became a confirmed sentence");
        return Task.CompletedTask;
    }

    private static Task Silence()
    {
        var emitted = new List<RecognizedSpeech>();
        var engine = new UtteranceRecognizer(_ => "这句话刚好在窗口边界说完。", "silence");
        engine.Ready += emitted.Add;
        engine.Accept(Audio(0, 128000));
        engine.ObserveSilence(0, 160000); // stale audio-clock evidence overlaps speech
        engine.ObserveSilence(128000, 128512); // native Flush followed by one frame
        Check(emitted.Count == 0, "stale or instantaneous silence finalized a sentence");
        engine.ObserveSilence(128000, 160000);
        Check(emitted.Count == 1 && emitted[0].EndReason == "observed_silence" && !emitted[0].IsIncomplete, "real contiguous silence did not finalize its tail");
        return Task.CompletedTask;
    }

    private static Task Anchors()
    {
        const string phrase = "这里重复的是同一句完整的话。";
        var aligned = Decode(phrase + phrase, Audio(0, 160000).Samples).Align(0, 160000)!;
        var anchor = aligned.AnchorBefore(phrase.Length)!;
        Check(aligned.Resolve(anchor, out int end) && end == phrase.Length, "actual second occurrence was discarded as overlap");
        var leadingCorrection = aligned with { Text = "在" + aligned.Text[1..] };
        Check(leadingCorrection.Resolve(anchor, out end) && end == phrase.Length,
            "a context-leading correction prevented an exact timed suffix handoff");
        var privateAnchor = aligned.AnchorBefore(phrase.Length - 1)!;
        Check(aligned.Resolve(privateAnchor, out end) && end == phrase.Length - 1,
            "private carry consumed punctuation that still belonged to its tail");
        Check(!aligned.Resolve(anchor with { FirstSample = anchor.FirstSample + 16000, LastSample = anchor.LastSample + 16000 }, out _), "shifted text match ignored its audio position");
        var ambiguous = new AlignedText(phrase + phrase, aligned.Characters.Select(c => c.Index >= phrase.Length
            ? c with { Sample = c.Sample - phrase.Length * 2400 } : c).ToArray());
        Check(!ambiguous.Resolve(anchor, out _), "ambiguous anchor silently chose one occurrence");
        return Task.CompletedTask;
    }

    private static float[] Fixture(string root)
    {
        using var reader = new WaveFileReader(Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "test_wavs", "zh.wav"));
        var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
        return Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
    }

    private static Task NativeEvents(string root)
    {
        var continuous = Enumerable.Range(0, 7).SelectMany(_ => Fixture(root).Skip(18000).Take(54000)).ToArray();
        using var vad = new BoundedVadSegmenter(Path.Combine(root, "models", "silero_vad.onnx"), 1, 4);
        var events = new List<AudioInputEvent>();
        vad.InputReady += events.Add;
        vad.Accept(continuous);
        Check(events.Any(x => x.Audio?.ForcedEnd == true), "native forced windows were not exercised");
        Check(events.All(x => x.Audio != null), "Flush manufactured a silence event during continuous speech");
        vad.Accept(new float[64000]);
        vad.Finish();
        int silence = events.FindIndex(x => x.Audio == null);
        Check(silence > 0 && events[silence].SilenceEnd - events[silence].SilenceStart >= 32000, "real silence never reached the FIFO");
        Check(events.Take(silence).Where(x => x.Audio != null).All(x => x.Audio!.EndSample <= events[silence].SilenceStart), "silence overtook pending audio");
        using var queue = new SpillQueue<AudioInputEvent>(1, "semantic-events");
        foreach (var item in events) queue.Enqueue(item);
        foreach (var expected in events)
        {
            Check(queue.TryDequeue(out var actual) && actual.SilenceStart == expected.SilenceStart && actual.Audio?.StartSample == expected.Audio?.StartSample,
                "disk spill reordered typed input events");
        }
        return Task.CompletedTask;
    }

    private static async Task NativeLong(string root, string output)
    {
        var samples = Enumerable.Range(0, 7).SelectMany(_ => Fixture(root)).Concat(new float[64000]).ToArray();
        var bytes = samples.SelectMany(x => BitConverter.GetBytes((short)(x * 32768))).ToArray();
        using var client = new VadOfflineAsrClient(Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17"),
            Path.Combine(root, "models", "silero_vad.onnx"), 8, "zh", 1.5, 8);
        var speech = new List<RecognizedSpeech>();
        var errors = new List<string>();
        client.SegmentRecognized += speech.Add;
        client.OnError += errors.Add;
        await client.ConnectAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < bytes.Length; i += 1280)
            await client.SendAudioAsync(bytes.AsSpan(i, Math.Min(1280, bytes.Length - i)).ToArray());
        await client.SendEndAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "semantic-native-long.json"), JsonSerializer.Serialize(
            new { seconds = samples.Length / 16000.0, elapsedMs = watch.Elapsed.TotalMilliseconds, speech, errors }, new JsonSerializerOptions { WriteIndented = true }));
        Check(errors.Count == 0, "native semantic handoff failed: " + string.Join("; ", errors));
        Check(speech.Count > 1 && speech.Any(x => x.EndReason.StartsWith("semantic_")) &&
            speech.All(x => x.IsFinal && !x.IsIncomplete && x.EndReason != "capacity"), "real continuous speech did not produce stable semantic units");
        Check(string.Concat(speech.Select(x => x.Text)).Count(c => c == '9') == 7 &&
            string.Concat(speech.Select(x => x.Text)).Count(c => c == '5') == 7, "real repeated sentences lost or duplicated their numbers");
    }

    private static Task NativeCoverage(string root)
    {
        var samples = Enumerable.Range(0, 4).SelectMany(_ => Fixture(root)).Concat(new float[64000]).ToArray();
        var reference = new List<AudioSegment>();
        var split = new List<AudioSegment>();
        var observed = new List<AudioSegment>();
        using var whole = new BoundedVadSegmenter(Path.Combine(root, "models", "silero_vad.onnx"), 1.5, 30);
        using var capped = new BoundedVadSegmenter(Path.Combine(root, "models", "silero_vad.onnx"), 1.5, 8);
        using var frequent = new BoundedVadSegmenter(Path.Combine(root, "models", "silero_vad.onnx"), 1.5, 8, 2);
        whole.SegmentReady += reference.Add; capped.SegmentReady += split.Add;
        frequent.SegmentReady += observed.Add;
        whole.Accept(samples); whole.Finish(); capped.Accept(samples); capped.Finish(); frequent.Accept(samples); frequent.Finish();
        foreach (var plan in new[] {split,observed})
        {
            var covered = new bool[samples.Length];
            foreach (var segment in plan)
            {
                Check(segment.Samples.SequenceEqual(samples.AsSpan((int)segment.StartSample, segment.Samples.Length).ToArray()),
                    "window reset changed actual audio or substituted silence");
                Check(!covered.AsSpan((int)segment.StartSample, segment.Samples.Length).ToArray().Any(x => x), "window reset submitted audio twice");
                Array.Fill(covered, true, (int)segment.StartSample, segment.Samples.Length);
            }
            foreach (var segment in reference)
                Check(covered.AsSpan((int)segment.StartSample, segment.Samples.Length).ToArray().All(x => x), "window reset lost samples around a pause");
        }
        return Task.CompletedTask;
    }
}
