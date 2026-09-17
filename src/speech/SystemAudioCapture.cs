using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.speech
{
    // Captures the system's playback audio (WASAPI loopback) and converts it to
    // 16kHz / 16-bit / mono PCM. Useful to feed a video/meeting's audio into the ASR.
    public class SystemAudioCapture : IAudioCapture
    {
        private WasapiLoopbackCapture? _capture;
        private QueuedWaveProvider? _buffer;
        private IWaveProvider? _pcm16;
        private Thread? _readThread;
        private volatile bool _running;
        private bool _disposed;
        private long _rawBytes;
        private long _outBytes;
        private long _lastInputTick = Environment.TickCount64;
        private long _lastSilenceTick;
        private WaveFormat _inFormat = new WaveFormat(48000, 16, 2);

        public event Action<byte[]>? DataAvailable;

        public void Start()
        {
            if (_running || _disposed)
                return;

            _capture = new WasapiLoopbackCapture();
            WaveFormat inFormat = _capture.WaveFormat;
            _inFormat = inFormat;
            DiagLog.Write($"[SysAudio] start, device format: {inFormat.SampleRate}Hz {inFormat.BitsPerSample}bit {inFormat.Channels}ch {inFormat.Encoding}");

            _buffer = new QueuedWaveProvider(inFormat);

            _capture.DataAvailable += (s, e) =>
            {
                try
                {
                    _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _lastInputTick = Environment.TickCount64;
                    _rawBytes += e.BytesRecorded;
                }
                catch (Exception ex) { DiagLog.Write("[SysAudio] CAPTURE OVERFLOW: " + ex.Message); }
            };

            ISampleProvider sp = _buffer.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1)
                sp = sp.ToMono();

            var resampled = new WdlResamplingSampleProvider(sp, 16000);
            _pcm16 = new SampleToWaveProvider16(resampled);

            _capture.StartRecording();
            _running = true;

            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "SystemAudioRead"
            };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[1280]; // 40ms @ 16kHz/16bit
            // Only pull output once ~40ms of raw input has accumulated, otherwise the
            // resampler would emit endless silence and never see the real audio.
            int rawFrame = Math.Max(1024, _inFormat.AverageBytesPerSecond / 25);
            int ticks = 0;

            while (_running)
            {
                try
                {
                    if (_buffer == null || _pcm16 == null)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (_buffer.BufferedBytes < rawFrame && _running)
                    {
                        long now = Environment.TickCount64;
                        if (now - _lastInputTick >= 80 && now - _lastSilenceTick >= 40)
                        {
                            int tail = _pcm16.Read(buf, 0, buf.Length);
                            DataAvailable?.Invoke(tail > 0 ? buf.AsSpan(0, tail).ToArray() : new byte[1280]);
                            _lastSilenceTick = now;
                        }
                        Thread.Sleep(5);
                        continue;
                    }

                    int read = _pcm16.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        var chunk = new byte[read];
                        Buffer.BlockCopy(buf, 0, chunk, 0, read);
                        _outBytes += read;
                        DataAvailable?.Invoke(chunk);
                    }

                    if (++ticks % 500 == 0)
                        DiagLog.Write($"[SysAudio] raw={_rawBytes}B out={_outBytes}B buffered={_buffer.BufferedBytes}B");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[SysAudio] read error: {ex.Message}");
                    Thread.Sleep(20);
                }
            }
        }

        public void Stop()
        {
            if (!_running) return;
            try { _capture?.StopRecording(); } catch { }
            _running = false;
            if (_readThread != Thread.CurrentThread && _readThread != null && !_readThread.Join(2000))
            {
                DiagLog.Write("[SysAudio] read thread did not stop; tail will not be read concurrently");
                return;
            }
            if (_pcm16 != null)
            {
                var tail = new byte[1280];
                int read;
                while ((read = _pcm16.Read(tail, 0, tail.Length)) > 0)
                    DataAvailable?.Invoke(tail.AsSpan(0, read).ToArray());
            }
        }
        public static bool IsAvailable()
        {
            try
            {
                using var capture = new WasapiLoopbackCapture();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            _buffer?.Dispose();
        }
    }
}
