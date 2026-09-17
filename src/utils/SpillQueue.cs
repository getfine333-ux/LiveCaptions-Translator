using System.IO;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils;

// Bounded RAM, FIFO overflow on local disk. Never evict older work.
public sealed class SpillQueue<T> : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<T> memory = new();
    private readonly int capacity;
    private readonly string directory;
    private FileStream? spool;
    private string? spoolPath;
    private long readPosition;
    private int diskCount;
    private bool complete;
    private bool disposed;

    public SpillQueue(int capacity, string? directory = null)
    {
        this.capacity = Math.Max(1, capacity);
        this.directory = directory ?? Path.Combine(Directory.GetCurrentDirectory(), "transcripts", ".pending");
    }
    public int Count { get { lock (gate) return memory.Count + diskCount; } }
    public bool IsCompleted { get { lock (gate) return complete && memory.Count + diskCount == 0; } }
    public long SpilledItems { get; private set; }

    public void Enqueue(T value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (complete) throw new InvalidOperationException("Queue is complete.");
            if (diskCount == 0 && memory.Count < capacity) { memory.Enqueue(value); return; }
            if (spool == null)
            {
                Directory.CreateDirectory(directory);
                spoolPath = Path.Combine(directory, $"{Guid.NewGuid():N}.queue");
                spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                DiagLog.Write($"[Queue] spilling {typeof(T).Name} to disk: {spoolPath}");
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
            if (spool.Length + payload.Length + 4 > 256L * 1024 * 1024)
                throw new IOException("Pending audio/text exceeded the 256 MiB queue budget.");
            long previousEnd = spool.Length;
            try
            {
                spool.Position = previousEnd;
                spool.Write(BitConverter.GetBytes(payload.Length));
                spool.Write(payload);
                spool.Flush();
            }
            catch { spool.SetLength(previousEnd); throw; }
            diskCount++;
            SpilledItems++;
        }
    }
    public bool TryDequeue(out T value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memory.TryDequeue(out value!)) return true;
            if (diskCount == 0) { value = default!; return false; }
            value = ReadDisk();
            diskCount--;
            if (diskCount == 0) CloseSpool();
            return true;
        }
    }
    private T ReadDisk()
    {
        spool!.Position = readPosition;
        Span<byte> length = stackalloc byte[4];
        spool.ReadExactly(length);
        int size = BitConverter.ToInt32(length);
        if (size < 0 || size > spool.Length - spool.Position) throw new IOException("Invalid pending queue record.");
        byte[] bytes = new byte[size];
        spool.ReadExactly(bytes);
        T value = JsonSerializer.Deserialize<T>(bytes)!;
        readPosition = spool.Position;
        return value;
    }
    public void Complete() { lock (gate) complete = true; }
    private void CloseSpool()
    {
        spool?.Dispose();
        spool = null;
        if (spoolPath != null) File.Delete(spoolPath);
        spoolPath = null;
        readPosition = 0;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (memory.Count + diskCount > 0)
            {
                Directory.CreateDirectory(directory);
                string recovery = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.pending.jsonl");
                using (var writer = new StreamWriter(recovery))
                {
                    foreach (var item in memory) writer.WriteLine(JsonSerializer.Serialize(item));
                    while (diskCount > 0) { writer.WriteLine(JsonSerializer.Serialize(ReadDisk())); diskCount--; }
                }
                DiagLog.Write($"[Queue] unfinished {typeof(T).Name} saved: {recovery}");
            }
            memory.Clear();
            CloseSpool();
            disposed = true;
        }
    }
}
