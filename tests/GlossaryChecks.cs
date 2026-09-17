using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

internal static class GlossaryChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Glossary parser validates generated JSON and editable line format", Schema);
        yield return ("Generated terms merge without changing user translations or comments", Merge);
        yield return ("Glossary save is atomic, backs up existing text and detects external edits", () => Save(output));
        yield return ("Glossary cache follows file identity and refreshes immediately after save", () => Cache(output));
        yield return ("Glossary generation uses one configured request with explicit language direction", Request);
        yield return ("Failed, truncated and cancelled generation cannot return usable draft terms", Failures);
        yield return ("WPF glossary workflow generates, edits and applies only on Save", () => Editor(output));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private const string Terms = "{\"terms\":[{\"source\":\"掩膜板\",\"target\":\"photomask\"},{\"source\":\"晶圆\",\"target\":\"wafer\"}]}";
    private static string Completion(string content, string reason = "stop") => JsonSerializer.Serialize(new { choices = new[] {
        new { message = new { content }, finish_reason = reason } } });
    private static Task Schema()
    {
        Check(GlossaryDocument.ParseGenerated("```json\n" + Terms + "\n```").Count == 2, "valid JSON fence not handled");
        foreach (string invalid in new[] { "{}", "not json", "{\"terms\":[]}", "{\"terms\":[{\"source\":\"x\"}]}",
            "{\"terms\":[{\"source\":\"x\\ny\",\"target\":\"z\"}]}",
            "{\"terms\":[{\"source\":\"x\",\"target\":\"y\"},{\"source\":\"x\",\"target\":\"z\"}]}" })
        {
            bool failed = false; try { GlossaryDocument.ParseGenerated(invalid); } catch (InvalidDataException) { failed = true; }
            Check(failed, "invalid AI terms accepted: " + invalid);
        }
        Check(GlossaryDocument.Parse("# note\nGPU = GPU\n\nGPU = GPU").Count == 1, "comments or exact duplicates misparsed");
        return Task.CompletedTask;
    }
    private static Task Merge()
    {
        const string existing = "# 用户修改\n掩膜板 = lithography mask\nGPU = GPU\n";
        var result = GlossaryDocument.Merge(existing, GlossaryDocument.ParseGenerated(Terms), true);
        Check(result.Text.StartsWith(existing.TrimEnd()) && result.Text.Contains("晶圆 = wafer") && result.Added == 1 && result.Kept == 1,
            "generation overwrote user edits or comments");
        result = GlossaryDocument.Merge(existing, GlossaryDocument.ParseGenerated(Terms), false);
        Check(result.Text.Contains("掩膜板 = photomask") && !result.Text.Contains("GPU"), "new-session replacement retained old terms");
        return Task.CompletedTask;
    }
    private static Task Save(string output)
    {
        string path = Path.Combine(output, "saved-glossary.txt");
        const string before = "晶圆 = wafer\n";
        File.WriteAllText(path, before);
        var editor = new GlossaryFile(path); Check(editor.Load() == before, "loading changed file");
        editor.Save("晶圆 = silicon wafer\n");
        Check(File.ReadAllText(path + ".bak") == before, "backup did not preserve original");
        File.WriteAllText(path, "外部编辑 = external edit\n");
        bool conflict = false; try { editor.Save(before); } catch (IOException) { conflict = true; }
        Check(conflict && File.ReadAllText(path).Contains("外部编辑"), "external edits overwritten");
        string fresh = Path.Combine(output, "new-glossary", "glossary.txt");
        var empty = new GlossaryFile(fresh); Check(empty.Load() == "" && !File.Exists(fresh), "opening created an active glossary");
        empty.Save(before); Check(File.ReadAllText(fresh) == before, "first save failed");
        return Task.CompletedTask;
    }
    private static Task Cache(string output)
    {
        string a = Path.Combine(output, "cache-a.txt"), b = Path.Combine(output, "cache-b.txt");
        File.WriteAllText(a, "a = first"); File.WriteAllText(b, "b = other");
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
        var cache = new GlossaryCache();
        Check(cache.Read(a) == "a = first" && cache.Read(b) == "b = other", "same timestamp reused another glossary");
        var file = new GlossaryFile(a); file.Load(); file.Save("a = revised"); cache.Invalidate();
        Check(cache.Read(a) == "a = revised", "saved terms are not used immediately");
        return Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
    }
    private static async Task Request()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Check(request.RequestUri!.Host == "example.invalid" && request.Headers.Authorization?.Parameter == "test-only-key", "configured endpoint/key not used");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(json.RootElement.GetProperty("model").GetString() == "configured-model", "configured model not used");
            var messages = json.RootElement.GetProperty("messages");
            using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Check(input.RootElement.GetProperty("source_language").GetString() == "en" && input.RootElement.GetProperty("target_language").GetString() == "zh-CN", "direction ignored");
            Check(messages.GetArrayLength() == 2 && input.RootElement.EnumerateObject().Count() == 3, "unrelated session content sent");
            return new(HttpStatusCode.OK) { Content = new StringContent(Completion(Terms)) };
        });
        using var client = new HttpClient(handler);
        var terms = await new GlossaryGenerator(client).GenerateAsync("https://example.invalid/chat/completions", "test-only-key", "configured-model", "光刻领域", "en", "zh-CN", default);
        Check(terms.Count == 2 && handler.Calls == 1 && client.DefaultRequestHeaders.Authorization == null, "generation retried or mutated shared authorization");
    }
    private static async Task Failures()
    {
        foreach (var response in new[] { new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Completion(Terms, "length")) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Completion("broken json")) } })
        {
            using var handler = new Handler((_, _) => Task.FromResult(response));
            using var client = new HttpClient(handler);
            bool failed = false;
            try { await new GlossaryGenerator(client).GenerateAsync("https://example.invalid", "", "test", "芯片设计", "zh", "en", default); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { failed = true; }
            Check(failed && handler.Calls == 1, "failed generation returned terms or silently retried");
        }
        using var cancelHandler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using var cancelClient = new HttpClient(cancelHandler);
        using var cancellation = new CancellationTokenSource(50);
        bool cancelled = false;
        try { await new GlossaryGenerator(cancelClient).GenerateAsync("https://example.invalid", "", "test", "芯片设计", "zh", "en", cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancel button cannot stop network generation");
    }
    private static Task Editor(string output)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            GlossaryWindow? window = null;
            try
            {
                string path = Path.Combine(output, "ui-glossary.txt");
                const string before = "# 原有术语\n掩膜板 = lithography mask\n";
                File.WriteAllText(path, before);
                int applied = 0;
                var pending = new TaskCompletionSource<IReadOnlyList<GlossaryTerm>>();
                window = new GlossaryWindow(path, "DeepSeek · zh-CN → en-US（离线界面测试）", (_, _) => pending.Task, () => applied++)
                { Background = Brushes.White, Foreground = Brushes.Black, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                T Find<T>(string name) => (T)window.FindName(name);
                void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.Show();
                Find<TextBox>("TopicBox").Text = "半导体光刻，涉及晶圆、掩膜板和衍射。";
                Click("GenerateButton");
                Check(!Find<Button>("GenerateButton").IsEnabled && !Find<Button>("SaveButton").IsEnabled, "busy window accepts duplicate generation or save");
                Check(File.ReadAllText(path) == before, "generation changed active file");
                pending.SetResult(GlossaryDocument.ParseGenerated(Terms)); Pump();
                var editor = Find<TextBox>("EditorBox");
                Check(editor.Text.Contains("晶圆 = wafer") && editor.Text.Contains("掩膜板 = lithography mask") && applied == 0,
                    "draft not merged or prematurely applied");
                editor.Text = editor.Text.Replace("晶圆 = wafer", "晶圆 = silicon wafer");
                Click("SaveButton");
                Check(applied == 1 && File.ReadAllText(path) == editor.Text && File.ReadAllText(path + ".bak") == before,
                    "manual edit did not reach saved active glossary");
                pending = new TaskCompletionSource<IReadOnlyList<GlossaryTerm>>();
                Click("GenerateButton"); Click("CancelButton");
                pending.SetResult(new[] { new GlossaryTerm("不应出现", "cancelled") }); Pump();
                Check(!editor.Text.Contains("不应出现") && Find<Button>("GenerateButton").IsEnabled, "late cancelled response altered draft or left busy state");
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, "glossary-editor.png"))) encoder.Save(stream);
                window.Close(); window = null;
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (window != null) { ((TextBox)window.FindName("EditorBox")).Text = File.ReadAllText(Path.Combine(output, "ui-glossary.txt")); window.Close(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
