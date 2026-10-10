namespace WeChatAiCopilot.Api.Modules.Memory.Contracts;

/// <summary>
/// Internal scope resolved by the server from the configured Web account and conversation.
/// It is never accepted as part of a client request.
/// </summary>
public sealed record MemoryConversationScopeV1(
    Guid AccountId,
    Guid ConversationId,
    Guid ContactId);

public sealed record UserEnteredMemoryCreateRequestV1
{
    public ParticipantSubjectTypeV1 SubjectType { get; init; } = ParticipantSubjectTypeV1.Contact;
    public string? Text { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<Guid>? SourceMessageIds { get; init; }
}

public sealed record UserEnteredMemoryUpdateRequestV1
{
    /// <summary>Updates replace the complete editable state; use an empty evidence array to clear evidence.</summary>
    public long ExpectedVersion { get; init; }
    public string? Text { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    /// <summary>Required for updates so omitting evidence cannot silently remove it.</summary>
    public IReadOnlyList<Guid>? SourceMessageIds { get; init; }
}

public sealed record MemoryRequestViolationV1(string Field, string Code);
