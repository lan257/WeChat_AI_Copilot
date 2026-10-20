using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Domain;

namespace WeChatAiCopilot.DomainTests;

public sealed class MessageIdentityResolverV1Tests
{
    [Theory]
    [InlineData("wxid_self", "wxid_self", MessageDirectionV1.Outgoing)]
    [InlineData("wxid_self", "wxid_contact", MessageDirectionV1.Incoming)]
    [InlineData(null, "wxid_contact", MessageDirectionV1.Unknown)]
    [InlineData("wxid_self", null, MessageDirectionV1.Unknown)]
    [InlineData("", "wxid_contact", MessageDirectionV1.Unknown)]
    public void Resolve_UsesStableWxidsOnly(
        string? accountWxid,
        string? senderWxid,
        MessageDirectionV1 expected)
    {
        var result = MessageIdentityResolverV1.Resolve(accountWxid, senderWxid);

        Assert.Equal(expected, result.Direction);
        Assert.Equal(
            expected == MessageDirectionV1.Unknown
                ? DirectionEvidenceV1.Unresolved
                : DirectionEvidenceV1.SourceConfirmed,
            result.Evidence);
    }
}
