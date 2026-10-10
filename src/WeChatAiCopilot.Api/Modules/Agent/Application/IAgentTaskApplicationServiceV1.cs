using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

public interface IAgentTaskApplicationServiceV1
{
    Task<AuthorizedContextScopeV1?> ResolveAuthorizedScopeAsync(
        AgentWebConversationTargetV1 target,
        CancellationToken cancellationToken = default);

    Task<SuggestionBatchV1> CreateSuggestionsAsync(
        AuthorizedContextScopeV1 scope,
        CreateAgentSuggestionsRequestV1 request,
        CancellationToken cancellationToken = default);

    Task<SuggestionBatchV1?> GetSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<AgentRunV1?> GetAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<AgentConsultationThreadV1> CreateConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        CancellationToken cancellationToken = default);

    Task<AgentConsultationHistoryV1?> GetConsultationHistoryAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task<ConsultationTurnV1> CreateConsultationTurnAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        string userInput,
        CancellationToken cancellationToken = default);
}

public sealed record AgentWebConversationTargetV1(
    string Provider,
    string AccountExternalId,
    string ConversationExternalId);

public sealed record CreateAgentSuggestionsRequestV1
{
    public string? PersonaPresetId { get; init; }
    public Guid? PreviousBatchId { get; init; }
}

public sealed record AgentConsultationHistoryV1(
    AgentConsultationThreadV1 Thread,
    IReadOnlyList<ConsultationTurnV1> Turns);

public sealed class AgentResourceNotFoundException(string resourceName)
    : Exception($"The requested {resourceName} is not available in the authorized conversation.");

public sealed class AgentModuleNotReadyException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
