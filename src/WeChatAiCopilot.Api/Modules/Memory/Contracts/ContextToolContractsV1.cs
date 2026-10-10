using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Contracts;

public sealed record ContextToolRequestV1
{
    public int RecentMessageLimit { get; init; } = 30;
    public Guid? AnchorMessageId { get; init; }
    public bool IncludeMemory { get; init; } = true;
}

/// <summary>
/// Tool-facing projection. Authorization scope and participant subject IDs are omitted.
/// </summary>
public sealed record ContextToolContextResultV1
{
    public string ContextRevision { get; init; } = string.Empty;
    /// <summary>Stable M2 message-set revision for this context; pass it to later search/evidence calls.</summary>
    public string MessageSetRevision { get; init; } = string.Empty;
    public PerspectiveStateV1 PerspectiveState { get; init; }
    public long CalibrationVersion { get; init; }
    public ProfileStatusV1 ProfileStatus { get; init; }
    public int UnprofiledTextCharacters { get; init; }
    public int ConsolidationThresholdCharacters { get; init; }
    public IReadOnlyList<ContextMessageV1> RecentMessages { get; init; } = Array.Empty<ContextMessageV1>();
    public IReadOnlyList<ContextToolParticipantProfileV1> ParticipantProfiles { get; init; } = Array.Empty<ContextToolParticipantProfileV1>();
    public IReadOnlyList<ContextToolMemoryItemV1> MemoryItems { get; init; } = Array.Empty<ContextToolMemoryItemV1>();
    public IReadOnlyList<ContextOmissionV1> Omissions { get; init; } = Array.Empty<ContextOmissionV1>();
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record ContextToolParticipantProfileV1
{
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public long ProfileRevision { get; init; }
    public long CalibrationVersion { get; init; }
    public ProfileStatusV1 Status { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<Guid> EvidenceMessageIds { get; init; } = Array.Empty<Guid>();
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record ContextToolMemoryItemV1
{
    public Guid MemoryId { get; init; }
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Category { get; init; }
    public MemoryOriginV1 Origin { get; init; }
    public MemoryItemStatusV1 Status { get; init; }
    public IReadOnlyList<Guid> SourceMessageIds { get; init; } = Array.Empty<Guid>();
    public long? CalibrationVersion { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Search is restricted to the bound session. An anchor is inclusive; set ExcludeAnchor
/// when continuing from a prior result's NextAnchorMessageId.
/// </summary>
public sealed record HistorySearchRequestV1
{
    public string Query { get; init; } = string.Empty;
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Limit { get; init; } = 10;
    public Guid? AnchorMessageId { get; init; }
    public bool ExcludeAnchor { get; init; }
    public string? ExpectedMessageSetRevision { get; init; }
}

public enum HistorySearchStatusV1
{
    Complete,
    Truncated,
    RevisionChanged,
    AnchorNotFound
}

public sealed record HistorySearchHitV1
{
    public ContextMessageV1 Message { get; init; } = new();
    public bool IsTextTruncated { get; init; }
}

/// <summary>
/// Hits are ordered newest to oldest. RevisionChanged always has an empty Hits list.
/// A truncated continuation must reuse MessageSetRevision as ExpectedMessageSetRevision
/// and pass NextAnchorMessageId with NextPageExcludesAnchor. RevisionChanged requires
/// the caller to rebuild context before continuing.
/// </summary>
public sealed record HistorySearchResultV1
{
    public HistorySearchStatusV1 Status { get; init; }
    public string? MessageSetRevision { get; init; }
    public string? ObservedMessageSetRevision { get; init; }
    public PerspectiveStateV1? PerspectiveState { get; init; }
    public PerspectiveStateV1? ObservedPerspectiveState { get; init; }
    public long? CalibrationVersion { get; init; }
    public long? ObservedCalibrationVersion { get; init; }
    public MessageHistoryCoverageV1 HistoryCoverage { get; init; } = MessageHistoryCoverageV1.Unknown;
    public IReadOnlyList<HistorySearchHitV1> Hits { get; init; } = Array.Empty<HistorySearchHitV1>();
    public int PagesRead { get; init; }
    public int MessagesScanned { get; init; }
    public bool SearchTextTruncated { get; init; }
    public bool HasMoreMessages { get; init; }
    public Guid? NextAnchorMessageId { get; init; }
    public bool NextPageExcludesAnchor { get; init; }
}

public sealed record MessageEvidenceRequestV1
{
    public IReadOnlyList<Guid> MessageIds { get; init; } = Array.Empty<Guid>();
    public string? ExpectedMessageSetRevision { get; init; }
}

public enum MessageEvidenceStatusV1
{
    Complete,
    Partial,
    Truncated,
    RevisionChanged
}

public sealed record MessageEvidenceItemV1
{
    public ContextMessageV1 Message { get; init; } = new();
    public bool IsTextTruncated { get; init; }
}

/// <summary>
/// OmittedCount combines invalid, unavailable/out-of-scope, and beyond-limit IDs. Those
/// IDs are never echoed in the result.
/// </summary>
public sealed record MessageEvidenceResultV1
{
    public MessageEvidenceStatusV1 Status { get; init; }
    public string? MessageSetRevision { get; init; }
    public string? ObservedMessageSetRevision { get; init; }
    public PerspectiveStateV1? PerspectiveState { get; init; }
    public PerspectiveStateV1? ObservedPerspectiveState { get; init; }
    public long? CalibrationVersion { get; init; }
    public long? ObservedCalibrationVersion { get; init; }
    public IReadOnlyList<MessageEvidenceItemV1> Evidence { get; init; } = Array.Empty<MessageEvidenceItemV1>();
    public int OmittedCount { get; init; }
}
