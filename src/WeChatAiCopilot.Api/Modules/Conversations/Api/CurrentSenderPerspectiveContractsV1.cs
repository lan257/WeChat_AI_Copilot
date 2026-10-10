using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

/// <summary>
/// Read model for the configured Web conversation. Sender identities remain separated
/// by the full M2 calibration scope so candidates from different source shards are not
/// presented as if they were comparable.
/// </summary>
public sealed record CurrentSenderPerspectiveResponseV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public IReadOnlyList<SenderPerspectiveScopeViewV1> Scopes { get; init; } =
        Array.Empty<SenderPerspectiveScopeViewV1>();
}

public sealed record SenderPerspectiveScopeViewV1
{
    public string? IdentityNamespace { get; init; }
    public string? MessageTable { get; init; }
    public string? ShardFingerprint { get; init; }
    public long CalibrationVersion { get; init; }
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public PerspectiveEvidenceV1? Evidence { get; init; }
    public IReadOnlyList<string> CandidateRawSenderIds { get; init; } = Array.Empty<string>();
    public string? DefaultSelfRawSenderId { get; init; }
    public string? EffectiveSelfRawSenderId { get; init; }
    public string? ContactRawSenderId { get; init; }
    public bool CanConfirm { get; init; }
}
