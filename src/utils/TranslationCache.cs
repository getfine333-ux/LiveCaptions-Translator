namespace LiveCaptionsTranslator.utils;
public sealed class TranslationCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string translated, DateTime time)> cache = new();
    private readonly int maxSize;
    public int Count { get { lock (gate) return cache.Count; } }
    public TranslationCache(int maxSize = 500) => this.maxSize = Math.Max(1, maxSize);
    public bool TryGet(string source, out string translated)
    {
        lock (gate)
        {
            if (cache.TryGetValue(source, out var entry)) { translated = entry.translated; return true; }
            translated = ""; return false;
        }
    }
    public void Add(string source, string translated)
    {
        lock (gate)
        {
            if (!cache.ContainsKey(source) && cache.Count >= maxSize)
                cache.Remove(cache.MinBy(kvp => kvp.Value.time).Key);
            cache[source] = (translated, DateTime.UtcNow);
        }
    }
    public void Clear() { lock (gate) cache.Clear(); }
    public void Remove(string key) { lock (gate) cache.Remove(key); }
}
