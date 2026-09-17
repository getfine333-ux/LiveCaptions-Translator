using System.IO;
using System.Reflection;
using System.Text.Json;
using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.models;

public sealed class SettingsDraft
{
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new ConfigDictConverter() } };
    private static readonly string[] fields = [nameof(Setting.AudioInput), nameof(Setting.AsrProvider),
        nameof(Setting.AsrModelDir), nameof(Setting.AsrThreads), nameof(Setting.VadMinSilence), nameof(Setting.VadMaxSpeech),
        nameof(Setting.CorrectAsrErrors), nameof(Setting.ApiName), nameof(Setting.TranslationMode), nameof(Setting.SourceLang),
        nameof(Setting.TargetLanguage), nameof(Setting.Prompt), nameof(Setting.UiLanguage), nameof(Setting.NumContexts),
        nameof(Setting.DisplaySentences), nameof(Setting.ContextAware), nameof(Setting.MaxSyncInterval)];
    private static readonly string[] configFields = ["ApiUrl", "ApiKey", "ModelName", "Temperature"];
    private readonly Setting baseline;
    public Setting Values { get; }

    public SettingsDraft(Setting active) { baseline = Clone(active); Values = Clone(active); }
    private static Setting Clone(Setting value) => JsonSerializer.Deserialize<Setting>(JsonSerializer.Serialize(value, json), json)!;

    private sealed record Change(object Owner, PropertyInfo Property, object? Value)
    { public void Apply() => Property.SetValue(Owner, Value); }

    private IEnumerable<Change> Changes(Setting target)
    {
        foreach (string field in fields)
        {
            var property = typeof(Setting).GetProperty(field)!;
            if (!Equals(property.GetValue(baseline), property.GetValue(Values)))
                yield return new(target, property, property.GetValue(Values));
        }
        if (Values.MainWindow.LatencyShow != baseline.MainWindow.LatencyShow)
            yield return new(target.MainWindow, typeof(MainWindowState).GetProperty(nameof(MainWindowState.LatencyShow))!, Values.MainWindow.LatencyShow);
        foreach (var (provider, configs) in Values.Configs)
            for (int index = 0; index < configs.Count; index++)
                foreach (string field in configFields)
                {
                    var property = configs[index].GetType().GetProperty(field);
                    if (property?.CanWrite != true) continue;
                    if (!Equals(property.GetValue(baseline.Configs[provider][index]), property.GetValue(configs[index])))
                        yield return new(target.Configs[provider][index], property, property.GetValue(configs[index]));
                }
    }

    public bool IsDirty => Changes(Values).Any();
    public bool RequiresAudioRestart(Setting current)
    {
        var next = Build(current);
        return next.AudioInput != current.AudioInput || next.AsrProvider != current.AsrProvider ||
            next.AsrModelDir != current.AsrModelDir || next.AsrThreads != current.AsrThreads ||
            next.SourceLang != current.SourceLang || next.VadMinSilence != current.VadMinSilence || next.VadMaxSpeech != current.VadMaxSpeech;
    }

    public void SetDirection(string mode)
    {
        if (Values.TranslationMode == mode) return;
        Values.TranslationMode = mode;
        Values.SourceLang = Setting.DefaultSourceLang(mode);
        Values.TargetLanguage = Setting.DefaultTargetLang(mode);
        if (string.IsNullOrWhiteSpace(Values.Prompt) || Values.Prompt == Setting.PROMPT_EN2ZH || Values.Prompt == Setting.PROMPT_ZH2EN)
            Values.Prompt = Setting.DefaultPrompt(mode);
    }

    public Setting Build(Setting current)
    {
        var candidate = Clone(current);
        foreach (var change in Changes(candidate)) change.Apply();
        return candidate;
    }

    public void Validate(Setting current)
    {
        var next = Build(current);
        if (!next.Configs.ContainsKey(next.ApiName) || string.IsNullOrWhiteSpace(next.TargetLanguage))
            throw new InvalidDataException("请选择翻译接口并填写目标语言。");
        if (next.AsrThreads < 1 || next.AsrThreads > 32 || !double.IsFinite(next.VadMinSilence) ||
            next.VadMinSilence < 0.2 || next.VadMinSilence > 3 || !double.IsFinite(next.VadMaxSpeech) || next.VadMaxSpeech < 3 || next.VadMaxSpeech > 30)
            throw new InvalidDataException("请检查识别线程数和切句时长。");
        var config = next[next.ApiName];
        string? url = config.GetType().GetProperty("ApiUrl")?.GetValue(config) as string;
        if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new InvalidDataException("Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        if (config is BaseLLMConfig llm && (!double.IsFinite(llm.Temperature) || llm.Temperature < 0 || llm.Temperature > 2))
            throw new InvalidDataException("Temperature 必须在 0 到 2 之间。");
    }

    public void Commit(Setting active, string path)
    {
        lock (SettingsPersistence.SyncRoot)
        {
            Validate(active);
            var candidate = Build(active);
            // A failed write leaves both the active object and original file intact.
            candidate.Save(path);
            SettingsPersistence.Batch(() => { foreach (var change in Changes(active)) change.Apply(); });
        }
    }
}
