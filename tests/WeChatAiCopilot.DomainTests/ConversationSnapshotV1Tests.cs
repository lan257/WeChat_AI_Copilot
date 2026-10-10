using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.DomainTests;

public sealed class ConversationSnapshotV1Tests
{
    [Fact]
    public void SnapshotExposesMessageSetRevisionAsAStableStringValue()
    {
        const string revision = "9223372036854775806";
        var snapshot = new ConversationSnapshotV1 { MessageSetRevision = revision };

        Assert.Equal(revision, snapshot.MessageSetRevision);
    }

    [Fact]
    public void SnapshotDefaultsMessageSetRevisionToAnEmptyValue()
    {
        var snapshot = new ConversationSnapshotV1();

        Assert.Equal(string.Empty, snapshot.MessageSetRevision);
    }
}
