using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.speech;
using NAudio.Wave;
using SherpaOnnx;

internal static class SourceLatencyChecks
{
    public static IEnumerable<(string name,Func<Task> test)> All(string root,string output)
    {
        yield return ("Confirmed full sentences publish before the long-clause length target", EarlySentence);
        yield return ("A corrected opening does not starve a stable boundary or accept changed numbers", CorrectedOpening);
        yield return ("Source timestamps account for acoustic lookahead rather than starting at translation", SourceClock);
        yield return ("Late pages retain a stable previous page while catching up at the protected dwell", RetainedPage);
        if (TestMode.IncludeLocalFixtures && File.Exists(Path.Combine(root,"artifacts","missing-sentences-before","session.jsonl")))
            yield return ("Current 96-segment session keeps every displayed result while reducing total-age queueing",()=>DisplayReplay(root,output));
        if (TestMode.IncludeLocalFixtures && File.Exists(Path.Combine(root,"bin","PacedReading","LiveCaptionsTranslator.dll")))
            yield return ("Old and new pipelines replay the same continuous Chinese audio with source-time latency", () => NativeComparison(root,output));
    }
    private static void Check(bool ok,string message) {if(!ok)throw new Exception(message);}
    private static readonly DateTimeOffset Epoch = new(2026,9,17,0,0,0,TimeSpan.Zero);
    private static AsrSnapshot Timed(string text,float step=.15f) => new(text,text.Select(c=>c.ToString()).ToArray(),Enumerable.Range(0,text.Length).Select(i=>i*step).ToArray());
    private static Task EarlySentence()
    {
        const string source="我们已经完成这一步。下面继续说明另外的问题";
        var a=Timed(source).Align(0,64000)!;
        var boundary=SemanticBoundary.Find(a,0,source.Length,"",0,64000,128000,a,true);
        Check(boundary?.End==source.IndexOf('。')+1,"a confirmed whole sentence still waits for 8s");
        var edge=Timed(source[..(source.IndexOf('。')+1)]).Align(0,64000)!;
        Check(SemanticBoundary.Find(a,0,source.Length,"",0,64000,128000,edge,true)==null,"an edge-appended period was treated as confirmed");
        Check(SemanticBoundary.Find(a,0,source.Length,"",0,64000,128000,edge,true,(_,_)=>false)==null,"absence of acoustic evidence was ignored");
        Check(SemanticBoundary.Find(a,0,source.Length,"",0,64000,128000,edge,true,(_,_)=>true)!=null,"a stable full sentence with real pause evidence still waited for a third observation");
        return Task.CompletedTask;
    }
    private static Task CorrectedOpening()
    {
        const string before="这里己经完成了这一阶段的分析与验证。接下来继续检查另外的结果";
        string after=before.Replace("己经","已经");
        var old=Timed(before).Align(0,160000)!;var current=Timed(after).Align(0,160000)!;
        int agreed=SemanticBoundary.StableLength(before,after);
        Check(agreed==2 && SemanticBoundary.Find(current,0,agreed,"",0,160000,128000,old,true)!=null,"a corrected first word blocked all later confirmed boundaries");
        const string digits="模型第21层提供了很多可以用来研究内部机理的重要特征。下面再看其他部分";
        var prior=Timed(digits).Align(0,160000)!;var changed=Timed(digits.Replace("21","22")).Align(0,160000)!;
        Check(SemanticBoundary.Find(changed,0,SemanticBoundary.StableLength(prior.Text,changed.Text),"",0,160000,128000,prior,true)==null,"unstable earlier numbers were ignored by boundary confirmation");
        return Task.CompletedTask;
    }
    private static Task SourceClock()
    {
        var results=new List<RecognizedSpeech>();
        var engine=new UtteranceRecognizer(_=>"这是一句已经说完的完整话。","clock");engine.Ready+=results.Add;
        engine.Accept(new(0,new float[64000],Epoch.AddSeconds(5)) {ObservedThroughSample=80000});
        Check(results.Single().SourceEndedAt==Epoch.AddSeconds(4),"VAD silence/lookahead was excluded from source age");
        return Task.CompletedTask;
    }
    private static Task RetainedPage()
    {
        TranslationResult R(long id)=>new(new TranslationSegment {Id=id,SourceRevision=1,FinalAsr=true,UtteranceKey=$"timed:{id}",Text=$"这是第{id}句完整话。",AudioStreamId="timed",AudioStartSample=(id-1)*128000,AudioEndSample=id*128000},"A complete translated sentence.") {SourceToReadyMs=9000};
        var reader=new CaptionReader();reader.Accept(R(1),TimeSpan.Zero);var first=reader.Current;
        reader.Accept(R(2),TimeSpan.FromSeconds(1));reader.Tick(TimeSpan.FromSeconds(3.99));
        Check(ReferenceEquals(first,reader.Current),"late text flashed through the current slot");
        reader.Tick(TimeSpan.FromSeconds(4));
        Check(reader.Current!.Id==2 && ReferenceEquals(first,reader.Previous) && reader.SourceToShowSeconds==12,"source age was hidden or previous page was discarded");
        var second=reader.Current;reader.Accept(R(3),TimeSpan.FromSeconds(4.1));
        Check(ReferenceEquals(second,reader.Current) && ReferenceEquals(first,reader.Previous),"arrival changed one of the reading slots");
        reader.SetPaused(true,TimeSpan.FromSeconds(5));reader.Tick(TimeSpan.FromSeconds(100));
        Check(ReferenceEquals(second,reader.Current),"catch-up ignored explicit reading pause");
        return Task.CompletedTask;
    }
    private sealed record Emission(string Text,long Start,long End,double ObservedSeconds,string Reason)
    { public double ConfirmationSeconds=>Math.Max(0,ObservedSeconds-End/16000d); }
    private static async Task NativeComparison(string root,string output)
    {
        string model=Path.Combine(root,"models","sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
        using var wave=new WaveFileReader(Path.Combine(model,"test_wavs","zh.wav"));
        var bytes=new byte[wave.Length];wave.ReadExactly(bytes);
        var sentence=Enumerable.Range(0,bytes.Length/2).Select(i=>BitConverter.ToInt16(bytes,i*2)/32768f).ToArray();
        var samples=Enumerable.Range(0,7).SelectMany(_=>sentence).Concat(new float[64000]).ToArray();
        var config=new OfflineRecognizerConfig();config.FeatConfig.SampleRate=16000;config.FeatConfig.FeatureDim=80;
        config.ModelConfig.NumThreads=8;config.ModelConfig.Provider="cpu";config.ModelConfig.SenseVoice.Model=Path.Combine(model,"model.onnx");
        config.ModelConfig.SenseVoice.Language="zh";config.ModelConfig.SenseVoice.UseInverseTextNormalization=1;config.ModelConfig.Tokens=Path.Combine(model,"tokens.txt");
        using var decoder=new OfflineRecognizer(config);
        AsrSnapshot Decode(float[] audio)
        {
            using var stream=decoder.CreateStream();stream.AcceptWaveform(16000,audio);decoder.Decode(stream);var r=stream.Result;
            return new(r.Text.Trim(),r.Tokens,r.Timestamps);
        }
        List<Emission> Run(bool legacy)
        {
            var events=new List<AudioInputEvent>();
            using(var vad=new BoundedVadSegmenter(Path.Combine(root,"models","silero_vad.onnx"),1.5,8,legacy?null:2))
            {vad.InputReady+=events.Add;vad.Accept(samples);vad.Finish();}
            var emitted=new List<Emission>();double observed=0;
            object? oldEngine=null;Type? oldType=null;Type? oldAudio=null;
            var engine=new UtteranceRecognizer(Decode,"comparison");
            engine.Ready+=r=>emitted.Add(new(r.Text,r.StartSample,r.EndSample,observed,r.EndReason));
            if(legacy)
            {
                var asm=Assembly.LoadFile(Path.Combine(root,"bin","PacedReading","LiveCaptionsTranslator.dll"));
                oldType=asm.GetType("LiveCaptionsTranslator.speech.UtteranceRecognizer")!;
                oldAudio=asm.GetType("LiveCaptionsTranslator.speech.AudioSegment")!;
                var snap=asm.GetType("LiveCaptionsTranslator.speech.AsrSnapshot")!;
                Func<float[],object> adapter=audio=>{var r=Decode(audio);return Activator.CreateInstance(snap,r.Text,r.Tokens,r.Timestamps)!;};
                var input=Expression.Parameter(typeof(float[]));var factoryType=typeof(Func<,>).MakeGenericType(typeof(float[]),snap);
                var factory=Expression.Lambda(factoryType,Expression.Convert(Expression.Invoke(Expression.Constant(adapter),input),snap),input).Compile();
                oldEngine=Activator.CreateInstance(oldType,factory,"legacy",480000,128000)!;
                var ready=oldType.GetEvent("Ready")!;var arg=Expression.Parameter(ready.EventHandlerType!.GenericTypeArguments[0]);
                Action<object> save=r=>{var t=r.GetType();emitted.Add(new((string)t.GetProperty("Text")!.GetValue(r)!,
                    (long)t.GetProperty("StartSample")!.GetValue(r)!,(long)t.GetProperty("EndSample")!.GetValue(r)!,observed,(string)t.GetProperty("EndReason")!.GetValue(r)!));};
                ready.AddEventHandler(oldEngine,Expression.Lambda(ready.EventHandlerType,Expression.Invoke(Expression.Constant(save),Expression.Convert(arg,typeof(object))),arg).Compile());
            }
            foreach(var e in events)
            {
                observed=(e.Audio?.ObservedThroughSample??e.SilenceEnd)/16000d;
                if(e.Audio is { } a)
                {
                    if(legacy)
                    {
                        var audio=Activator.CreateInstance(oldAudio!,a.StartSample,a.Samples,Epoch.AddSeconds(observed))!;
                        oldAudio!.GetProperty("ForcedEnd")!.SetValue(audio,a.ForcedEnd);oldAudio.GetProperty("ContinuesPrevious")!.SetValue(audio,a.ContinuesPrevious);
                        oldType!.GetMethod("Accept")!.Invoke(oldEngine,new[]{audio});
                    }
                    else engine.Accept(a with {QueuedAt=Epoch.AddSeconds(observed)});
                }
                else if(legacy) oldType!.GetMethod("ObserveSilence")!.Invoke(oldEngine,new object[]{e.SilenceStart,e.SilenceEnd});
                else engine.ObserveSilence(e.SilenceStart,e.SilenceEnd);
            }
            if(legacy)oldType!.GetMethod("Finish")!.Invoke(oldEngine,new object[]{"input_end",true});else engine.Finish();
            string joined=string.Concat(emitted.Select(e=>e.Text));
            Check(joined.Count(c=>c=='9')==7 && joined.Count(c=>c=='5')==7,"native replay lost or duplicated repeated numeric content");
            return emitted;
        }
        var before=Run(true);var after=Run(false);
        object Stats(List<Emission> units)
        {var lags=units.Select(x=>x.ConfirmationSeconds).OrderBy(x=>x).ToArray();return new{units=units.Count,first_emit_audio_s=units[0].ObservedSeconds,median_confirmation_s=lags[lags.Length/2],p95_confirmation_s=lags[(int)(lags.Length*.95)],max_confirmation_s=lags[^1],mean_confirmation_s=lags.Average()};}
        await File.WriteAllTextAsync(Path.Combine(output,"source-latency-comparison.json"),JsonSerializer.Serialize(new{
            note="Same 43.144s local Chinese recording, repeated to exercise continuous speech. Confirmation latency on the audio clock; excludes inference, network translation and presentation. Not a web-video end-to-end measurement.",
            before=Stats(before),after=Stats(after),before_units=before,after_units=after},new JsonSerializerOptions{WriteIndented=true}));
        Check(after[0].ObservedSeconds<=before[0].ObservedSeconds-1.5 && after.Average(x=>x.ConfirmationSeconds)<before.Average(x=>x.ConfirmationSeconds)*.8,"same-audio recognition confirmation did not materially improve");
    }

    private static async Task DisplayReplay(string root,string output)
    {
        string directory=Path.Combine(root,"artifacts","missing-sentences-before");
        var confirmation=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"confirmation-delay.json")));
        var sourceDelays=confirmation.RootElement.EnumerateArray().ToDictionary(x=>x.GetProperty("id").GetInt64(),x=>x.GetProperty("confirmation_lower_bound_ms").GetDouble());
        var rows=File.ReadLines(Path.Combine(directory,"session.jsonl")).Where(l=>!string.IsNullOrWhiteSpace(l)).Select(line=>
        {
            using var doc=JsonDocument.Parse(line);var x=doc.RootElement;
            var created=DateTimeOffset.Parse(x.GetProperty("timestamp").GetString()!);
            double ready=x.GetProperty("ready_ms").GetDouble();long id=x.GetProperty("segment_id").GetInt64();
            var result=new TranslationResult(new TranslationSegment {Id=id,SourceRevision=x.GetProperty("source_revision").GetInt32(),
                UtteranceKey=x.GetProperty("utterance_key").GetString()!,FinalAsr=x.GetProperty("is_final").GetBoolean(),
                Text=x.GetProperty("raw_source_text").GetString()!,CreatedAt=created,AudioStreamId=x.GetProperty("audio_stream_id").GetString()!,
                AudioStartSample=x.GetProperty("audio_start_sample").GetInt64(),AudioEndSample=x.GetProperty("audio_end_sample").GetInt64()
            },x.GetProperty("translated_text").GetString()!) {CorrectedSource=x.GetProperty("source_text").GetString(),
                SourceToReadyMs=sourceDelays[id]+ready};
            return (at:created.AddMilliseconds(ready),result);
        }).OrderBy(x=>x.at).ToArray();
        Check(rows.Length==96 && rows.Select(x=>x.result.Segment.Id).SequenceEqual(Enumerable.Range(1,96).Select(i=>(long)i)),"recorded display fixture changed");
        var epoch=rows[0].at;
        List<(long Id,double Queue,double Total)> Run(bool legacy)
        {
            var records=new List<(long,double,double)>();var current=new CaptionReader();
            var asm=Assembly.LoadFile(Path.Combine(root,"bin","PacedReading","LiveCaptionsTranslator.dll"));
            var oldType=asm.GetType("LiveCaptionsTranslator.models.CaptionReader")!;
            var resultType=asm.GetType("LiveCaptionsTranslator.models.TranslationResult")!;
            object old=Activator.CreateInstance(oldType,200)!;
            var legacyResults=rows.Select(x=>JsonSerializer.Deserialize(JsonSerializer.Serialize(x.result),resultType)!).ToArray();
            int cursor=0;long version=0;
            for(int tick=0;tick<=(rows[^1].at-epoch).TotalSeconds*5+36000;tick++)
            {
                var now=TimeSpan.FromSeconds(tick/5d);
                while(cursor<rows.Length && rows[cursor].at-epoch<=now)
                {
                    if(legacy)oldType.GetMethod("Accept")!.Invoke(old,new[]{legacyResults[cursor],(object)now});else current.Accept(rows[cursor].result,now);
                    cursor++;
                }
                if(legacy)oldType.GetMethod("Tick")!.Invoke(old,new object[]{now});else current.Tick(now);
                long next=legacy?(long)oldType.GetProperty("PresentationVersion")!.GetValue(old)!:current.PresentationVersion;
                if(next!=version)
                {
                    version=next;
                    IEnumerable<long> ids;
                    if(legacy)
                    {
                        var card=oldType.GetProperty("Current")!.GetValue(old)!;
                        var results=(System.Collections.IEnumerable)card.GetType().GetProperty("Results")!.GetValue(card)!;
                        ids=results.Cast<object>().Select(r=>{var s=r.GetType().GetProperty("Segment")!.GetValue(r)!;return(long)s.GetType().GetProperty("Id")!.GetValue(s)!;}).ToArray();
                    }
                    else ids=current.Current!.Results.Select(r=>r.Segment.Id).ToArray();
                    foreach(long id in ids)
                    {var input=rows.Single(r=>r.result.Segment.Id==id);double wait=now.TotalSeconds-(input.at-epoch).TotalSeconds;records.Add((id,wait,wait+input.result.SourceToReadyMs!.Value/1000));}
                }
                int pending=legacy?(int)oldType.GetProperty("PendingCount")!.GetValue(old)!:current.PendingCount;
                if(cursor==rows.Length && pending==0)break;
            }
            Check(records.Select(r=>r.Item1).SequenceEqual(rows.Select(r=>r.result.Segment.Id)),"display replay lost, duplicated or reordered a recorded result");
            return records;
        }
        var before=Run(true);var after=Run(false);
        object Stats(IEnumerable<double> samples)
        {var a=samples.OrderBy(x=>x).ToArray();return new{median_s=a[a.Length/2],p95_s=a[(int)(a.Length*.95)],max_s=a[^1]};}
        await File.WriteAllTextAsync(Path.Combine(output,"current-session-latency.json"),JsonSerializer.Serialize(new{
            segments=rows.Length,note="Fixed recorded ASR/MT results; both readers use a 200ms clock. Source age estimated from ASR sample frontiers is a lower bound; this is not a new web-video end-to-end measurement.",
            before=new{display_queue=Stats(before.Select(r=>r.Queue)),estimated_total=Stats(before.Select(r=>r.Total))},
            after=new{display_queue=Stats(after.Select(r=>r.Queue)),estimated_total=Stats(after.Select(r=>r.Total))}
        },new JsonSerializerOptions{WriteIndented=true}));
        Check(after.Max(r=>r.Queue)<before.Max(r=>r.Queue)*.7 && after.Average(r=>r.Queue)<before.Average(r=>r.Queue),"real-session queueing was not materially reduced");
    }
}
