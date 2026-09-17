using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

internal static class SettingsChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Settings draft cannot autosave or mutate active nested configuration", () => Isolation(output));
        yield return ("Settings commit preserves live window/glossary state and normalizes direction", () => Commit(output));
        yield return ("Invalid settings and failed writes leave active state and file unchanged", () => Failure(output));
        yield return ("WPF settings edits, provider switching and revert apply only on Save", () => Editor(root, output));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Setting Sample() => new()
    {
        ApiName = "OpenAI", AudioSource = "microphone", AudioInput = "system", AsrProvider = "local",
        TranslationMode = "zh2en", SourceLang = "zh-CN", TargetLanguage = "en-US", Prompt = Setting.PROMPT_ZH2EN,
        Configs = new Setting().Configs.Select(pair => pair.Key == "OpenAI"
            ? new KeyValuePair<string, List<TranslateAPIConfig>>(pair.Key, [new OpenAIConfig
                { ApiUrl = "https://example.invalid/chat/completions", ApiKey = "test-only", ModelName = "saved-model", Temperature = 0 }])
            : pair).ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    private static Task Isolation(string output)
    {
        var active = Sample();
        var previous = SettingsPersistence.Active;
        try
        {
            SettingsPersistence.Active = active;
            active.Save();
            DateTime sentinel = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(Setting.FILENAME, sentinel);
            string before = File.ReadAllText(Setting.FILENAME);
            var draft = new SettingsDraft(active);
            draft.Values.AsrThreads = 12;
            draft.Values.MainWindow.LatencyShow = true;
            ((OpenAIConfig)draft.Values["OpenAI"]).ApiKey = "unsaved";
            draft.Values.WindowBounds["MainWindow"] = "draft bounds";
            Check(active.AsrThreads == 4 && !active.MainWindow.LatencyShow && ((OpenAIConfig)active["OpenAI"]).ApiKey == "test-only",
                "draft changed active nested state");
            Check(active.WindowBounds["MainWindow"] != "draft bounds", "dictionary shared with draft");
            Check(File.ReadAllText(Setting.FILENAME) == before && File.GetLastWriteTimeUtc(Setting.FILENAME) == sentinel,
                "cloning or editing auto-saved active settings");
            active.MainWindow.Topmost = false;
            Check(!Setting.Load(Setting.FILENAME).MainWindow.Topmost, "active window autosave was broken");
        }
        finally { SettingsPersistence.Active = previous; }
        return Task.CompletedTask;
    }

    private static Task Commit(string output)
    {
        var active = Sample();
        var state = active.MainWindow;
        var config = (OpenAIConfig)active["OpenAI"];
        var draft = new SettingsDraft(active);
        draft.Values.MainWindow.LatencyShow = true;
        ((OpenAIConfig)draft.Values["OpenAI"]).ModelName = "edited-model";
        Check(!draft.RequiresAudioRestart(active), "translation/display edit unnecessarily restarts capture");
        draft.SetDirection("en2zh");
        Check(draft.RequiresAudioRestart(active), "source language change doesn't restart recognition");
        active.MainWindow.Topmost = false;
        active.GlossaryFile = "newly-saved-glossary.txt";
        active.WindowBounds["MainWindow"] = "new live bounds";
        string path = Path.Combine(output, "committed-settings.json");
        draft.Commit(active, path);
        var saved = Setting.Load(path);
        Check(active.SourceLang == "en" && saved.TargetLanguage == "zh-CN" && active.Prompt == Setting.PROMPT_EN2ZH,
            "direction/source/target/default prompt diverged");
        Check(ReferenceEquals(state, active.MainWindow) && ReferenceEquals(config, active["OpenAI"]), "commit broke existing UI bindings");
        Check(active.MainWindow.LatencyShow && !saved.MainWindow.Topmost && saved.GlossaryFile == "newly-saved-glossary.txt" &&
            saved.WindowBounds["MainWindow"] == "new live bounds", "stale draft overwrote unrelated live state");
        Check(config.ModelName == "edited-model" && ((OpenAIConfig)saved["OpenAI"]).Temperature == 0, "provider edits or zero temperature lost");
        draft = new SettingsDraft(active); draft.Values.Prompt = "custom prompt"; draft.SetDirection("zh2en");
        Check(draft.Values.Prompt == "custom prompt", "custom prompt overwritten");
        return Task.CompletedTask;
    }

    private static Task Failure(string output)
    {
        var active = Sample();
        string path = Path.Combine(output, "unchanged-settings.json"); active.Save(path);
        string before = File.ReadAllText(path);
        var draft = new SettingsDraft(active); draft.Values.AsrThreads = 0;
        bool failed = false; try { draft.Commit(active, path); } catch (InvalidDataException) { failed = true; }
        Check(failed && active.AsrThreads == 4 && File.ReadAllText(path) == before, "invalid settings partly applied");
        draft.Values.AsrThreads = 12;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failed = false; try { draft.Commit(active, path); } catch (IOException) { failed = true; }
            Check(failed && active.AsrThreads == 4, "failed save changed runtime settings");
        }
        Check(File.ReadAllText(path) == before && !Directory.EnumerateFiles(output, "unchanged-settings.json.*.tmp").Any(),
            "failed atomic save damaged original or left temporary credentials");
        return Task.CompletedTask;
    }

    private static Task Editor(string root, string output)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            string oldLanguage = Loc.Language;
            try
            {
                var active = Sample();
                active.AsrModelDir = Path.Combine(root, "models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
                string path = Path.Combine(output, "ui-settings.json"); active.Save(path);
                string before = File.ReadAllText(path);
                int applications = 0;
                var page = new SettingPage(() => active, draft =>
                {
                    draft.Commit(active, path); applications++; Loc.Language = active.UiLanguage;
                    return Task.FromResult(false);
                }, true);
                T Find<T>(string name) => (T)page.FindName(name);
                void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window = new Window { Content = page, Width = 1340, Height = 740, Background = Brushes.White, Foreground = Brushes.Black,
                    ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                window.Show(); Pump();
                Check((string)Find<ComboBox>("TranslateAPIBox").SelectedItem == "OpenAI", "page replaced saved provider on open");
                Check(Find<Wpf.Ui.Controls.NumberBox>("TemperatureBox").Value == 0, "zero temperature changed on load");
                Find<TextBox>("ModelBox").Text = "draft-model";
                Find<ComboBox>("TranslateAPIBox").SelectedItem = "Ollama";
                Find<TextBox>("ModelBox").Text = "local-draft";
                Find<ComboBox>("TranslateAPIBox").SelectedItem = "OpenAI";
                Check(Find<TextBox>("ModelBox").Text == "draft-model", "switching provider discarded its draft");
                Find<Wpf.Ui.Controls.NumberBox>("AsrThreadsBox").Value = 10;
                Find<Wpf.Ui.Controls.ToggleSwitch>("LatencyBox").IsChecked = true;
                Click("LangToggle");
                Check(Loc.Language == oldLanguage && active.AsrThreads == 4 && applications == 0 && File.ReadAllText(path) == before,
                    "controls applied before Save");
                Click("RevertSettingsButton");
                Check(Find<TextBox>("ModelBox").Text == "saved-model" && Find<Wpf.Ui.Controls.NumberBox>("AsrThreadsBox").Value == 4,
                    "discard did not restore saved values");
                Find<TextBox>("ModelBox").Text = "final-model";
                Find<ComboBox>("TargetLangBox").Text = "en-GB";
                Click("SaveSettingsButton"); Pump();
                Check(applications == 1 && ((OpenAIConfig)active["OpenAI"]).ModelName == "final-model" && active.TargetLanguage == "en-GB",
                    $"save missed last focused/typed text: applies={applications}, model={((OpenAIConfig)active["OpenAI"]).ModelName}, target={active.TargetLanguage}, status={Find<TextBlock>("SaveStatus").Text}");
                Find<Wpf.Ui.Controls.NumberBox>("AsrThreadsBox").Value = 9;
                Find<TextBox>("BaseUrlBox").Text = "broken-url";
                Click("SaveSettingsButton"); Pump();
                Check(applications == 1 && active.AsrThreads == 4 && Find<TextBlock>("SaveStatus").Text.Contains("未完成"), "failed UI save partly applied or hid error");
                Click("RevertSettingsButton");
                window.UpdateLayout();
                Snapshot(window, Path.Combine(output, "settings-save.png"));
                window.Width = 750; window.UpdateLayout();
                var save = Find<Button>("SaveSettingsButton");
                Check(save.ActualWidth > 70 && save.TranslatePoint(new Point(), window).Y < 50, "save button lost at narrow width");
                Snapshot(window, Path.Combine(output, "settings-save-narrow.png"));
                window.Close(); window = null;
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { window?.Close(); Loc.Language = oldLanguage; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    private static void Snapshot(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
