using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Messages.Contracts;

/// <summary>
/// M2-owned, read-only query contract consumed by M3. The contract is colocated under
/// Modules/Memory only for the isolated M3 implementation slice; the message store and
/// query implementation remain owned by M2. Implementations must enforce the requested
/// account/conversation scope and must not expose database access to M3.
/// </summary>
public interface IMessageQueryV1
{
    ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
        MessageQueryRequestV1 request,
        CancellationToken cancellationToken = default);
}

public sealed record MessageQueryRequestV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public int Limit { get; init; } = 30;
    public Guid? AnchorMessageId { get; init; }
}

/// <summary>
/// M2's scoped query result. Messages must be in chronological ascending order and
/// restricted to the requested conversation. MessageSetRevision must change when
/// messages are added, removed, or backfilled, including backfills before the latest row.
/// PerspectiveState and CalibrationVersion must describe the same effective snapshot as
/// the returned message directions.
/// </summary>
public sealed record MessageQuerySnapshotV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? ConversationType { get; init; }
    public string? IdentityNamespace { get; init; }
    public string MessageSetRevision { get; init; } = string.Empty;
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public long CalibrationVersion { get; init; }
    public MessageHistoryCoverageV1 HistoryCoverage { get; init; } = MessageHistoryCoverageV1.Unknown;
    public bool HasMoreMessages { get; init; }
    public bool AnchorMessageFound { get; init; }
    public IReadOnlyList<MessageQueryItemV1> Messages { get; init; } = Array.Empty<MessageQueryItemV1>();
}

public enum MessageHistoryCoverageV1
{
    Unknown,
    Partial,
    Complete
}

public sealed record MessageQueryItemV1
{
    public Guid MessageId { get; init; }
    public string? Text { get; init; }
    public string MessageType { get; init; } = "Text";
    public DateTimeOffset? OccurredAt { get; init; }
    public MessageDirectionV1 Direction { get; init; } = MessageDirectionV1.Unknown;
    public DirectionEvidenceV1 DirectionEvidence { get; init; } = DirectionEvidenceV1.Unresolved;
}
