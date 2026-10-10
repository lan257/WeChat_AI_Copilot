namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Service-side bounds for the first context builder. These values cap what M4 receives;
/// callers and models cannot increase them through ContextRequestV1.
/// </summary>
public sealed record ContextBuilderOptionsV1
{
    public int MaximumTotalTextCodePoints { get; init; } = 32000;
    public int MaximumMemoryItems { get; init; } = 50;
    public int MaximumMemoryEvidenceIdsPerItem { get; init; } = 50;
    public int MaximumProfileEvidenceIds { get; init; } = 100;
}
