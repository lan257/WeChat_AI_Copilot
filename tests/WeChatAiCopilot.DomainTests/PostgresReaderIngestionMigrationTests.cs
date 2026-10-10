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
    public async Task IngestionPreservesSourceEvidenceAndRejectsServerIdAndShardCollisions()
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
                ConversationExternalId = conversationExternalId,
                ConversationType = "Direct",
                MessageType = "Text",
                Text = "first source-identity ingest",
                SourceOccurredAt = DateTimeOffset.UtcNow,
                RawSenderId = "1",
                IdentityNamespace = identityNamespace,
                SourceIdentity = new SourceIdentityV1
                {
                    LocalId = "local-message-1",
                    SortSeq = "1",
                    ServerId = "server-message-1",
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
                    ShardFingerprint = "other-test-shard"
                }
            };
            var otherShardConflictReceipt = await PostMessageAsync(client, requestJsonOptions, otherShardConflict);
            Assert.Equal(MatchStatusV1.NeedsReview, otherShardConflictReceipt.MatchStatus);
            Assert.Equal("EXTERNAL_MESSAGE_ID_SCOPE_COLLISION", otherShardConflictReceipt.ReviewCode);
            Assert.Null(otherShardConflictReceipt.MessageId);

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

            await AssertSinglePersistedMessageAsync(
                dataSource,
                accountExternalId,
                conversationExternalId,
                identityNamespace,
                firstReceipt.MessageId!.Value,
                distinctSourceReceipt.MessageId!.Value);
            await AssertResolvedEqualSenderIdsAreRejectedAsync(
                dataSource,
                accountExternalId,
                conversationExternalId,
                identityNamespace);
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
        string identityNamespace,
        Guid expectedMessageId,
        Guid expectedDifferentSourceMessageId)
    {
        const string sql = """
            SELECT account.id,
                   conversation.id,
                   (SELECT count(*) FROM copilot.chat_messages AS message
                    WHERE message.account_id = account.id AND message.conversation_id = conversation.id),
                   (SELECT count(*) FROM copilot.message_sources AS source
                    WHERE source.account_id = account.id AND source.conversation_id = conversation.id),
                   (SELECT count(*) FROM copilot.message_sources AS source
                    WHERE source.account_id = account.id AND source.conversation_id = conversation.id
                      AND source.match_status = 'NeedsReview'),
                   calibration.perspective_state,
                   calibration.calibration_version,
                   calibration.account_self_raw_sender_id,
                   calibration.contact_raw_sender_id
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation ON conversation.account_id = account.id
            JOIN copilot.sender_calibrations AS calibration
              ON calibration.account_id = account.id
             AND calibration.conversation_id = conversation.id
            WHERE account.provider = 'local-wechat'
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id
              AND calibration.identity_namespace = @identity_namespace
              AND calibration.message_table = 'Message_test'
              AND calibration.shard_fingerprint = 'test-shard';
            """;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_external_id", accountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", conversationExternalId);
        command.Parameters.AddWithValue("identity_namespace", identityNamespace);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "The first source identity should create an unresolved calibration scope.");
        var accountId = reader.GetGuid(0);
        var conversationId = reader.GetGuid(1);
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal(4L, reader.GetInt64(3));
        Assert.Equal(2L, reader.GetInt64(4));
        Assert.Equal("Unresolved", reader.GetString(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.True(reader.IsDBNull(7));
        Assert.True(reader.IsDBNull(8));
        Assert.False(await reader.ReadAsync(), "The configured conversation should have one matching calibration scope.");
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
        messageCommand.Parameters["message_id"].Value = expectedDifferentSourceMessageId;
        Assert.Equal(1L, await messageCommand.ExecuteScalarAsync());

        await using var sourceCommand = new NpgsqlCommand("""
            SELECT local_id, sort_seq, server_id, shard_fingerprint, message_table,
                   raw_sender_id, identity_namespace, external_message_id
            FROM copilot.message_sources
            WHERE message_id = @message_id AND match_status = 'Created';
            """, connection);
        sourceCommand.Parameters.AddWithValue("message_id", expectedMessageId);
        await using var sourceReader = await sourceCommand.ExecuteReaderAsync();
        Assert.True(await sourceReader.ReadAsync());
        Assert.Equal("local-message-1", sourceReader.GetString(0));
        Assert.Equal("1", sourceReader.GetString(1));
        Assert.Equal("server-message-1", sourceReader.GetString(2));
        Assert.Equal("test-shard", sourceReader.GetString(3));
        Assert.Equal("Message_test", sourceReader.GetString(4));
        Assert.Equal("1", sourceReader.GetString(5));
        Assert.Equal(identityNamespace, sourceReader.GetString(6));
        Assert.StartsWith("wx4:", sourceReader.GetString(7), StringComparison.Ordinal);
        Assert.False(await sourceReader.ReadAsync());
    }

    private static async Task AssertResolvedEqualSenderIdsAreRejectedAsync(
        NpgsqlDataSource dataSource,
        string accountExternalId,
        string conversationExternalId,
        string identityNamespace)
    {
        const string sql = """
            UPDATE copilot.sender_calibrations AS calibration
            SET account_self_raw_sender_id = '1',
                contact_raw_sender_id = '1',
                calibration_version = calibration.calibration_version + 1,
                perspective_state = 'Confirmed',
                evidence = 'UserConfirmed',
                confirmed_at = now()
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation ON conversation.account_id = account.id
            WHERE calibration.account_id = account.id
              AND calibration.conversation_id = conversation.id
              AND account.provider = 'local-wechat'
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id
              AND calibration.identity_namespace = @identity_namespace;
            """;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_external_id", accountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", conversationExternalId);
        command.Parameters.AddWithValue("identity_namespace", identityNamespace);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
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
