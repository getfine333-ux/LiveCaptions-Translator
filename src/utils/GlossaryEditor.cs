using System.IO;
using System.Net.Http;
using System.Windows;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils;

public static class GlossaryEditor
{
    private static readonly HttpClient glossaryClient = new() { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 256 * 1024 };
    public static void Show(Window? owner)
    {
        try
        {
            var settings = Translator.Setting!;
            string path = string.IsNullOrWhiteSpace(settings.GlossaryFile) ? "glossary.txt" : settings.GlossaryFile;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), path);
            Func<string, CancellationToken, Task<IReadOnlyList<GlossaryTerm>>>? generate = null;
            string description = Loc.IsZh ? "可编辑现有术语。AI 生成需要选择 OpenAI 兼容接口（支持 DeepSeek 等）。" :
                "Edit existing terms. AI generation requires an OpenAI-compatible provider, such as DeepSeek.";
            if (settings.ApiName == "OpenAI" && settings["OpenAI"] is OpenAIConfig config)
            {
                string endpoint = TextUtil.NormalizeUrl(config.ApiUrl), keyReference = config.ApiKey, model = config.ModelName;
                string source = settings.SourceLang, target = settings.TargetLanguage;
                description = Loc.IsZh ? $"使用当前 AI：{model} · {source} → {target}。仅发送领域描述和语言方向。" :
                    $"Current AI: {model} · {source} → {target}. Sends only the topic and language direction.";
                generate = (topic, token) => new GlossaryGenerator(glossaryClient).GenerateAsync(endpoint, TextUtil.ResolveSecret(keyReference),
                    model, topic, source, target, token);
            }
            var editor = new GlossaryWindow(path, description, generate, () =>
            {
                settings.GlossaryFile = path;
                Translator.InvalidateGlossary();
            }) { Owner = owner };
            editor.ShowDialog();
        }
        catch (Exception ex)
        {
            SnackbarHost.Show("打开失败", ex.Message, SnackbarType.Error);
        }

    }
}


