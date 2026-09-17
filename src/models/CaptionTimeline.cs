namespace LiveCaptionsTranslator.models;

// A late correction edits a row, never moves it to the end of the timeline.
public sealed class CaptionTimeline
{
    private readonly object gate = new();
    private readonly SortedDictionary<long, TranslationResult> rows = new();
    private readonly SortedDictionary<long, TranslationResult> readable = new();
    private readonly int capacity;
    public CaptionTimeline(int capacity = 200) => this.capacity = Math.Max(1, capacity);
    public bool Upsert(TranslationResult result)
    {
        lock (gate)
        {
            long id = result.Segment.Id;
            if (rows.TryGetValue(id, out var old) &&
                (result.Segment.SourceRevision < old.Segment.SourceRevision ||
                 (result.Segment.SourceRevision == old.Segment.SourceRevision && result.Revision <= old.Revision))) return false;
            if (!rows.ContainsKey(id) && rows.Count >= capacity && id < rows.First().Key) return false;
            rows[id] = result;
            // Keep the last valid source/translation pair while its replacement is
            // retrying. Never pair a new source with an old translation.
            if ((!result.IsError && !result.IsPending) || !readable.ContainsKey(id)) readable[id] = result;
            while (rows.Count > capacity)
            {
                long expired = rows.First().Key;
                rows.Remove(expired); readable.Remove(expired);
            }
            return true;
        }
    }
    public TranslationResult[] Snapshot() { lock (gate) return rows.Values.ToArray(); }
    public TranslationResult[] ReadingSnapshot() { lock (gate) return readable.Values.ToArray(); }
}
