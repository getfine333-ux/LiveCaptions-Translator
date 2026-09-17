// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private string? ignoredUpdateVersion;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;

        private string sessionSaveDir = "transcripts";
        private bool autoSaveJsonl = true;
        private bool autoExportTxt = false;
        private bool autoExportMarkdown = false;
        private bool includeTimestamps = true;
        private bool includeMetadata = true;
        private bool cacheTranslations = true;
        private int rateLimitMs = 300;
        private int maxRetries = 2;
        private string sourceLang = "en";
        private string translationMode = "en2zh";

        private string audioSource = "livecaptions";
        private string iFlytekAppId = "";
        private string iFlytekApiKey = "";
        private string iFlytekApiSecret = "";
        private string asrProvider = "rtasr_llm";
        private string asrLang = "cn";
        private int microphoneDevice = 0;
        private string asrModelDir = "";
        private int asrThreads = 4;
        private string audioInput = "microphone";
        private bool correctAsrErrors = true;
        private string correctionPrompt = CORRECTION_PROMPT;
        private double vadMinSilence = 0.6;
        private double vadMaxSpeech = 8.0;
        private string glossaryFile = "glossary.txt";
        private string uiLanguage = "zh";

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = value;
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = value;
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        public string SessionSaveDir
        {
            get => sessionSaveDir;
            set { sessionSaveDir = value; OnPropertyChanged("SessionSaveDir"); }
        }
        public bool AutoSaveJsonl
        {
            get => autoSaveJsonl;
            set { autoSaveJsonl = value; OnPropertyChanged("AutoSaveJsonl"); }
        }
        public bool AutoExportTxt
        {
            get => autoExportTxt;
            set { autoExportTxt = value; OnPropertyChanged("AutoExportTxt"); }
        }
        public bool AutoExportMarkdown
        {
            get => autoExportMarkdown;
            set { autoExportMarkdown = value; OnPropertyChanged("AutoExportMarkdown"); }
        }
        public bool IncludeTimestamps
        {
            get => includeTimestamps;
            set { includeTimestamps = value; OnPropertyChanged("IncludeTimestamps"); }
        }
        public bool IncludeMetadata
        {
            get => includeMetadata;
            set { includeMetadata = value; OnPropertyChanged("IncludeMetadata"); }
        }
        public bool CacheTranslations
        {
            get => cacheTranslations;
            set { cacheTranslations = value; OnPropertyChanged("CacheTranslations"); }
        }
        public int RateLimitMs
        {
            get => rateLimitMs;
            set { rateLimitMs = value; OnPropertyChanged("RateLimitMs"); }
        }
        public int MaxRetries
        {
            get => maxRetries;
            set { maxRetries = value; OnPropertyChanged("MaxRetries"); }
        }
        public string SourceLang
        {
            get => sourceLang;
            set { sourceLang = value; OnPropertyChanged("SourceLang"); }
        }
        public string TranslationMode
        {
            get => translationMode;
            set { translationMode = value; OnPropertyChanged("TranslationMode"); }
        }

        // "livecaptions" (read Windows Live Captions text) or "microphone" (iFlytek RTASR).
        public string AudioSource
        {
            get => audioSource;
            set { audioSource = value; OnPropertyChanged("AudioSource"); }
        }
        public string IFlytekAppId
        {
            get => iFlytekAppId;
            set { iFlytekAppId = value; OnPropertyChanged("IFlytekAppId"); }
        }
        public string IFlytekApiKey
        {
            get => iFlytekApiKey;
            set { iFlytekApiKey = value; OnPropertyChanged("IFlytekApiKey"); }
        }
        // Console "APISecret", used to sign the ASR LLM handshake.
        public string IFlytekApiSecret
        {
            get => iFlytekApiSecret;
            set { iFlytekApiSecret = value; OnPropertyChanged("IFlytekApiSecret"); }
        }
        // "rtasr_llm" (中文识别大模型) or "rtasr" (实时语音转写标准版).
        public string AsrProvider
        {
            get => asrProvider;
            set { asrProvider = value; OnPropertyChanged("AsrProvider"); }
        }
        // Local sherpa-onnx model directory (empty = auto-detect under ./models).
        public string AsrModelDir
        {
            get => asrModelDir;
            set { asrModelDir = value; OnPropertyChanged("AsrModelDir"); }
        }
        public int AsrThreads
        {
            get => asrThreads;
            set { asrThreads = value; OnPropertyChanged("AsrThreads"); }
        }
        // "microphone" (default input device) or "system" (WASAPI loopback, e.g. video audio).
        public string AudioInput
        {
            get => audioInput;
            set { audioInput = value; OnPropertyChanged("AudioInput"); }
        }
        // Use the LLM to fix homophone/ASR errors before translating.
        public bool CorrectAsrErrors
        {
            get => correctAsrErrors;
            set { correctAsrErrors = value; OnPropertyChanged("CorrectAsrErrors"); }
        }
        public string CorrectionPrompt
        {
            get => correctionPrompt;
            set { correctionPrompt = value; OnPropertyChanged("CorrectionPrompt"); }
        }
        // Seconds of silence before a speech segment is cut (higher = more complete sentences).
        public double VadMinSilence
        {
            get => vadMinSilence;
            set { vadMinSilence = value; OnPropertyChanged("VadMinSilence"); }
        }
        // Max length of a single speech segment in seconds (higher = fewer forced cuts).
        public double VadMaxSpeech
        {
            get => vadMaxSpeech;
            set { vadMaxSpeech = value; OnPropertyChanged("VadMaxSpeech"); }
        }
        // Path to a glossary file ("source = target" per line) used to steer terminology.
        public string GlossaryFile
        {
            get => glossaryFile;
            set { glossaryFile = value; OnPropertyChanged("GlossaryFile"); }
        }
        // UI language: "zh" or "en".
        public string UiLanguage
        {
            get => uiLanguage;
            set { uiLanguage = value; OnPropertyChanged("UiLanguage"); }
        }
        // RTASR language: "cn" / "en" for standard; "autodialect" / "autominor" for LLM.
        public string AsrLang
        {
            get => asrLang;
            set { asrLang = value; OnPropertyChanged("AsrLang"); }
        }
        public int MicrophoneDevice
        {
            get => microphoneDevice;
            set { microphoneDevice = value; OnPropertyChanged("MicrophoneDevice"); }
        }

        // Default prompts per translation direction.
        public const string PROMPT_EN2ZH =
            "你是国际学术会议和技术会议的实时字幕翻译器。请将英文会议字幕翻译成简体中文。\n\n" +
            "要求：\n" +
            "1. 保留重要英文专业术语，不要强行翻译所有术语。\n" +
            "2. 对关键术语可以采用\"中文解释（English term）\"或\"English term（中文解释）\"格式。\n" +
            "3. 不要总结，不要扩写，不要加入原文没有的信息。\n" +
            "4. 如果字幕是不完整句子，只做最小必要补全。\n" +
            "5. 如果原文明显是语音识别错误，可以根据上下文做轻微修正。\n" +
            "6. 输出只包含中文译文，不要输出解释、Markdown 或多余前后缀。";

        public const string PROMPT_ZH2EN =
            "You are a real-time interpreter for international business and technical meetings. " +
            "Translate the Chinese meeting subtitles into natural, fluent English.\n\n" +
            "Requirements:\n" +
            "1. Keep important technical terms and proper nouns accurate; do not mistranslate them.\n" +
            "2. Keep the translation concise and natural, suitable for live subtitles.\n" +
            "3. Do not summarize, do not add information that is not in the source.\n" +
            "4. If the subtitle is an incomplete sentence, only complete it minimally.\n" +
            "5. If the source is clearly a speech-recognition error, correct it slightly based on context.\n" +
            "6. Output ONLY the English translation, with no explanation, Markdown, or extra text.";

        // Used when CorrectAsrErrors is on: fix homophone/ASR mistakes, then translate.
        public const string CORRECTION_PROMPT =
            "你是国际会议的实时字幕处理器。输入是中文语音识别结果，可能包含同音字错误、断句错误或识别错误。\n\n" +
            "任务：\n" +
            "1. 结合上下文，纠正明显的同音字错误和识别错误，得到纠正后的中文。只纠正明显错误，不要改写、润色或增删内容。\n" +
            "2. 如果原文没有明显错误，保持原样。\n" +
            "3. 保留专业术语的准确性。\n\n" +
            "严格只输出以下 JSON，不要输出任何其他内容：\n" +
            "{\"zh\":\"纠正后的中文\",\"en\":\"纠正后中文的英文翻译\"}\n" +
            "注意：en 字段必须是 zh 字段的英文翻译，不要翻译原始错误文本。";

        public static string DefaultPrompt(string mode) =>
            mode == "zh2en" ? PROMPT_ZH2EN : PROMPT_EN2ZH;

        public static string DefaultTargetLang(string mode) =>
            mode == "zh2en" ? "en-US" : "zh-CN";

        public static string DefaultSourceLang(string mode) =>
            mode == "zh2en" ? "zh-CN" : "en";

        public TranslateAPIConfig this[string key] =>
            configs.ContainsKey(key) && configIndices.ContainsKey(key)
                ? configs[key][configIndices[key]]
                : new TranslateAPIConfig();

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            translationMode = "en2zh";
            prompt = PROMPT_EN2ZH;

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 775) / 2, screenHeight * 3 / 4 - 167, 775, 167)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Google2", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "LMStudio", [new LMStudioConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Google2", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "LMStudio", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 }
            };
        }

        public static Setting Load()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            try
            {
                return Load(jsonPath);
            }
            catch (JsonException)
            {
                string backupPath = jsonPath + ".bak";
                File.Move(jsonPath, backupPath);
                return Load(jsonPath);
            }
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;

            // Load from JSON file if it exists
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.Open(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new ConfigDictConverter() }
                    };
                    setting = JsonSerializer.Deserialize<Setting>(fileStream, options) ?? new Setting();
                }
            }
            else
                setting = new Setting();

            // Ensure all required API configs are present
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (setting.Configs.ContainsKey(key))
                    continue;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    setting.Configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)];
                else
                    setting.Configs[key] = [new TranslateAPIConfig()];
            }

            // Ensure ConfigIndices has all keys (for upgrades from older setting.json)
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (!setting.ConfigIndices.ContainsKey(key))
                    setting.ConfigIndices[key] = 0;
            }

            // Keep translation direction consistent with the selected mode.
            setting.NormalizeMode();

            return setting;
        }

        // Fix source/target language and prompt if they don't match the selected mode.
        // Only touches fields that are empty or still at the other mode's default,
        // so a user-customized prompt is preserved.
        private void NormalizeMode()
        {
            bool zh2en = translationMode == "zh2en";

            if (string.IsNullOrWhiteSpace(targetLanguage) ||
                targetLanguage == (zh2en ? "zh-CN" : "en-US"))
                targetLanguage = DefaultTargetLang(translationMode);

            if (string.IsNullOrWhiteSpace(sourceLang) ||
                sourceLang == (zh2en ? "en" : "zh-CN"))
                sourceLang = DefaultSourceLang(translationMode);

            if (string.IsNullOrWhiteSpace(prompt) ||
                prompt == PROMPT_EN2ZH || prompt == PROMPT_ZH2EN)
                prompt = DefaultPrompt(translationMode);
        }

        public void Save()
        {
            Save(FILENAME);
        }

        public void Save(string jsonPath)
        {
            lock (SettingsPersistence.SyncRoot)
            {
                string path = Path.GetFullPath(jsonPath);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new ConfigDictConverter() }
                };
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, this, options);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            SettingsPersistence.SaveIfActive(this);
        }

        public static bool IsConfigExist()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            return File.Exists(jsonPath);
        }
    }
}
