using WeChatAiCopilot.Api.Modules.Agent.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Runtime;

public interface IAgentRuntime
{
    Task<AgentRuntimeResultV1> ExecuteAsync(
        ScopedAgentRequestV1 request,
        CancellationToken cancellationToken);
}
