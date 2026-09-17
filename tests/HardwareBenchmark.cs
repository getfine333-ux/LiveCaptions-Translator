using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;

internal static class HardwareBenchmark
{
    public static void Run(string root, string output)
    {
        string directory = Path.Combine(root, "models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        using var wave = new WaveFileReader(Path.Combine(directory, "test_wavs/zh.wav"));
        var bytes = new byte[wave.Length]; wave.ReadExactly(bytes);
        var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
        double duration = samples.Length / 16000d;
        var rows = new List<object>();
        foreach (int threads in new[] { 1, 2, 4, 8 })
        {
            var config = new OfflineRecognizerConfig(); config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Provider = "cpu"; config.ModelConfig.NumThreads = threads;
            config.ModelConfig.SenseVoice.Model = Path.Combine(directory, "model.onnx");
            config.ModelConfig.SenseVoice.Language = "zh"; config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
            var watch = Stopwatch.StartNew();
            using var recognizer = new OfflineRecognizer(config);
            double load = watch.Elapsed.TotalMilliseconds;
            string Decode()
            {
                using var stream = recognizer.CreateStream(); stream.AcceptWaveform(16000, samples); recognizer.Decode(stream);
                return stream.Result.Text;
            }
            Decode();
            var timings = new List<double>(); string text = "";
            for (int i = 0; i < 3; i++) { watch.Restart(); text = Decode(); timings.Add(watch.Elapsed.TotalMilliseconds); }
            double average = timings.Average();
            var row = new { threads, audio_seconds = duration, load_ms = load, decode_ms = average, real_time_factor = average / (duration * 1000),
                working_set_mib = Process.GetCurrentProcess().WorkingSet64 / 1048576d, text };
            rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
        }
        File.WriteAllText(Path.Combine(output, "hardware-benchmark.json"), JsonSerializer.Serialize(new
        { note = "One local Mandarin fixture, full-precision SenseVoice CPU decoding; not live streaming, a minimum-PC guarantee, or translation latency.", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
