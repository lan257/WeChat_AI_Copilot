using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.Api.Modules.Memory.Contracts;

public enum MemoryConsolidationTriggerKindV1
{
    MessageCreated,
    PerspectiveConfirmed,
    PerspectiveChanged
}

public enum MemoryConsolidationBatchKindV1
{
    Initial,
    Incremental,
    Rebuild
}

public enum MemoryConsolidationEventOutcomeV1
{
    NoOp,
    Accumulated,
    Consolidated,
    Deferred,
    RebuildRequired
}

/// <summary>
/// A trusted M2 outbox envelope. It is an internal module contract, never a Web request.
/// </summary>
public sealed record MemoryConsolidationOutboxEventV1
{
    public Guid EventId { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public MemoryConsolidationTriggerKindV1 Kind { get; init; }
    public string? MessageCursor { get; init; }
    public long? CalibrationVersion { get; init; }
}

public sealed record MemoryConsolidationEventResultV1(
    MemoryConsolidationEventOutcomeV1 Outcome,
    string? DetailCode = null);

/// <summary>
/// M3's model boundary. Implementations must return only claims supported by the supplied
/// confirmed, scoped messages; this project deliberately provides no fake implementation.
/// </summary>
public interface IMemoryConsolidationGeneratorV1
{
    ValueTask<MemoryConsolidationGenerationOutputV1> GenerateAsync(
        MemoryConsolidationGenerationRequestV1 request,
        CancellationToken cancellationToken = default);
}

public sealed record MemoryConsolidationGenerationRequestV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string IdentityNamespace { get; init; } = string.Empty;
    public long CalibrationVersion { get; init; }
    public string? HighWaterMessageCursor { get; init; }
    public MemoryConsolidationBatchKindV1 BatchKind { get; init; }
    public IReadOnlyList<MemoryConsolidationInputMessageV1> Messages { get; init; } = Array.Empty<MemoryConsolidationInputMessageV1>();
}

/// <summary>
/// A persisted Direct/Text message whose perspective and direction have already been
/// confirmed. Direction is retained so the generator can distinguish account and contact
/// writing-style evidence.
/// </summary>
public sealed record MemoryConsolidationInputMessageV1(
    Guid MessageId,
    string Text,
    DateTimeOffset? OccurredAt,
    MessageDirectionV1 Direction);

public sealed record MemoryConsolidationGenerationOutputV1
{
    public IReadOnlyList<ParticipantProfileDraftV1> ParticipantProfiles { get; init; } = Array.Empty<ParticipantProfileDraftV1>();
    public IReadOnlyList<MessageExtractedMemoryDraftV1> MemoryItems { get; init; } = Array.Empty<MessageExtractedMemoryDraftV1>();
}

public sealed record ParticipantProfileDraftV1
{
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<Guid> EvidenceMessageIds { get; init; } = Array.Empty<Guid>();
}

public sealed record MessageExtractedMemoryDraftV1
{
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public string? Text { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<Guid> EvidenceMessageIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// Entry point for a future M2 outbox consumer. Returning Deferred/RebuildRequired means
/// the consumer must retain or requeue the original outbox item.
/// </summary>
public interface IMemoryConsolidationEventHandlerV1
{
    ValueTask<MemoryConsolidationEventResultV1> HandleAsync(
        MemoryConsolidationOutboxEventV1 message,
        CancellationToken cancellationToken = default);
}

public sealed record MemoryConsolidationStateV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string IdentityNamespace { get; init; } = string.Empty;
    public long CalibrationVersion { get; init; }
    public string MessageSetRevision { get; init; } = string.Empty;
    public string? ConsolidationSourceMessageCursor { get; init; }
    public string? ObservedMessageCursor { get; init; }
    public int UnprofiledTextCharacters { get; init; }
    public bool InitialConsolidationCompleted { get; init; }
    public bool ReprocessRequired { get; init; }
    public string? ReprocessRequiredRevision { get; init; }
    public ProfileStatusV1 Status { get; init; } = ProfileStatusV1.WaitingForConfirmation;
    public long StateVersion { get; init; }
    public Guid? ActiveBatchId { get; init; }
    public DateTimeOffset? ActiveLeaseUntil { get; init; }
}
