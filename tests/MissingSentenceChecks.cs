using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.speech;
using SherpaOnnx;

internal static class MissingSentenceChecks
{
    public static IEnumerable<(string name, Func<Task> test)> Probe(string root, string output)
    {
        yield return ("Probe the three saved missing-sentence ranges with the local recognizer", () => Inspect(root,output));
    }
    public static IEnumerable<(string name, Func<Task> test)> All(string root,string output)
    {
        if (TestMode.IncludeLocalFixtures && Directory.Exists(Path.Combine(root,"artifacts","missing-sentences-before","pending")))
            yield return ("All three actual dropped audio ranges recover their numbers, words and final tails", () => Inspect(root,output,true));
    }
    private static async Task Inspect(string root, string output, bool verify = false)
    {
        string path = Path.Combine(root,"artifacts","missing-sentences-before","pending");
        var records = Directory.GetFiles(path,"*.pending.jsonl").SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<PendingRecognition>(line)!)
            .GroupBy(p => p.UtteranceKey).Select(g => g.OrderByDescending(p => p.Text.Length).First()).OrderBy(p => p.UnitStartSample).ToArray();
        string model = Path.Combine(root,"models","sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = 8; config.ModelConfig.Provider = "cpu";
        config.ModelConfig.SenseVoice.Model = Path.Combine(model,"model.onnx");
        config.ModelConfig.SenseVoice.Language = "zh"; config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = Path.Combine(model,"tokens.txt");
        using var decoder = new OfflineRecognizer(config);
        AsrSnapshot Decode(float[] samples)
        {
            using var stream = decoder.CreateStream();
            stream.AcceptWaveform(16000,samples); decoder.Decode(stream);
            var result = stream.Result;
            return new(result.Text.Trim(),result.Tokens,result.Timestamps);
        }
        var report = new List<object>();
        foreach (var p in records)
        {
            var snapshot = Decode(p.Audio.Samples);
            var alignment = snapshot.Align(p.Audio.StartSample,p.Audio.Samples.Length);
            bool resolved = alignment != null && alignment.Resolve(p.Anchor!,out _);
            var cropped = new List<object>();
            foreach (int preroll in new[] {0,1600,3200})
            {
                int cut = (int)(p.UnitStartSample-p.Audio.StartSample)-preroll;
                var tail = Decode(p.Audio.Samples[cut..]);
                cropped.Add(new { preroll,tail.Text,near_start=tail.Tokens.Zip(tail.Timestamps).Take(10).Select(t => new {token=t.First,time=t.Second}) });
            }
            var engine = new UtteranceRecognizer(Decode,p.AudioStreamId);
            var units = new List<RecognizedSpeech>(); engine.Ready += units.Add;
            string? error = null;
            try { engine.Recover(p); engine.Finish(); } catch(Exception e) {error=e.Message;}
            report.Add(new {p.UtteranceKey,p.UnitStartSample,p.Anchor,saved_text=p.Text,full_text=snapshot.Text,
                resolved,near_boundary=alignment?.Characters.Where(c => Math.Abs(c.Sample-p.UnitStartSample)<20000)
                .Select(c => new {character=alignment.Text[c.Index],relative_s=(c.Sample-p.UnitStartSample)/16000d}), cropped,error,units});
            if (verify)
            {
                if (error != null || units.Count == 0 || units[0].StartSample != p.UnitStartSample || units[^1].EndSample != p.Audio.EndSample ||
                    !units.Zip(units.Skip(1)).All(x => x.First.EndSample==x.Second.StartSample) ||
                    units.Select(x => x.UtteranceKey).Distinct().Count()!=units.Count)
                    throw new Exception("Actual missing source still drops audio or duplicates units: "+p.UtteranceKey+" "+error);
                string joined=string.Concat(units.Select(x => x.Text));
                string[] expected = p.UtteranceKey.EndsWith(":44") ? new[]{"人口预测模型","ARMA","命题人"} :
                    p.UtteranceKey.EndsWith(":66") ? new[]{"10个小时","7天"} : new[]{"刻出更精密的微观结构","他们是怎么做到的","一键三连"};
                if (!expected.All(joined.Contains)) throw new Exception("Recovered source lost content: "+p.UtteranceKey);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output,"missing-sentence-probe.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
    }
}
