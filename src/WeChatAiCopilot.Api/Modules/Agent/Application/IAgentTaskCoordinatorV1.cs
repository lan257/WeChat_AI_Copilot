using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

/// <summary>
/// M4-owned boundary used by the task application service. The HTTP and
/// persistence layers do not depend directly on M3's context service port.
/// </summary>
public interface IAgentTaskCoordinatorV1
{
    Task<SuggestionBatchV1> CreateSuggestionsAsync(
        AgentTaskV1 task,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default);

    Task<ConsultationTurnV1> CreateConsultationTurnAsync(
        AgentTaskV1 task,
        AgentConsultationThreadV1 boundThread,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default);
}
