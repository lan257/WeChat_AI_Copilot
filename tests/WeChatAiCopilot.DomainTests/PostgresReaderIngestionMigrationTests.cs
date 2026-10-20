using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Platform.Migrations;

namespace WeChatAiCopilot.DomainTests;

public sealed class PostgresReaderIngestionMigrationTests
{
    private const string ConnectionStringEnvironmentVariable = "WECHAT_AI_COPILOT_M2_TEST_POSTGRES";

    [M2PostgresFact]
    public async Task IngestionPersistsAutomaticDirectionAndKeepsShardLocalIdsDistinct()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionStringEnvironmentVariable} to a disposable PostgreSQL test database.");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = connectionStringBuilder.Database ?? string.Empty;
        Assert.True(
            databaseName.Contains("test", StringComparison.OrdinalIgnoreCase)
                || databaseName.Contains("temp", StringComparison.OrdinalIgnoreCase),
            $"The database name must contain 'test' or 'temp' so this integration test cannot target a normal database. Actual name: '{databaseName}'.");

        var accountExternalId = $"m2-test-account-{Guid.NewGuid():N}";
        var conversationExternalId = $"m2-test-conversation-{Guid.NewGuid():N}";
        var identityNamespace = $"m2-test-namespace-{Guid.NewGuid():N}";
        const string bearerToken = "m2-reader-integration-test-token";

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(ConversationEndpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reader:Provider"] = "local-wechat",
            ["Reader:AllowedAccountExternalId"] = accountExternalId,
            ["Reader:AllowedConversationExternalId"] = conversationExternalId,
            ["Authentication:ReaderBearerToken"] = bearerToken
        });
        builder.Services.AddSingleton(dataSource);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(allowIntegerValues: false));
        });

        await using var app = builder.Build();
        app.MapConversationEndpoints(app.Configuration);

        try
        {
            await MigrationRunner.ApplyPendingAsync(dataSource);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            var requestJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            requestJsonOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));

            var message = new RawMessageEventV1
            {
                Provider = "local-wechat",
                SourceKind = SourceKindV1.Realtime,
                AccountExternalId = accountExternalId,
                AccountWxid = "wxid_test_account",
                ConversationExternalId = conversationExternalId,
                ConversationType = "Direct",
                MessageType = "Text",
                Text = "first source-identity ingest",
                SourceOccurredAt = DateTimeOffset.UtcNow,
                RawSenderId = "1",
                SenderWxid = "wxid_test_account",
                IdentityNamespace = identityNamespace,
                SourceIdentity = new SourceIdentityV1
                {
                    LocalId = "local-message-1",
                    SortSeq = "1",
                    ServerId = "server-message-1",
                    Shard = "message_0.db",
                    ShardFingerprint = "test-shard",
                    MessageTable = "Message_test"
                }
            };

            using var firstResponse = await client.PostAsync(
                "/api/reader/messages",
                JsonContent.Create(message, options: requestJsonOptions));
            Assert.True(firstResponse.IsSuccessStatusCode, await firstResponse.Content.ReadAsStringAsync());
            var firstReceipt = await firstResponse.Content.ReadFromJsonAsync<IngestReceiptV1>(requestJsonOptions);
            Assert.NotNull(firstReceipt);
            Assert.Equal(MatchStatusV1.Created, firstReceipt.MatchStatus);
            Assert.True(firstReceipt.Created);
            Assert.NotNull(firstReceipt.MessageId);

            using var retryResponse = await client.PostAsync(
                "/api/reader/messages",
                JsonContent.Create(message, options: requestJsonOptions));
            Assert.True(retryResponse.IsSuccessStatusCode, await retryResponse.Content.ReadAsStringAsync());
            var retryReceipt = await retryResponse.Content.ReadFromJsonAsync<IngestReceiptV1>(requestJsonOptions);
            Assert.NotNull(retryReceipt);
            Assert.Equal(MatchStatusV1.Matched, retryReceipt.MatchStatus);
            Assert.False(retryReceipt.Created);
            Assert.Equal(firstReceipt.MessageId, retryReceipt.MessageId);
            var thirdReceipt = await PostMessageAsync(client, requestJsonOptions, message);
            Assert.Equal(MatchStatusV1.Matched, thirdReceipt.MatchStatus);
            Assert.Equal(firstReceipt.MessageId, thirdReceipt.MessageId);

            var serverIdConflict = message with
            {
                SourceIdentity = message.SourceIdentity! with { ServerId = "server-message-2" }
            };
            var serverIdConflictReceipt = await PostMessageAsync(client, requestJsonOptions, serverIdConflict);
            Assert.Equal(MatchStatusV1.NeedsReview, serverIdConflictReceipt.MatchStatus);
            Assert.Equal("EXTERNAL_MESSAGE_ID_SCOPE_COLLISION", serverIdConflictReceipt.ReviewCode);
            Assert.Null(serverIdConflictReceipt.MessageId);

            var otherShardConflict = message with
            {
                IdentityNamespace = identityNamespace + ":other-shard",
                SourceIdentity = message.SourceIdentity! with
                {
                    ServerId = "server-message-other-shard",
                    Shard = "message_1.db",
                    ShardFingerprint = "other-test-shard"
                }
            };
            var otherShardReceipt = await PostMessageAsync(client, requestJsonOptions, otherShardConflict);
            Assert.Equal(MatchStatusV1.Created, otherShardReceipt.MatchStatus);
            Assert.NotEqual(firstReceipt.MessageId, otherShardReceipt.MessageId);

            var sameTextDifferentSource = message with
            {
                SourceIdentity = message.SourceIdentity! with
                {
                    LocalId = "local-message-2",
                    SortSeq = "2"
                }
            };
            var distinctSourceReceipt = await PostMessageAsync(client, requestJsonOptions, sameTextDifferentSource);
            Assert.Equal(MatchStatusV1.Created, distinctSourceReceipt.MatchStatus);
            Assert.True(distinctSourceReceipt.Created);
            Assert.NotNull(distinctSourceReceipt.MessageId);
            Assert.NotEqual(firstReceipt.MessageId, distinctSourceReceipt.MessageId);

            var unknownIdentityReceipt = await PostMessageAsync(client, requestJsonOptions, message with
            {
                AccountWxid = null,
                SenderWxid = null,
                SourceIdentity = message.SourceIdentity! with { LocalId = "local-message-3", SortSeq = "3" }
            });
            Assert.Equal(MatchStatusV1.Created, unknownIdentityReceipt.MatchStatus);

            using var accountConflictResponse = await client.PostAsync(
                "/api/reader/messages",
                JsonContent.Create(message with
                {
                    AccountWxid = "wxid_different_account",
                    SourceIdentity = message.SourceIdentity! with { LocalId = "local-message-4", SortSeq = "4" }
                }, options: requestJsonOptions));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, accountConflictResponse.StatusCode);
            Assert.Contains("READER_ACCOUNT_WXID_CONFLICT", await accountConflictResponse.Content.ReadAsStringAsync());

            await AssertSinglePersistedMessageAsync(
                dataSource,
                accountExternalId,
                conversationExternalId,
                firstReceipt.MessageId!.Value,
                otherShardReceipt.MessageId!.Value,
                distinctSourceReceipt.MessageId!.Value,
                unknownIdentityReceipt.MessageId!.Value);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<IngestReceiptV1> PostMessageAsync(
        HttpClient client,
        JsonSerializerOptions jsonOptions,
        RawMessageEventV1 message)
    {
        using var response = await client.PostAsync(
            "/api/reader/messages",
            JsonContent.Create(message, options: jsonOptions));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var receipt = await response.Content.ReadFromJsonAsync<IngestReceiptV1>(jsonOptions);
        Assert.NotNull(receipt);
        return receipt;
    }

    private static async Task AssertSinglePersistedMessageAsync(
        NpgsqlDataSource dataSource,
        string accountExternalId,
        string conversationExternalId,
        Guid expectedMessageId,
        Guid expectedOtherShardMessageId,
        Guid expectedDifferentSourceMessageId,
        Guid expectedUnknownMessageId)
    {
        const string sql = """
            SELECT account.id,
                   conversation.id,
                   account.account_wxid,
                   (SELECT count(*) FROM copilot.chat_messages AS message
                    WHERE message.account_id = account.id AND message.conversation_id = conversation.id),
                   (SELECT count(*) FROM copilot.message_sources AS source
                    WHERE source.account_id = account.id AND source.conversation_id = conversation.id),
                   (SELECT count(*) FROM copilot.message_sources AS source
                    WHERE source.account_id = account.id AND source.conversation_id = conversation.id
                      AND source.match_status = 'NeedsReview'),
                   (SELECT count(*) FROM copilot.sender_calibrations AS calibration
                    WHERE calibration.account_id = account.id AND calibration.conversation_id = conversation.id)
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation ON conversation.account_id = account.id
            WHERE account.provider = 'local-wechat'
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_external_id", accountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", conversationExternalId);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "The ingested account should retain its stable wxid.");
        var accountId = reader.GetGuid(0);
        var conversationId = reader.GetGuid(1);
        Assert.Equal("wxid_test_account", reader.GetString(2));
        Assert.Equal(4L, reader.GetInt64(3));
        Assert.Equal(5L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(0L, reader.GetInt64(6));
        Assert.False(await reader.ReadAsync(), "The account should have one configured conversation.");
        await reader.CloseAsync();

        await using var messageCommand = new NpgsqlCommand("""
            SELECT count(*)
            FROM copilot.chat_messages
            WHERE id = @message_id AND account_id = @account_id AND conversation_id = @conversation_id;
            """, connection);
        messageCommand.Parameters.AddWithValue("message_id", expectedMessageId);
        messageCommand.Parameters.AddWithValue("account_id", accountId);
        messageCommand.Parameters.AddWithValue("conversation_id", conversationId);
        Assert.Equal(1L, await messageCommand.ExecuteScalarAsync());
        messageCommand.Parameters["message_id"].Value = expectedOtherShardMessageId;
        Assert.Equal(1L, await messageCommand.ExecuteScalarAsync());
        messageCommand.Parameters["message_id"].Value = expectedDifferentSourceMessageId;
        Assert.Equal(1L, await messageCommand.ExecuteScalarAsync());
        messageCommand.Parameters["message_id"].Value = expectedUnknownMessageId;
        Assert.Equal(1L, await messageCommand.ExecuteScalarAsync());

        await using var sourceCommand = new NpgsqlCommand("""
            SELECT local_id, sort_seq, server_id, shard, shard_fingerprint, message_table,
                   raw_sender_id, sender_wxid, identity_namespace, external_message_id
            FROM copilot.message_sources
            WHERE message_id = @message_id AND match_status = 'Created';
            """, connection);
        sourceCommand.Parameters.AddWithValue("message_id", expectedMessageId);
        await using var sourceReader = await sourceCommand.ExecuteReaderAsync();
        Assert.True(await sourceReader.ReadAsync());
        Assert.Equal("local-message-1", sourceReader.GetString(0));
        Assert.Equal("1", sourceReader.GetString(1));
        Assert.Equal("server-message-1", sourceReader.GetString(2));
        Assert.Equal("message_0.db", sourceReader.GetString(3));
        Assert.Equal("test-shard", sourceReader.GetString(4));
        Assert.Equal("Message_test", sourceReader.GetString(5));
        Assert.Equal("1", sourceReader.GetString(6));
        Assert.Equal("wxid_test_account", sourceReader.GetString(7));
        Assert.StartsWith("m2-test-namespace-", sourceReader.GetString(8), StringComparison.Ordinal);
        Assert.StartsWith("wx4:", sourceReader.GetString(9), StringComparison.Ordinal);
        Assert.False(await sourceReader.ReadAsync());
        await sourceReader.CloseAsync();

        await using var directionCommand = new NpgsqlCommand("""
            SELECT direction, direction_evidence
            FROM copilot.chat_messages
            WHERE id = @message_id;
            """, connection);
        directionCommand.Parameters.AddWithValue("message_id", expectedMessageId);
        await using var directionReader = await directionCommand.ExecuteReaderAsync();
        Assert.True(await directionReader.ReadAsync());
        Assert.Equal("Outgoing", directionReader.GetString(0));
        Assert.Equal("SourceConfirmed", directionReader.GetString(1));
        await directionReader.CloseAsync();

        directionCommand.Parameters["message_id"].Value = expectedUnknownMessageId;
        await using var unknownDirectionReader = await directionCommand.ExecuteReaderAsync();
        Assert.True(await unknownDirectionReader.ReadAsync());
        Assert.Equal("Unknown", unknownDirectionReader.GetString(0));
        Assert.Equal("Unresolved", unknownDirectionReader.GetString(1));
    }
}

public sealed class M2PostgresFactAttribute : FactAttribute
{
    public M2PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                "WECHAT_AI_COPILOT_M2_TEST_POSTGRES")))
        {
            Skip = "Set WECHAT_AI_COPILOT_M2_TEST_POSTGRES to a disposable PostgreSQL test database to run this integration test.";
        }
    }
}
