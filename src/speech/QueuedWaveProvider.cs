using LiveCaptionsTranslator.utils;
using NAudio.Wave;

namespace LiveCaptionsTranslator.speech;

// Preserve capture packets before resampling, including bursts larger than a device buffer.
public sealed class QueuedWaveProvider : IWaveProvider, IDisposable
{
    private readonly SpillQueue<byte[]> packets = new(100);
    private byte[]? current;
    private int offset;
    private long bufferedBytes;
    public WaveFormat WaveFormat { get; }
    public long BufferedBytes => Interlocked.Read(ref bufferedBytes);
    public QueuedWaveProvider(WaveFormat format) => WaveFormat = format;

    public void AddSamples(byte[] buffer, int start, int count)
    {
        Interlocked.Add(ref bufferedBytes, count);
        try { packets.Enqueue(buffer.AsSpan(start, count).ToArray()); }
        catch { Interlocked.Add(ref bufferedBytes, -count); throw; }
    }
    public int Read(byte[] buffer, int start, int count)
    {
        int total = 0;
        while (total < count)
        {
            if (current == null)
            {
                if (!packets.TryDequeue(out current)) break;
                offset = 0;
            }
            int take = Math.Min(count - total, current.Length - offset);
            Array.Copy(current, offset, buffer, start + total, take);
            total += take;
            offset += take;
            if (offset == current.Length) current = null;
        }
        Interlocked.Add(ref bufferedBytes, -total);
        return total;
    }
    public void Dispose()
    {
        if (current != null)
        {
            using var head = new SpillQueue<byte[]>(1);
            head.Enqueue(current.AsSpan(offset).ToArray());
        }
        packets.Dispose();
    }
}
