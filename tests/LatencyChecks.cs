using System.IO;
using System.Text.Json;
using LiveCaptionsTranslator.models;

internal static class LatencyChecks
{
    public static IEnumerable<(string name, Func<Task> test)> All(string root, string output)
    {
        yield return ("Normal speech with expanded translations does not accumulate reading delay", Normal);
        yield return ("Sustained short sentences form stable complete pages without losing IDs", ShortSentences);
        yield return ("Grouped revisions, pause and history retain every original bilingual pair", GroupedRevision);
        yield return ("Page grouping respects missing IDs, stream boundaries, errors and text budgets", Boundaries);
        yield return ("Comfort reading retains full text budgets and disables automatic grouping", Comfort);
        var session = Path.Combine(root, "artifacts", "latency-before", "session.jsonl");
        if (TestMode.IncludeLocalFixtures && File.Exists(session))
            yield return ("98-segment live-session replay reduces display backlog without dropping or flashing", () => Replay(session, output));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TimeSpan At(double second) => TimeSpan.FromSeconds(second);
    private static TranslationResult Result(long id, double duration = 1) => new(new TranslationSegment
    {
        Id = id, FinalAsr = true, SourceRevision = 1, UtteranceKey = $"stream:{id}", Text = $"第{id}句是完整的。",
        AudioStreamId = "stream", AudioStartSample = (long)((id-1)*duration*16000), AudioEndSample = (long)(id*duration*16000)
    }, $"Complete sentence {id}.");
    private sealed record Arrival(double At, TranslationResult Result);
    private sealed record Display(double At, TranslationResult[] Results, double Hold, double Wait);

    private static List<Display> Run(Arrival[] events)
    {
        var reader = new CaptionReader();
        var pages = new List<Display>();
        long version = 0;
        int cursor = 0;
        for (int tick = 0; tick <= (events[^1].At + 3600)*5; tick++)
        {
            double now = tick/5d;
            while (cursor < events.Length && events[cursor].At <= now)
                reader.Accept(events[cursor++].Result, At(now));
            reader.Tick(At(now));
            if (reader.PresentationVersion != version)
            {
                version = reader.PresentationVersion;
                pages.Add(new Display(now, reader.Current!.Results.ToArray(), reader.RemainingSeconds(At(now)), reader.DisplayQueueWait.TotalSeconds));
            }
            if (cursor == events.Length && reader.PendingCount == 0 && !reader.HasRevision) break;
        }
        Check(cursor == events.Length && reader.PendingCount == 0 && !reader.HasRevision, "reader did not drain");
        Check(pages.Zip(pages.Skip(1), (a,b) => b.At-a.At).All(d => d >= 3.999), "an automatic page flashed for less than 4s");
        Check(pages.SelectMany(p => p.Results).SequenceEqual(events.Select(e => e.Result)), "a source or translation was dropped, reordered, edited or duplicated");
        return pages;
    }

    private static Task Normal()
    {
        var events = Enumerable.Range(1,180).Select(id => new Arrival(id*4, Result(id,4) with
        { Text = "This deliberately longer translation expresses the same complete thought with several extra words for a natural English reading." })).ToArray();
        Check(CaptionReader.ReadingTime(events[0].Result).TotalSeconds > 4, "fixture did not amplify the old pacing mismatch");
        var pages = Run(events);
        Check(pages.Count == events.Length && pages.Max(p => p.Wait) < .21, "normal-speed translation expansion accumulated display debt");
        var nonIntegral = Enumerable.Range(1,1800).Select(id => new Arrival(id*4.13, Result(id,4.13) with
        { Text = events[0].Result.Text })).ToArray();
        Check(Run(nonIntegral).Max(p => p.Wait) < 5, "over two hours of timer rounding accumulated display drift");
        return Task.CompletedTask;
    }
    private static Task ShortSentences()
    {
        var events = Enumerable.Range(1,300).Select(id => new Arrival(id,Result(id))).ToArray();
        var pages = Run(events);
        Check(pages.Any(p => p.Results.Length > 1) && pages.All(p => p.Results.Length <= 4), "short-sentence pages were not bounded");
        Check(pages.Max(p => p.Wait) <= 3.01, "short complete sentences accumulate one minimum dwell each");
        Check(pages.All(p => p.Results.All(r => r.Segment.FinalAsr)), "a partial sentence was used to catch up");
        return Task.CompletedTask;
    }
    private static Task GroupedRevision()
    {
        var reader = new CaptionReader();
        reader.Accept(Result(1), At(0));
        foreach (int id in new[] {2,3,4}) reader.Accept(Result(id), At(id-1));
        reader.Tick(At(4));
        Check(reader.Current!.Results.Select(r => r.Segment.Id).SequenceEqual(new long[] {2,3,4}), "ready sentences did not form a stable page");
        var current = reader.Current;
        var fixed2 = Result(2) with { Segment = Result(2).Segment with { SourceRevision = 2 }, Text = "Corrected complete sentence two." };
        reader.Accept(fixed2, At(5));
        reader.Accept(Result(5), At(5.1));
        reader.SetPaused(true, At(6));
        reader.Tick(At(60));
        Check(ReferenceEquals(current,reader.Current) && !reader.Current.TranslatedText.Contains("Corrected"), "group text changed during reading");
        Check(reader.PendingCount == 1 && reader.HasRevision, "group revision counted as a new unread sentence");
        reader.SetPaused(false, At(60));
        reader.Tick(At(61.9));
        Check(ReferenceEquals(current,reader.Current), "pause consumed protected dwell");
        reader.Tick(At(62));
        Check(reader.Current!.IsRevision && reader.Current.Results.Count == 3 && reader.Current.Results[0] == fixed2, "revision lost another sentence from its page");
        reader.Tick(At(66));
        Check(reader.Current!.Id == 5 && reader.History.Select(c => c.Id).SequenceEqual(new long[] {1,2,3,4}), "group history lost individual segment IDs");
        Check(reader.History[1].TranslatedText == fixed2.Text, "corrected group member was not archived");
        reader.Accept(Result(3) with { Revision = 1, Text = "Late corrected sentence three." }, At(67));
        Check(reader.Current.Id == 5 && reader.History[2].TranslatedText.StartsWith("Late"), "late group retry stole focus or failed to repair history");
        var jump = new CaptionReader();
        jump.Accept(Result(1),At(0)); jump.Accept(Result(2),At(1)); jump.Accept(Result(3),At(2)); jump.Tick(At(4));
        jump.Accept(fixed2,At(5)); jump.SetPaused(true,At(5)); jump.JumpToLatest(At(6));
        Check(jump.Current!.LastId == 3 && !jump.IsPaused && jump.PendingCount == 0, "jump to a member revision rewound the active page");
        jump.Accept(Result(4),At(7)); jump.Tick(At(10));
        Check(jump.Current!.Id == 4, "jump to grouped correction stranded the following sentence");
        return Task.CompletedTask;
    }
    private static Task Boundaries()
    {
        foreach (var bad in new[] {
            Result(3) with { Segment = Result(3).Segment with { AudioStreamId = "new-stream" } },
            Result(3) with { Segment = Result(3).Segment with { AudioStartSample = null } },
            Result(3) with { Segment = Result(3).Segment with { AudioStartSample = 1 } },
            Result(3) with { Segment = Result(3).Segment with { FinalAsr = false } },
            Result(3) with { Segment = Result(3).Segment with { IsIncomplete = true } },
            Result(3) with { IsError = true, IsPending = true },
            Result(3) with { Text = string.Join(" ", Enumerable.Repeat("word", 80)) }
        })
        {
            var reader = new CaptionReader();
            reader.Accept(Result(1),At(0)); reader.Accept(Result(2),At(1)); reader.Accept(bad,At(2));
            reader.Tick(At(4));
            Check(reader.Current!.Results.Count == 1 && reader.Current.Id == 2 && reader.PendingCount == 1, "group crossed a semantic/source/size boundary");
        }
        var missing = new CaptionReader();
        missing.Accept(Result(1),At(0)); missing.Accept(Result(3),At(1)); missing.Tick(At(100));
        Check(missing.Current!.Id == 1 && missing.PendingCount == 1, "catch-up skipped a missing earlier sentence");
        missing.Accept(Result(2),At(101)); missing.Tick(At(101));
        Check(missing.Current!.Results.Count == 2, "restored consecutive IDs failed to resume");
        missing.Accept(Result(4),At(102)); missing.Accept(Result(5),At(102));
        missing.JumpToLatest(At(103));
        Check(missing.Current.Id == 5 && missing.History.Select(c => c.Id).SequenceEqual(new long[] {1,2,3,4}), "explicit jump lost grouped source records");
        return Task.CompletedTask;
    }
    private static Task Comfort()
    {
        var reader = new CaptionReader();
        reader.SetComfortMode(true);
        var longResult = Result(1,4) with { Text = string.Join(" ", Enumerable.Repeat("word",120)) };
        reader.Accept(longResult,At(0)); reader.Accept(Result(2),At(1)); reader.Accept(Result(3),At(2));
        reader.Tick(At(12));
        Check(reader.Current!.Id == 1 && reader.RemainingSeconds(At(12)) > 18, "audio pacing overrode comfortable reading");
        reader.Tick(At(31));
        Check(reader.Current.Id == 2 && reader.Current.Results.Count == 1, "comfort mode automatically combined unread sentences");
        reader.SetPaused(true,At(32)); reader.SetComfortMode(false); reader.Tick(At(100));
        Check(reader.Current.Id == 2 && reader.IsPaused, "catch-up bypassed manual hold");
        return Task.CompletedTask;
    }

    private static async Task Replay(string path, string output)
    {
        var absolute = File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            var x = doc.RootElement;
            var created = DateTimeOffset.Parse(x.GetProperty("timestamp").GetString()!);
            var result = new TranslationResult(new TranslationSegment {
                Id = x.GetProperty("segment_id").GetInt64(), SourceRevision = x.GetProperty("source_revision").GetInt32(),
                UtteranceKey = x.GetProperty("utterance_key").GetString()!, FinalAsr = x.GetProperty("is_final").GetBoolean(),
                Text = x.GetProperty("raw_source_text").GetString()!, CreatedAt = created,
                AudioStartSample = x.GetProperty("audio_start_sample").GetInt64(), AudioEndSample = x.GetProperty("audio_end_sample").GetInt64(),
                AudioStreamId = x.GetProperty("audio_stream_id").GetString()!
            }, x.GetProperty("translated_text").GetString()!) {
                CorrectedSource = x.GetProperty("source_text").GetString(), Revision = x.GetProperty("revision").GetInt32(),
                IsError = x.GetProperty("is_error").GetBoolean(), IsPending = x.GetProperty("is_pending").GetBoolean()
            };
            return (at: created.AddMilliseconds(x.GetProperty("ready_ms").GetDouble()), result);
        }).OrderBy(e => e.at).ToArray();
        // This fixture has one final successful result per consecutive ID, so the
        // old scheduler is exactly a FIFO with its published text-based dwell.
        Check(absolute.Length == 98 && absolute.All(e => e.result.Segment.FinalAsr && !e.result.IsError && !e.result.IsPending) &&
            absolute.Select(e => e.result.Segment.Id).SequenceEqual(Enumerable.Range(1,98).Select(i => (long)i)), "baseline fixture changed; old FIFO comparison is no longer valid");
        var epoch = absolute[0].at;
        var events = absolute.Select(e => new Arrival((e.at-epoch).TotalSeconds,e.result)).ToArray();
        var pages = Run(events);
        double deadline = 0;
        var legacyDelays = new List<double>();
        foreach (var e in events)
        {
            double shown = Math.Ceiling((Math.Max(e.At, deadline)-1e-8)*5)/5;
            legacyDelays.Add(shown-e.At);
            deadline = shown + CaptionReader.ReadingTime(e.Result).TotalSeconds;
        }
        var delays = pages.SelectMany(p => p.Results.Select(r => p.At-events.Single(e => e.Result.Segment.Id == r.Segment.Id).At)).ToArray();
        var sorted = delays.OrderBy(d => d).ToArray();
        Check(sorted[^1] < 12 && sorted[(int)(sorted.Length*.95)] < 10, "recorded display queue still accumulates too much latency");
        Check(delays.Average() < legacyDelays.Average()*.5, "same-input replay did not materially reduce latency");
        object Stats(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return new { median_s = sorted[sorted.Length/2], p95_s = sorted[(int)(sorted.Length*.95)], max_s = sorted[^1] };
        }
        await File.WriteAllTextAsync(Path.Combine(output,"latency-replay.json"),JsonSerializer.Serialize(new {
            fixture = Path.GetFileName(path), segments = events.Length, pages = pages.Count, max_sentences_per_page = pages.Max(p => p.Results.Length),
            minimum_dwell_s = pages.Zip(pages.Skip(1),(a,b) => b.At-a.At).Min(),
            old_scheduler = Stats(legacyDelays), source_paced_pages = Stats(delays),
            note = "Offline 200ms presentation clock; identical recorded ASR/MT results. Additional display wait only, not end-to-end audio latency or human reading validation.",
            displays = pages.Select(p => new {at_s=p.At, ids=p.Results.Select(r => r.Segment.Id),hold_s=p.Hold,queue_wait_s=p.Wait})
        },new JsonSerializerOptions { WriteIndented = true }));
    }
}
