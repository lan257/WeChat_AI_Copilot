namespace WeChatAiCopilot.Api.Contracts.V1;

public enum PerspectiveStateV1
{
    Unresolved,
    PendingConfirmation,
    Confirmed
}

public enum PerspectiveEvidenceV1
{
    DefaultSmallSenderId,
    UserConfirmed
}

public sealed record ConversationSnapshotV1
{
    public Guid ConversationId { get; init; }
    public Guid AccountId { get; init; }
    public string? DisplayName { get; init; }
    public string ConversationType { get; init; } = "Direct";
    public string? LastMessageCursor { get; init; }
    public string MessageSetRevision { get; init; } = string.Empty;
    public long CalibrationVersion { get; init; }
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public string? EffectiveSelfRawSenderId { get; init; }
    public PerspectiveEvidenceV1? PerspectiveEvidence { get; init; }
    public string CaptureState { get; init; } = "Inactive";
}

public sealed record SetWeChatAccountPerspectiveRequestV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? IdentityNamespace { get; init; }
    public string? AccountSelfRawSenderId { get; init; }
    public long ExpectedCalibrationVersion { get; init; }
}

public sealed record SenderMappingV1
{
    public string? AccountSelfRawSenderId { get; init; }
    public string? ContactRawSenderId { get; init; }
}

public sealed record SetWeChatAccountPerspectiveResultV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? IdentityNamespace { get; init; }
    public long CalibrationVersion { get; init; }
    public SenderMappingV1 Mapping { get; init; } = new();
    public PerspectiveEvidenceV1 Evidence { get; init; } = PerspectiveEvidenceV1.UserConfirmed;
    public long AffectedMessageCount { get; init; }
}

public sealed record SenderCalibrationViewV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? IdentityNamespace { get; init; }
    public long CalibrationVersion { get; init; }
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public PerspectiveEvidenceV1? Evidence { get; init; }
    public IReadOnlyList<string> CandidateRawSenderIds { get; init; } = Array.Empty<string>();
    public string? DefaultSelfRawSenderId { get; init; }
    public string? EffectiveSelfRawSenderId { get; init; }
    public string? ContactRawSenderId { get; init; }
    public bool CanConfirm { get; init; }
}
