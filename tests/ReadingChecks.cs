using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;

internal static class ReadingChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Reader absorbs a 185ms result burst without displacing the current card", Burst);
        yield return ("Reader waits for missing earlier IDs and drains every caption in order", Ordering);
        yield return ("Visible text is frozen; queued source revisions coalesce before release", Revisions);
        yield return ("Finality-only changes preserve dwell and UI identity", Finality);
        yield return ("Pause excludes paused time; manual next leaves background results intact", Pause);
        yield return ("Late retries repair history without rewinding the current caption", Retry);
        yield return ("Only explicit jump skips unread cards and retains their history", Jump);
        yield return ("Long paused bursts retain unread content beyond timeline history capacity", Backlog);
        yield return ("Follow and comfort modes separate live pacing from full reading time", Modes);
        yield return ("Failed final revisions and late results after a jump cannot strand reading", EdgeCases);
        yield return ("WPF reader keeps the active card and scroll position while new results arrive", () => ReadingUi(output));
        var session = Path.Combine(root, "artifacts", "reading-before", "session.jsonl");
        if (TestMode.IncludeLocalFixtures && File.Exists(session))
            yield return ("Recorded video session replay preserves order, content and minimum dwell", () => Replay(session, output));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    private static TranslationResult Result(long id, int sourceVersion = 1, bool final = true, string text = "完整句子。") =>
        new(new TranslationSegment { Id = id, SourceRevision = sourceVersion, UtteranceKey = $"recording:{id}",
            FinalAsr = final, Text = text }, "A complete sentence.");

    private static Task Burst()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1), At(0));
        var card = reader.Current;
        int changed = 0;
        reader.Cards.CollectionChanged += (_, _) => changed++;
        reader.Accept(Result(2), At(.185));
        reader.Accept(Result(3), At(.2));
        reader.Tick(At(3.99));
        Check(ReferenceEquals(card, reader.Current) && changed == 0, "an arriving result rebuilt the visible card");
        reader.Tick(At(4));
        Check(reader.Current?.Id == 2 && reader.PendingCount == 1 && reader.LastDwell.TotalSeconds >= 4, "burst was skipped or flashed");
        return Task.CompletedTask;
    }
    private static Task Ordering()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(3), At(0));
        reader.Accept(Result(2), At(.1));
        Check(reader.Current == null, "an earlier missing utterance was bypassed");
        reader.Accept(Result(1), At(.2));
        reader.Tick(At(100));
        Check(reader.Current?.Id == 2, "a delayed UI tick skipped multiple captions to catch up");
        reader.Tick(At(101));
        Check(reader.Current?.Id == 2, "new dwell was based on an old deadline");
        reader.Tick(At(104));
        Check(reader.Current?.Id == 3 && reader.History.Select(c => c.Id).SequenceEqual(new long[] { 1, 2 }), "reading order changed");
        return Task.CompletedTask;
    }
    private static Task Revisions()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1, 1, false, "完整前缀。"), At(0));
        reader.Accept(Result(1, 2, false, "完整前缀。较旧续句。"), At(.1));
        reader.Accept(Result(2), At(.2));
        reader.Accept(Result(1, 3, true, "完整前缀。最终续句。"), At(.3));
        Check(reader.Current?.SourceText == "完整前缀。", "revision changed text during its protected dwell");
        reader.Tick(At(4));
        Check(reader.Current?.SourceText == "完整前缀。最终续句。" && reader.Current.IsRevision, "revision did not coalesce, or the next utterance won");
        reader.Tick(At(4.185));
        Check(reader.Current?.Id == 1, "final content immediately disappeared behind the following result");
        reader.Tick(At(8));
        Check(reader.Current?.Id == 2 && reader.History.Single().SourceText.Contains("最终续句"), "final content was lost from history");
        return Task.CompletedTask;
    }
    private static Task Finality()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1, 1, false), At(0));
        var card = reader.Current;
        reader.Accept(Result(1, 2, true), At(3));
        reader.Accept(Result(2), At(3.1));
        Check(ReferenceEquals(card, reader.Current), "identical final text recreated the card");
        reader.Tick(At(4));
        Check(reader.Current?.Id == 2, "metadata-only revision reset dwell");
        return Task.CompletedTask;
    }
    private static Task Pause()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1), At(0));
        reader.SetPaused(true, At(1));
        reader.Accept(Result(2), At(10));
        reader.Accept(Result(3), At(20));
        reader.Tick(At(100));
        Check(reader.Current?.Id == 1 && reader.PendingCount == 2, "reading pause blocked ingestion or moved current text");
        reader.SetPaused(false, At(100));
        reader.Tick(At(102.9));
        Check(reader.Current?.Id == 1, "resume counted paused time as reading time");
        reader.Tick(At(103));
        reader.SetPaused(true, At(104));
        reader.Advance(At(105));
        Check(reader.Current?.Id == 3 && reader.IsPaused, "manual next incorrectly resumed auto reading");
        return Task.CompletedTask;
    }
    private static Task Retry()
    {
        var reader = new CaptionReader();
        var error = Result(1) with { IsError = true, IsPending = true, Text = "[待补译]" };
        reader.Accept(error, At(0));
        reader.Accept(Result(2), At(1));
        reader.Tick(At(4));
        var current = reader.Current;
        reader.Accept(Result(1) with { Revision = 1 }, At(5));
        Check(ReferenceEquals(current, reader.Current) && reader.History[0].TranslatedText == "A complete sentence.", "late retry stole focus or remained uncorrected");
        reader.Accept(Result(2, 2) with { IsError = true }, At(5.1));
        Check(reader.Current?.TranslatedText == "A complete sentence.", "failed replacement erased the valid bilingual pair");
        return Task.CompletedTask;
    }
    private static Task Jump()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1), At(0));
        reader.SetPaused(true, At(.1));
        foreach (int id in new[] { 2, 3, 4 }) reader.Accept(Result(id), At(1));
        reader.JumpToLatest(At(2));
        Check(reader.Current?.Id == 4 && !reader.IsPaused && reader.PendingCount == 0, "explicit jump did not select latest");
        Check(reader.History.Select(c => c.Id).SequenceEqual(new long[] { 1, 2, 3 }), "jump discarded skipped content");
        return Task.CompletedTask;
    }
    private static Task Backlog()
    {
        var reader = new CaptionReader(3);
        reader.Accept(Result(1), At(0));
        reader.SetPaused(true, At(0));
        for (int id = 2; id <= 220; id++) reader.Accept(Result(id), At(1));
        Check(reader.PendingCount == 219, "history cap dropped unread content");
        reader.SetPaused(false, At(2));
        for (int id = 2; id <= 220; id++)
        {
            reader.Tick(At(id * 5));
            Check(reader.Current?.Id == id, "backlog skipped content");
        }
        Check(reader.History.Count == 3 && reader.PendingCount == 0, "archive cap and unread queue were not separated");
        return Task.CompletedTask;
    }

    private static Task Modes()
    {
        var longText = Result(1, text: new string('字', 210)) with { Text = string.Join(" ", Enumerable.Repeat("word", 120)) };
        Check(CaptionReader.ReadingTime(longText).TotalSeconds == 12, "follow mode imposed a full long-paragraph delay");
        Check(CaptionReader.ReadingTime(longText, true).TotalSeconds > 30, "comfort mode truncated the selected reading budget");
        var reader = new CaptionReader();
        reader.Accept(longText, At(0));
        reader.Accept(Result(2), At(1));
        reader.SetComfortMode(true);
        reader.Tick(At(12));
        Check(reader.Current?.Id == 1, "comfort selection did not extend the current card");
        reader.SetPaused(true, At(13));
        reader.SetComfortMode(false);
        reader.Tick(At(100));
        Check(reader.Current?.Id == 1, "changing pacing bypassed an explicit reading hold");
        return Task.CompletedTask;
    }

    private static Task EdgeCases()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1, 1, false, "开始。"), At(0));
        reader.Accept(Result(1, 2, false, "开始。完整的后续。"), At(1));
        reader.Accept(Result(1, 3) with { IsError = true }, At(2));
        reader.Accept(Result(2), At(3));
        reader.Tick(At(4));
        Check(reader.Current?.SourceText == "开始。完整的后续。" && reader.Current.Result.Segment.FinalAsr, "failed final attempt stranded an older pending prefix");
        reader.Tick(At(8));
        Check(reader.Current?.Id == 2, "next caption never became readable after final failure");
        var jumped = new CaptionReader();
        jumped.Accept(Result(3), At(0));
        jumped.JumpToLatest(At(1));
        jumped.Accept(Result(1), At(2));
        jumped.Accept(Result(2), At(3));
        Check(jumped.Current?.Id == 3 && jumped.History.Select(c => c.Id).SequenceEqual(new long[] { 1, 2 }), "late skipped IDs were discarded or rewound the reader");
        return Task.CompletedTask;
    }

    private static Task ReadingUi(string output)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Isolated Caption instance: no Translator initialization, settings,
                // device capture or provider calls are needed for the real bindings.
                var caption = (Caption)Activator.CreateInstance(typeof(Caption), nonPublic: true)!;
                var page = new CaptionPage(caption, true, true) { Width = 750, Height = 460,
                    Background = Brushes.White, Foreground = Brushes.Black, FontFamily = new FontFamily("Microsoft YaHei UI") };
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                caption.ApplyTranslation(Result(1) with { Text = string.Join(" ", Enumerable.Repeat("This sentence remains available while another translation arrives.", 12)) });
                caption.HoldReading();
                void Layout()
                {
                    page.Measure(new Size(page.Width, page.Height));
                    page.Arrange(new Rect(0, 0, page.Width, page.Height));
                    page.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Layout();
                var scroll = (ScrollViewer)page.FindName("ReadingScroll");
                Check(scroll.ScrollableHeight > 50, "long-card fixture did not exercise scrolling");
                scroll.ScrollToVerticalOffset(50);
                Layout();
                double offset = scroll.VerticalOffset;
                var card = caption.DisplayReadingCards.Single();
                caption.ApplyTranslation(Result(2));
                caption.ApplyTranslation(Result(1, 2, true, "新的原文补全。") with { Text = "A corrected and extended translation." });
                Layout();
                Check(ReferenceEquals(card, caption.DisplayReadingCards.Single()) && Math.Abs(scroll.VerticalOffset - offset) < .1,
                    "a live result reset the protected reading viewport");
                Check(caption.ReadingStatus.Contains("已停留") && caption.ReadingStatus.Contains("待阅读 1"), "bound reader state did not reflect continued ingestion");
                caption.ReadNext(); // release the current revision, still paused
                Layout();
                Check(caption.DisplayReadingCards.Single().Id == 1 && scroll.VerticalOffset == 0, "explicit revision did not start at its beginning");
                caption.ReadNext();
                Layout();
                Check(caption.DisplayReadingCards.Single().Id == 2 && caption.DisplayLogCards.Single().Id == 1 &&
                    caption.PreviousReadingPage?.Id == 1 && caption.OverlayPreviousTranslation.Contains("A corrected and extended translation."), "real bindings lost previous-page retention or chronological history");
                var history = (ListBox)page.FindName("LogCardItems");
                Check(history.ActualHeight >= 80 && VirtualizingPanel.GetScrollUnit(history) == ScrollUnit.Pixel,
                    "small-window history clips a tall item without pixel scrolling");
                var bitmap = new RenderTargetBitmap(750, 460, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "reading-interaction.png"));
                encoder.Save(file);
                TranslationResult Timed(long id, string source, string target) => Result(id, text: source) with
                {
                    Segment = Result(id, text: source).Segment with { AudioStreamId = "ui", AudioStartSample = id*48000, AudioEndSample = (id+1)*48000 },
                    Text = target
                };
                var third = Timed(3, "完整的句子会保留原来的顺序。", "Complete sentences remain in their original order.");
                var fourth = Timed(4, "连续的短句可以合为一页，减少显示等待。", "Consecutive short sentences can share a page to reduce display waiting.");
                caption.ApplyTranslation(third);
                caption.ApplyTranslation(fourth);
                caption.ToggleReadingPause();
                caption.ReadNext();
                Layout();
                var grouped = caption.DisplayReadingCards.Single();
                Check(grouped.Results.SequenceEqual(new[] { third, fourth }) && caption.LatestCaptionId == 4 &&
                    caption.ReadingTitle == "第 3–4 段" && caption.DisplayTranslatedCaption == third.Text + " " + fourth.Text &&
                    caption.OverlayCurrentTranslation == caption.DisplayTranslatedCaption, "main/overlay bindings did not expose the complete grouped page");
                caption.HoldReading();
                caption.ApplyTranslation(Timed(5, "下一句在后台等待。", "The following sentence waits in the background."));
                Layout();
                Check(ReferenceEquals(grouped,caption.DisplayReadingCards.Single()) && caption.PreviousReadingPage?.Id == 2 && scroll.VerticalOffset == 0,
                    "another result rebuilt or scrolled the grouped page");
                Check(history.Items.Cast<ReadingCard>().All(c => c.Id != caption.PreviousReadingPage!.Id),
                    "the retained previous page appeared twice in the same window");
                var groupedBitmap = new RenderTargetBitmap(750,460,96,96,PixelFormats.Pbgra32);
                groupedBitmap.Render(page);
                var groupedEncoder = new PngBitmapEncoder();
                groupedEncoder.Frames.Add(BitmapFrame.Create(groupedBitmap));
                using var groupedFile = File.Create(Path.Combine(output,"paced-reading-interaction.png"));
                groupedEncoder.Save(groupedFile);
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task Replay(string path, string output)
    {
        var events = File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            var x = doc.RootElement;
            var created = DateTimeOffset.Parse(x.GetProperty("timestamp").GetString()!);
            var result = new TranslationResult(new TranslationSegment {
                Id = x.GetProperty("segment_id").GetInt64(), SourceRevision = x.GetProperty("source_revision").GetInt32(),
                UtteranceKey = x.GetProperty("utterance_key").GetString()!, FinalAsr = x.GetProperty("is_final").GetBoolean(),
                Text = x.GetProperty("raw_source_text").GetString()!, CreatedAt = created
            }, x.GetProperty("translated_text").GetString()!) {
                CorrectedSource = x.GetProperty("source_text").GetString(), Revision = x.GetProperty("revision").GetInt32(),
                IsError = x.GetProperty("is_error").GetBoolean(), IsPending = x.GetProperty("is_pending").GetBoolean()
            };
            return (at: created.AddMilliseconds(x.GetProperty("ready_ms").GetDouble()), result);
        }).OrderBy(e => e.at).ToArray();
        Check(events.Length > 0, "empty session");
        var epoch = events[0].at;
        var reader = new CaptionReader();
        var displays = new List<(double at, TranslationResult result)>();
        int cursor = 0;
        long version = 0;
        double max = (events[^1].at - epoch).TotalSeconds + 3600;
        for (double second = 0; second <= max; second += .1)
        {
            while (cursor < events.Length && (events[cursor].at - epoch).TotalSeconds <= second)
                reader.Accept(events[cursor++].result, At(second));
            reader.Tick(At(second));
            if (reader.PresentationVersion != version)
            {
                version = reader.PresentationVersion;
                displays.Add((second, reader.Current!.Result));
            }
            if (cursor == events.Length && reader.PendingCount == 0 && !reader.HasRevision) break;
        }
        Check(displays.Select(x => x.result.Segment.Id).SequenceEqual(displays.Select(x => x.result.Segment.Id).OrderBy(id => id)), "recorded captions were shown out of order");
        var latest = events.GroupBy(e => e.result.Segment.Id).Select(g => g.Last().result).ToArray();
        Check(latest.All(r => displays.Any(d => d.result.Segment.Id == r.Segment.Id && d.result.Segment.SourceRevision == r.Segment.SourceRevision)), "a final recorded version was never shown");
        var dwells = displays.Zip(displays.Skip(1), (a, b) => b.at - a.at).ToArray();
        Check(dwells.All(d => d >= 3.99), "recorded session still contains a flash shorter than the minimum dwell");
        var delays = displays.Select(d => d.at - (events.First(e => e.result.Segment.Id == d.result.Segment.Id && e.result.Segment.SourceRevision == d.result.Segment.SourceRevision).at - epoch).TotalSeconds).OrderBy(x => x).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "reading-replay.json"), JsonSerializer.Serialize(new {
            inputEvents = events.Length, uniqueUtterances = latest.Length, presentations = displays.Count,
            minimumDwellSeconds = dwells.Min(), addedPresentationDelayP50Seconds = delays[delays.Length / 2],
            addedPresentationDelayP95Seconds = delays[(int)Math.Floor((delays.Length - 1) * .95)],
            addedPresentationDelayMaxSeconds = delays.Max(),
            limitation = "Offline replay of recorded results; does not measure speech recognition, translation quality, or human reading comfort.",
            displays = displays.Select(d => new { seconds = d.at, id = d.result.Segment.Id, sourceRevision = d.result.Segment.SourceRevision })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
