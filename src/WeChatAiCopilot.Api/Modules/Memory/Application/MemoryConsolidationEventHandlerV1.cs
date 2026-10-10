using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// M3 event coordinator to be called by a future M2 outbox consumer. It deliberately has
/// no HTTP route, timer, or default generator; failed/deferred events remain caller-owned.
/// </summary>
public sealed class MemoryConsolidationEventHandlerV1(
    ConsolidationMessageCollectorV1 collector,
    IConsolidationStateRepositoryV1 stateRepository,
    IMemoryConsolidationGeneratorV1? generator = null) : IMemoryConsolidationEventHandlerV1
{
    public async ValueTask<MemoryConsolidationEventResultV1> HandleAsync(
        MemoryConsolidationOutboxEventV1 message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.EventId == Guid.Empty
            || message.AccountId == Guid.Empty
            || message.ConversationId == Guid.Empty)
        {
            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "invalid_outbox_scope");
        }

        var scope = await stateRepository.ResolveScopeAsync(
            message.AccountId,
            message.ConversationId,
            cancellationToken);
        if (scope is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.NoOp, "conversation_unavailable");
        }

        if (await stateRepository.IsEventProcessedAsync(scope, message.EventId, cancellationToken))
        {
            return Result(MemoryConsolidationEventOutcomeV1.NoOp, "event_already_processed");
        }

        var headResult = await collector.ReadHeadAsync(
            message.AccountId,
            message.ConversationId,
            expectedCalibrationVersion: null,
            cancellationToken);
        if (!headResult.Succeeded)
        {
            return MapCaptureFailure(headResult.FailureCode);
        }

        var head = headResult.Page!;
        if (head.PerspectiveState != PerspectiveStateV1.Confirmed)
        {
            return Result(MemoryConsolidationEventOutcomeV1.NoOp, "perspective_not_confirmed");
        }

        if (message.CalibrationVersion is { } eventVersion
            && eventVersion != head.CalibrationVersion)
        {
            return eventVersion < head.CalibrationVersion
                ? Result(MemoryConsolidationEventOutcomeV1.NoOp, "calibration_version_stale")
                : Result(MemoryConsolidationEventOutcomeV1.Deferred, "calibration_version_changed");
        }

        var identityNamespace = head.IdentityNamespace;
        var state = await stateRepository.GetStateAsync(
            message.AccountId,
            message.ConversationId,
            identityNamespace,
            head.CalibrationVersion,
            cancellationToken);

        return message.Kind switch
        {
            MemoryConsolidationTriggerKindV1.PerspectiveConfirmed => await HandlePerspectiveConfirmedAsync(
                message, scope, identityNamespace, head.CalibrationVersion, state, cancellationToken),
            MemoryConsolidationTriggerKindV1.PerspectiveChanged => await HandlePerspectiveChangedAsync(
                message, scope, identityNamespace, head.CalibrationVersion, state, cancellationToken),
            MemoryConsolidationTriggerKindV1.MessageCreated => await HandleMessageCreatedAsync(
                message, scope, identityNamespace, head.CalibrationVersion, state, cancellationToken),
            _ => Result(MemoryConsolidationEventOutcomeV1.NoOp, "unsupported_event")
        };
    }

    private async ValueTask<MemoryConsolidationEventResultV1> HandlePerspectiveConfirmedAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        MemoryConsolidationStateV1? state,
        CancellationToken cancellationToken)
    {
        var captureResult = await collector.CaptureAllAsync(
            message.AccountId,
            message.ConversationId,
            calibrationVersion,
            identityNamespace,
            cancellationToken);
        if (!captureResult.Succeeded)
        {
            return MapCaptureFailure(captureResult.FailureCode);
        }

        var capture = captureResult.Capture!;
        var decision = MemoryConsolidationWindowV1.PlanPerspectiveConfirmed(capture, generator is not null);
        if (decision.Kind == MemoryConsolidationDecisionKindV1.CompleteEmptyInitialWindow)
        {
            if (state is not null)
            {
                return await CommitEmptyCaptureAsync(
                    message,
                    scope,
                    state,
                    capture,
                    MemoryConsolidationBatchKindV1.Rebuild,
                    cancellationToken);
            }

            await stateRepository.RecordEmptyInitialWindowAsync(decision.NextState!, message.EventId, 0, cancellationToken);
            return Result(MemoryConsolidationEventOutcomeV1.Accumulated, "empty_initial_window");
        }

        if (decision.Kind == MemoryConsolidationDecisionKindV1.DeferForGenerator)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, decision.DetailCode);
        }

        return await GenerateAndCommitAsync(
            message,
            scope,
            state,
            capture,
            MemoryConsolidationBatchKindV1.Initial,
            cancellationToken);
    }

    private async ValueTask<MemoryConsolidationEventResultV1> HandlePerspectiveChangedAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        MemoryConsolidationStateV1? state,
        CancellationToken cancellationToken)
    {
        var captureResult = await collector.CaptureAllAsync(
            message.AccountId,
            message.ConversationId,
            calibrationVersion,
            identityNamespace,
            cancellationToken);
        if (!captureResult.Succeeded)
        {
            return MapCaptureFailure(captureResult.FailureCode);
        }

        var capture = captureResult.Capture!;
        if (capture.EligibleTextCodePoints == 0)
        {
            if (state is not null)
            {
                return await CommitEmptyCaptureAsync(
                    message,
                    scope,
                    state,
                    capture,
                    MemoryConsolidationBatchKindV1.Rebuild,
                    cancellationToken);
            }

            var empty = MemoryConsolidationWindowV1.PlanPerspectiveChanged(capture, generatorAvailable: false);
            await stateRepository.RecordEmptyInitialWindowAsync(empty.NextState!, message.EventId, 0, cancellationToken);
            return Result(MemoryConsolidationEventOutcomeV1.Accumulated, "empty_rebuild_baseline");
        }

        if (generator is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "generator_unavailable");
        }

        return await GenerateAndCommitAsync(
            message,
            scope,
            state,
            capture,
            MemoryConsolidationBatchKindV1.Rebuild,
            cancellationToken);
    }

    private async ValueTask<MemoryConsolidationEventResultV1> HandleMessageCreatedAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        MemoryConsolidationStateV1? state,
        CancellationToken cancellationToken)
    {
        if (state is null || !state.InitialConsolidationCompleted)
        {
            // M2's message.created event may be earlier in FIFO order than the
            // perspective-confirmed event. Once the query snapshot is Confirmed,
            // bootstrap the initial window here so the earlier event cannot block it.
            return await HandlePerspectiveConfirmedAsync(
                message,
                scope,
                identityNamespace,
                calibrationVersion,
                state,
                cancellationToken);
        }

        if (state.ReprocessRequired)
        {
            return await RebuildAsync(message, scope, state, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(message.MessageCursor))
        {
            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "message_cursor_missing");
        }

        var newMessagesResult = await collector.CaptureAfterCursorThroughEventAsync(
            message.AccountId,
            message.ConversationId,
            state.ObservedMessageCursor,
            message.MessageCursor,
            calibrationVersion,
            identityNamespace,
            cancellationToken);
        if (!newMessagesResult.Succeeded)
        {
            if (newMessagesResult.FailureCode is ConsolidationCaptureFailureCodeV1.CursorNotInNewerRange
                or ConsolidationCaptureFailureCodeV1.AnchorUnavailable)
            {
                return await RebuildAsync(message, scope, state, cancellationToken);
            }

            return MapCaptureFailure(newMessagesResult.FailureCode);
        }

        var newMessages = newMessagesResult.Capture!;
        var nextCount = checked(state.UnprofiledTextCharacters + newMessages.EligibleTextCodePoints);
        if (nextCount <= MemoryConsolidationWindowV1.FollowUpThresholdCodePoints)
        {
            var accumulation = MemoryConsolidationWindowV1.PlanMessageCreated(
                state,
                newMessages,
                fullPendingWindow: null,
                generatorAvailable: generator is not null);
            if (accumulation.Kind != MemoryConsolidationDecisionKindV1.RecordAccumulation)
            {
                return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, accumulation.DetailCode);
            }

            await stateRepository.RecordAccumulationAsync(
                accumulation.NextState!,
                message.EventId,
                state.StateVersion,
                cancellationToken);
            return Result(MemoryConsolidationEventOutcomeV1.Accumulated);
        }

        if (generator is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "generator_unavailable");
        }

        var fullPendingResult = await collector.CaptureAfterCursorThroughEventAsync(
            message.AccountId,
            message.ConversationId,
            state.ConsolidationSourceMessageCursor,
            message.MessageCursor,
            calibrationVersion,
            identityNamespace,
            cancellationToken);
        if (!fullPendingResult.Succeeded)
        {
            if (fullPendingResult.FailureCode is ConsolidationCaptureFailureCodeV1.CursorNotInNewerRange
                or ConsolidationCaptureFailureCodeV1.AnchorUnavailable)
            {
                return await RebuildAsync(message, scope, state, cancellationToken);
            }

            return MapCaptureFailure(fullPendingResult.FailureCode);
        }

        var pendingWindow = fullPendingResult.Capture!;
        var decision = MemoryConsolidationWindowV1.PlanMessageCreated(
            state,
            newMessages,
            pendingWindow,
            generatorAvailable: true);
        if (decision.Kind != MemoryConsolidationDecisionKindV1.GenerateIncremental)
        {
            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, decision.DetailCode);
        }

        return await GenerateAndCommitAsync(
            message,
            scope,
            state,
            decision.Capture!,
            MemoryConsolidationBatchKindV1.Incremental,
            cancellationToken);
    }

    private async ValueTask<MemoryConsolidationEventResultV1> RebuildAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        MemoryConsolidationStateV1 state,
        CancellationToken cancellationToken)
    {
        var captureResult = await collector.CaptureAllAsync(
            message.AccountId,
            message.ConversationId,
            state.CalibrationVersion,
            state.IdentityNamespace,
            cancellationToken);
        if (!captureResult.Succeeded)
        {
            return MapCaptureFailure(captureResult.FailureCode);
        }

        var capture = captureResult.Capture!;
        if (capture.EligibleTextCodePoints == 0)
        {
            if (capture.HighWaterMessageCursor is null)
            {
                return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "empty_rebuild_has_no_high_water");
            }

            return await CommitEmptyCaptureAsync(
                message,
                scope,
                state,
                capture,
                MemoryConsolidationBatchKindV1.Rebuild,
                cancellationToken);
        }

        if (generator is null)
        {
            if (!state.ReprocessRequired
                || !string.Equals(
                    state.ReprocessRequiredRevision,
                    capture.MessageSetRevision,
                    StringComparison.Ordinal))
            {
                await stateRepository.MarkReprocessRequiredAsync(
                    state.AccountId,
                    state.ConversationId,
                    state.IdentityNamespace,
                    state.CalibrationVersion,
                    state.StateVersion,
                    capture.MessageSetRevision,
                    cancellationToken);
            }

            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "generator_unavailable_reprocess_queued");
        }

        return await GenerateAndCommitAsync(
            message,
            scope,
            state,
            capture,
            MemoryConsolidationBatchKindV1.Rebuild,
            cancellationToken);
    }

    private async ValueTask<MemoryConsolidationEventResultV1> CommitEmptyCaptureAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        MemoryConsolidationStateV1? state,
        ConsolidationCaptureV1 capture,
        MemoryConsolidationBatchKindV1 batchKind,
        CancellationToken cancellationToken)
    {
        if (capture.HighWaterMessageCursor is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "empty_window_requires_baseline");
        }

        var output = new MemoryConsolidationGenerationOutputV1();
        var claim = await stateRepository.TryBeginBatchAsync(
            scope,
            capture.IdentityNamespace,
            capture.CalibrationVersion,
            capture.MessageSetRevision,
            capture.HighWaterMessageCursor,
            batchKind,
            Guid.NewGuid(),
            message.EventId,
            state?.StateVersion ?? 0,
            cancellationToken);
        if (claim.Outcome is ConsolidationBatchClaimOutcomeV1.EventAlreadyProcessed
            or ConsolidationBatchClaimOutcomeV1.BatchAlreadyCommitted)
        {
            return Result(MemoryConsolidationEventOutcomeV1.NoOp, claim.Outcome.ToString());
        }

        if (claim.Outcome != ConsolidationBatchClaimOutcomeV1.Claimed || claim.State is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, claim.Outcome.ToString());
        }

        var nextState = MemoryConsolidationWindowV1.CompleteSuccessfulBatch(
            capture,
            hasProfileOutput: false,
            claim.State.StateVersion);
        await stateRepository.CommitGeneratedBatchAsync(new MemoryConsolidationBatchCommitV1
        {
            BatchId = claim.State.ActiveBatchId!.Value,
            EventId = message.EventId,
            Scope = scope,
            ExpectedStateVersion = claim.State.StateVersion,
            BatchKind = batchKind,
            Capture = capture,
            Output = output,
            NextState = nextState
        }, cancellationToken);
        return Result(MemoryConsolidationEventOutcomeV1.Consolidated, "empty_output_batch");
    }

    private async ValueTask<MemoryConsolidationEventResultV1> GenerateAndCommitAsync(
        MemoryConsolidationOutboxEventV1 message,
        MemoryConversationScopeV1 scope,
        MemoryConsolidationStateV1? state,
        ConsolidationCaptureV1 capture,
        MemoryConsolidationBatchKindV1 batchKind,
        CancellationToken cancellationToken)
    {
        if (generator is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "generator_unavailable");
        }

        if (capture.EligibleTextCodePoints == 0)
        {
            return await CommitEmptyCaptureAsync(message, scope, state, capture, batchKind, cancellationToken);
        }

        if (capture.HighWaterMessageCursor is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, "high_water_cursor_missing");
        }

        var claim = await stateRepository.TryBeginBatchAsync(
            scope,
            capture.IdentityNamespace,
            capture.CalibrationVersion,
            capture.MessageSetRevision,
            capture.HighWaterMessageCursor,
            batchKind,
            Guid.NewGuid(),
            message.EventId,
            state?.StateVersion ?? 0,
            cancellationToken);
        if (claim.Outcome is ConsolidationBatchClaimOutcomeV1.EventAlreadyProcessed
            or ConsolidationBatchClaimOutcomeV1.BatchAlreadyCommitted)
        {
            return Result(MemoryConsolidationEventOutcomeV1.NoOp, claim.Outcome.ToString());
        }

        if (claim.Outcome != ConsolidationBatchClaimOutcomeV1.Claimed || claim.State?.ActiveBatchId is null)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, claim.Outcome.ToString());
        }

        MemoryConsolidationGenerationOutputV1 output;
        try
        {
            output = await generator.GenerateAsync(new MemoryConsolidationGenerationRequestV1
            {
                AccountId = capture.AccountId,
                ConversationId = capture.ConversationId,
                IdentityNamespace = capture.IdentityNamespace,
                CalibrationVersion = capture.CalibrationVersion,
                HighWaterMessageCursor = capture.HighWaterMessageCursor,
                BatchKind = batchKind,
                Messages = capture.EligibleMessages
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryMarkFailedAsync(claim.State, capture, CancellationToken.None, bestEffort: true);
            throw;
        }
        catch (Exception)
        {
            await TryMarkFailedAsync(claim.State, capture, cancellationToken);
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "generator_failed");
        }

        var violations = MemoryConsolidationOutputValidatorV1.Validate(output, capture);
        if (violations.Count > 0)
        {
            await TryMarkFailedAsync(claim.State, capture, cancellationToken);
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "generator_output_invalid");
        }

        var nextState = MemoryConsolidationWindowV1.CompleteSuccessfulBatch(
            capture,
            output.ParticipantProfiles.Count > 0,
            claim.State.StateVersion);
        try
        {
            await stateRepository.CommitGeneratedBatchAsync(new MemoryConsolidationBatchCommitV1
            {
                BatchId = claim.State.ActiveBatchId.Value,
                EventId = message.EventId,
                Scope = scope,
                ExpectedStateVersion = claim.State.StateVersion,
                BatchKind = batchKind,
                Capture = capture,
                Output = output,
                NextState = nextState
            }, cancellationToken);
        }
        catch (MemoryConsolidationStateConflictException)
        {
            return Result(MemoryConsolidationEventOutcomeV1.Deferred, "state_changed_during_generation");
        }

        return Result(MemoryConsolidationEventOutcomeV1.Consolidated);
    }

    private async ValueTask TryMarkFailedAsync(
        MemoryConsolidationStateV1 claimedState,
        ConsolidationCaptureV1 capture,
        CancellationToken cancellationToken,
        bool bestEffort = false)
    {
        if (claimedState.ActiveBatchId is null)
        {
            return;
        }

        try
        {
            await stateRepository.MarkBatchFailedAsync(
                claimedState.AccountId,
                claimedState.ConversationId,
                capture.IdentityNamespace,
                capture.CalibrationVersion,
                claimedState.ActiveBatchId.Value,
                claimedState.StateVersion,
                cancellationToken);
        }
        catch (MemoryConsolidationStateConflictException)
        {
            // A newer calibration/batch already owns this row; never overwrite its cursor.
        }
        catch (Exception) when (bestEffort)
        {
            // Cancellation cleanup is best effort. If storage is unavailable, the lease expires.
        }
    }

    private static MemoryConsolidationEventResultV1 MapCaptureFailure(ConsolidationCaptureFailureCodeV1 failureCode) =>
        failureCode switch
        {
            ConsolidationCaptureFailureCodeV1.PerspectiveNotConfirmed
                or ConsolidationCaptureFailureCodeV1.UnsupportedConversationType =>
                Result(MemoryConsolidationEventOutcomeV1.NoOp, failureCode.ToString()),
            _ => Result(MemoryConsolidationEventOutcomeV1.RebuildRequired, failureCode.ToString())
        };

    private static MemoryConsolidationEventResultV1 Result(
        MemoryConsolidationEventOutcomeV1 outcome,
        string? detailCode = null) => new(outcome, detailCode);
}
