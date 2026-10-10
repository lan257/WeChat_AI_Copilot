using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// M3-owned read port for persisted memory/profile projections. It is deliberately
/// storage-agnostic; M3's context builder never opens or queries a database directly.
/// The returned scoped revision must change when an Active memory item, usable profile
/// snapshot, or profile progress value represented by this snapshot changes.
/// </summary>
public interface IMemoryContextReaderV1
{
    ValueTask<MemoryContextSnapshotV1> ReadAsync(
        MemoryContextReadRequestV1 request,
        CancellationToken cancellationToken = default);
}

public sealed record MemoryContextReadRequestV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string IdentityNamespace { get; init; } = string.Empty;
    public PerspectiveStateV1 PerspectiveState { get; init; }
    public long CalibrationVersion { get; init; }

    /// <summary>
    /// False means the reader may return UserEntered memories only; it must not load
    /// participant profiles or MessageExtracted records for this context.
    /// </summary>
    public bool MayReadPerspectiveBoundData => PerspectiveState == PerspectiveStateV1.Confirmed;
}

public sealed record MemoryContextSnapshotV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string Revision { get; init; } = string.Empty;
    public ProfileStatusV1 ProfileStatus { get; init; } = ProfileStatusV1.FirstConsolidation;
    /// <summary>
    /// Count of unprofiled text for this account/conversation/confirmed calibration, after
    /// CRLF/CR are normalized to LF and counted by Unicode code point without trimming.
    /// </summary>
    public int UnprofiledTextCharacters { get; init; }
    public string? ConsolidationSourceMessageCursor { get; init; }
    public IReadOnlyList<ParticipantProfileSnapshotV1> ParticipantProfiles { get; init; } = Array.Empty<ParticipantProfileSnapshotV1>();
    public IReadOnlyList<MemoryItemV1> MemoryItems { get; init; } = Array.Empty<MemoryItemV1>();
}
