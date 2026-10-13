using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

public sealed class AgentTaskApplicationServiceV1(
    IAgentRepositoryV1 repository,
    IAgentTaskCoordinatorV1 coordinator,
    IAgentPresetCatalogV1 presetCatalog,
    TimeProvider? timeProvider = null) : IAgentTaskApplicationServiceV1
{
    public const int MaximumConsultationUserInputLength = 4000;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<AuthorizedContextScopeV1?> ResolveAuthorizedScopeAsync(
        AgentWebConversationTargetV1 target,
        CancellationToken cancellationToken = default) =>
        repository.ResolveAuthorizedScopeAsync(target, cancellationToken);

    public async Task<SuggestionBatchV1> CreateSuggestionsAsync(
        AuthorizedContextScopeV1 scope,
        CreateAgentSuggestionsRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PersonaPresetId))
        {
            throw new ArgumentException("A persona preset ID is required.", nameof(request));
        }

        if (!presetCatalog.IsConfigured)
        {
            throw new AgentModuleNotReadyException(
                "AGENT_PRESETS_NOT_CONFIGURED", "No Agent persona presets are registered.");
        }

        if (!presetCatalog.Contains(request.PersonaPresetId))
        {
            throw new ArgumentException("The requested persona preset is not registered.", nameof(request));
        }

        var previousBatchId = request.PreviousBatchId?.ToString("N");
        if (request.PreviousBatchId is { } previousId
            && await repository.GetSuggestionBatchAsync(scope, previousId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new AgentResourceNotFoundException("suggestion batch");
        }

        var task = new AgentTaskV1
        {
            TaskType = AgentTaskTypeV1.ReplyCandidates,
            ExecutionProfileId = request.ExecutionProfileId,
            AccountId = scope.AccountId,
            ConversationId = scope.ConversationId,
            PersonaPresetId = request.PersonaPresetId,
            PreviousBatchId = previousBatchId,
            ContextRevision = null
        };

        var batch = await coordinator.CreateSuggestionsAsync(task, scope, cancellationToken).ConfigureAwait(false);
        try
        {
            await repository.StoreSuggestionBatchAsync(scope, batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryMarkPersistenceFailureAsync(scope, batch.RunId).ConfigureAwait(false);
            throw;
        }

        return batch;
    }

    public Task<SuggestionBatchV1?> GetSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        Guid batchId,
        CancellationToken cancellationToken = default) =>
        repository.GetSuggestionBatchAsync(scope, batchId, cancellationToken);

    public Task<AgentRunV1?> GetAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        CancellationToken cancellationToken = default) =>
        repository.GetAgentRunAsync(scope, runId, cancellationToken);

    public async Task<AgentConsultationThreadV1> CreateConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var thread = new AgentConsultationThreadV1
        {
            ThreadId = Guid.NewGuid().ToString("N"),
            AccountId = scope.AccountId,
            ConversationId = scope.ConversationId,
            Title = null,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };
        await repository.StoreConsultationThreadAsync(scope, thread, cancellationToken).ConfigureAwait(false);
        return thread;
    }

    public async Task<AgentConsultationHistoryV1?> GetConsultationHistoryAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var thread = await repository.GetConsultationThreadAsync(scope, threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return null;
        }

        var turns = await repository.GetConsultationTurnsAsync(scope, threadId, cancellationToken).ConfigureAwait(false);
        return new AgentConsultationHistoryV1(thread, turns);
    }

    public async Task<ConsultationTurnV1> CreateConsultationTurnAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        string userInput,
        CancellationToken cancellationToken = default,
        AgentExecutionProfileIdV1 executionProfileId = AgentExecutionProfileIdV1.Balanced)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(userInput))
        {
            throw new ArgumentException("A consultation question is required.", nameof(userInput));
        }

        if (userInput.Length > MaximumConsultationUserInputLength)
        {
            throw new ArgumentException(
                $"A consultation question cannot exceed {MaximumConsultationUserInputLength} characters.",
                nameof(userInput));
        }

        // Load the immutable scope binding before invoking M3 or the Agent.
        var thread = await repository.GetConsultationThreadAsync(scope, threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            throw new AgentResourceNotFoundException("consultation thread");
        }

        if (thread.AccountId != scope.AccountId || thread.ConversationId != scope.ConversationId)
        {
            throw new AgentResourceNotFoundException("consultation thread");
        }

        var task = new AgentTaskV1
        {
            TaskType = AgentTaskTypeV1.FreeConsultation,
            ExecutionProfileId = executionProfileId,
            AccountId = scope.AccountId,
            ConversationId = scope.ConversationId,
            ThreadId = thread.ThreadId,
            UserInput = userInput,
            ContextRevision = null
        };

        var turn = await coordinator.CreateConsultationTurnAsync(task, thread, scope, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await repository.StoreConsultationTurnAsync(scope, turn, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryMarkPersistenceFailureAsync(scope, turn.RunId).ConfigureAwait(false);
            throw;
        }

        return turn;
    }

    private async Task TryMarkPersistenceFailureAsync(AuthorizedContextScopeV1 scope, string? runId)
    {
        if (!Guid.TryParse(runId, out var parsedRunId))
        {
            return;
        }

        try
        {
            await repository.FinishAgentRunAsync(
                scope,
                parsedRunId,
                AgentRunStatusV1.Failed,
                "RESULT_PERSISTENCE_FAILED",
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original persistence failure; never turn it into a successful response.
        }
    }
}
