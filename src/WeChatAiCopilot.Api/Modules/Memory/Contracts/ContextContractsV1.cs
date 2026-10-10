using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.Api.Modules.Memory.Contracts;

public enum ParticipantSubjectTypeV1
{
    WechatAccount,
    Contact
}

public enum ProfileStatusV1
{
    WaitingForConfirmation,
    FirstConsolidation,
    Accumulating,
    Consolidating,
    Rebuilding,
    Current,
    Failed
}

public enum MemoryOriginV1
{
    UserEntered,
    MessageExtracted
}

public enum MemoryItemStatusV1
{
    Active,
    Superseded,
    Deleted,
    NeedsReview
}

public enum ContextOmissionCodeV1
{
    NoRecentMessages,
    AnchorMessageUnavailable,
    RecentMessagesTruncated,
    RecentMessagesOmitted,
    RecentMessageTextTruncated,
    HistoryCoverageUnknown,
    HistoryCoveragePartial,
    PerspectiveNotConfirmed,
    PerspectiveBoundDataUnavailable,
    MemoryItemsTruncated,
    MemoryItemsOmitted,
    MemoryItemTextTruncated,
    MemoryEvidenceTruncated,
    ProfileSummaryTruncated,
    ProfileEvidenceTruncated,
    ParticipantProfilesOmitted
}

public sealed record ContextRequestV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string RequestPurpose { get; init; } = "ReplyCandidates";
    public int RecentMessageLimit { get; init; } = 30;
    public Guid? AnchorMessageId { get; init; }
    public bool IncludeMemory { get; init; } = true;
}

/// <summary>
/// An authorization decision made outside M3 (normally by M6 or an M4 task binding).
/// M3 requires the request scope to match this trusted scope before querying any data.
/// </summary>
public sealed record AuthorizedContextScopeV1(Guid AccountId, Guid ConversationId);

public sealed record ContextMessageV1
{
    public Guid MessageId { get; init; }
    public string? Text { get; init; }
    public string MessageType { get; init; } = "Text";
    public DateTimeOffset? OccurredAt { get; init; }
    public MessageDirectionV1 Direction { get; init; } = MessageDirectionV1.Unknown;
    public DirectionEvidenceV1 DirectionEvidence { get; init; } = DirectionEvidenceV1.Unresolved;
}

public sealed record ParticipantProfileSnapshotV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? IdentityNamespace { get; init; }
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public Guid SubjectId { get; init; }
    public long ProfileRevision { get; init; }
    public long CalibrationVersion { get; init; }
    public ProfileStatusV1 Status { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? SourceMessageCursor { get; init; }
    public IReadOnlyList<Guid> EvidenceMessageIds { get; init; } = Array.Empty<Guid>();
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record MemoryItemV1
{
    public Guid MemoryId { get; init; }
    public Guid AccountId { get; init; }
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public Guid SubjectId { get; init; }
    public Guid? ConversationId { get; init; }
    public string? IdentityNamespace { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Category { get; init; }
    public MemoryOriginV1 Origin { get; init; }
    public MemoryItemStatusV1 Status { get; init; } = MemoryItemStatusV1.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<Guid> SourceMessageIds { get; init; } = Array.Empty<Guid>();
    public long? CalibrationVersion { get; init; }
    public string? SourceMessageCursor { get; init; }
    public long Version { get; init; }
}

public sealed record ContextOmissionV1(ContextOmissionCodeV1 Code);

public sealed record ContextBundleV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string ContextRevision { get; init; } = string.Empty;
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public long CalibrationVersion { get; init; }
    public ProfileStatusV1 ProfileStatus { get; init; } = ProfileStatusV1.WaitingForConfirmation;
    public int UnprofiledTextCharacters { get; init; }
    public int ConsolidationThresholdCharacters { get; init; } = 5000;
    public string? ConsolidationSourceMessageCursor { get; init; }
    public IReadOnlyList<ContextMessageV1> RecentMessages { get; init; } = Array.Empty<ContextMessageV1>();
    public IReadOnlyList<ParticipantProfileSnapshotV1> ParticipantProfiles { get; init; } = Array.Empty<ParticipantProfileSnapshotV1>();
    public IReadOnlyList<MemoryItemV1> MemoryItems { get; init; } = Array.Empty<MemoryItemV1>();
    public IReadOnlyList<ContextOmissionV1> Omissions { get; init; } = Array.Empty<ContextOmissionV1>();
    public DateTimeOffset GeneratedAt { get; init; }
}
