using System.IO;
using System.Text.RegularExpressions;

using SherpaOnnx;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.speech
{
    // High-accuracy on-device ASR: Silero VAD segments speech on natural pauses,
    // then an offline model (SenseVoice or Whisper) recognizes each segment.
    //
    //  - VAD runs inline; recognition runs on a worker thread
    //  - bounded memory with disk spill when recognition falls behind
    //  - a sample-count deadline caps continuous-speech segment duration
    public class VadOfflineAsrClient : IAsrClient
    {
        private const int MaxQueuedSegments = 32;

        private readonly string _modelDir;
        private readonly string _vadModelPath;
        private readonly int _numThreads;
        private readonly string _language;
        private readonly double _vadMinSilence;
        private readonly double _vadMaxSpeech;

        private BoundedVadSegmenter? _vad;
        private OfflineRecognizer? _recognizer;
        private UtteranceRecognizer? _utterances;
        private SpillQueue<AudioInputEvent>? _segmentQueue;
        private Task? _worker;
        private volatile bool _disposed;
        private readonly object _vadGate = new();
        private int _feedCount;
        private string _modelKind = "?";
        private readonly string _audioStreamId = Guid.NewGuid().ToString("N");

        public event Action<string, bool>? OnResult;
        public event Action<string>? OnError;
        public event Action? OnClosed;
        public event Action<RecognizedSpeech>? SegmentRecognized;

        public bool IsOpen => _recognizer != null && _vad != null;

        public VadOfflineAsrClient(string modelDir, string vadModelPath, int numThreads = 8,
            string language = "auto", double vadMinSilence = 0.6, double vadMaxSpeech = 8.0)
        {
            _modelDir = modelDir;
            _vadModelPath = vadModelPath;
            _numThreads = numThreads <= 0 ? 8 : numThreads;
            _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
            _vadMinSilence = vadMinSilence <= 0 ? 0.6 : vadMinSilence;
            _vadMaxSpeech = vadMaxSpeech <= 0 ? 8.0 : vadMaxSpeech;
        }

        public static bool IsSupportedModel(string modelDir)
        {
            if (IsWhisperModel(modelDir))
                return true;
            return File.Exists(Path.Combine(modelDir, "model.onnx")) ||
                   File.Exists(Path.Combine(modelDir, "model.int8.onnx"));
        }

        private static string FirstFile(string dir, string pattern, string fallback)
        {
            var files = Directory.GetFiles(dir, pattern);
            if (files.Length == 0)
                return fallback;
            // Prefer int8 (faster) when multiple exist.
            foreach (var f in files)
                if (f.Contains("int8"))
                    return f;
            return files[0];
        }

        private static string MapWhisperLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang) || lang == "auto")
                return "zh";
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
            if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
            return "zh";
        }

        private static string MapSenseVoiceLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang) || lang == "auto")
                return "auto";
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
            if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
            return "auto";
        }

        public Task ConnectAsync(CancellationToken token = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VadOfflineAsrClient));

            try
            {
                _vad = new BoundedVadSegmenter(_vadModelPath, _vadMinSilence, _vadMaxSpeech,
                    observationSeconds: Math.Min(2, _vadMaxSpeech));

                var asrConfig = new OfflineRecognizerConfig();
                asrConfig.FeatConfig.SampleRate = 16000;
                asrConfig.FeatConfig.FeatureDim = 80;
                asrConfig.ModelConfig.NumThreads = _numThreads;
                asrConfig.ModelConfig.Provider = "cpu";

                bool isWhisper = IsWhisperModel(_modelDir);

                if (isWhisper)
                {
                    _modelKind = "whisper";
                    asrConfig.ModelConfig.Whisper.Encoder = FirstFile(_modelDir, "*encoder*.onnx", "");
                    asrConfig.ModelConfig.Whisper.Decoder = FirstFile(_modelDir, "*decoder*.onnx", "");
                    asrConfig.ModelConfig.Whisper.Language = MapWhisperLang(_language);
                    asrConfig.ModelConfig.Whisper.Task = "transcribe";
                    asrConfig.ModelConfig.Tokens = FirstFile(_modelDir, "*tokens*.txt", "");
                }
                else
                {
                    _modelKind = "sensevoice";
                    string model = Path.Combine(_modelDir, "model.onnx");
                    if (!File.Exists(model))
                        model = Path.Combine(_modelDir, "model.int8.onnx");
                    asrConfig.ModelConfig.SenseVoice.Model = model;
                    asrConfig.ModelConfig.SenseVoice.Language = MapSenseVoiceLang(_language);
                    asrConfig.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                    asrConfig.ModelConfig.Tokens = Path.Combine(_modelDir, "tokens.txt");
                }

                _recognizer = new OfflineRecognizer(asrConfig);

                _utterances = new UtteranceRecognizer(samples =>
                {
                    using var stream = _recognizer.CreateStream();
                    stream.AcceptWaveform(16000, samples);
                    _recognizer.Decode(stream);
                    var result = stream.Result;
                    return new AsrSnapshot(Clean(result.Text), result.Tokens, result.Timestamps);
                }, _audioStreamId);
                _utterances.Ready += result =>
                {
                    SegmentRecognized?.Invoke(result);
                    OnResult?.Invoke(result.Text, result.IsFinal);
                    DiagLog.Write($"[Utterance] key={result.UtteranceKey} source_revision={result.SourceRevision} final={result.IsFinal} incomplete={result.IsIncomplete} reason={result.EndReason} start={result.StartSample} end={result.EndSample}");
                };

                _segmentQueue = new SpillQueue<AudioInputEvent>(MaxQueuedSegments);
                _vad.InputReady += input => _segmentQueue.Enqueue(input);
                _worker = Task.Run(WorkerLoop);
                DiagLog.Write($"[ASR] loaded kind={_modelKind} threads={_numThreads} pause={_vadMinSilence:F2}s max={_vadMaxSpeech:F1}s stream={_audioStreamId} dir={Path.GetFileName(_modelDir)}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"ASR init failed: {ex.Message}");
                throw;
            }

            return Task.CompletedTask;
        }

        private static bool IsWhisperModel(string directory) =>
            Directory.GetFiles(directory, "*encoder*.onnx").Any(f => Path.GetFileName(f).StartsWith("turbo-", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("whisper", StringComparison.OrdinalIgnoreCase)) ||
            (Path.GetFileName(directory).Contains("whisper", StringComparison.OrdinalIgnoreCase) &&
             Directory.GetFiles(directory, "*encoder*.onnx").Length > 0 &&
             Directory.GetFiles(directory, "*decoder*.onnx").Length > 0);

        public Task SendAudioAsync(byte[] data, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (_vadGate)
            {
                if (_disposed || _vad == null) return Task.CompletedTask;
                var samples = new float[data.Length / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(data[2 * i] | data[2 * i + 1] << 8) / 32768f;
                _vad.Accept(samples);
                if (++_feedCount % 250 == 0)
                    DiagLog.Write($"[ASR] feeds={_feedCount} queued={_segmentQueue?.Count} forced={_vad.ForcedSplits}");
            }
            return Task.CompletedTask;
        }

        private async Task WorkerLoop()
        {
            var queue = _segmentQueue!;
            while (!_disposed && !queue.IsCompleted)
            {
                if (!queue.TryDequeue(out var input))
                {
                    await Task.Delay(5).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (input.Audio is not { } segment)
                    {
                        _utterances!.ObserveSilence(input.SilenceStart, input.SilenceEnd);
                        continue;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    double waitMs = (DateTimeOffset.UtcNow - segment.QueuedAt).TotalMilliseconds;
                    _utterances!.Accept(segment);
                    DiagLog.Write($"[ASR] start={segment.StartSample} end={segment.EndSample} duration={segment.Samples.Length / 16000.0:F3}s queue={waitMs:F0}ms infer={watch.ElapsedMilliseconds}ms forced={segment.ForcedEnd} continuation={segment.ContinuesPrevious}");
                }
                catch (Exception ex)
                {
                    var pending = _utterances?.PendingRecovery;
                    if (pending != null) SaveRecovery(pending);
                    // A gap can fail finalization before Accept owns the new audio.
                    if (input.Audio is { } incoming &&
                        (_utterances?.PendingAudio?.EndSample ?? -1) < incoming.EndSample)
                        SaveRecovery(new(incoming, "", null, ""));
                    DiagLog.Write($"[ASR] pending recognition saved: {ex.Message}");
                    bool recovered = false;
                    if (pending != null)
                    {
                        try
                        {
                            _utterances!.Recover(pending);
                            if (input.Audio is { } arrived && pending.Audio.EndSample < arrived.EndSample)
                                _utterances.Accept(arrived);
                            if (input.Audio == null) _utterances.ObserveSilence(input.SilenceStart, input.SilenceEnd);
                            recovered = true;
                            DiagLog.Write("[ASR] automatic recovery completed; live recognition continues");
                        }
                        catch (Exception retryError)
                        {
                            if (_utterances!.PendingRecovery is { } retryPending) SaveRecovery(retryPending);
                            DiagLog.Write($"[ASR] bounded recovery failed: {retryError.Message}");
                        }
                    }
                    if (!recovered)
                    {
                        _utterances?.Reset();
                        // Isolate this failed range, then attempt the VERY NEXT
                        // input. Never wait indefinitely for a natural pause.
                        OnError?.Invoke($"本段识别异常，音频已保存；后续语音继续处理：{ex.Message}");
                    }
                }
            }
            if (!_disposed)
            {
                try { _utterances?.Finish(); }
                catch (Exception ex)
                {
                    if (_utterances?.PendingRecovery is { } pending) SaveRecovery(pending);
                    _utterances?.Reset();
                    OnError?.Invoke($"ASR tail requires recovery (audio saved): {ex.Message}");
                }
            }
        }

        private void SaveRecovery(PendingRecognition pending)
        {
            using var recovery = new SpillQueue<PendingRecognition>(1);
            recovery.Enqueue(pending with { AudioStreamId = _audioStreamId });
        }

        private static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return Regex.Replace(text, @"<\|[^|]*\|>", "").Trim();
        }

        public async Task SendEndAsync(CancellationToken token = default)
        {
            lock (_vadGate)
            {
                if (_disposed) return;
                _vad?.Finish();
                _segmentQueue?.Complete();
            }
            if (_worker != null) await _worker.WaitAsync(token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_vadGate)
            {
                if (_disposed) return;
                _disposed = true;
                _segmentQueue?.Complete();
                _vad?.Dispose();
                _vad = null;
            }
            // Wait for native decoding before freeing its handles or saving remaining audio.
            void Cleanup()
            {
                if (_utterances?.PendingRecovery is { } pending) SaveRecovery(pending);
                _recognizer?.Dispose(); _segmentQueue?.Dispose();
            }
            if (_worker == null || _worker.IsCompleted) Cleanup();
            else
            {
                if (_worker.Wait(TimeSpan.FromSeconds(5))) Cleanup();
                else _ = _worker.ContinueWith(_ => Cleanup());
            }
            OnClosed?.Invoke();
        }
    }
}
