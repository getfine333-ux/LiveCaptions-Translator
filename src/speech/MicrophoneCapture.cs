using NAudio.Wave;

namespace LiveCaptionsTranslator.speech
{
    // Captures microphone audio as 16kHz / 16-bit / mono PCM,
    // which is the format required by iFlytek RTASR.
    public class MicrophoneCapture : IAudioCapture
    {
        private WaveInEvent? _waveIn;
        private readonly int _deviceNumber;
        private bool _running;
        private bool _disposed;

        public event Action<byte[]>? DataAvailable;

        public MicrophoneCapture(int deviceNumber = 0)
        {
            _deviceNumber = deviceNumber;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            if (_running || _disposed)
                return;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = _deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 3
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            _running = true;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
                return;

            var data = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
            DataAvailable?.Invoke(data);
        }

        public void Stop()
        {
            if (!_running)
                return;

            try
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.StopRecording();
                }
            }
            catch
            {
            }
            _running = false;
        }

        public static int DeviceCount
        {
            get
            {
                try { return WaveInEvent.DeviceCount; }
                catch { return 0; }
            }
        }

        public static string GetDeviceName(int index)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(index);
                return caps.ProductName;
            }
            catch
            {
                return $"Device {index}";
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;
        }
    }
}
