// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.speech;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private bool _loading = true;
        private bool _saving;
        private readonly bool preview;
        private readonly Func<Setting> getActive;
        private readonly Func<SettingsDraft, Task<bool>> apply;
        private SettingsDraft draft;
        private Setting Draft => draft.Values;
        private TranslateAPIConfig? editingConfig;

        public SettingPage() : this(() => Translator.Setting!, Translator.ApplySettingsAsync, false) { }

        // Injectable boundary for offline verification of the actual settings controls.
        public SettingPage(Func<Setting> getActive, Func<SettingsDraft, Task<bool>> apply, bool preview)
        {
            this.getActive = getActive;
            this.apply = apply;
            this.preview = preview;
            draft = new SettingsDraft(getActive());
            InitializeComponent();
            if (!preview) ApplicationThemeManager.ApplySystemTheme();
            ResetDraft();

            Loaded += (s, e) =>
            {
                if (!preview)
                {
                    (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: 480);
                    CheckForFirstUse();
                }
                Loc.LanguageChanged += ApplyLanguage;
                ApplyLanguage();
            };

            Unloaded += (s, e) => Loc.LanguageChanged -= ApplyLanguage;
        }

        private void ResetDraft()
        {
            draft = new SettingsDraft(getActive());
            Draft.PropertyChanged += (_, _) => UpdateSaveStatus();
            Draft.MainWindow.PropertyChanged += (_, _) => UpdateSaveStatus();
            foreach (var config in Draft.Configs.Values.SelectMany(list => list))
                config.PropertyChanged += (_, _) => UpdateSaveStatus();
            LoadAll();
            UpdateSaveStatus();
        }

        private void UpdateSaveStatus()
        {
            if (_loading || SaveStatus == null) return;
            SaveStatus.Text = draft.IsDirty
                ? (Loc.IsZh ? "有未保存的修改；点击“保存并应用”后生效。" : "Unsaved changes. Click Save and apply to activate them.")
                : (Loc.IsZh ? "当前为已保存设置。修改后需点击“保存并应用”。" : "Saved settings. Edits take effect only after Save and apply.");
            LangToggle.Content = Draft.UiLanguage == "zh" ? "界面：中文" : "UI: English";
        }

        // ================= i18n =================

        private void LangToggle_Click(object sender, RoutedEventArgs e)
        {
            Draft.UiLanguage = Draft.UiLanguage == "zh" ? "en" : "zh";
        }

        // Shared info popup for the ⓘ buttons.
        private void Info_MouseEnter(object sender, MouseEventArgs e)
        {
            ShowInfo(sender);
        }

        private void Info_MouseLeave(object sender, MouseEventArgs e)
        {
            InfoPopup.IsOpen = false;
        }

        private void Info_Click(object sender, RoutedEventArgs e)
        {
            ShowInfo(sender);
        }

        private void ShowInfo(object sender)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tip && !string.IsNullOrEmpty(tip))
            {
                InfoPopupText.Text = tip;
                InfoPopup.PlacementTarget = fe;
                InfoPopup.IsOpen = true;
            }
        }

        private void ApplyLanguage()
        {
            SaveSettingsButton.Content = Loc.IsZh ? "保存并应用" : "Save and apply";
            RevertSettingsButton.Content = Loc.IsZh ? "撤销修改" : "Discard edits";
            UpdateSaveStatus();

            SecSpeech.Text = Loc.T("sec.speech");
            SecTranslate.Text = Loc.T("sec.translate");
            SecCaption.Text = Loc.T("sec.caption");

            LblAudioSource.Text = Loc.T("audio.source");
            InfoAudioSource.Tag = Loc.T("audio.source.tip");
            AudioMicItem.Content = Loc.T("audio.mic");
            AudioSysItem.Content = Loc.T("audio.system");

            LblAsrEngine.Text = Loc.T("asr.engine");
            InfoAsrEngine.Tag = Loc.T("asr.engine.tip");
            AsrLocalItem.Content = Loc.T("asr.local");
            AsrOtherItem.Content = Loc.T("asr.other");

            LblAsrModel.Text = Loc.T("asr.model");
            InfoAsrModel.Tag = Loc.T("asr.model.tip");

            LblAsrThreads.Text = Loc.T("asr.threads");
            InfoAsrThreads.Tag = Loc.T("asr.threads.tip");

            LblVadSilence.Text = Loc.T("vad.silence");
            InfoVadSilence.Tag = Loc.T("vad.silence.tip");

            LblVadMaxSpeech.Text = Loc.T("vad.maxspeech");
            InfoVadMaxSpeech.Tag = Loc.T("vad.maxspeech.tip");

            LblCorrect.Text = Loc.T("asr.correct");
            InfoCorrect.Tag = Loc.T("asr.correct.tip");

            LblGlossary.Text = Loc.T("asr.glossary");
            InfoGlossary.Tag = Loc.T("asr.glossary.tip");
            GlossaryButton.Content = Loc.T("asr.glossary.open");

            LblLiveCaptions.Text = "LiveCaptions";

            LblTrApi.Text = Loc.T("tr.api");
            InfoTrApi.Tag = Loc.T("tr.api.tip");
            LblTrDirection.Text = Loc.T("tr.mode");
            InfoTrDirection.Tag = Loc.T("tr.mode.tip");
            DirZh2EnItem.Content = Loc.T("tr.mode.zh2en");
            DirEn2ZhItem.Content = Loc.T("tr.mode.en2zh");
            LblTrTarget.Text = Loc.T("tr.target");
            InfoTrTarget.Tag = Loc.T("tr.target.tip");
            LblTrBaseUrl.Text = Loc.T("tr.baseurl");
            InfoTrBaseUrl.Tag = Loc.T("tr.baseurl.tip");
            LblTrApiKey.Text = Loc.T("tr.apikey");
            InfoTrApiKey.Tag = Loc.T("tr.apikey.tip");
            LblTrModel.Text = Loc.T("tr.model");
            InfoTrModel.Tag = Loc.T("tr.model.tip");
            LblTrTemperature.Text = Loc.T("tr.temperature");
            InfoTrTemperature.Tag = Loc.T("tr.temperature.tip");
            LblTrTest.Text = " ";
            TestButton.Content = Loc.T("tr.test");

            LblContexts.Text = Loc.T("cap.contexts");
            InfoContexts.Tag = Loc.T("cap.contexts.tip");
            LblDisplaySentences.Text = Loc.T("cap.display");
            LblReadingArea.Text = Loc.T("cap.display.fixed");
            InfoDisplaySentences.Tag = Loc.T("cap.display.tip");
            LblContextAware.Text = Loc.T("cap.contextaware");
            InfoContextAware.Tag = Loc.T("cap.contextaware.tip");
            LblLatency.Text = Loc.T("cap.latency");
            InfoLatency.Tag = Loc.T("cap.latency.tip");
            LblApiInterval.Text = Loc.T("cap.interval");
            InfoApiInterval.Tag = Loc.T("cap.interval.tip");
        }

        // ================= load / save =================

        private void LoadAll()
        {
            _loading = true;
            try
            {
                var s = Draft;
                if (s == null) return;

                TranslateAPIBox.ItemsSource = s.Configs.Keys;
                TranslateAPIBox.SelectedItem = s.ApiName;

                SelectByTag(AudioInputBox, s.AudioInput, "microphone");
                SelectByTag(AsrProviderBox, s.AsrProvider, "local");

                var models = LocalModelCatalog.Discover(s.AsrModelDir, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
                AsrModelBox.ItemsSource = models;
                string? selectedPath = LocalModelCatalog.Resolve(s.AsrModelDir, Directory.GetCurrentDirectory()) ??
                    SherpaStreamingAsrClient.FindModelDir(null);
                AsrModelBox.SelectedItem = models.FirstOrDefault(m => string.Equals(m.DirectoryPath, selectedPath, StringComparison.OrdinalIgnoreCase));

                AsrThreadsBox.Value = s.AsrThreads;
                VadMinSilenceBox.Value = s.VadMinSilence;
                VadMaxSpeechBox.Value = s.VadMaxSpeech;
                CorrectAsrErrorsBox.IsChecked = s.CorrectAsrErrors;

                ContextsBox.Value = s.NumContexts;
                DisplaySentencesBox.Value = s.DisplaySentences;
                ContextAwareBox.IsChecked = s.ContextAware;
                LatencyBox.IsChecked = s.MainWindow?.LatencyShow ?? false;
                ApiIntervalSlider.Value = s.MaxSyncInterval;

                SelectByTag(DirectionBox, s.TranslationMode, "zh2en");
                LoadTranslationSettings();
            }
            finally
            {
                _loading = false;
            }
        }

        private static void SelectByTag(ComboBox box, string? value, string fallback)
        {
            foreach (var obj in box.Items)
                if (obj is ComboBoxItem item && (string?)item.Tag == value) { box.SelectedItem = item; return; }
            foreach (var obj in box.Items)
                if (obj is ComboBoxItem item && (string?)item.Tag == fallback) { box.SelectedItem = item; return; }
            if (box.Items.Count > 0)
                box.SelectedIndex = 0;
        }

        private void LoadTranslationSettings()
        {
            var s = Draft;
            if (s == null) return;

            var config = editingConfig = s[s.ApiName];
            BaseUrlBox.Text = GetStr(config, "ApiUrl");
            ApiKeyBox.Text = GetStr(config, "ApiKey");
            ModelBox.Text = GetStr(config, "ModelName");
            double temp = GetDouble(config, "Temperature");
            TemperatureBox.Value = temp;

            LoadAPISetting();
        }

        private static string GetStr(object obj, string prop)
        {
            var p = obj.GetType().GetProperty(prop);
            return p?.GetValue(obj) as string ?? "";
        }

        private static void SetStr(object obj, string prop, string value)
        {
            var p = obj.GetType().GetProperty(prop);
            if (p != null && p.CanWrite)
                p.SetValue(obj, value);
        }

        private static double GetDouble(object obj, string prop)
        {
            var p = obj.GetType().GetProperty(prop);
            if (p?.GetValue(obj) is double d) return d;
            return 0;
        }

        private static void SetDouble(object obj, string prop, double value)
        {
            var p = obj.GetType().GetProperty(prop);
            if (p != null && p.CanWrite && p.PropertyType == typeof(double))
                p.SetValue(obj, value);
        }

        public void LoadAPISetting()
        {
            var configType = Draft[Draft.ApiName].GetType();
            var languagesProp = configType.GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            while (configType != null && languagesProp == null)
            {
                configType = configType.BaseType;
                languagesProp = configType?.GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            }
            languagesProp ??= typeof(TranslateAPIConfig).GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp.GetValue(null);
            string targetLang = Draft.TargetLanguage;
            TargetLangBox.ItemsSource = supportedLanguages.Keys.Append(targetLang).Distinct().ToList();
            TargetLangBox.SelectedItem = targetLang;
        }

        // ================= handlers =================

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_saving) return;
            FlushTextFields();
            if (!draft.IsDirty) { UpdateSaveStatus(); return; }
            _saving = true;
            SettingsForm.IsEnabled = false;
            SaveSettingsButton.IsEnabled = RevertSettingsButton.IsEnabled = false;
            SaveStatus.Text = Loc.IsZh ? "正在保存并应用…" : "Saving and applying…";
            try
            {
                bool restarted = await apply(draft);
                ResetDraft();
                SaveStatus.Text = restarted
                    ? (Loc.IsZh ? "设置已保存，正在重新加载识别模型。" : "Saved. Reloading the recognizer.")
                    : (Loc.IsZh ? "设置已保存并应用。" : "Settings saved and applied.");
            }
            catch (Exception ex) { SaveStatus.Text = (Loc.IsZh ? "保存未完成：" : "Save failed: ") + ex.Message; }
            finally
            {
                _saving = false;
                SettingsForm.IsEnabled = true;
                SaveSettingsButton.IsEnabled = RevertSettingsButton.IsEnabled = true;
            }
        }

        private void RevertSettings_Click(object sender, RoutedEventArgs e) { if (!_saving) ResetDraft(); }

        private void FlushTextFields()
        {
            if (_loading || editingConfig == null) return;
            SetStr(editingConfig, "ApiUrl", BaseUrlBox.Text.Trim());
            SetStr(editingConfig, "ApiKey", ApiKeyBox.Text.Trim());
            SetStr(editingConfig, "ModelName", ModelBox.Text.Trim());
            Draft.TargetLanguage = TargetLangBox.Text.Trim();
        }

        private void ConfigText_Changed(object sender, TextChangedEventArgs e) => FlushTextFields();
        private void AudioInputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Draft == null) return;
            if (AudioInputBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                Draft.AudioInput = tag;
        }

        private void AsrProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Draft == null) return;
            if (AsrProviderBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                Draft.AsrProvider = tag;
        }

        private void AsrModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Draft == null) return;
            if (AsrModelBox.SelectedItem is LocalAsrModel { Available: true } model)
                Draft.AsrModelDir = model.DirectoryPath;
        }

        private void AsrThreadsBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            Draft.AsrThreads = (int)v;
        }

        private void VadMinSilenceBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            Draft.VadMinSilence = v;
        }

        private void VadMaxSpeechBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            Draft.VadMaxSpeech = v;
        }

        private void CorrectAsrErrorsBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || Draft == null) return;
            Draft.CorrectAsrErrors = CorrectAsrErrorsBox.IsChecked == true;
        }

        private void GlossaryButton_click(object sender, RoutedEventArgs e)
        { if (!preview) GlossaryEditor.Show(Window.GetWindow(this)); }

        private void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Window == null)
                return;

            bool isHide = Translator.Window.Current.BoundingRectangle == Rect.Empty;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                ButtonText.Text = "Hide";
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                ButtonText.Text = "Show";
            }
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            FlushTextFields();
            if (TranslateAPIBox.SelectedItem is not string provider) return;
            Draft.ApiName = provider;
            _loading = true;
            try { LoadTranslationSettings(); }
            finally { _loading = false; }
            UpdateSaveStatus();
        }

        private void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Draft == null) return;
            if (DirectionBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                draft.SetDirection(tag);
                _loading = true;
                try { LoadAPISetting(); }
                finally { _loading = false; }
                UpdateSaveStatus();
            }
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Draft == null) return;
            if (TargetLangBox.SelectedItem != null)
                Draft.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_loading && Draft != null)
                Draft.TargetLanguage = TargetLangBox.Text;
        }

        private void BaseUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            FlushTextFields();
        }

        private void ApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            FlushTextFields();
        }

        private void ModelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            FlushTextFields();
        }

        private void TemperatureBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            SetDouble(Draft[Draft.ApiName], "Temperature", v);
        }

        private void ApiIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || Draft == null) return;
            Draft.MaxSyncInterval = (int)e.NewValue;
        }

        private void Contexts_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            Draft.NumContexts = (int)v;
            if (Draft.DisplaySentences > Draft.NumContexts)
                Draft.DisplaySentences = Draft.NumContexts;
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || Draft == null || args.NewValue is not double v || double.IsNaN(v)) return;
            Draft.DisplaySentences = (int)v;
            if (Draft.DisplaySentences > Draft.NumContexts)
                Draft.NumContexts = Draft.DisplaySentences;


        }

        private void ContextAwareBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || Draft == null) return;
            Draft.ContextAware = ContextAwareBox.IsChecked == true;
        }

        private void LatencyBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || Draft == null || Draft.MainWindow == null) return;
            Draft.MainWindow.LatencyShow = LatencyBox.IsChecked == true;
        }

        // ================= test connection =================

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            FlushTextFields();
            if (Draft == null) return;
            TestButton.IsEnabled = false;
            TestButton.Content = Loc.T("tr.test.running");
            try
            {
                var config = Draft[Draft.ApiName];
                string url = TextUtil.NormalizeUrl(GetStr(config, "ApiUrl"));
                string key = TextUtil.ResolveSecret(GetStr(config, "ApiKey"));
                string model = GetStr(config, "ModelName");

                if (string.IsNullOrWhiteSpace(url))
                {
                    SnackbarHost.Show(Loc.T("tr.test.fail"), "Base URL empty", SnackbarType.Error);
                    return;
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
                var body = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[] { new { role = "user", content = "ping" } },
                    max_tokens = 5
                });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(url, content);
                if (resp.IsSuccessStatusCode)
                    SnackbarHost.Show(Loc.T("tr.test.ok"), $"{model}", SnackbarType.Success);
                else
                    SnackbarHost.Show(Loc.T("tr.test.fail"), $"HTTP {(int)resp.StatusCode}", SnackbarType.Error);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show(Loc.T("tr.test.fail"), ex.Message, SnackbarType.Error);
            }
            finally
            {
                TestButton.IsEnabled = true;
                TestButton.Content = Loc.T("tr.test");
            }
        }

        private void CheckForFirstUse()
        {
            if (Translator.FirstUseFlag)
                ButtonText.Text = "Hide";
        }
    }
}
