using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Domain;

namespace WeChatAiCopilot.DomainTests;

public sealed class SenderPerspectiveResolverTests
{
    private static readonly SenderCalibrationScopeV1 Scope = new(
        Guid.Parse("e1b96792-a972-4b63-8e44-97b6fe152032"),
        Guid.Parse("227fd6e9-efdf-43b6-bde6-cb55b2a68963"),
        "wx4:account-a",
        "MSG_0",
        "shard-a");

    [Fact]
    public void ResolveDefault_UsesNumericOrderAndLeavesPerspectivePending()
    {
        var result = SenderPerspectiveResolver.ResolveDefault(Scope, Observed(Scope, "10", "2"));

        Assert.Equal(PerspectiveStateV1.PendingConfirmation, result.State);
        Assert.Equal(PerspectiveEvidenceV1.DefaultSmallSenderId, result.Evidence);
        Assert.Equal("2", result.AccountSelfRawSenderId);
        Assert.Equal("10", result.ContactRawSenderId);
        Assert.Equal(0, result.CalibrationVersion);

        var outgoing = SenderPerspectiveResolver.Project(Scope, result, "2");
        var incoming = SenderPerspectiveResolver.Project(Scope, result, "10");

        Assert.Equal(MessageDirectionV1.Outgoing, outgoing.Direction);
        Assert.Equal(DirectionEvidenceV1.DefaultSmallSenderId, outgoing.Evidence);
        Assert.Equal(MessageDirectionV1.Incoming, incoming.Direction);
        Assert.Equal(DirectionEvidenceV1.DefaultSmallSenderId, incoming.Evidence);
    }

    [Fact]
    public void ConfirmSelection_UsesTheExplicitlySelectedSender()
    {
        var result = SenderPerspectiveResolver.ConfirmSelection(Scope, Observed(Scope, "2", "10"), "10");

        Assert.Equal(PerspectiveStateV1.Confirmed, result.State);
        Assert.Equal(PerspectiveEvidenceV1.UserConfirmed, result.Evidence);
        Assert.Equal("10", result.AccountSelfRawSenderId);
        Assert.Equal("2", result.ContactRawSenderId);

        var outgoing = SenderPerspectiveResolver.Project(Scope, result, "10");
        Assert.Equal(MessageDirectionV1.Outgoing, outgoing.Direction);
        Assert.Equal(DirectionEvidenceV1.UserConfirmed, outgoing.Evidence);
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("Conversation")]
    [InlineData("IdentityNamespace")]
    [InlineData("MessageTable")]
    [InlineData("ShardFingerprint")]
    public void ResolveDefault_RejectsObservationsFromAnotherCalibrationScope(string changedField)
    {
        var foreignScope = changedField switch
        {
            "Account" => Scope with { AccountId = Guid.NewGuid() },
            "Conversation" => Scope with { ConversationId = Guid.NewGuid() },
            "IdentityNamespace" => Scope with { IdentityNamespace = "wx4:account-b" },
            "MessageTable" => Scope with { MessageTable = "MSG_1" },
            "ShardFingerprint" => Scope with { ShardFingerprint = "shard-b" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        var result = SenderPerspectiveResolver.ResolveDefault(
            Scope,
            [new(Scope, "2"), new(foreignScope, "10")]);

        AssertUnresolved(result);
    }

    [Fact]
    public void Project_ReturnsUnknownWhenMessageScopeDiffersFromCalibrationScope()
    {
        var perspective = SenderPerspectiveResolver.ResolveDefault(Scope, Observed(Scope, "2", "10"));
        var projection = SenderPerspectiveResolver.Project(Scope with { ShardFingerprint = "shard-b" }, perspective, "2");

        Assert.Equal(MessageDirectionV1.Unknown, projection.Direction);
        Assert.Equal(DirectionEvidenceV1.Unresolved, projection.Evidence);
    }

    [Fact]
    public void ResolveDefault_ReturnsUnresolvedForInvalidOrAmbiguousIds()
    {
        string?[][] invalidCandidates =
        [
            ["-1", "2"],
            [" 1", "2"],
            ["one", "2"],
            ["1", "2", "3"],
            ["1"],
            ["2", "02"],
            ["2", "2"]
        ];

        foreach (var senderIds in invalidCandidates)
        {
            var result = SenderPerspectiveResolver.ResolveDefault(Scope, Observed(Scope, senderIds));
            AssertUnresolved(result);
        }
    }

    [Fact]
    public void ConfirmSelection_ReturnsUnresolvedWhenSelectionIsNotOneOfTheObservedIds()
    {
        var result = SenderPerspectiveResolver.ConfirmSelection(Scope, Observed(Scope, "2", "10"), "99");

        AssertUnresolved(result);
    }

    private static ScopedSenderIdentityV1[] Observed(SenderCalibrationScopeV1 scope, params string?[] senderIds) =>
        senderIds.Select(senderId => new ScopedSenderIdentityV1(scope, senderId)).ToArray();

    private static void AssertUnresolved(SenderPerspectiveV1 result)
    {
        Assert.Equal(PerspectiveStateV1.Unresolved, result.State);
        Assert.Null(result.Evidence);
        Assert.Null(result.AccountSelfRawSenderId);
        Assert.Null(result.ContactRawSenderId);
        Assert.Equal(0, result.CalibrationVersion);
    }
}
