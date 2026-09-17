// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly ConcurrentQueue<string> pendingTextQueue = new();
        private static readonly SourceContext sourceContext = new();
        private static TranslationTaskQueue translationTaskQueue = null!;
        private static volatile bool stopping;
        private static readonly SemaphoreSlim audioGate = new(1, 1);

        private static TranslationCache? translationCache = null;
        private static RateLimiter? rateLimiter = null;
        private static JsonlLogger? jsonlLogger = null;
        private static string currentSessionId = string.Empty;
        // Allow a few translations in flight so fast speech does not build a backlog.

        private static Timer? cacheCleanupTimer = null;
        private static DateTime lastCacheCleanup = DateTime.Now;
        private static speech.AudioSpeechLoop? audioLoop = null;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static bool PausedFlag { get; set; } = false;
        public static JsonlLogger? JsonlLogger => jsonlLogger;
        public static string CurrentSessionId => currentSessionId;

        public static bool IsMicrophoneMode => setting?.AudioSource == "microphone";

        public static event Action? TranslationLogged;

        static Translator()
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
            SettingsPersistence.Active = setting;

            // Only launch Windows Live Captions when it is the selected audio source.
            if (!IsMicrophoneMode)
            {
                window = LiveCaptionsHandler.LaunchLiveCaptions();
                LiveCaptionsHandler.FixLiveCaptions(Window);
                LiveCaptionsHandler.HideLiveCaptions(Window);
            }

            translationCache = new TranslationCache();
            rateLimiter = new RateLimiter(setting?.RateLimitMs ?? 200);
            translationTaskQueue = new TranslationTaskQueue(TranslateSegment, PublishResult, maxRetries: setting.MaxRetries);

            // Setup cache cleanup timer (every hour)
            cacheCleanupTimer = new Timer(CacheCleanupCallback, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        // Starts microphone + ASR (local sherpa-onnx or iFlytek cloud) as the text source.
        public static void StartAudioLoop()
        {
            if (setting == null)
                return;

            string provider = string.IsNullOrWhiteSpace(setting.AsrProvider) ? "local" : setting.AsrProvider;

            Func<speech.IAsrClient> factory;

            if (provider == "local")
            {
                string? modelDir = speech.SherpaStreamingAsrClient.FindModelDir(setting.AsrModelDir);
                if (modelDir == null)
                {
                    if (Caption != null)
                        Caption.StatusText =
                            "[ERROR] Local ASR model not found. Put a sherpa-onnx model under ./models.";
                    return;
                }

                string vadPath = Path.Combine(Directory.GetCurrentDirectory(), "models", "silero_vad.onnx");
                if (!File.Exists(vadPath))
                    vadPath = Path.Combine(Path.GetDirectoryName(modelDir)!, "silero_vad.onnx");
                bool useOffline = speech.VadOfflineAsrClient.IsSupportedModel(modelDir) &&
                                  File.Exists(vadPath);

                factory = useOffline
                    ? () => new speech.VadOfflineAsrClient(modelDir, vadPath, setting.AsrThreads,
                        setting.SourceLang, setting.VadMinSilence, setting.VadMaxSpeech)
                    : () => new speech.SherpaStreamingAsrClient(modelDir, setting.AsrThreads);
            }
            else if (provider == "other")
            {
                if (Caption != null)
                    Caption.StatusText =
                        "[INFO] Custom ASR engine not implemented. Please use 'Local (offline)'.";
                return;
            }
            else
            {
                string appId = setting.IFlytekAppId ?? "";
                string apiKey = TextUtil.ResolveSecret(setting.IFlytekApiKey);
                string apiSecret = TextUtil.ResolveSecret(setting.IFlytekApiSecret);

                bool needSecret = provider != "rtasr";
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey) ||
                    (needSecret && string.IsNullOrWhiteSpace(apiSecret)))
                {
                    if (Caption != null)
                        Caption.StatusText =
                            "[ERROR] iFlytek AppId / ApiKey / ApiSecret not configured.";
                    return;
                }

                factory = provider switch
                {
                    "rtasr" => () => new speech.IFlytekRtasrClient(appId, apiKey, setting.AsrLang),
                    "rtasr_llm" => () => new speech.IFlytekAsrLlmClient(appId, apiKey, apiSecret, setting.AsrLang),
                    _ => () => new speech.IFlytekIatLlmClient(appId, apiKey, apiSecret),
                };
            }

            int device = setting.MicrophoneDevice;
            bool useSystemAudio = setting.AudioInput == "system";
            Func<speech.IAudioCapture> captureFactory = useSystemAudio
                ? () => new speech.SystemAudioCapture()
                : () => new speech.MicrophoneCapture(device);

            audioLoop = new speech.AudioSpeechLoop(factory, captureFactory);
            audioLoop.OnResult += OnAsrResult;
            audioLoop.OnSegment += result => EnqueueSegment(result.Text, result.IsFinal, result);
            audioLoop.OnError += msg =>
            {
                System.Diagnostics.Debug.WriteLine($"[ASR] {msg}");
                if (Caption != null)
                    Caption.StatusText = $"识别异常：{msg}";
            };
            audioLoop.OnStatus += status =>
            {
                System.Diagnostics.Debug.WriteLine($"[ASR] status: {status}");
                if (Caption != null && status == "listening")
                    Caption.StatusText = "正在聆听，句子确认后显示译文";
            };
            audioLoop.Start();
        }

        public static async Task<bool> ApplySettingsAsync(SettingsDraft draft)
        {
            await audioGate.WaitAsync();
            bool restart = false, stopped = false;
            try
            {
                if (stopping) throw new InvalidOperationException("程序正在关闭，请稍后重试。");
                draft.Validate(Setting!);
                restart = draft.RequiresAudioRestart(Setting!) && IsMicrophoneMode;
                var candidate = draft.Build(Setting!);
                if (restart && candidate.AsrProvider == "other")
                    throw new InvalidOperationException("自定义识别引擎暂未实现，请选择本地识别。");
                if (restart && candidate.AsrProvider == "local" && speech.SherpaStreamingAsrClient.FindModelDir(candidate.AsrModelDir) == null)
                    throw new InvalidOperationException("未找到所选识别模型，请检查模型目录。");
                if (restart)
                {
                    // Drain the old audio under the old language/provider settings.
                    if (audioLoop != null) await audioLoop.StopAsync();
                    stopped = true;
                    if (audioLoop != null) await audioLoop.DisposeAsync();
                    audioLoop = null;
                }
                draft.Commit(Setting!, models.Setting.FILENAME);
                Loc.Language = Setting!.UiLanguage;
                Caption?.RefreshDisplay();
                return restart;
            }
            finally
            {
                // If saving failed, restart with the unchanged active configuration.
                try { if (stopped && !stopping) StartAudioLoop(); }
                finally { audioGate.Release(); }
            }
        }

        public static async Task RestartAudioAsync()
        {
            await audioGate.WaitAsync();
            try
            {
                if (stopping) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (audioLoop != null) await audioLoop.StopAsync(timeout.Token);
                if (audioLoop != null) await audioLoop.DisposeAsync().ConfigureAwait(false);
                audioLoop = null;
                Setting!.AudioSource = "microphone";
                if (!stopping) StartAudioLoop();
            }
            finally { audioGate.Release(); }
        }
        private static void OnAsrResult(string text, bool isFinal)
        {
            if (isFinal) EnqueueSegment(text, true);
        }

        private static void EnqueueSegment(string text, bool finalAsr, speech.RecognizedSpeech? speech = null)
        {
            if (PausedFlag || string.IsNullOrWhiteSpace(text)) return;
            var config = Setting![Setting.ApiName];
            var segment = new TranslationSegment
            {
                Text = text, FinalAsr = finalAsr, LogOnly = LogOnlyFlag,
                UtteranceKey = speech?.UtteranceKey ?? "", SourceRevision = speech?.SourceRevision ?? 0,
                IsIncomplete = speech?.IsIncomplete ?? false, EndReason = speech?.EndReason ?? "",
                SourceLang = Setting.SourceLang, TargetLang = Setting.TargetLanguage,
                Provider = Setting.ApiName, Model = (config as OpenAIConfig)?.ModelName ?? "unknown",
                AudioStartSample = speech?.StartSample, AudioEndSample = speech?.EndSample,
                AudioStreamId = speech?.AudioStreamId ?? "", ContinuesPrevious = speech?.ContinuesPrevious ?? false,
                ForcedEnd = speech?.ForcedEnd ?? false,
                AsrMs = speech?.InferenceMs ?? 0, AsrQueueMs = speech?.QueueMs ?? 0,
                SourceEndedAt = speech?.SourceEndedAt
            };
            try
            {
                long id = translationTaskQueue.Enqueue(segment);
                sourceContext.Remember(segment with { Id = id });
                DiagLog.Write($"[Input] id={id} start={segment.AudioStartSample} end={segment.AudioEndSample} source_age_ms={(segment.SourceEndedAt.HasValue ? (DateTimeOffset.UtcNow-segment.SourceEndedAt.Value).TotalMilliseconds.ToString("F0") : "unknown")} outstanding={translationTaskQueue.OutstandingCount}");
            }
            catch (Exception ex)
            {
                using var recovery = new SpillQueue<TranslationSegment>(1);
                recovery.Enqueue(segment);
                DiagLog.Write("[Input] unable to schedule translation; text saved: " + ex.Message);
            }
        }
        private static void CacheCleanupCallback(object? state)
        {
            try
            {
                if (translationCache != null && translationCache.Count > 100)
                {
                    translationCache.Clear();
                    System.Diagnostics.Debug.WriteLine("[Translator] Cache cleared");
                }
                lastCacheCleanup = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Translator] Cache cleanup error: {ex.Message}");
            }
        }

        private static readonly GlossaryCache glossaryCache = new();
        public static void InvalidateGlossary() => glossaryCache.Invalidate();
        public static string GetGlossary() => glossaryCache.Read(setting?.GlossaryFile ?? "glossary.txt");

        public static void StartSession()
        {
            sourceContext.Clear();
            currentSessionId = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
            string saveDir = setting?.SessionSaveDir ?? "transcripts";
            jsonlLogger?.Dispose();
            jsonlLogger = setting?.AutoSaveJsonl == true ? new JsonlLogger(saveDir, currentSessionId) : null;
        }

        public static async Task StopAsync()
        {
            if (stopping) return;
            stopping = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await audioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (audioLoop != null) await audioLoop.StopAsync(timeout.Token).ConfigureAwait(false);
                while (pendingTextQueue.TryDequeue(out var text)) EnqueueSegment(text, false);
                await translationTaskQueue.CompleteAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { DiagLog.Write("[Shutdown] pending work will be saved: " + ex.Message); }
            finally
            {
                try { if (audioLoop != null) await audioLoop.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { DiagLog.Write("[Shutdown] audio cleanup: " + ex.Message); }
                audioLoop = null;
                audioGate.Release();
                try { await translationTaskQueue.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { DiagLog.Write("[Shutdown] translation cleanup: " + ex.Message); }
                jsonlLogger?.Dispose();
                jsonlLogger = null;
                cacheCleanupTimer?.Dispose();
            }
        }
        public static void EndSession()
        {
            // Emergency process-exit fallback; normal window closing awaits StopAsync.
            audioLoop?.Dispose();
            jsonlLogger?.Dispose();
        }
        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (!stopping)
            {
                if (IsMicrophoneMode) { Thread.Sleep(50); continue; }
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.Contexts.Count > 0)
                    ClearContexts();

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.DisplaySentences, Caption.Contexts.Count);
                     historyCount > 0 && lastEOSIndex > 0;
                     historyCount--)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);

                // Caption Buffer: ignore fragments shorter than 3 units
                // (language-aware: CJK counts characters, Latin counts words)
                int unitCount = TextUtil.CountUnits(latestCaption);
                if (unitCount < 3)
                {
                    // Too short, wait for more text
                    Thread.Sleep(25);
                    continue;
                }

                // `OriginalCaption`: The sentence to be really translated.
                if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;

                    idleCount = 0;
                    // Only enqueue when sentence ends with punctuation (complete sentence)
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        pendingTextQueue.Enqueue(Caption.OriginalCaption);
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;
                }
                else
                    idleCount++;

                // Only enqueue incomplete sentences if they've been stable for a long time AND are long enough
                // This is a fallback for sentences that LiveCaptions doesn't end with punctuation
                if (idleCount == Setting.MaxIdleInterval &&
                    Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.VERYLONG_THRESHOLD)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }

                Thread.Sleep(25);
            }
        }

        public static async Task TranslateLoop()
        {
            while (!stopping)
            {
                if (Window == null && !IsMicrophoneMode)
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                if (!PausedFlag && pendingTextQueue.TryDequeue(out var text)) EnqueueSegment(text, false);
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        private static async Task<TranslationResult> TranslateSegment(TranslationSegment segment, CancellationToken token)
        {
            bool correction = Setting!.CorrectAsrErrors && segment.Provider == "OpenAI" &&
                segment.SourceLang.StartsWith("zh") && segment.TargetLang.StartsWith("en");
            // Snapshot only preceding source IDs. A queued/retried sentence must
            // not borrow a later caption from the UI's mutable reading history.
            segment = segment with { Context = Setting.ContextAware ? sourceContext.Before(segment, Setting.NumContexts) : "" };
            if (correction)
            {
                // Keep a small local terminology horizon even when the provider
                // receives only two sentences. It expires by source time and is
                // never added wholesale to the prompt or waited on.
                string termEvidence = Setting.ContextAware ? sourceContext.Before(segment, 10) : "";
                var prepared = AsrTerminology.LoadDefault().Prepare(segment.Text, termEvidence);
                segment = segment with { TranslationSource = prepared.Text, TerminologyHints = prepared.Hints, TermCorrections = prepared.Edits };
                if (prepared.Edits.Length > 0)
                    DiagLog.Write($"[Terminology] id={segment.Id} " + string.Join("; ", prepared.Edits.Select(e => e.Heard + " -> " + e.Term)));
            }
            string text = segment.TranslationSource ?? segment.Text;
            string glossary = GetGlossary();
            segment = segment with { RequestGlossary = glossary,
                TerminologyHints = string.Join("\n", new[] { segment.TerminologyHints,
                    correction ? GlossaryConsistency.Hints(text, glossary) : "" }.Where(s => !string.IsNullOrWhiteSpace(s))) };
            string cacheKey = string.Join("\u001f", "source-correction-v2", segment.Provider, segment.Model, segment.SourceLang,
                segment.TargetLang, Setting.Prompt, correction, Setting.CorrectionPrompt, glossary, segment.TerminologyHints, segment.Context, text);
            string raw = "";
            string? finishReason = null;
            bool cacheHit = Setting.CacheTranslations && translationCache!.TryGet(cacheKey, out raw!);
            if (cacheHit)
            {
                try { TranslationResponse.Parse(raw, text, segment.Context, correction, segment.TermCorrections.Select(e => e.Term), glossary); }
                catch (InvalidDataException) { translationCache!.Remove(cacheKey); cacheHit = false; }
            }
            if (!cacheHit)
            {
                await rateLimiter!.WaitForNextCall(token).ConfigureAwait(false);
                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    raw = await TranslateAPI.TranslateFunction($"{Caption!.AwareContextsCaption} 🔤 {text} 🔤", token).ConfigureAwait(false);
                    if (!raw.Contains("[ERROR]")) raw = RegexPatterns.TargetSentence().Match(raw).Groups[1].Value;
                }
                else if (segment.Provider == "OpenAI")
                {
                    var reply = await TranslateAPI.OpenAIReply(segment, token).ConfigureAwait(false);
                    raw = reply.Content;
                    finishReason = reply.FinishReason;
                }
                else raw = await TranslateAPI.TRANSLATE_FUNCTIONS[segment.Provider](text, token).ConfigureAwait(false);
            }
            bool error = string.IsNullOrWhiteSpace(raw) || raw.Contains("[ERROR]");
            if (error) return new TranslationResult(segment, "[ERROR] 翻译失败；原文：" + text) { IsError = true };
            var parsed = TranslationResponse.Parse(raw, text, segment.Context, correction, segment.TermCorrections.Select(e => e.Term), glossary);
            if (!cacheHit && Setting.CacheTranslations) translationCache!.Add(cacheKey, raw);
            return new TranslationResult(segment, parsed.Translation)
            { CorrectedSource = parsed.Source, CacheHit = cacheHit, FinishReason = finishReason };
        }

        private static async Task PublishResult(TranslationResult result)
        {
            var segment = result.Segment;
            sourceContext.Remember(result);
            result = result with { SourceToReadyMs = segment.SourceEndedAt.HasValue ? Math.Max(0,(DateTimeOffset.UtcNow-segment.SourceEndedAt.Value).TotalMilliseconds) : null };
            LogToJsonl(result);
            if (segment.FinalAsr || segment.UtteranceKey.Length == 0)
                await SQLiteHistoryLogger.UpsertSegment(currentSessionId, result).ConfigureAwait(false);
            TranslationLogged?.Invoke();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
                await dispatcher.InvokeAsync(() => Caption!.ApplyTranslation(result)).Task.ConfigureAwait(false);
        }
        private static void LogToJsonl(TranslationResult result)
        {
            if (jsonlLogger == null || !Setting!.AutoSaveJsonl) return;
            var segment = result.Segment;
            jsonlLogger.Log(new TranslationRecord
            {
                SessionId = currentSessionId, SegmentId = segment.Id, Revision = result.Revision,
                UtteranceKey = segment.UtteranceKey, SourceRevision = segment.SourceRevision,
                IsFinal = segment.FinalAsr, IsIncomplete = segment.IsIncomplete,
                EndReason = segment.EndReason, FinishReason = result.FinishReason,
                Timestamp = segment.CreatedAt.LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                SourceLang = segment.SourceLang, TargetLang = segment.TargetLang,
                SourceText = result.CorrectedSource ?? segment.Text, RawSourceText = segment.Text,
                TranslationInput = segment.TranslationSource, TermCorrections = segment.TermCorrections,
                TranslatedText = result.Text, Provider = segment.Provider, Model = segment.Model,
                LatencyMs = (long)result.RequestMs, QueueMs = result.QueueMs,
                AsrMs = segment.AsrMs, AsrQueueMs = segment.AsrQueueMs,
                AudioStartSample = segment.AudioStartSample, AudioEndSample = segment.AudioEndSample,
                AudioStreamId = segment.AudioStreamId, ContinuesPrevious = segment.ContinuesPrevious, ForcedEnd = segment.ForcedEnd,
                IsError = result.IsError, IsPending = result.IsPending, CacheHit = result.CacheHit,
                ReadyMs = (DateTimeOffset.UtcNow - segment.CreatedAt).TotalMilliseconds,
                SourceEndedAt = segment.SourceEndedAt, SourceToReadyMs = result.SourceToReadyMs
            });
        }
        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.TryDequeue(out _);
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            sourceContext.Clear();
            Caption?.Contexts.Clear();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
