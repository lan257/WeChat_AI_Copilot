using System.Globalization;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Runtime;

/// <summary>
/// Deterministic local runtime for contract integration and UI work. It neither
/// reads data nor invokes tools; it only consumes the supplied scoped snapshot.
/// </summary>
public sealed class FakeAgentRuntime : IAgentRuntime
{
    public const string RuntimeId = "fake-v1";

    public Task<AgentRuntimeResultV1> ExecuteAsync(
        ScopedAgentRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(AgentRuntimeResultV1.Cancelled(RuntimeId));
        }

        var requestViolations = AgentContractValidatorV1.Validate(request);
        if (requestViolations.Count > 0)
        {
            return Task.FromResult(AgentRuntimeResultV1.Failed(RuntimeId, requestViolations[0].Code));
        }

        var task = request.Task!;
        var context = request.Context!;

        var usableMessages = context.RecentMessages!
            .Where(message => !string.IsNullOrWhiteSpace(message?.MessageId)
                && !string.IsNullOrWhiteSpace(message.Text))
            .ToArray();
        if (usableMessages.Length == 0)
        {
            return Task.FromResult(AgentRuntimeResultV1.Failed(RuntimeId, "CONTEXT_UNAVAILABLE"));
        }

        var result = task.TaskType switch
        {
            AgentTaskTypeV1.ReplyCandidates => CreateSuggestions(),
            AgentTaskTypeV1.FreeConsultation => CreateConsultation(usableMessages.Length),
            _ => AgentRuntimeResultV1.Failed(RuntimeId, "INVALID_TASK_TYPE")
        };

        return Task.FromResult(result);
    }

    private static AgentRuntimeResultV1 CreateSuggestions()
    {
        IReadOnlyList<AgentSuggestionDraftV1> suggestions =
        [
            new() { Text = "【FakeRuntime示例】我明白了，想多了解一点，你当时是什么感受？" },
            new() { Text = "【FakeRuntime示例】谢谢你跟我说这件事。要是你愿意，可以再讲讲最重要的部分。" },
            new() { Text = "【FakeRuntime示例】我先想一想，等下再认真回复你。" }
        ];

        return AgentRuntimeResultV1.CompletedSuggestions(RuntimeId, suggestions);
    }

    private static AgentRuntimeResultV1 CreateConsultation(int messageCount)
    {
        var count = messageCount.ToString(CultureInfo.InvariantCulture);
        var answer = $"这是 FakeRuntime 的固定演示回复。当前授权上下文包含 {count} 条近期消息，但此回复没有进行真实语义分析。";
        return AgentRuntimeResultV1.CompletedConsultation(RuntimeId, answer);
    }

}
