using System.Data;
using System.Globalization;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

internal sealed record WebConversationTarget(
    string Provider,
    string AccountExternalId,
    string ConversationExternalId);

/// <summary>
/// Read-only access to an account/conversation resolved by the Web API scope resolver.
/// </summary>
internal sealed class PostgresWebConversationReadServiceV1(NpgsqlDataSource dataSource)
{
    public async Task<ConversationSnapshotV1?> GetCurrentConversationAsync(
        WebConversationTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var conversation = await ReadConversationAsync(
            connection,
            transaction,
            target,
            cancellationToken);
        if (conversation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var accountWxid = await ReadAccountWxidAsync(
            connection,
            transaction,
            conversation.AccountId,
            cancellationToken);
        var identityAvailable = !string.IsNullOrWhiteSpace(accountWxid);

        await transaction.CommitAsync(cancellationToken);
        return new ConversationSnapshotV1
        {
            AccountId = conversation.AccountId,
            ConversationId = conversation.ConversationId,
            DisplayName = conversation.DisplayName,
            ConversationType = conversation.ConversationType,
            LastMessageCursor = conversation.LastMessageCursor,
            MessageSetRevision = conversation.MessageSetRevision.ToString(CultureInfo.InvariantCulture),
            CalibrationVersion = identityAvailable ? 1 : 0,
            PerspectiveState = identityAvailable ? PerspectiveStateV1.Confirmed : PerspectiveStateV1.Unresolved,
            EffectiveSelfRawSenderId = null,
            PerspectiveEvidence = null,
            CaptureState = conversation.CaptureState
        };
    }

    public async Task<MessageQuerySnapshotV1?> GetCurrentMessagesAsync(
        IMessageQueryV1 messageQuery,
        WebConversationTarget target,
        int limit,
        Guid? anchorMessageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var conversation = await ReadConversationAsync(
            connection,
            transaction: null,
            target,
            cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        return await messageQuery.GetRecentMessagesAsync(
            new MessageQueryRequestV1
            {
                AccountId = conversation.AccountId,
                ConversationId = conversation.ConversationId,
                Limit = limit,
                AnchorMessageId = anchorMessageId
            },
            cancellationToken);
    }

    private static async Task<string?> ReadAccountWxidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT account_wxid FROM copilot.chat_accounts WHERE id = @account_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<CurrentConversationRow?> ReadConversationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        WebConversationTarget target,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT conversation.id,
                   conversation.account_id,
                   contact.display_name,
                   conversation.conversation_type,
                   conversation.last_message_cursor,
                   conversation.message_set_revision,
                   CASE
                       WHEN target.conversation_id IS NULL THEN 'NotMonitored'
                       WHEN reader_status.account_id IS NULL THEN 'Unknown'
                       WHEN reader_status.heartbeat_at < now() - interval '20 seconds' THEN 'Offline'
                       WHEN reader_status.state = 'Error' THEN 'Error'
                       WHEN reader_status.target_version <> COALESCE(target_set.target_version, 0) THEN 'Starting'
                       ELSE reader_status.state
                   END AS capture_state
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
            JOIN copilot.contacts AS contact
              ON contact.id = conversation.contact_id
             AND contact.account_id = conversation.account_id
            LEFT JOIN copilot.monitored_conversation_targets AS target
              ON target.account_id = conversation.account_id
             AND target.conversation_id = conversation.id
            LEFT JOIN copilot.reader_target_sets AS target_set
              ON target_set.account_id = conversation.account_id
            LEFT JOIN copilot.reader_status AS reader_status
              ON reader_status.account_id = conversation.account_id
            WHERE account.provider = @provider
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("provider", target.Provider);
        command.Parameters.AddWithValue("account_external_id", target.AccountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", target.ConversationExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CurrentConversationRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6));
    }

    private sealed record CurrentConversationRow(
        Guid ConversationId,
        Guid AccountId,
        string? DisplayName,
        string ConversationType,
        string? LastMessageCursor,
        long MessageSetRevision,
        string CaptureState);
}
