using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Application;

namespace WeChatAiCopilot.DomainTests;

public sealed class PostgresMessageIngestionServiceLogicTests
{
    [Fact]
    public void EnsureHistoryJobRangeMatches_AcceptsThePersistedJobRange()
    {
        var exception = Record.Exception(
            () => PostgresMessageIngestionService.EnsureHistoryJobRangeMatches(requestedRangeMatches: true));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureHistoryJobRangeMatches_RejectsAReusedJobIdWithAnotherRange()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgresMessageIngestionService.EnsureHistoryJobRangeMatches(requestedRangeMatches: false));

        Assert.Contains("cannot be reused with a different requested range", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void EnsureHistoryBatchSize_AcceptsZeroThroughFiveHundredItems(int itemCount)
    {
        var exception = Record.Exception(
            () => PostgresMessageIngestionService.EnsureHistoryBatchSize(itemCount));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureHistoryBatchSize_RejectsMoreThanFiveHundredItems()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgresMessageIngestionService.EnsureHistoryBatchSize(itemCount: 501));

        Assert.Contains("cannot contain more than 500 items", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMessageIdentity_UsesWxidsAndIgnoresSubmittedDirection()
    {
        var resolved = PostgresMessageIngestionService.ResolveMessageIdentity(new RawMessageEventV1
        {
            AccountWxid = "wxid_self",
            SenderWxid = "wxid_friend",
            Direction = MessageDirectionV1.Outgoing,
            DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
        });

        Assert.Equal(MessageDirectionV1.Incoming, resolved.Direction);
        Assert.Equal(DirectionEvidenceV1.SourceConfirmed, resolved.DirectionEvidence);

        var unknown = PostgresMessageIngestionService.ResolveMessageIdentity(new RawMessageEventV1
        {
            AccountWxid = "wxid_self",
            SenderWxid = null,
            Direction = MessageDirectionV1.Outgoing,
            DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
        });

        Assert.Equal(MessageDirectionV1.Unknown, unknown.Direction);
        Assert.Equal(DirectionEvidenceV1.Unresolved, unknown.DirectionEvidence);
    }

    [Fact]
    public void ExternalMessageIdentity_IncludesTheShardNamespace()
    {
        var first = SourceMessage("message_0.db", "shard-0");
        var second = SourceMessage("message_1.db", "shard-1");

        Assert.NotEqual(
            PostgresMessageIngestionService.ComputeLocalWechatExternalMessageId(first),
            PostgresMessageIngestionService.ComputeLocalWechatExternalMessageId(second));
    }

    private static RawMessageEventV1 SourceMessage(string shard, string shardFingerprint) => new()
    {
        AccountExternalId = "account-dir",
        AccountWxid = "wxid_self",
        ConversationExternalId = "wxid_contact",
        SourceIdentity = new SourceIdentityV1
        {
            LocalId = "58",
            SortSeq = "9001",
            Shard = shard,
            ShardFingerprint = shardFingerprint,
            MessageTable = "Msg_fixture"
        }
    };
}
