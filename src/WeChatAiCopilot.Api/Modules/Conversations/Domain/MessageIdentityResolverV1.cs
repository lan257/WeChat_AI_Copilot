using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.Api.Modules.Conversations.Domain;

/// <summary>Maps stable WeChat identities to the product's incoming/outgoing direction.</summary>
public static class MessageIdentityResolverV1
{
    public static (MessageDirectionV1 Direction, DirectionEvidenceV1 Evidence) Resolve(
        string? accountWxid,
        string? senderWxid)
    {
        var self = Normalize(accountWxid);
        var sender = Normalize(senderWxid);
        if (self is null || sender is null)
        {
            return (MessageDirectionV1.Unknown, DirectionEvidenceV1.Unresolved);
        }

        return string.Equals(self, sender, StringComparison.Ordinal)
            ? (MessageDirectionV1.Outgoing, DirectionEvidenceV1.SourceConfirmed)
            : (MessageDirectionV1.Incoming, DirectionEvidenceV1.SourceConfirmed);
    }

    private static string? Normalize(string? wxid)
    {
        var value = wxid?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
