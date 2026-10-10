using System.Text;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public enum MemoryConsolidationDecisionKindV1
{
    Ignore,
    RecordAccumulation,
    CompleteEmptyInitialWindow,
    GenerateInitial,
    GenerateIncremental,
    GenerateRebuild,
    DeferForGenerator,
    RebuildRequired
}

public sealed record MemoryConsolidationDecisionV1(
    MemoryConsolidationDecisionKindV1 Kind,
    MemoryConsolidationStateV1? NextState = null,
    ConsolidationCaptureV1? Capture = null,
    string? DetailCode = null);

/// <summary>
/// Pure M3 policy for confirmed-message windows. Successful generated batches alone advance
/// ConsolidationSourceMessageCursor; below-threshold appends only advance the observed cursor.
/// </summary>
public static class MemoryConsolidationWindowV1
{
    public const int FollowUpThresholdCodePoints = 5000;

    public static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    public static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    public static MemoryConsolidationDecisionV1 PlanPerspectiveConfirmed(
        ConsolidationCaptureV1 capture,
        bool generatorAvailable)
    {
        if (!IsSupportedCapture(capture))
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "invalid_confirmed_capture");
        }

        if (capture.EligibleTextCodePoints == 0)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.CompleteEmptyInitialWindow,
                CreateState(capture, completed: true, ProfileStatusV1.Accumulating),
                capture);
        }

        return generatorAvailable
            ? new MemoryConsolidationDecisionV1(MemoryConsolidationDecisionKindV1.GenerateInitial, Capture: capture)
            : new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.DeferForGenerator,
                DetailCode: "generator_unavailable");
    }

    public static MemoryConsolidationDecisionV1 PlanPerspectiveChanged(
        ConsolidationCaptureV1 capture,
        bool generatorAvailable)
    {
        if (!IsSupportedCapture(capture))
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "invalid_confirmed_capture");
        }

        if (capture.EligibleTextCodePoints == 0)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.CompleteEmptyInitialWindow,
                CreateState(capture, completed: true, ProfileStatusV1.Accumulating),
                capture,
                "empty_rebuild");
        }

        return generatorAvailable
            ? new MemoryConsolidationDecisionV1(MemoryConsolidationDecisionKindV1.GenerateRebuild, Capture: capture)
            : new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.DeferForGenerator,
                DetailCode: "generator_unavailable");
    }

    public static MemoryConsolidationDecisionV1 PlanMessageCreated(
        MemoryConsolidationStateV1 state,
        ConsolidationCaptureV1 newMessages,
        ConsolidationCaptureV1? fullPendingWindow,
        bool generatorAvailable)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(newMessages);
        if (!state.InitialConsolidationCompleted)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "initial_confirmation_not_completed");
        }

        if (state.ReprocessRequired)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "reprocess_already_required");
        }

        if (!IsSupportedCapture(newMessages)
            || !MatchesState(state, newMessages)
            || !string.Equals(state.ObservedMessageCursor, newMessages.RangeStartMessageCursor, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(newMessages.MessageSetRevision)
            || string.IsNullOrWhiteSpace(newMessages.HighWaterMessageCursor))
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "message_range_does_not_match_state");
        }

        var newUnprofiledCount = checked(state.UnprofiledTextCharacters + newMessages.EligibleTextCodePoints);
        var nextObserved = state with
        {
            MessageSetRevision = newMessages.MessageSetRevision,
            ObservedMessageCursor = newMessages.HighWaterMessageCursor,
            UnprofiledTextCharacters = newUnprofiledCount,
            Status = ProfileStatusV1.Accumulating,
            StateVersion = state.StateVersion + 1
        };

        if (newUnprofiledCount <= FollowUpThresholdCodePoints)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RecordAccumulation,
                nextObserved,
                newMessages);
        }

        if (!generatorAvailable)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.DeferForGenerator,
                DetailCode: "generator_unavailable");
        }

        if (fullPendingWindow is null
            || !IsSupportedCapture(fullPendingWindow)
            || !MatchesState(state, fullPendingWindow)
            || !string.Equals(state.ConsolidationSourceMessageCursor, fullPendingWindow.RangeStartMessageCursor, StringComparison.Ordinal)
            || !string.Equals(fullPendingWindow.MessageSetRevision, newMessages.MessageSetRevision, StringComparison.Ordinal)
            || !string.Equals(fullPendingWindow.HighWaterMessageCursor, newMessages.HighWaterMessageCursor, StringComparison.Ordinal)
            || fullPendingWindow.EligibleTextCodePoints != newUnprofiledCount
            || fullPendingWindow.EligibleTextCodePoints <= FollowUpThresholdCodePoints)
        {
            return new MemoryConsolidationDecisionV1(
                MemoryConsolidationDecisionKindV1.RebuildRequired,
                DetailCode: "pending_window_snapshot_mismatch");
        }

        return new MemoryConsolidationDecisionV1(
            MemoryConsolidationDecisionKindV1.GenerateIncremental,
            Capture: fullPendingWindow);
    }

    public static MemoryConsolidationStateV1 CompleteSuccessfulBatch(
        ConsolidationCaptureV1 capture,
        bool hasProfileOutput,
        long previousStateVersion = 0) => new()
    {
        AccountId = capture.AccountId,
        ConversationId = capture.ConversationId,
        IdentityNamespace = capture.IdentityNamespace,
        CalibrationVersion = capture.CalibrationVersion,
        MessageSetRevision = capture.MessageSetRevision,
        ConsolidationSourceMessageCursor = capture.HighWaterMessageCursor,
        ObservedMessageCursor = capture.HighWaterMessageCursor,
        UnprofiledTextCharacters = 0,
        InitialConsolidationCompleted = true,
        ReprocessRequired = false,
        Status = hasProfileOutput ? ProfileStatusV1.Current : ProfileStatusV1.Accumulating,
        StateVersion = previousStateVersion + 1
    };

    public static MemoryConsolidationStateV1 FailBatch(
        MemoryConsolidationStateV1 state) => state with
    {
        Status = ProfileStatusV1.Failed,
        StateVersion = state.StateVersion + 1
    };

    public static MemoryConsolidationStateV1 RequireReprocess(
        MemoryConsolidationStateV1 state) => state with
    {
        ReprocessRequired = true,
        Status = ProfileStatusV1.Rebuilding,
        StateVersion = state.StateVersion + 1
    };

    private static bool IsSupportedCapture(ConsolidationCaptureV1 capture) =>
        capture.AccountId != Guid.Empty
        && capture.ConversationId != Guid.Empty
        && string.Equals(capture.ConversationType, "Direct", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(capture.IdentityNamespace)
        && capture.CalibrationVersion > 0
        && !string.IsNullOrWhiteSpace(capture.MessageSetRevision);

    private static bool MatchesState(MemoryConsolidationStateV1 state, ConsolidationCaptureV1 capture) =>
        state.AccountId == capture.AccountId
        && state.ConversationId == capture.ConversationId
        && state.CalibrationVersion == capture.CalibrationVersion
        && string.Equals(state.IdentityNamespace, capture.IdentityNamespace, StringComparison.Ordinal);

    private static MemoryConsolidationStateV1 CreateState(
        ConsolidationCaptureV1 capture,
        bool completed,
        ProfileStatusV1 status) => new()
    {
        AccountId = capture.AccountId,
        ConversationId = capture.ConversationId,
        IdentityNamespace = capture.IdentityNamespace,
        CalibrationVersion = capture.CalibrationVersion,
        MessageSetRevision = capture.MessageSetRevision,
        ConsolidationSourceMessageCursor = capture.HighWaterMessageCursor,
        ObservedMessageCursor = capture.HighWaterMessageCursor,
        UnprofiledTextCharacters = 0,
        InitialConsolidationCompleted = completed,
        Status = status
    };
}
