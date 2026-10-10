using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public enum ConsolidationBatchClaimOutcomeV1
{
    Claimed,
    EventAlreadyProcessed,
    BatchAlreadyCommitted,
    Busy,
    StateConflict
}

public sealed record ConsolidationBatchClaimResultV1(
    ConsolidationBatchClaimOutcomeV1 Outcome,
    MemoryConsolidationStateV1? State = null);

public sealed record MemoryConsolidationBatchCommitV1
{
    public Guid BatchId { get; init; }
    public Guid EventId { get; init; }
    public MemoryConversationScopeV1 Scope { get; init; } = new(Guid.Empty, Guid.Empty, Guid.Empty);
    public long ExpectedStateVersion { get; init; }
    public MemoryConsolidationBatchKindV1 BatchKind { get; init; }
    public ConsolidationCaptureV1 Capture { get; init; } = new();
    public MemoryConsolidationGenerationOutputV1 Output { get; init; } = new();
    public MemoryConsolidationStateV1 NextState { get; init; } = new();
}

/// <summary>
/// Persistence boundary for the confirmed-perspective consolidation state machine.
/// CommitGeneratedBatchAsync must commit profiles, MessageExtracted items, their evidence,
/// idempotency row, event receipt, and state cursor in one PostgreSQL transaction.
/// </summary>
public interface IConsolidationStateRepositoryV1
{
    ValueTask<MemoryConversationScopeV1?> ResolveScopeAsync(
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    ValueTask<MemoryConsolidationStateV1?> GetStateAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsEventProcessedAsync(
        MemoryConversationScopeV1 scope,
        Guid eventId,
        CancellationToken cancellationToken = default);

    ValueTask RecordAccumulationAsync(
        MemoryConsolidationStateV1 nextState,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default);

    ValueTask RecordEmptyInitialWindowAsync(
        MemoryConsolidationStateV1 nextState,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default);

    ValueTask<ConsolidationBatchClaimResultV1> TryBeginBatchAsync(
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        string messageSetRevision,
        string highWaterMessageCursor,
        MemoryConsolidationBatchKindV1 batchKind,
        Guid batchId,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default);

    ValueTask MarkReprocessRequiredAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        long expectedStateVersion,
        string observedMessageSetRevision,
        CancellationToken cancellationToken = default);

    ValueTask MarkBatchFailedAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        Guid batchId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default);

    ValueTask CommitGeneratedBatchAsync(
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken = default);
}
