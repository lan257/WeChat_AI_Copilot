using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Platform.Outbox;

public sealed record OutboxDeliveryClaimV1(
    MemoryConsolidationOutboxEventV1 Event,
    string EventType,
    Guid LeaseId,
    int Attempts);

public interface IConversationChangeOutboxDeliveryStoreV1
{
    ValueTask<OutboxDeliveryClaimV1?> TryClaimNextAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CompleteAsync(
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ScheduleRetryAsync(
        OutboxDeliveryClaimV1 claim,
        string errorCode,
        TimeSpan delay,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ReleaseLeaseAsync(
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken = default);
}

public sealed record OutboxDispatcherOptionsV1
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    // A missing optional capability (for example the memory consolidation runtime being switched off)
    // will not heal itself in five minutes, so those events wait longer instead of burning a claim,
    // a handler call and a warning once per backoff cap.
    public TimeSpan CapabilityRetryDelay { get; init; } = TimeSpan.FromMinutes(30);
}
