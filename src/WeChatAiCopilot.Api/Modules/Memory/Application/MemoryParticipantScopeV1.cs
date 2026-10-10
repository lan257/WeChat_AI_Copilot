using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Resolves participant subjects only from the server-authorized conversation scope.
/// Client requests carry a subject type, never a subject/account/contact identifier.
/// </summary>
public static class MemoryParticipantScopeV1
{
    public static Guid ResolveSubjectId(
        MemoryConversationScopeV1 scope,
        ParticipantSubjectTypeV1 subjectType)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AccountId == Guid.Empty || scope.ConversationId == Guid.Empty || scope.ContactId == Guid.Empty)
        {
            throw new ArgumentException("A complete server-resolved conversation scope is required.", nameof(scope));
        }

        return subjectType switch
        {
            ParticipantSubjectTypeV1.WechatAccount => scope.AccountId,
            ParticipantSubjectTypeV1.Contact => scope.ContactId,
            _ => throw new ArgumentOutOfRangeException(nameof(subjectType), subjectType, "Unsupported participant subject type.")
        };
    }

    public static bool IsSubjectIdInScope(
        MemoryConversationScopeV1 scope,
        ParticipantSubjectTypeV1 subjectType,
        Guid subjectId)
    {
        if (subjectId == Guid.Empty)
        {
            return false;
        }

        try
        {
            return ResolveSubjectId(scope, subjectType) == subjectId;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
