using System.IO;

namespace LiveCaptionsTranslator.speech;

public sealed record LocalAsrModel(string DirectoryPath, string Name, bool Available)
{
    public string DisplayName => !Available ? Name + " (unavailable)" :
        Name.Contains("sense-voice", StringComparison.OrdinalIgnoreCase) ? "SenseVoice" :
        Name.Contains("paraformer", StringComparison.OrdinalIgnoreCase) ? "Paraformer" :
        Name.Contains("zipformer", StringComparison.OrdinalIgnoreCase) ? "Zipformer" :
        Name.Contains("whisper-turbo", StringComparison.OrdinalIgnoreCase) ? "Whisper Turbo" : Name;
}

public static class LocalModelCatalog
{
    public static string? Resolve(string? path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, workingDirectory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static IReadOnlyList<LocalAsrModel> Discover(string? configured, string workingDirectory, string applicationDirectory)
    {
        var found = new Dictionary<string, LocalAsrModel>(StringComparer.OrdinalIgnoreCase);
        string? selected = Resolve(configured, workingDirectory);
        bool HasTokens(string path)
        {
            try { return Directory.Exists(path) && Directory.EnumerateFiles(path, "*tokens*.txt").Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        void Add(string path, bool configuredPath = false)
        {
            string? fullPath = Resolve(path, workingDirectory);
            if (fullPath == null) return;
            bool available = HasTokens(fullPath);
            if (available || configuredPath) found[fullPath] = new(fullPath, Path.GetFileName(fullPath), available);
        }
        // Isolated profiles share the configured acoustic model directory. Its
        // siblings contain the other installed recognizers, not profile/models.
        if (selected != null) Add(selected, configuredPath: true);
        var roots = new[] { selected == null ? null : Path.GetDirectoryName(selected),
            Path.Combine(workingDirectory, "models"), Path.Combine(applicationDirectory, "models") };
        foreach (string root in roots.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root)) Add(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Other roots can still be usable. */ }
        }
        return found.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.DirectoryPath).ToArray();
    }
}
