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
}
