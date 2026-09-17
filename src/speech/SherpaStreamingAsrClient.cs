using System.IO;

using SherpaOnnx;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.speech
{
    // Offline / on-device streaming ASR using sherpa-onnx (Zipformer transducer).
    // No network, no API cost. Audio must be 16kHz / 16-bit / mono PCM.
    public class SherpaStreamingAsrClient : IAsrClient
    {
        private readonly string _modelDir;
        private readonly int _numThreads;

        private OnlineRecognizer? _recognizer;
        private OnlineStream? _stream;
        private string _lastText = string.Empty;
        private bool _disposed;
        private int _feedCount;
        private DateTime _lastFinalTime = DateTime.Now;
        private readonly double _flushSeconds;

        public event Action<string, bool>? OnResult;
        public event Action<string>? OnError;
        public event Action? OnClosed;

        public bool IsOpen => _recognizer != null && _stream != null;

        public SherpaStreamingAsrClient(string modelDir, int numThreads = 4, double flushSeconds = 4.0)
        {
            _modelDir = modelDir;
            _numThreads = numThreads <= 0 ? 4 : numThreads;
            _flushSeconds = flushSeconds <= 0 ? 4.0 : flushSeconds;
        }

        // Locate a usable model directory (either the given path or a known default).
        public static string? FindModelDir(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(Directory.GetCurrentDirectory(), configured);
                if (Directory.Exists(path) && Directory.GetFiles(path, "*tokens*.txt").Length > 0)
                    return path;
            }

            string baseDir = Path.Combine(Directory.GetCurrentDirectory(), "models");
            if (!Directory.Exists(baseDir))
                return null;

            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                if (Directory.GetFiles(dir, "*tokens*.txt").Length > 0)
                    return dir;
            }
            return null;
        }

        private static string PreferInt8(string dir, string name)
        {
            string int8 = Path.Combine(dir, $"{name}.int8.onnx");
            if (File.Exists(int8))
                return int8;
            return Path.Combine(dir, $"{name}.onnx");
        }

        public Task ConnectAsync(CancellationToken token = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SherpaStreamingAsrClient));

            try
            {
                var config = new OnlineRecognizerConfig();
                config.FeatConfig.SampleRate = 16000;
                config.FeatConfig.FeatureDim = 80;

                bool isParaformer = Directory.GetFiles(_modelDir, "encoder*.onnx").Length > 0 &&
                                    Directory.GetFiles(_modelDir, "decoder*.onnx").Length > 0 &&
                                    Directory.GetFiles(_modelDir, "joiner*.onnx").Length == 0;

                if (isParaformer)
                {
                    config.ModelConfig.Paraformer.Encoder = PreferInt8(_modelDir, "encoder");
                    config.ModelConfig.Paraformer.Decoder = PreferInt8(_modelDir, "decoder");
                }
                else
                {
                    config.ModelConfig.Transducer.Encoder = PreferInt8(_modelDir, "encoder-epoch-99-avg-1");
                    config.ModelConfig.Transducer.Decoder = PreferInt8(_modelDir, "decoder-epoch-99-avg-1");
                    config.ModelConfig.Transducer.Joiner = PreferInt8(_modelDir, "joiner-epoch-99-avg-1");
                }

                config.ModelConfig.Tokens = Path.Combine(_modelDir, "tokens.txt");
                config.ModelConfig.NumThreads = _numThreads;
                config.ModelConfig.Provider = "cpu";

                config.DecodingMethod = "greedy_search";
                config.EnableEndpoint = 1;
                config.Rule1MinTrailingSilence = 1.5f;
                config.Rule2MinTrailingSilence = 0.8f;
                config.Rule3MinUtteranceLength = 15.0f;

                _recognizer = new OnlineRecognizer(config);
                _stream = _recognizer.CreateStream();
                _lastText = string.Empty;
                _lastFinalTime = DateTime.Now;
                string kind = isParaformer ? "paraformer" : "transducer";
                DiagLog.Write($"[Sherpa] model loaded ({kind}) from {_modelDir}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"local ASR init failed: {ex.Message}");
                throw;
            }

            return Task.CompletedTask;
        }

        public Task SendAudioAsync(byte[] data, CancellationToken token = default)
        {
            if (_recognizer == null || _stream == null || data.Length < 2)
                return Task.CompletedTask;

            try
            {
                int count = data.Length / 2;
                float[] samples = new float[count];
                for (int i = 0; i < count; i++)
                {
                    short s = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                    samples[i] = s / 32768f;
                }

                _stream.AcceptWaveform(16000, samples);
                while (_recognizer.IsReady(_stream))
                    _recognizer.Decode(_stream);

                string text = _recognizer.GetResult(_stream).Text ?? string.Empty;

                bool isEndpoint = _recognizer.IsEndpoint(_stream);
                bool timeFlush = !string.IsNullOrEmpty(text) &&
                                 (DateTime.Now - _lastFinalTime).TotalSeconds >= _flushSeconds;

                _feedCount++;
                if (_feedCount % 100 == 0)
                    DiagLog.Write($"[Sherpa] feeds={_feedCount} endpoint={isEndpoint} flush={timeFlush} text='{text}'");

                if ((isEndpoint || timeFlush) && !string.IsNullOrEmpty(text))
                {
                    // Final segment -> triggers translation.
                    OnResult?.Invoke(text, true);
                    _recognizer.Reset(_stream);
                    _lastText = string.Empty;
                    _lastFinalTime = DateTime.Now;
                }
                else if (!string.IsNullOrEmpty(text) && text != _lastText)
                {
                    _lastText = text;
                    // Intermediate update for on-screen display only.
                    OnResult?.Invoke(text, false);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"local ASR error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task SendEndAsync(CancellationToken token = default)
        {
            if (_recognizer == null || _stream == null)
                return Task.CompletedTask;

            try
            {
                _stream.InputFinished();
                while (_recognizer.IsReady(_stream))
                    _recognizer.Decode(_stream);

                string text = _recognizer.GetResult(_stream).Text ?? string.Empty;
                if (!string.IsNullOrEmpty(text))
                    OnResult?.Invoke(text, true);

                _recognizer.Reset(_stream);
                _lastText = string.Empty;
            }
            catch { }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _stream?.Dispose(); } catch { }
            try { _recognizer?.Dispose(); } catch { }
            _stream = null;
            _recognizer = null;
        }
    }
}
