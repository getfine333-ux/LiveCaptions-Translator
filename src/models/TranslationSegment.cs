namespace LiveCaptionsTranslator.models;

public sealed record TranslationSegment
{
    public long Id { get; init; }
    public long WorkSequence { get; init; }
    public string UtteranceKey { get; init; } = "";
    public int SourceRevision { get; init; }
    public bool IsIncomplete { get; init; }
    public string EndReason { get; init; } = "";
    public string Text { get; init; } = "";
    public string Context { get; init; } = "";
    public string? TranslationSource { get; init; }
    public string TerminologyHints { get; init; } = "";
    public string? RequestGlossary { get; init; }
    public SourceTermEdit[] TermCorrections { get; init; } = Array.Empty<SourceTermEdit>();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FinalAsr { get; init; }
    public bool LogOnly { get; init; }
    public string SourceLang { get; init; } = "";
    public string TargetLang { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public long? AudioStartSample { get; init; }
    public long? AudioEndSample { get; init; }
    public string AudioStreamId { get; init; } = "";
    public bool ContinuesPrevious { get; init; }
    public bool ForcedEnd { get; init; }
    public double AsrMs { get; init; }
    public double AsrQueueMs { get; init; }
    public DateTimeOffset? SourceEndedAt { get; init; }
}

public sealed record TranslationResult(TranslationSegment Segment, string Text)
{
    public string? CorrectedSource { get; init; }
    public bool IsError { get; init; }
    public bool IsPending { get; init; }
    public bool CacheHit { get; init; }
    public string? FinishReason { get; init; }
    public int Revision { get; init; }
    public double QueueMs { get; init; }
    public double RequestMs { get; init; }
    public double? SourceToReadyMs { get; init; }
}
