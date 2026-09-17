using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.speech;

internal static class ModelCatalogChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Isolated profile lists the configured model and installed sibling models", () => Installed(root, output));
        yield return ("Model choices retain full paths across duplicates, relative paths and missing selections", () => Paths(output));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Installed(string root, string output)
    {
        string fixture = Path.Combine(output, "catalog-fixture");
        string profile = Path.Combine(fixture, "profile");
        string configured = Path.Combine(fixture, "models", "sense-voice-test");
        foreach (string name in new[] { "sense-voice-test", "paraformer-test", "zipformer-test", "whisper-turbo-test" })
        {
            string directory = Path.Combine(fixture, "models", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "tokens.txt"), "synthetic catalog fixture");
        }
        var models = LocalModelCatalog.Discover(configured, profile, Path.Combine(fixture, "app"));
        Check(models.Count == 4 && models.All(m => m.Available), "installed models disappeared under an isolated working directory");
        var current = models.Single(m => m.DirectoryPath == configured);
        Check(current.DisplayName == "SenseVoice", "the configured model is not visibly labeled");
        Check(models.All(m => SherpaStreamingAsrClient.FindModelDir(m.DirectoryPath) == m.DirectoryPath), "a selected path cannot be resolved by the recognizer");
        await File.WriteAllTextAsync(Path.Combine(output, "model-catalog.json"), JsonSerializer.Serialize(new { selected = current, models }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static Task Paths(string output)
    {
        string work = Path.Combine(output, "catalog-profile"), app = Path.Combine(output, "catalog-app");
        string first = Path.Combine(work, "models", "same-name"), second = Path.Combine(app, "models", "same-name");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "tokens.txt"), "test"); File.WriteAllText(Path.Combine(second, "tokens.txt"), "test");
        var list = LocalModelCatalog.Discover("models/same-name/", work, app);
        Check(list.Count == 2 && list.Select(m => m.DirectoryPath).Distinct().Count() == 2, "same-named models were merged or the configured model duplicated");
        Check(LocalModelCatalog.Resolve("models/same-name/", work) == first, "relative configured path was not normalized");
        string missing = Path.Combine(work, "models", "removed-model");
        list = LocalModelCatalog.Discover(missing, work, app);
        Check(list.Single(m => m.DirectoryPath == missing).Available == false, "missing configuration silently selected another model");
        return Task.CompletedTask;
    }
}
