// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows.Threading;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";
        private string statusText = "";
        public string StatusText
        {
            get => statusText;
            set { statusText = value; OnPropertyChanged(); }
        }

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public ConcurrentQueue<TranslationHistoryEntry> Contexts { get; } = new();
        private readonly CaptionTimeline timeline = new();
        private readonly CaptionReader reader = new();
        private readonly Stopwatch readingClock = Stopwatch.StartNew();
        private DispatcherTimer? readingTimer;
        private long displayedVersion = -1;

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public ObservableCollection<ReadingCard> DisplayLogCards => reader.History;
        public ObservableCollection<ReadingCard> DisplayReadingCards => reader.Cards;
        public ReadingCard? PreviousReadingPage => reader.Previous;
        public long LatestCaptionId => reader.Current?.LastId ?? 0;
        public long PresentationVersion => reader.PresentationVersion;
        public string ReadingTitle => reader.Current?.Label ?? "等待字幕";
        public string ReadingPauseLabel => reader.IsPaused ? "继续阅读" : "停留";
        public bool CanReadNext => reader.CanAdvance;
        public bool CanJumpToLatest => reader.PendingCount > 0 || reader.HasRevision;
        public bool ComfortReading
        {
            get => reader.ComfortMode;
            set { reader.SetComfortMode(value); OnPropertyChanged(); RefreshDisplay(); }
        }
        public string ReadingStatus => (reader.IsPaused ? "已停留，后台继续处理" : reader.ComfortMode ? "从容阅读 · 长段落会增加与视频的时差" : "跟随语音节奏 · 可点「停留」慢读") +
            (reader.SourceToShowSeconds.HasValue ? $" · 本页时差约 {reader.SourceToShowSeconds.Value:F1} 秒" : "") +
            (reader.PendingCount > 0 ? $" · 待阅读 {reader.PendingCount} 段，最早已等待 {reader.OldestPendingSeconds(readingClock.Elapsed):F0} 秒" : "") +
            (reader.HasRevision ? " · 本段有补全待显示" : "");

        public void ToggleReadingPause() { reader.SetPaused(!reader.IsPaused, readingClock.Elapsed); RefreshDisplay(); }
        public void HoldReading() { reader.SetPaused(true, readingClock.Elapsed); RefreshDisplay(); }
        public void ReadNext() { reader.Advance(readingClock.Elapsed); RefreshDisplay(); }
        public void JumpToLatest() { reader.JumpToLatest(readingClock.Elapsed); RefreshDisplay(); }

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation => reader.Previous == null ? "" : "\n上一页：" + reader.Previous.TranslatedText;

        // Short language labels shown in the overlay, e.g. "EN" / "ZH".
        public string OverlayOriginalLabel =>
            LangLabel(Translator.Setting?.SourceLang, Translator.Setting?.TranslationMode == "zh2en" ? "ZH" : "EN");

        public string OverlayTranslationLabel =>
            LangLabel(Translator.Setting?.TargetLanguage, Translator.Setting?.TranslationMode == "zh2en" ? "EN" : "ZH");

        private static string LangLabel(string? lang, string fallback)
        {
            if (string.IsNullOrWhiteSpace(lang))
                return fallback;
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "ZH";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return "EN";
            if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return "JA";
            if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
                return "KO";
            return lang.Length >= 2 ? lang[..2].ToUpperInvariant() : fallback;
        }

        private static TranslationHistoryEntry ToHistory(TranslationResult r) => new()
        {
            Timestamp = r.Segment.CreatedAt.LocalDateTime.ToString("HH:mm:ss"),
            TimestampFull = r.Segment.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceText = r.CorrectedSource ?? r.Segment.Text,
            TranslatedText = r.Text, TargetLanguage = r.Segment.TargetLang, ApiUsed = r.Segment.Provider
        };

        private static TranslationHistoryEntry ToHistory(CaptionParagraph paragraph)
        {
            var entry = ToHistory(paragraph.Segments[0]);
            entry.SourceText = paragraph.SourceText;
            entry.TranslatedText = paragraph.TranslatedText;
            return entry;
        }

        public void ApplyTranslation(TranslationResult result)
        {
            if (result.Segment.SourceEndedAt is { } ended)
                result = result with { SourceToReadyMs = Math.Max(0,(DateTimeOffset.UtcNow-ended).TotalMilliseconds) };
            bool timelineChanged = timeline.Upsert(result);
            bool readerChanged = reader.Accept(result, readingClock.Elapsed);
            if (!timelineChanged && !readerChanged) return;
            if (readingTimer == null)
            {
                readingTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
                readingTimer.Tick += (_, _) =>
                {
                    if (reader.Tick(readingClock.Elapsed)) RefreshDisplay();
                    else if (reader.PendingCount > 0) OnPropertyChanged(nameof(ReadingStatus));
                };
                readingTimer.Start();
            }
            RefreshDisplay();
            var all = timeline.Snapshot();
            Contexts.Clear();
            foreach (var r in all.TakeLast(MAX_CONTEXTS).Where(r => !r.IsError && !r.IsPending &&
                (r.Segment.FinalAsr || r.Segment.UtteranceKey.Length == 0)))
                Contexts.Enqueue(ToHistory(r));
            int pending = all.Count(r => r.IsPending);
            int failed = all.Count(r => r.IsError && !r.IsPending);
            StatusText = pending > 0 ? $"有 {pending} 句等待补译，已完成内容仍可阅读" :
                failed > 0 ? $"有 {failed} 句翻译失败，原文已保留" :
                all.LastOrDefault()?.Segment.IsIncomplete == true ? "语音停在未完成处，已保留原话" : "";
            DiagLog.Write($"[Display] id={result.Segment.Id} revision={result.Revision} ready={(DateTimeOffset.UtcNow-result.Segment.CreatedAt).TotalMilliseconds:F0}ms");
        }

        public void RefreshDisplay()
        {
            if (displayedVersion != reader.PresentationVersion)
            {
                displayedVersion = reader.PresentationVersion;
                DisplayOriginalCaption = reader.Current?.SourceText ?? "";
                DisplayTranslatedCaption = reader.Current?.TranslatedText ?? "";
                TranslatedCaption = DisplayTranslatedCaption;
                OverlayOriginalCaption = DisplayOriginalCaption;
                OverlayCurrentTranslation = DisplayTranslatedCaption;
                OverlayNoticePrefix = "";
                OnPropertyChanged(nameof(PresentationVersion));
                OnPropertyChanged(nameof(ReadingTitle));
                OnPropertyChanged(nameof(PreviousReadingPage));
                OnPropertyChanged(nameof(OverlayPreviousTranslation));
            }
            OnPropertyChanged(nameof(ReadingStatus));
            OnPropertyChanged(nameof(ReadingPauseLabel));
            OnPropertyChanged(nameof(CanReadNext));
            OnPropertyChanged(nameof(CanJumpToLatest));
        }
        private Caption()
        {
            reader.Trace += message => DiagLog.Write("[Reader] " + message);
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType) => string.Join("\n",
            GetPreviousContexts(count).Select(entry => textType == TextType.Caption
                ? entry.SourceText : RegexPatterns.NoticePrefix().Replace(entry.TranslatedText, "")));

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count) => Contexts.ToArray()
            .TakeLast(Math.Max(0, count)).Where(entry => entry.TranslatedText != "N/A" &&
                !entry.TranslatedText.Contains("[ERROR]") && !entry.TranslatedText.Contains("[WARNING]")).ToArray();
        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
