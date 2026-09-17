// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Diagnostics;
using LiveCaptionsTranslator.utils;
namespace LiveCaptionsTranslator.models;

// Fixed foreground workers and one retry worker. First results are ordered;
// corrections replace the same ID instead of replaying old text on screen.
public sealed class TranslationTaskQueue : IAsyncDisposable
{
    private readonly Func<TranslationSegment, CancellationToken, Task<TranslationResult>> translate;
    private readonly Func<TranslationResult, Task> publish;
    private readonly SpillQueue<TranslationSegment> pending;
    private readonly SpillQueue<RetryItem> retries;
    private readonly SortedDictionary<long, TranslationResult> completed = new();
    private readonly Dictionary<long, TranslationResult> updates = new();
    private readonly SemaphoreSlim commitGate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task[] workers;
    private readonly Task retryWorker;
    private readonly TimeSpan realtimeTimeout, retryTimeout;
    private readonly int maxRetries;
    private readonly object inputGate = new();
    private long sequence, captionSequence, nextCommit = 1;
    private readonly Dictionary<string, (long Id, int Revision)> utterances = new();
    private int active, outstanding;
    private bool accepting = true;
    private volatile bool primaryComplete;
    public int PendingCount => pending.Count;
    public int ActiveCount => Volatile.Read(ref active);
    public int OutstandingCount => Volatile.Read(ref outstanding);
    public int RetryCount => retries.Count;

    public TranslationTaskQueue(
        Func<TranslationSegment, CancellationToken, Task<TranslationResult>> translate,
        Func<TranslationResult, Task> publish, int concurrency = 3, int maxRetries = 2,
        TimeSpan? realtimeTimeout = null, TimeSpan? retryTimeout = null, string? spoolDirectory = null)
    {
        this.translate = translate; this.publish = publish;
        this.maxRetries = Math.Clamp(maxRetries, 0, 5);
        this.realtimeTimeout = realtimeTimeout ?? TimeSpan.FromSeconds(3);
        this.retryTimeout = retryTimeout ?? TimeSpan.FromSeconds(8);
        pending = new(64, spoolDirectory); retries = new(64, spoolDirectory);
        workers = Enumerable.Range(0, Math.Clamp(concurrency, 1, 8)).Select(_ => Task.Run(WorkerLoop)).ToArray();
        retryWorker = Task.Run(RetryLoop);
    }
    public long Enqueue(TranslationSegment segment)
    {
        lock (inputGate)
        {
            if (!accepting) throw new InvalidOperationException("Translation input is closed.");
            long work = sequence + 1;
            bool existing = segment.UtteranceKey.Length > 0 && utterances.TryGetValue(segment.UtteranceKey, out _);
            var previous = existing ? utterances[segment.UtteranceKey] : default;
            if (existing && segment.SourceRevision <= previous.Revision) return previous.Id;
            long id = existing ? previous.Id : captionSequence + 1;
            Interlocked.Increment(ref outstanding);
            try { pending.Enqueue(segment with { Id = id, WorkSequence = work }); }
            catch { Interlocked.Decrement(ref outstanding); throw; }
            sequence = work;
            if (!existing) captionSequence = id;
            if (segment.UtteranceKey.Length > 0) utterances[segment.UtteranceKey] = (id, segment.SourceRevision);
            return id;
        }
    }
    private bool IsCurrent(TranslationSegment segment)
    {
        lock (inputGate) return segment.UtteranceKey.Length == 0 ||
            (utterances.TryGetValue(segment.UtteranceKey, out var current) && current.Revision == segment.SourceRevision);
    }
    private async Task WorkerLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            if (!pending.TryDequeue(out var segment))
            {
                if (pending.IsCompleted) return;
                await Task.Delay(10, stop.Token).ConfigureAwait(false); continue;
            }
            Interlocked.Increment(ref active);
            try
            {
                double queueMs = (DateTimeOffset.UtcNow - segment.CreatedAt).TotalMilliseconds;
                var result = IsCurrent(segment)
                    ? await Attempt(segment, realtimeTimeout).ConfigureAwait(false)
                    : new TranslationResult(segment, "");
                bool retry = result.IsError && maxRetries > 0;
                result = result with { QueueMs = queueMs, IsPending = retry };
                if (retry) result = result with { Text = "[待补译] " + segment.Text };
                await Commit(result).ConfigureAwait(false);
                if (retry && IsCurrent(segment)) retries.Enqueue(new(segment, 1, DateTimeOffset.UtcNow, queueMs));
            }
            catch (OperationCanceledException) { Preserve(segment); throw; }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private async Task<TranslationResult> Attempt(TranslationSegment segment, TimeSpan timeout)
    {
        if (segment.LogOnly) return new(segment, "N/A");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        cts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            var task = translate(segment, cts.Token);
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            var result = await task.WaitAsync(cts.Token).ConfigureAwait(false);
            return result with { RequestMs = watch.Elapsed.TotalMilliseconds };
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return new(segment, "[ERROR] 翻译超时；原文：" + segment.Text)
            { IsError = true, RequestMs = watch.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagLog.Write($"[Translate] id={segment.Id} failed: {ex.Message}");
            return new(segment, "[ERROR] 翻译失败；原文：" + segment.Text)
            { IsError = true, RequestMs = watch.Elapsed.TotalMilliseconds };
        }
    }
    private async Task Commit(TranslationResult result)
    {
        await commitGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            if (result.Revision > 0)
            {
                if (result.Segment.WorkSequence >= nextCommit) updates[result.Segment.WorkSequence] = result;
                else await Publish(result).ConfigureAwait(false);
                return;
            }
            completed.Add(result.Segment.WorkSequence, result);
            while (completed.Remove(nextCommit, out var next))
            {
                await Publish(next).ConfigureAwait(false);
                if (updates.Remove(nextCommit, out var update)) await Publish(update).ConfigureAwait(false);
                nextCommit++; Interlocked.Decrement(ref outstanding);
            }
        }
        finally { commitGate.Release(); }
    }
    private async Task Publish(TranslationResult result)
    {
        if (!IsCurrent(result.Segment)) return;
        try { await publish(result).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Preserve(result);
            DiagLog.Write($"[Translate] id={result.Segment.Id} commit failed; saved for recovery: {ex.Message}");
        }
        DiagLog.Write($"[Translate] id={result.Segment.Id} revision={result.Revision} queue={result.QueueMs:F0}ms request={result.RequestMs:F0}ms pending={result.IsPending} outstanding={OutstandingCount}");
    }
    private async Task RetryLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            if (!retries.TryDequeue(out var item))
            {
                if (primaryComplete) return;
                await Task.Delay(20, stop.Token).ConfigureAwait(false); continue;
            }
            try
            {
                var wait = item.NotBefore - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                if (!IsCurrent(item.Segment)) continue;
                var result = await Attempt(item.Segment, retryTimeout).ConfigureAwait(false);
                if (result.IsError && item.Attempt < maxRetries)
                {
                    retries.Enqueue(item with
                    {
                        Attempt = item.Attempt + 1,
                        NotBefore = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, item.Attempt))
                    });
                    continue;
                }
                await Commit(result with { Revision = 1, QueueMs = item.QueueMs }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { Preserve(item); throw; }
        }
    }
    private static void Preserve<T>(T item) { using var recovery = new SpillQueue<T>(1); recovery.Enqueue(item); }
    public async Task CompleteAsync(CancellationToken token = default)
    {
        lock (inputGate) { accepting = false; pending.Complete(); }
        await Task.WhenAll(workers).WaitAsync(token).ConfigureAwait(false);
        primaryComplete = true;
        await retryWorker.WaitAsync(token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await Task.WhenAll(workers.Append(retryWorker)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var item in completed.Values) Preserve(item);
            foreach (var item in updates.Values) Preserve(item);
            pending.Dispose(); retries.Dispose(); stop.Dispose(); commitGate.Dispose();
        }
    }
    public sealed record RetryItem(TranslationSegment Segment, int Attempt, DateTimeOffset NotBefore, double QueueMs);
}
