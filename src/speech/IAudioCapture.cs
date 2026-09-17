namespace LiveCaptionsTranslator.speech
{
    // Common contract for audio sources feeding the ASR (16kHz / 16-bit / mono PCM).
    public interface IAudioCapture : IDisposable
    {
        event Action<byte[]>? DataAvailable;
        void Start();
        void Stop();
    }
}
