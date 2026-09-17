namespace LiveCaptionsTranslator.speech
{
    // Common contract for streaming ASR clients (iFlytek RTASR standard / LLM).
    public interface IAsrClient : IDisposable
    {
        bool IsOpen { get; }

        // (text, isFinal)
        event Action<string, bool>? OnResult;
        event Action<string>? OnError;
        event Action? OnClosed;

        Task ConnectAsync(CancellationToken token = default);
        Task SendAudioAsync(byte[] data, CancellationToken token = default);
        Task SendEndAsync(CancellationToken token = default);
    }
}
