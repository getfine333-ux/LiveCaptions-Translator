using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.speech;
using NAudio.Wave;

// Explicit local commands only. Normal regression tests never capture the user's
// sound device or contact a paid provider.
internal static class PipelineTools
{
    public static async Task<bool> TryRun(string[] args, string root, string output)
    {
        if (args.Contains("--hardware-benchmark"))
        {
            HardwareBenchmark.Run(root, output);
            Console.WriteLine("HARDWARE_BENCHMARK " + output);
            return true;
        }
        if (args.Contains("--baseline-asr"))
        {
            int index = Array.IndexOf(args, "--result-directory");
            string directory = index >= 0 ? Path.GetFullPath(args[index + 1]) : output;
            string artifacts = Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Baseline reports must stay inside the workspace artifacts directory.");
            await ArchitectureChecks.BaselineAudio(root, directory);
            Console.WriteLine("BASELINE_REPORT " + directory);
            return true;
        }
        if (args.Contains("--capture"))
        {
            int index = Array.IndexOf(args, "--capture");
            int seconds = index + 1 < args.Length ? int.Parse(args[index + 1]) : 120;
            if (seconds < 1 || seconds > 1800) throw new ArgumentOutOfRangeException(nameof(seconds));
            string file = Path.Combine(output, "system-audio.wav");
            using var writer = new WaveFileWriter(file, new WaveFormat(16000, 16, 1));
            using var capture = new SystemAudioCapture();
            capture.DataAvailable += bytes => writer.Write(bytes, 0, bytes.Length);
            Console.WriteLine($"Recording playback audio for {seconds} seconds: {file}");
            capture.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
            finally { capture.Stop(); }
            writer.Flush();
            Console.WriteLine("CAPTURE " + file);
            return true;
        }
        bool numberOnly = args.Contains("--provider-number-smoke");
        if (!numberOnly && !args.Contains("--provider-smoke")) return false;

        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "setting.json")))!;
        if (config["ApiName"]?.GetValue<string>() != "OpenAI")
            throw new InvalidOperationException("This explicit smoke check requires the configured OpenAI-compatible provider.");
        config["AudioSource"] = "microphone"; // suppress the Windows Live Captions launch in the static coordinator
        config["SourceLang"] = "zh-CN";
        config["TargetLanguage"] = "en-US";
        config["TranslationMode"] = "zh2en";
        config["CorrectAsrErrors"] = true;
        config["ContextAware"] = false;
        config["GlossaryFile"] = Path.Combine(root, "glossary.txt");
        // This file remains in the ignored local test directory; never print it.
        await File.WriteAllTextAsync("setting.json", config.ToJsonString());
        try
        {
            var examples = new[]
            {
                new TranslationSegment { Text = "谷歌DeepMind团队最近发布了一个名为Gemma Scope的项目，里面包含400多个彼此独立的稀疏自编码器，它们分别在模型的不同位置，以及Gemma的不同版本上训练而成。",
                    Context = "我们讨论稀疏自编码器。", SourceLang = "zh-CN", TargetLang = "en-US", Provider = "OpenAI" },
                new TranslationSegment { Text = "我们把示例文本输入模型，再把第21层的输出送入训练好的稀疏自编码器。这个编码器共有16384个潜在特征。",
                    Context = "谷歌DeepMind团队发布了Gemma Scope项目。", SourceLang = "zh-CN", TargetLang = "en-US", Provider = "OpenAI" }
            };
            var results = new List<object>();
            foreach (var sample in numberOnly ? examples.Skip(1) : examples)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var watch = Stopwatch.StartNew();
                var reply = await TranslateAPI.OpenAIReply(sample, timeout.Token);
                // Preserve each response before any assertion, including failures.
                // These are the explicit test texts, never configuration/credentials.
                await File.WriteAllTextAsync(Path.Combine(output, $"provider-response-{results.Count + 1}.json"),
                    JsonSerializer.Serialize(new { sample.Text, reply.Content, reply.FinishReason,
                        requestMs = watch.Elapsed.TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
                if (reply.Content.StartsWith("[ERROR]", StringComparison.Ordinal))
                {
                    string category = reply.Content.Contains("HTTP Error") ? "HTTP error" :
                        reply.Content.Contains("timeout", StringComparison.OrdinalIgnoreCase) ? "timeout" : "transport error";
                    throw new IOException("Configured provider request failed: " + category + ". No response was accepted.");
                }
                var parsed = TranslationResponse.Parse(reply.Content, sample.Text, sample.Context, true);
                if (sample.Text.Contains("16384") && !ContainsFeatureCount(parsed.Translation))
                    throw new InvalidDataException("The numeric fixture could not confirm 16384; the response is preserved for inspection.");
                results.Add(new { sample.Text, sample.Context, parsed.Source, parsed.Translation, reply.FinishReason,
                    requestMs = watch.Elapsed.TotalMilliseconds });
            }
            await File.WriteAllTextAsync(Path.Combine(output, "provider-smoke.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PROVIDER_SMOKE valid_complete_responses={results.Count}; no audio or end-to-end quality claim; RESULTS {output}");
        }
        finally
        {
            await Translator.StopAsync();
            // Remove only the credential-bearing copy created by this command.
            File.Delete(Path.Combine(output, "setting.json"));
        }
        return true;
    }

    public static bool ContainsFeatureCount(string translation) =>
        System.Text.RegularExpressions.Regex.IsMatch(translation, @"(?<!\d)16(?:,|\u00a0|\u202f| )?384(?!\d)");
}
