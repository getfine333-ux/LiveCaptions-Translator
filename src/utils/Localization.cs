using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.utils
{
    // Very small i18n helper: key -> (zh, en). Bind text via Loc.T("key").
    public static class Loc
    {
        public static event Action? LanguageChanged;

        private static string _lang = "zh";
        public static string Language
        {
            get => _lang;
            set
            {
                if (_lang == value) return;
                _lang = value;
                LanguageChanged?.Invoke();
            }
        }

        public static bool IsZh => _lang == "zh";

        public static string T(string key)
        {
            if (_map.TryGetValue(key, out var pair))
                return IsZh ? pair.zh : pair.en;
            return key;
        }

        private static readonly Dictionary<string, (string zh, string en)> _map = new()
        {
            // ---- window / nav ----
            ["app.title"] = ("MeetLingo", "MeetLingo"),
            ["nav.caption"] = ("字幕", "Caption"),
            ["nav.setting"] = ("设置", "Setting"),
            ["nav.history"] = ("记录", "History"),
            ["nav.info"] = ("关于", "Info"),
            ["lang.toggle"] = ("EN", "中文"),

            // ---- sections ----
            ["sec.speech"] = ("语音识别", "Speech Recognition"),
            ["sec.translate"] = ("翻译", "Translation"),
            ["sec.caption"] = ("字幕显示", "Caption Display"),

            // ---- speech ----
            ["audio.source"] = ("音频来源", "Audio Source"),
            ["audio.source.tip"] = ("选择声音来源。\n推荐：会议发言用「麦克风」；用视频做测试用「系统声音」。",
                                    "Where audio comes from.\nRecommended: \"Microphone\" for meetings; \"System\" when testing with a video."),
            ["audio.mic"] = ("麦克风", "Microphone"),
            ["audio.system"] = ("系统声音", "System Audio"),

            ["asr.engine"] = ("识别引擎", "ASR Engine"),
            ["asr.engine.tip"] = ("语音识别引擎。\n推荐：「本地（离线）」——免费、隐私、无需联网。\n「其他」为预留接口，可在代码中扩展。",
                                  "Speech recognition engine.\nRecommended: \"Local (offline)\" - free, private, no network needed.\n\"Other\" is a reserved interface for custom integrations."),
            ["asr.local"] = ("本地（离线）", "Local (offline)"),
            ["asr.other"] = ("其他（预留）", "Other (reserved)"),

            ["asr.model"] = ("识别模型", "ASR Model"),
            ["asr.model.tip"] = ("本地识别模型，自动扫描 models/ 目录。\n推荐：sense-voice（中文准、速度快）；whisper-turbo（准确率更高、稍慢）。",
                                 "Local model, auto-detected from the models/ folder.\nRecommended: sense-voice (fast, accurate Chinese); whisper-turbo (higher accuracy, a bit slower)."),

            ["asr.threads"] = ("线程数", "Threads"),
            ["asr.threads.tip"] = ("识别使用的 CPU 线程数。\n推荐：8（20 核 CPU 可用 8-12；线程过多收益递减）。",
                                   "CPU threads used for recognition.\nRecommended: 8 (use 8-12 on a 20-core CPU; more gives diminishing returns)."),

            ["vad.silence"] = ("切句停顿（秒）", "Sentence Pause (s)"),
            ["vad.silence.tip"] = ("停顿多久判定一句话结束。\n推荐：0.6。调大→句子更完整但延迟增加；调小→更快但易断句。",
                                   "Silence duration that marks the end of a sentence.\nRecommended: 0.6. Larger = more complete sentences but higher latency; smaller = faster but may cut sentences."),

            ["vad.maxspeech"] = ("最长句（秒）", "Max Segment (s)"),
            ["vad.maxspeech.tip"] = ("单句最长时长，超过则强制切分。\n推荐：8。优先保留自然语句，持续讲话到达上限后才强制切分。",
                                     "Maximum length of one segment; longer speech is force-split.\nRecommended: 8. Prefer natural utterances; cap uninterrupted speech at this limit."),

            ["asr.correct"] = ("同音字纠错", "Homophone Fix"),
            ["asr.correct.tip"] = ("用翻译模型结合上下文纠正同音字/识别错误。\n推荐：开启。可显著减少误解。",
                                   "Use the LLM to fix homophone/recognition errors from context.\nRecommended: On. Greatly reduces misunderstandings."),

            ["asr.glossary"] = ("术语表", "Glossary"),
            ["asr.glossary.tip"] = ("专业术语对照表，翻译时强制采用。\n格式：每行一条  原文 = 译文。修改后即时生效。",
                                    "Terminology mapping enforced during translation.\nFormat: one \"source = target\" per line. Takes effect immediately."),
            ["asr.glossary.open"] = ("生成 / 编辑", "Generate / Edit"),

            // ---- translation ----
            ["tr.api"] = ("翻译 API", "Translate API"),
            ["tr.api.tip"] = ("翻译服务商。\n推荐：OpenAI（兼容接口，可填 DeepSeek / MiMo 等）。",
                              "Translation provider.\nRecommended: OpenAI (compatible API; use DeepSeek / MiMo etc.)."),
            ["tr.apikey"] = ("API Key", "API Key"),
            ["tr.apikey.tip"] = ("翻译服务的密钥。\n安全提示：也可用环境变量，例如 ${DEEPSEEK_API_KEY}。",
                                 "Key for the translation service.\nSecurity: you may also use an environment variable, e.g. ${DEEPSEEK_API_KEY}."),
            ["tr.baseurl"] = ("Base URL", "Base URL"),
            ["tr.baseurl.tip"] = ("OpenAI 兼容接口地址。\n示例：https://api.deepseek.com/chat/completions",
                                  "OpenAI-compatible endpoint.\nExample: https://api.deepseek.com/chat/completions"),
            ["tr.model"] = ("模型", "Model"),
            ["tr.model.tip"] = ("使用的模型名。\n示例：deepseek-chat",
                                "Model name.\nExample: deepseek-chat"),
            ["tr.temperature"] = ("Temperature", "Temperature"),
            ["tr.temperature.tip"] = ("采样温度，越低越稳定。\n推荐：0.2",
                                      "Sampling temperature; lower is more deterministic.\nRecommended: 0.2"),
            ["tr.target"] = ("目标语言", "Target Language"),
            ["tr.target.tip"] = ("译文语言。\n推荐：中→英场景选 en-US。",
                                 "Target language of the translation.\nRecommended: en-US for Chinese-to-English."),
            ["tr.mode"] = ("翻译方向", "Direction"),
            ["tr.mode.tip"] = ("翻译方向。\n中→英：中文发言、对方看英文；英→中：英文会议、自己看中文。",
                               "Translation direction.\nzh2en: speak Chinese, others read English; en2zh: English meeting, read Chinese."),
            ["tr.mode.zh2en"] = ("中文 → 英文", "Chinese → English"),
            ["tr.mode.en2zh"] = ("英文 → 中文", "English → Chinese"),
            ["tr.test"] = ("测试连接", "Test Connection"),
            ["tr.test.running"] = ("测试中…", "Testing..."),
            ["tr.test.ok"] = ("连接成功", "Connection OK"),
            ["tr.test.fail"] = ("连接失败", "Connection failed"),

            // ---- caption ----
            ["cap.contexts"] = ("上下文句数", "Context Sentences"),
            ["cap.contexts.tip"] = ("启用「上下文感知」时参考的前文句数。\n推荐：2",
                                    "Number of previous sentences used when \"Context Aware\" is on.\nRecommended: 2"),
            ["cap.display"] = ("字幕阅读", "Caption Reading"),
            ["cap.display.fixed"] = ("固定显示当前段", "One current passage"),
            ["cap.display.tip"] = ("当前段固定在顶部，历史字幕在下方，最近的在前。可在字幕页选择从容阅读，或点击停留和下一段。",
                                   "The current passage stays at the top; history appears below, newest first. Use Comfort reading, Hold or Next on the caption page."),
            ["cap.contextaware"] = ("上下文感知", "Context Aware"),
            ["cap.contextaware.tip"] = ("翻译时参考前文，提升连贯性。\n推荐：开启（会略增 token）。",
                                        "Use previous sentences for more coherent translation.\nRecommended: On (slightly more tokens)."),
            ["cap.latency"] = ("显示延迟", "Show Latency"),
            ["cap.latency.tip"] = ("在主窗口显示每次翻译耗时。\n推荐：关闭（调试时可开）。",
                                   "Show per-translation latency in the main window.\nRecommended: Off (turn on for debugging)."),
            ["cap.interval"] = ("API 间隔", "API Interval"),
            ["cap.interval.tip"] = ("字幕变化多少次后调用一次翻译 API。\n推荐：3。调大→更省 API、更慢。",
                                    "How many caption changes trigger one translation call.\nRecommended: 3. Larger = fewer API calls, slower."),
        };
    }
}
