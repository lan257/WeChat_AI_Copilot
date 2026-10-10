using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

/// <summary>
/// M4 persistence boundary for D07 suggestion batches and D09 consultation data.
/// Implementations must scope every read and write by both account and conversation.
/// </summary>
public interface IAgentRepositoryV1
{
    Task<AuthorizedContextScopeV1?> ResolveAuthorizedScopeAsync(
        AgentWebConversationTargetV1 target,
        CancellationToken cancellationToken = default);

    Task StoreSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        SuggestionBatchV1 batch,
        CancellationToken cancellationToken = default);

    Task<SuggestionBatchV1?> GetSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task StoreConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        AgentConsultationThreadV1 thread,
        CancellationToken cancellationToken = default);

    Task<AgentConsultationThreadV1?> GetConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task StoreConsultationTurnAsync(
        AuthorizedContextScopeV1 scope,
        ConsultationTurnV1 turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsultationTurnV1>> GetConsultationTurnsAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsultationTurnV1>> GetRecentConsultationTurnsAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        int limit,
        CancellationToken cancellationToken = default);

    Task StartAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        AgentRunV1 run,
        CancellationToken cancellationToken = default);

    Task UpdateAgentRunRuntimeNameAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        string runtimeName,
        CancellationToken cancellationToken = default);

    Task FinishAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        AgentRunStatusV1 status,
        string errorCode,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AgentRunV1?> GetAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        CancellationToken cancellationToken = default);
}
