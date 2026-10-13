namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public sealed record ReaderStatusHeartbeatRequestV1
{
    public Guid ReaderInstanceId { get; init; }
    public string? AccountExternalId { get; init; }
    public string? State { get; init; }
    public long TargetVersion { get; init; }
    public int ListeningTargetCount { get; init; }
    public string? LastErrorCode { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
}

public sealed record ReplaceReaderTargetsRequestV1
{
    public long ExpectedVersion { get; init; }
    public IReadOnlyList<Guid>? ConversationIds { get; init; }
}
