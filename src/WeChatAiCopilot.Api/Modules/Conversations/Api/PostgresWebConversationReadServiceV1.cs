using System.Data;
using System.Globalization;
using System.Numerics;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Domain;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

internal sealed record WebConversationTarget(
    string Provider,
    string AccountExternalId,
    string ConversationExternalId);

/// <summary>
/// Read-only access to the single account/conversation configured for the Web client.
/// The caller supplies only server configuration; request values never select a wider
/// account or conversation scope.
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

        var scopes = await ReadPerspectiveScopesAsync(
            connection,
            transaction,
            conversation.AccountId,
            conversation.ConversationId,
            cancellationToken);
        var onlyScope = scopes.Count == 1 ? scopes[0] : null;

        await transaction.CommitAsync(cancellationToken);
        return new ConversationSnapshotV1
        {
            AccountId = conversation.AccountId,
            ConversationId = conversation.ConversationId,
            DisplayName = conversation.DisplayName,
            ConversationType = conversation.ConversationType,
            LastMessageCursor = conversation.LastMessageCursor,
            MessageSetRevision = conversation.MessageSetRevision.ToString(CultureInfo.InvariantCulture),
            CalibrationVersion = onlyScope?.CalibrationVersion ?? 0,
            PerspectiveState = onlyScope?.PerspectiveState ?? PerspectiveStateV1.Unresolved,
            EffectiveSelfRawSenderId = onlyScope?.EffectiveSelfRawSenderId,
            PerspectiveEvidence = onlyScope?.Evidence,
            CaptureState = "Inactive"
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

    public async Task<CurrentSenderPerspectiveResponseV1?> GetCurrentSenderPerspectiveAsync(
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

        var scopes = await ReadPerspectiveScopesAsync(
            connection,
            transaction,
            conversation.AccountId,
            conversation.ConversationId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CurrentSenderPerspectiveResponseV1
        {
            AccountId = conversation.AccountId,
            ConversationId = conversation.ConversationId,
            Scopes = scopes
        };
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
                   conversation.message_set_revision
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
            JOIN copilot.contacts AS contact
              ON contact.id = conversation.contact_id
             AND contact.account_id = conversation.account_id
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
            reader.GetInt64(5));
    }

    private static async Task<IReadOnlyList<SenderPerspectiveScopeViewV1>> ReadPerspectiveScopesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH scopes AS (
                SELECT identity_namespace, message_table, shard_fingerprint
                FROM copilot.sender_calibrations
                WHERE account_id = @account_id AND conversation_id = @conversation_id
                UNION
                SELECT identity_namespace, message_table, shard_fingerprint
                FROM copilot.message_sources
                WHERE account_id = @account_id
                  AND conversation_id = @conversation_id
                  AND identity_namespace IS NOT NULL
                  AND raw_sender_id IS NOT NULL
                  AND match_status IN ('Created', 'Matched')
            )
            SELECT scopes.identity_namespace,
                   scopes.message_table,
                   scopes.shard_fingerprint,
                   calibration.calibration_version,
                   calibration.perspective_state,
                   calibration.evidence,
                   calibration.account_self_raw_sender_id,
                   calibration.contact_raw_sender_id,
                   COALESCE((
                       SELECT array_agg(sender_ids.raw_sender_id ORDER BY sender_ids.raw_sender_id COLLATE "C")
                       FROM (
                           SELECT DISTINCT source.raw_sender_id
                           FROM copilot.message_sources AS source
                           WHERE source.account_id = @account_id
                             AND source.conversation_id = @conversation_id
                             AND source.identity_namespace IS NOT DISTINCT FROM scopes.identity_namespace
                             AND source.message_table IS NOT DISTINCT FROM scopes.message_table
                             AND source.shard_fingerprint IS NOT DISTINCT FROM scopes.shard_fingerprint
                             AND source.raw_sender_id IS NOT NULL
                             AND source.match_status IN ('Created', 'Matched')
                       ) AS sender_ids
                   ), ARRAY[]::text[]) AS candidate_raw_sender_ids
            FROM scopes
            LEFT JOIN copilot.sender_calibrations AS calibration
              ON calibration.account_id = @account_id
             AND calibration.conversation_id = @conversation_id
             AND calibration.identity_namespace IS NOT DISTINCT FROM scopes.identity_namespace
             AND calibration.message_table IS NOT DISTINCT FROM scopes.message_table
             AND calibration.shard_fingerprint IS NOT DISTINCT FROM scopes.shard_fingerprint
            ORDER BY scopes.identity_namespace NULLS FIRST,
                     scopes.message_table NULLS FIRST,
                     scopes.shard_fingerprint NULLS FIRST;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);

        var scopes = new List<SenderPerspectiveScopeViewV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identityNamespace = reader.IsDBNull(0) ? null : reader.GetString(0);
            var messageTable = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shardFingerprint = reader.IsDBNull(2) ? null : reader.GetString(2);
            var calibrationVersion = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            var calibrationState = reader.IsDBNull(4)
                ? (PerspectiveStateV1?)null
                : Enum.Parse<PerspectiveStateV1>(reader.GetString(4), ignoreCase: false);
            var calibrationEvidence = reader.IsDBNull(5)
                ? (PerspectiveEvidenceV1?)null
                : Enum.Parse<PerspectiveEvidenceV1>(reader.GetString(5), ignoreCase: false);
            var storedSelfSenderId = reader.IsDBNull(6) ? null : reader.GetString(6);
            var storedContactSenderId = reader.IsDBNull(7) ? null : reader.GetString(7);
            var candidateSenderIds = SortCandidateIds(reader.GetFieldValue<string[]>(8));

            var scope = new SenderCalibrationScopeV1(
                accountId,
                conversationId,
                identityNamespace,
                messageTable,
                shardFingerprint);
            var defaultPerspective = SenderPerspectiveResolver.ResolveDefault(
                scope,
                candidateSenderIds.Select(senderId => new ScopedSenderIdentityV1(scope, senderId)));
            var hasComparablePair = defaultPerspective.State == PerspectiveStateV1.PendingConfirmation;
            var storedMappingMatchesCandidates = hasComparablePair
                && storedSelfSenderId is not null
                && storedContactSenderId is not null
                && candidateSenderIds.Contains(storedSelfSenderId, StringComparer.Ordinal)
                && candidateSenderIds.Contains(storedContactSenderId, StringComparer.Ordinal)
                && !string.Equals(storedSelfSenderId, storedContactSenderId, StringComparison.Ordinal);

            var state = !hasComparablePair
                ? PerspectiveStateV1.Unresolved
                : storedMappingMatchesCandidates
                    ? calibrationState ?? defaultPerspective.State
                    : defaultPerspective.State;
            var evidence = state switch
            {
                PerspectiveStateV1.Confirmed when storedMappingMatchesCandidates => calibrationEvidence,
                PerspectiveStateV1.PendingConfirmation when storedMappingMatchesCandidates => calibrationEvidence,
                PerspectiveStateV1.PendingConfirmation => defaultPerspective.Evidence,
                _ => null
            };
            var effectiveSelfSenderId = state == PerspectiveStateV1.Unresolved
                ? null
                : storedMappingMatchesCandidates
                    ? storedSelfSenderId
                    : defaultPerspective.AccountSelfRawSenderId;
            var contactSenderId = state == PerspectiveStateV1.Unresolved
                ? null
                : storedMappingMatchesCandidates
                    ? storedContactSenderId
                    : defaultPerspective.ContactRawSenderId;

            scopes.Add(new SenderPerspectiveScopeViewV1
            {
                IdentityNamespace = identityNamespace,
                MessageTable = messageTable,
                ShardFingerprint = shardFingerprint,
                CalibrationVersion = calibrationVersion,
                PerspectiveState = state,
                Evidence = evidence,
                CandidateRawSenderIds = candidateSenderIds,
                DefaultSelfRawSenderId = hasComparablePair
                    ? defaultPerspective.AccountSelfRawSenderId
                    : null,
                EffectiveSelfRawSenderId = effectiveSelfSenderId,
                ContactRawSenderId = contactSenderId,
                CanConfirm = hasComparablePair
            });
        }

        return scopes;
    }

    private static IReadOnlyList<string> SortCandidateIds(IEnumerable<string> candidateIds)
    {
        var values = candidateIds.ToList();
        values.Sort((left, right) =>
        {
            var leftIsNumeric = BigInteger.TryParse(
                left,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var leftNumber);
            var rightIsNumeric = BigInteger.TryParse(
                right,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rightNumber);
            if (leftIsNumeric && rightIsNumeric)
            {
                var numericOrder = leftNumber.CompareTo(rightNumber);
                if (numericOrder != 0)
                {
                    return numericOrder;
                }
            }

            return StringComparer.Ordinal.Compare(left, right);
        });
        return values;
    }

    private sealed record CurrentConversationRow(
        Guid ConversationId,
        Guid AccountId,
        string? DisplayName,
        string ConversationType,
        string? LastMessageCursor,
        long MessageSetRevision);
}
