using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.DomainTests;

public sealed class PostgresMessageQueryV1Tests
{
    [Fact]
    public void MessageQuerySnapshot_ExposesConversationTypeAndOptionalIdentityNamespace()
    {
        var snapshot = new MessageQuerySnapshotV1
        {
            ConversationType = "Direct",
            IdentityNamespace = "wx4:account-a"
        };

        Assert.Equal("Direct", snapshot.ConversationType);
        Assert.Equal("wx4:account-a", snapshot.IdentityNamespace);
        Assert.Null(new MessageQuerySnapshotV1().IdentityNamespace);
    }

    [Fact]
    public void MessageQuerySnapshot_ExposesTheMessageSetRevisionToken()
    {
        const string revision = "9223372036854775806";
        var snapshot = new MessageQuerySnapshotV1 { MessageSetRevision = revision };

        Assert.Equal(revision, snapshot.MessageSetRevision);
    }

    [Fact]
    public void CompareChronologically_PlacesSourceTimedMessagesBeforeUntimedMessages()
    {
        var sourceTimed = Position(
            "00000000-0000-0000-0000-000000000001",
            "2026-10-08T00:00:00Z",
            "2026-10-10T00:00:00Z");
        var untimed = Position(
            "00000000-0000-0000-0000-000000000002",
            null,
            "2026-10-07T00:00:00Z");

        Assert.True(PostgresMessageQueryV1.CompareChronologically(sourceTimed, untimed) < 0);
        Assert.True(PostgresMessageQueryV1.CompareChronologically(untimed, sourceTimed) > 0);
    }

    [Fact]
    public void CompareChronologically_UsesIngestedTimeAndIdAsStableTieBreakers()
    {
        var sourceTime = "2026-10-08T00:00:00Z";
        var earlierIngest = Position(
            "00000000-0000-0000-0000-000000000003",
            sourceTime,
            "2026-10-08T00:00:00Z");
        var laterIngest = Position(
            "00000000-0000-0000-0000-000000000004",
            sourceTime,
            "2026-10-08T00:00:01Z");
        var smallerId = Position(
            "00000000-0000-0000-0000-000000000005",
            sourceTime,
            "2026-10-08T00:00:01Z");
        var largerId = Position(
            "00000000-0000-0000-0000-000000000006",
            sourceTime,
            "2026-10-08T00:00:01Z");

        Assert.True(PostgresMessageQueryV1.CompareChronologically(earlierIngest, laterIngest) < 0);
        Assert.True(PostgresMessageQueryV1.CompareChronologically(smallerId, largerId) < 0);
        Assert.True(PostgresMessageQueryV1.IsAtOrBeforeAnchor(smallerId, smallerId));
        Assert.False(PostgresMessageQueryV1.IsAtOrBeforeAnchor(largerId, smallerId));
    }

    [Fact]
    public void IsAtOrBeforeAnchor_MatchesNullsLastOrderForBothAnchorTimeCases()
    {
        var datedAnchor = Position(
            "00000000-0000-0000-0000-000000000008",
            "2026-10-08T00:00:00Z",
            "2026-10-08T00:00:02Z");
        var earlierDated = Position(
            "00000000-0000-0000-0000-000000000009",
            "2026-10-07T00:00:00Z",
            "2026-10-09T00:00:00Z");
        var undated = Position(
            "00000000-0000-0000-0000-00000000000a",
            null,
            "2026-10-07T00:00:00Z");

        Assert.True(PostgresMessageQueryV1.IsAtOrBeforeAnchor(earlierDated, datedAnchor));
        Assert.False(PostgresMessageQueryV1.IsAtOrBeforeAnchor(undated, datedAnchor));
        Assert.True(PostgresMessageQueryV1.IsAtOrBeforeAnchor(datedAnchor, undated));
        Assert.True(PostgresMessageQueryV1.IsAtOrBeforeAnchor(undated, undated));
    }

    [Fact]
    public void PageSqlUsesReverseOfChronologicalNullsLastOrderAndStableOlderAnchorBoundary()
    {
        var sql = PostgresMessageQueryV1.ReadPageSql;

        Assert.Contains("message.account_id = @account_id", sql, StringComparison.Ordinal);
        Assert.Contains("message.conversation_id = @conversation_id", sql, StringComparison.Ordinal);
        Assert.Contains("message.source_occurred_at DESC NULLS FIRST", sql, StringComparison.Ordinal);
        Assert.Contains("message.ingested_at DESC", sql, StringComparison.Ordinal);
        Assert.Contains("message.id DESC", sql, StringComparison.Ordinal);
        Assert.Contains("@anchor_source_occurred_at IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("message.source_occurred_at IS NOT DISTINCT FROM @anchor_source_occurred_at", sql, StringComparison.Ordinal);
        Assert.Contains("message.id <= @anchor_id", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit_plus_one", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorLookupSqlRequiresTheRequestedAccountAndConversationScope()
    {
        var sql = PostgresMessageQueryV1.ReadAnchorSql;

        Assert.Contains("account_id = @account_id", sql, StringComparison.Ordinal);
        Assert.Contains("conversation_id = @conversation_id", sql, StringComparison.Ordinal);
        Assert.Contains("id = @message_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAnchorQuery_DoesNotReadLatestPageWhenAnchorIsAbsentOrForeign()
    {
        var requestedId = Guid.Parse("e9c11f8a-2be7-442e-8775-e173d4838f73");
        var foundAnchor = new PostgresMessageQueryV1.AnchorPosition(
            null,
            DateTimeOffset.Parse("2026-10-08T00:00:00Z"),
            requestedId);

        var latestPage = PostgresMessageQueryV1.ResolveAnchorQuery(null, null);
        var matchingAnchor = PostgresMessageQueryV1.ResolveAnchorQuery(requestedId, foundAnchor);
        var missingAnchor = PostgresMessageQueryV1.ResolveAnchorQuery(requestedId, null);
        var foreignAnchor = PostgresMessageQueryV1.ResolveAnchorQuery(
            requestedId,
            foundAnchor with { MessageId = Guid.NewGuid() });

        Assert.True(latestPage.AnchorMessageFound);
        Assert.True(latestPage.ShouldReadPage);
        Assert.Null(latestPage.Position);
        Assert.True(matchingAnchor.AnchorMessageFound);
        Assert.True(matchingAnchor.ShouldReadPage);
        Assert.Same(foundAnchor, matchingAnchor.Position);
        Assert.False(missingAnchor.AnchorMessageFound);
        Assert.False(missingAnchor.ShouldReadPage);
        Assert.Null(missingAnchor.Position);
        Assert.False(foreignAnchor.AnchorMessageFound);
        Assert.False(foreignAnchor.ShouldReadPage);
        Assert.Null(foreignAnchor.Position);
    }

    private static PostgresMessageQueryV1.MessageSortPosition Position(
        string id,
        string? sourceOccurredAt,
        string ingestedAt) => new(
            sourceOccurredAt is null ? null : DateTimeOffset.Parse(sourceOccurredAt),
            DateTimeOffset.Parse(ingestedAt),
            Guid.Parse(id));
}
