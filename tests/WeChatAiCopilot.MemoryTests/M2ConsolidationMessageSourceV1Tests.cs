using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Integration.M2;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class M2ConsolidationMessageSourceV1Tests
{
    [Fact]
    public async Task Adapter_ForwardsScopedPageRequestAndMapsAllM2SnapshotFields()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var anchorId = Guid.NewGuid();
        var messages = new[]
        {
            new MessageQueryItemV1
            {
                MessageId = Guid.NewGuid(),
                Text = "sample",
                MessageType = "Text",
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            }
        };
        var snapshot = new MessageQuerySnapshotV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            ConversationType = "Direct",
            IdentityNamespace = "table-shard-v1",
            MessageSetRevision = "revision-45",
            PerspectiveState = PerspectiveStateV1.Confirmed,
            CalibrationVersion = 12,
            HasMoreMessages = true,
            AnchorMessageFound = false,
            Messages = messages
        };
        var query = new FakeMessageQuery(snapshot);
        var adapter = new M2ConsolidationMessageSourceAdapterV1(query);
        var request = new MessageQueryRequestV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            Limit = 13,
            AnchorMessageId = anchorId
        };
        using var cancellation = new CancellationTokenSource();

        var page = await adapter.GetPageAsync(request, cancellation.Token);

        Assert.Same(request, query.LastRequest);
        Assert.Equal(cancellation.Token, query.LastCancellationToken);
        Assert.Equal(accountId, page.AccountId);
        Assert.Equal(conversationId, page.ConversationId);
        Assert.Equal("Direct", page.ConversationType);
        Assert.Equal("table-shard-v1", page.IdentityNamespace);
        Assert.Equal("revision-45", page.MessageSetRevision);
        Assert.Equal(PerspectiveStateV1.Confirmed, page.PerspectiveState);
        Assert.Equal(12, page.CalibrationVersion);
        Assert.True(page.HasMoreMessages);
        Assert.False(page.AnchorMessageFound);
        Assert.Same(messages, page.Messages);
    }

    [Fact]
    public async Task Adapter_MapsUnavailableOptionalIdentityFieldsToFailClosedEmptyValues()
    {
        var query = new FakeMessageQuery(new MessageQuerySnapshotV1
        {
            AccountId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ConversationType = null,
            IdentityNamespace = null,
            MessageSetRevision = "revision-1"
        });
        var adapter = new M2ConsolidationMessageSourceAdapterV1(query);

        var page = await adapter.GetPageAsync(new MessageQueryRequestV1
        {
            AccountId = query.Snapshot.AccountId,
            ConversationId = query.Snapshot.ConversationId
        });

        Assert.Equal(string.Empty, page.ConversationType);
        Assert.Equal(string.Empty, page.IdentityNamespace);
    }

    [Fact]
    public void MemoryServiceRegistration_AddsM2AdapterCollectorAndHandlerWithoutGeneratorOrDatabaseConnection()
    {
        using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=localhost;Database=memorytests;Username=memorytests;Password=not-used")
            .Build();
        var snapshot = new MessageQuerySnapshotV1
        {
            AccountId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            MessageSetRevision = "revision-1",
            ConversationType = "Direct",
            IdentityNamespace = "ns-1",
            PerspectiveState = PerspectiveStateV1.Confirmed,
            CalibrationVersion = 1
        };
        var services = new ServiceCollection();
        services.AddSingleton<NpgsqlDataSource>(dataSource);
        services.AddSingleton<IMessageQueryV1>(new FakeMessageQuery(snapshot));
        services.AddPostgresMemoryServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var messageSource = scope.ServiceProvider.GetRequiredService<IConsolidationMessageSourceV1>();
        var collector = scope.ServiceProvider.GetRequiredService<ConsolidationMessageCollectorV1>();
        var handler = scope.ServiceProvider.GetRequiredService<IMemoryConsolidationEventHandlerV1>();

        Assert.IsType<M2ConsolidationMessageSourceAdapterV1>(messageSource);
        Assert.NotNull(collector);
        Assert.IsType<MemoryConsolidationEventHandlerV1>(handler);
        Assert.Null(scope.ServiceProvider.GetService<IMemoryConsolidationGeneratorV1>());
    }

    private sealed class FakeMessageQuery(MessageQuerySnapshotV1 snapshot) : IMessageQueryV1
    {
        public MessageQuerySnapshotV1 Snapshot { get; } = snapshot;
        public MessageQueryRequestV1? LastRequest { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(Snapshot);
        }
    }
}
