namespace WeChatAiCopilot.Api.Contracts.V1;

public enum SourceKindV1
{
    Realtime,
    HistoryBackfill
}

public enum MessageDirectionV1
{
    Incoming,
    Outgoing,
    Unknown
}

public enum DirectionEvidenceV1
{
    Unresolved,
    DefaultSmallSenderId,
    UserConfirmed,
    SourceConfirmed
}

public enum MatchStatusV1
{
    Matched,
    Created,
    NeedsReview
}

public enum HistoryJobStatusV1
{
    Pending,
    Running,
    Completed,
    Partial,
    Failed,
    Cancelled
}

public sealed record SourceIdentityV1
{
    public string? LocalId { get; init; }
    public string? SortSeq { get; init; }
    public string? ServerId { get; init; }
    public string? Shard { get; init; }
    public string? ShardFingerprint { get; init; }
    public string? MessageTable { get; init; }
}

public sealed record RawMessageEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string? Provider { get; init; }
    public SourceKindV1 SourceKind { get; init; } = SourceKindV1.Realtime;
    public string? AccountExternalId { get; init; }
    public string? AccountWxid { get; init; }
    public string? ConversationExternalId { get; init; }
    public string? ConversationType { get; init; }
    public string? MessageType { get; init; }
    public string? Text { get; init; }
    public DateTimeOffset? SourceOccurredAt { get; init; }
    public string? RawSenderId { get; init; }
    public string? SenderWxid { get; init; }
    public string? IdentityNamespace { get; init; }
    public SourceIdentityV1? SourceIdentity { get; init; }
    public string? ExternalMessageId { get; init; }
    public MessageDirectionV1 Direction { get; init; } = MessageDirectionV1.Unknown;
    public DirectionEvidenceV1 DirectionEvidence { get; init; } = DirectionEvidenceV1.Unresolved;
}

public sealed record RequestedRangeV1
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
}

public sealed record HistoryBatchV1
{
    public int SchemaVersion { get; init; } = 1;
    public string? JobId { get; init; }
    public string? AccountExternalId { get; init; }
    public string? ConversationExternalId { get; init; }
    public RequestedRangeV1? RequestedRange { get; init; }
    public IReadOnlyList<RawMessageEventV1>? Items { get; init; }
    public bool IsLastBatch { get; init; }
}

public sealed record IngestReceiptV1
{
    public MatchStatusV1 MatchStatus { get; init; }
    public Guid? MessageId { get; init; }
    public string? MessageCursor { get; init; }
    public bool Created { get; init; }
    public string? ReviewCode { get; init; }
}

public sealed record HistoryBatchReceiptV1
{
    public string JobId { get; init; } = string.Empty;
    public int Read { get; init; }
    public int Matched { get; init; }
    public int Created { get; init; }
    public int NeedsReview { get; init; }
    public int IgnoredUnsupported { get; init; }
    public int Failed { get; init; }
    public HistoryJobStatusV1 Status { get; init; }
    public bool IsLastBatch { get; init; }
}
