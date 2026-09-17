using LiveCaptionsTranslator.utils;
namespace LiveCaptionsTranslator.speech;

public sealed class AudioSpeechLoop : IDisposable, IAsyncDisposable
{
    private readonly Func<IAsrClient> clientFactory;
    private readonly Func<IAudioCapture> captureFactory;
    private readonly SpillQueue<byte[]> audio = new(250);
    private readonly CancellationTokenSource cancellation = new();
    private IAudioCapture? capture;
    private Task? runner;
    private volatile bool stopping;
    private bool disposed;
    private bool captureStarted;
    private readonly object captureGate = new();
    private Task cleanup = Task.CompletedTask;
    private readonly byte[] frame = new byte[1280];
    private int filled;
    private byte[]? packet;
    private int packetOffset;
    public event Action<string, bool>? OnResult;
    public event Action<RecognizedSpeech>? OnSegment;
    public event Action<string>? OnError;
    public event Action<string>? OnStatus;

    public AudioSpeechLoop(Func<IAsrClient> clientFactory, Func<IAudioCapture> captureFactory)
    { this.clientFactory = clientFactory; this.captureFactory = captureFactory; }

    public void Start(CancellationToken externalToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (runner != null) throw new InvalidOperationException("Audio capture has already started.");
        externalToken.Register(() => cancellation.Cancel());
        capture = captureFactory();
        capture.DataAvailable += OnAudio;
        // Load/connect the recognizer before starting the microphone or loopback device.
        runner = Task.Run(() => Run(cancellation.Token));
    }
    private void OnAudio(byte[] bytes)
    {
        try { audio.Enqueue(bytes.ToArray()); }
        catch (Exception ex)
        {
            stopping = true;
            DiagLog.Write($"[Audio] input could not be buffered: {ex.Message}");
            OnError?.Invoke("Audio buffering failed; capture stopped: " + ex.Message);
        }
    }
    private bool FillFrame()
    {
        while (filled < frame.Length)
        {
            if (packet == null)
            {
                if (!audio.TryDequeue(out packet)) return false;
                packetOffset = 0;
            }
            int take = Math.Min(frame.Length - filled, packet.Length - packetOffset);
            Buffer.BlockCopy(packet, packetOffset, frame, filled, take);
            filled += take; packetOffset += take;
            if (packetOffset == packet.Length) packet = null;
        }
        return true;
    }
    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = clientFactory();
                client.OnError += error => { DiagLog.Write("[ASR] " + error); OnError?.Invoke(error); };
                if (client is VadOfflineAsrClient local)
                    local.SegmentRecognized += segment => OnSegment?.Invoke(segment);
                else client.OnResult += (text, final) => OnResult?.Invoke(text, final);
                OnStatus?.Invoke("connecting");
                await client.ConnectAsync(token).ConfigureAwait(false);
                if (!captureStarted && !stopping)
                {
                    lock (captureGate)
                        if (!stopping) { captureStarted = true; capture!.Start(); }
                }
                OnStatus?.Invoke("listening");
                while (client.IsOpen && !token.IsCancellationRequested)
                {
                    if (FillFrame())
                    {
                        await client.SendAudioAsync(frame, token).ConfigureAwait(false);
                        filled = 0; // retain this frame if sending fails and we reconnect
                    }
                    else if (stopping)
                    {
                        if (filled > 0)
                        {
                            await client.SendAudioAsync(frame.AsSpan(0, filled).ToArray(), token).ConfigureAwait(false);
                            filled = 0;
                        }
                        await client.SendEndAsync(token).ConfigureAwait(false);
                        return;
                    }
                    else await Task.Delay(5, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                DiagLog.Write("[Audio] " + ex.Message);
                OnError?.Invoke("ASR connection failed: " + ex.Message);
            }
            if (stopping) return;
            OnStatus?.Invoke("reconnecting");
            await Task.Delay(2000, token).ConfigureAwait(false);
        }
    }
    public async Task StopAsync(CancellationToken token = default)
    {
        // Stop capture first, accepting its final callback before closing the queue.
        lock (captureGate) { capture?.Stop(); stopping = true; }
        audio.Complete();
        if (runner != null) await runner.WaitAsync(token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (captureGate) { capture?.Stop(); stopping = true; }
        cancellation.Cancel();
        void Cleanup()
        {
            capture?.Dispose();
            // Preserve the head packet/frame as well as the remaining queued audio.
            if (filled > 0 || packet != null)
            {
                using var head = new SpillQueue<byte[]>(1);
                if (filled > 0) head.Enqueue(frame.AsSpan(0, filled).ToArray());
                if (packet != null) head.Enqueue(packet.AsSpan(packetOffset).ToArray());
            }
            audio.Dispose();
            cancellation.Dispose();
        }
        if (runner == null || runner.IsCompleted) Cleanup();
        else cleanup = runner.ContinueWith(_ => Cleanup());
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
}
