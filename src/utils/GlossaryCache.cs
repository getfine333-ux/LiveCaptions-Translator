using System.IO;

namespace LiveCaptionsTranslator.utils;

public sealed class GlossaryCache
{
    private readonly object gate = new();
    private string? cached, cachedPath;
    private DateTime stamp;
    private long length;
    public void Invalidate() { lock (gate) { cached = null; cachedPath = null; } }
    public string Read(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return "";
        lock (gate)
        {
            try
            {
                string path = Path.GetFullPath(configuredPath);
                var info = new FileInfo(path);
                if (!info.Exists) return "";
                if (cached != null && string.Equals(path, cachedPath, StringComparison.OrdinalIgnoreCase) &&
                    info.LastWriteTimeUtc == stamp && info.Length == length) return cached;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                cached = string.Join("\n", reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
                cachedPath = path; stamp = info.LastWriteTimeUtc; length = info.Length;
                return cached;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
        }
    }
}
