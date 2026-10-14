using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

[assembly: InternalsVisibleTo("WeChatAiCopilot.PlatformTests")]

namespace WeChatAiCopilot.Api.Platform.Outbox;

public sealed class ConversationChangeOutboxDispatcherV1
{
    private readonly IConversationChangeOutboxDeliveryStoreV1 _store;
    private readonly IMemoryConsolidationEventHandlerV1 _handler;
    private readonly ILogger<ConversationChangeOutboxDispatcherV1> _logger;
    private readonly OutboxDispatcherOptionsV1 _options;

    public ConversationChangeOutboxDispatcherV1(
        IConversationChangeOutboxDeliveryStoreV1 store,
        IMemoryConsolidationEventHandlerV1 handler,
        ILogger<ConversationChangeOutboxDispatcherV1> logger,
        OutboxDispatcherOptionsV1 options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken = default)
    {
        var claim = await _store.TryClaimNextAsync(_options.LeaseDuration, cancellationToken);
        if (claim is null)
        {
            return false;
        }

        try
        {
            return await DispatchClaimedAsync(claim, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseBestEffortAsync(claim);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async Task<bool> DispatchClaimedAsync(
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken)
    {
        MemoryConsolidationEventResultV1 result;
        try
        {
            result = await _handler.HandleAsync(claim.Event, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ScheduleRetryAsync(claim, "handler_exception", cancellationToken);
            return true;
        }

        if (result is null)
        {
            await ScheduleRetryAsync(claim, "handler_exception", cancellationToken);
            return true;
        }

        if (result.Outcome is MemoryConsolidationEventOutcomeV1.NoOp
            or MemoryConsolidationEventOutcomeV1.Accumulated
            or MemoryConsolidationEventOutcomeV1.Consolidated)
        {
            var completed = await _store.CompleteAsync(claim, cancellationToken);
            if (!completed)
            {
                _logger.LogInformation(
                    "Outbox event {EventId} ({EventType}) completion lost its lease; error code {ErrorCode}.",
                    claim.Event.EventId,
                    claim.EventType,
                    "lease_lost");
            }

            return true;
        }

        var errorCode = GetRetryErrorCode(result);
        await ScheduleRetryAsync(claim, errorCode, cancellationToken);
        return true;
    }

    private async Task ScheduleRetryAsync(
        OutboxDeliveryClaimV1 claim,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var capabilityGap = WaitingForCapability(errorCode);
        var delay = capabilityGap
            ? _options.CapabilityRetryDelay
            : OutboxRetryPolicyV1.GetDelay(claim.Attempts, _options.BaseRetryDelay, _options.MaximumRetryDelay);
        var scheduled = await _store.ScheduleRetryAsync(claim, errorCode, delay, cancellationToken);
        if (capabilityGap)
        {
            _logger.LogInformation(
                "Outbox event {EventId} ({EventType}) waits for a capability that is not configured; error code {ErrorCode}; next attempt after {Delay}.",
                claim.Event.EventId,
                claim.EventType,
                errorCode,
                delay);
            return;
        }

        _logger.LogWarning(
            "Outbox event {EventId} ({EventType}) retry scheduling result is {ScheduleResult}; error code {ErrorCode}.",
            claim.Event.EventId,
            claim.EventType,
            scheduled ? "scheduled" : "lease_lost",
            errorCode);
    }

    private static bool WaitingForCapability(string errorCode) =>
        errorCode.Equals("generator_unavailable", StringComparison.OrdinalIgnoreCase)
        || errorCode.Equals("generator_unavailable_reprocess_queued", StringComparison.OrdinalIgnoreCase);

    private async Task ReleaseLeaseBestEffortAsync(OutboxDeliveryClaimV1 claim)
    {
        try
        {
            await _store.ReleaseLeaseAsync(claim, CancellationToken.None);
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Outbox event {EventId} ({EventType}) lease release failed; error code {ErrorCode}.",
                claim.Event.EventId,
                claim.EventType,
                "lease_release_failed");
        }
    }

    private static string GetRetryErrorCode(MemoryConsolidationEventResultV1 result)
    {
        if (result.Outcome is not (MemoryConsolidationEventOutcomeV1.Deferred
            or MemoryConsolidationEventOutcomeV1.RebuildRequired))
        {
            return "unsupported_outcome";
        }

        var detailCode = result.DetailCode;
        if (string.IsNullOrWhiteSpace(detailCode)
            || detailCode.Length > 96
            || detailCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '_' and not '-'))
        {
            return result.Outcome == MemoryConsolidationEventOutcomeV1.Deferred
                ? "deferred"
                : "rebuild_required";
        }

        return detailCode.ToLowerInvariant();
    }

    private static void ValidateOptions(OutboxDispatcherOptionsV1 options)
    {
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.LeaseDuration));
        }

        if (options.BaseRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BaseRetryDelay));
        }

        if (options.MaximumRetryDelay < options.BaseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumRetryDelay));
        }

        if (options.CapabilityRetryDelay < options.MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CapabilityRetryDelay));
        }
    }
}

internal static class OutboxRetryPolicyV1
{
    internal static TimeSpan GetDelay(int attempts, TimeSpan baseDelay, TimeSpan maximumDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay));
        }

        if (maximumDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        var ticks = baseDelay.Ticks;
        var remainingDoublings = Math.Max(0, attempts - 1);
        while (remainingDoublings > 0 && ticks < maximumDelay.Ticks)
        {
            ticks = ticks > maximumDelay.Ticks / 2
                ? maximumDelay.Ticks
                : ticks * 2;
            remainingDoublings--;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }
}
