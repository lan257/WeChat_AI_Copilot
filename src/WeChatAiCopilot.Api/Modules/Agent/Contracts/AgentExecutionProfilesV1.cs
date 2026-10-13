namespace WeChatAiCopilot.Api.Modules.Agent.Contracts;

public sealed record AgentExecutionProfileSummaryV1(
    AgentExecutionProfileIdV1 ExecutionProfileId,
    string Label,
    string Description,
    int RecentMessageLimit,
    bool IncludeMemory,
    int MaximumHistorySearches,
    bool ReviewSuggestions,
    string RuntimeNote);

public static class AgentExecutionProfilesV1
{
    private static readonly AgentExecutionProfileSummaryV1[] Definitions =
    [
        new(AgentExecutionProfileIdV1.Quick, "快速", "近期消息优先，跳过记忆和历史检索。", 15, false, 0, false,
            "可配置独立的较快模型；未配置时沿用当前 Runtime。"),
        new(AgentExecutionProfileIdV1.Balanced, "平衡", "近期消息与已验证记忆，可进行一次有界历史检索。", 40, true, 1, false,
            "适合一般回复和自由咨询。"),
        new(AgentExecutionProfileIdV1.Deep, "深度", "扩大近期上下文，允许多次有界历史检索。", 80, true, 2, false,
            "等待时间通常较长。"),
        new(AgentExecutionProfileIdV1.Max, "Max", "使用最大上下文预算并检查建议中的事实和越界推断。", 100, true, 4, true,
            "建议生成后会再执行一次独立复核。")
    ];

    public static IReadOnlyList<AgentExecutionProfileSummaryV1> GetAvailable() => Definitions;

    public static AgentExecutionProfileSummaryV1 Get(AgentExecutionProfileIdV1 profileId) =>
        Definitions.FirstOrDefault(item => item.ExecutionProfileId == profileId)
        ?? throw new ArgumentOutOfRangeException(nameof(profileId));
}
