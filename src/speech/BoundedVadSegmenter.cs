using SherpaOnnx;

namespace LiveCaptionsTranslator.speech;

public sealed record AudioSegment(long StartSample, float[] Samples, DateTimeOffset QueuedAt)
{
    public long EndSample => StartSample + Samples.Length;
    public bool ContinuesPrevious { get; init; }
    public bool ForcedEnd { get; init; }
    public long? ObservedThroughSample { get; init; }
}

// Audio and confirmed silence share one FIFO. A worker's empty queue or native
// Flush state is not evidence that the speaker stopped.
public sealed record AudioInputEvent(AudioSegment? Audio, long SilenceStart = 0, long SilenceEnd = 0);

public sealed record RecognizedSpeech(string Text, long StartSample, long EndSample,
    double QueueMs, double InferenceMs)
{
    public string AudioStreamId { get; init; } = "";
    public bool ContinuesPrevious { get; init; }
    public bool ForcedEnd { get; init; }
    public string UtteranceKey { get; init; } = "";
    public int SourceRevision { get; init; }
    public bool IsFinal { get; init; } = true;
    public bool IsIncomplete { get; init; }
    public string EndReason { get; init; } = "";
    public DateTimeOffset? SourceEndedAt { get; init; }
}

// All deadlines use audio samples, so replay, buffered audio and live capture behave alike.
public sealed class BoundedVadSegmenter : IDisposable
{
    private const int FrameSize = 512;
    private readonly VoiceActivityDetector vad;
    private readonly int maximumSamples;
    private readonly int? observationSamples;
    private readonly float[] frame = new float[FrameSize];
    private readonly List<float> context = new();
    private int filled;
    private long received, fed, speechStart = -1;
    private long previousEnd = -1;
    private bool previousForced;
    private bool finished;
    private bool speechActive, silenceReported;
    private long quietStart = -1;
    private long nativeOrigin;
    public long ForcedSplits { get; private set; }
    public long ReceivedSamples => received;
    public bool SpeechActive => speechActive;
    public event Action<AudioSegment>? SegmentReady;
    public event Action<AudioInputEvent>? InputReady;

    public BoundedVadSegmenter(string modelPath, double silenceSeconds, double maximumSeconds, double? observationSeconds = null)
    {
        if (!double.IsFinite(maximumSeconds) || maximumSeconds < 0.5 || maximumSeconds > 30)
            throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        maximumSamples = (int)(maximumSeconds * 16000);
        if (observationSeconds.HasValue && (!double.IsFinite(observationSeconds.Value) || observationSeconds < .5 || observationSeconds > maximumSeconds))
            throw new ArgumentOutOfRangeException(nameof(observationSeconds));
        observationSamples = observationSeconds.HasValue ? (int)(observationSeconds.Value * 16000) : null;
        var config = new VadModelConfig();
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.SileroVad.Model = modelPath;
        config.SileroVad.WindowSize = FrameSize;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = (float)silenceSeconds;
        config.SileroVad.MinSpeechDuration = 0.2f;
        // We own resource windows; the native length threshold must not shorten
        // the natural-pause threshold and manufacture sentence endings.
        config.SileroVad.MaxSpeechDuration = 60;
        vad = new VoiceActivityDetector(config, (float)Math.Max(60, maximumSeconds * 2));
    }

    public void Accept(float[] samples)
    {
        if (finished) throw new InvalidOperationException("Audio input is complete.");
        received += samples.Length;
        int offset = 0;
        while (offset < samples.Length)
        {
            int count = Math.Min(FrameSize - filled, samples.Length - offset);
            Array.Copy(samples, offset, frame, filled, count);
            filled += count;
            offset += count;
            if (filled == FrameSize) { FeedFrame(); filled = 0; }
        }
    }

    private void FeedFrame()
    {
        vad.AcceptWaveform(frame);
        fed += FrameSize;
        context.AddRange(frame);
        int contextLimit = maximumSamples + 32000;
        if (context.Count > contextLimit) context.RemoveRange(0, context.Count - contextLimit);
        speechActive = vad.IsSpeechDetected();
        Drain();
        if (!speechActive)
        {
            speechStart = -1;
            if (quietStart < 0) quietStart = Math.Min(fed, received);
            long end = Math.Min(fed, received);
            if (!silenceReported && end - quietStart >= 32000)
            {
                InputReady?.Invoke(new(null, quietStart, end));
                silenceReported = true;
                previousForced = false;
            }
            return;
        }
        quietStart = -1;
        silenceReported = false;
        // Conservative allowance for VAD pre-roll and the v5 lookahead frame.
        if (speechStart < 0) speechStart = Math.Max(0, fed - 4 * FrameSize - 3200);
        // Observation cadence and maximum storage window are independent. These
        // snapshots never declare a sentence final; the semantic coordinator
        // keeps the audio and waits for a confirmed boundary.
        int budget = observationSamples ?? (previousForced ? Math.Min(maximumSamples, 32000) : maximumSamples);
        if (fed - speechStart >= budget)
        {
            vad.Flush();
            ForcedSplits++;
            Drain(forced: true);
            // Flush clears the native segment start but not its silence hangover.
            // Reset with real left context so a pause immediately after a forced
            // edge cannot request a negative-length segment in the native ring.
            // Replayed samples are clipped against previousEnd in Drain.
            vad.Reset();
            int replayCount = Math.Min(8192, context.Count / FrameSize * FrameSize);
            nativeOrigin = fed - replayCount;
            for (int i = context.Count - replayCount; i < context.Count; i += FrameSize)
                vad.AcceptWaveform(context.GetRange(i, FrameSize).ToArray());
            while (!vad.IsEmpty()) vad.Pop();
            speechStart = -1;
        }
    }

    private void Drain(bool forced = false)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            float[] samples = segment.Samples;
            long absoluteStart = nativeOrigin + segment.Start;
            // A reset can suppress a short quiet span before speech resumes.
            // Retain its actual samples while a forced utterance is still open;
            // do not replace that gap with invented silence in the coordinator.
            long gap = absoluteStart - previousEnd;
            long contextStart = fed - context.Count;
            if (previousForced && gap is > 0 and <= 32000 && previousEnd >= contextStart)
            {
                samples = context.GetRange((int)(previousEnd - contextStart), (int)gap).Concat(samples).ToArray();
                absoluteStart = previousEnd;
            }
            int length = (int)Math.Min(samples.Length, Math.Max(0, received - absoluteStart));
            int alreadyEmitted = (int)Math.Clamp(previousEnd - absoluteStart, 0, Math.Max(0, length));
            // Also cap a naturally completed segment; retain every sample across the boundary.
            for (int offset = alreadyEmitted; offset < length; offset += maximumSamples)
            {
                int count = Math.Min(maximumSamples, length - offset);
                long start = absoluteStart + offset;
                bool continues = previousForced && start - previousEnd is >= 0 and <= 1024;
                bool forcedEnd = forced || offset + count < length;
                var ready = new AudioSegment(start,
                    samples.AsSpan(offset, count).ToArray(), DateTimeOffset.UtcNow)
                { ContinuesPrevious = continues, ForcedEnd = forcedEnd, ObservedThroughSample = Math.Min(fed,received) };
                SegmentReady?.Invoke(ready);
                InputReady?.Invoke(new(ready));
                previousEnd = start + count;
                previousForced = forcedEnd;
            }
            vad.Pop();
            speechStart = -1;
        }
    }

    public void Finish()
    {
        if (finished) return;
        if (filled > 0) { Array.Clear(frame, filled, FrameSize - filled); FeedFrame(); filled = 0; }
        // Drain v5 lookahead; synthetic padding is clipped against received above.
        Array.Clear(frame);
        FeedFrame();
        vad.Flush();
        Drain();
        finished = true;
    }
    public void Dispose() => vad.Dispose();
}
