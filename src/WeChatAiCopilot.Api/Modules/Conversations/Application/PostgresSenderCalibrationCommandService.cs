using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Domain;

namespace WeChatAiCopilot.Api.Modules.Conversations.Application;

public sealed class PostgresSenderCalibrationCommandService(NpgsqlDataSource dataSource)
{
    public async Task<SetWeChatAccountPerspectiveResultV1> SetPerspectiveAsync(
        SetWeChatAccountPerspectiveRequestV1 request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ContractValidatorV1.Validate(request).Count != 0)
        {
            throw new SenderCalibrationCommandException(
                "INVALID_PERSPECTIVE_REQUEST",
                "The perspective request is invalid.",
                isConflict: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new SenderCalibrationCommandException(
                "IDEMPOTENCY_KEY_REQUIRED",
                "A valid Idempotency-Key header is required.",
                isConflict: false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var sourceScopes = await ReadSourceScopesAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            request.IdentityNamespace!,
            cancellationToken);
        if (sourceScopes.Count != 1)
        {
            throw new SenderCalibrationCommandException(
                sourceScopes.Count == 0 ? "SENDER_SCOPE_UNAVAILABLE" : "SENDER_SCOPE_AMBIGUOUS",
                "Identity calibration requires one unambiguous source scope.",
                isConflict: false);
        }

        var scope = new SenderCalibrationScopeV1(
            request.AccountId,
            request.ConversationId,
            request.IdentityNamespace,
            sourceScopes[0].MessageTable,
            sourceScopes[0].ShardFingerprint);
        if (!scope.IsComparable)
        {
            throw new SenderCalibrationCommandException(
                "SENDER_SCOPE_UNAVAILABLE",
                "Identity calibration requires complete table and shard identity.",
                isConflict: false);
        }

        await PostgresMessageIngestionService.AcquireScopeLockAsync(connection, transaction, scope, cancellationToken);

        // Re-read after taking the scope lock so a newly observed shard cannot be
        // combined with the candidates from the pre-lock read.
        var lockedScopes = await ReadSourceScopesAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            request.IdentityNamespace!,
            cancellationToken);
        if (lockedScopes.Count != 1 || lockedScopes[0] != sourceScopes[0])
        {
            throw new SenderCalibrationCommandException(
                "SENDER_SCOPE_CHANGED",
                "The sender identity scope changed while the request was being applied.",
                isConflict: true);
        }

        var current = await ReadCalibrationAsync(connection, transaction, scope, cancellationToken);
        if (current is null)
        {
            throw new SenderCalibrationCommandException(
                "CALIBRATION_NOT_INITIALIZED",
                "The calibration state is not available for this source scope.",
                isConflict: false);
        }

        var priorCommand = await FindIdempotentResultAsync(
            connection,
            transaction,
            current.Id,
            idempotencyKey,
            cancellationToken);
        if (priorCommand is not null)
        {
            if (!string.Equals(priorCommand.AccountSelfRawSenderId, request.AccountSelfRawSenderId, StringComparison.Ordinal))
            {
                throw new SenderCalibrationCommandException(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The Idempotency-Key was already used for a different sender selection.",
                    isConflict: true);
            }

            await transaction.CommitAsync(cancellationToken);
            return ToResult(request, priorCommand);
        }

        if (request.ExpectedCalibrationVersion != current.CalibrationVersion)
        {
            throw new SenderCalibrationCommandException(
                "CALIBRATION_VERSION_CONFLICT",
                "The perspective changed. Refresh the current view and confirm again.",
                isConflict: true);
        }

        var observations = await ReadObservedSendersAsync(connection, transaction, scope, cancellationToken);
        var confirmed = SenderPerspectiveResolver.ConfirmSelection(
            scope,
            observations,
            request.AccountSelfRawSenderId!);
        if (confirmed.State != PerspectiveStateV1.Confirmed)
        {
            throw new SenderCalibrationCommandException(
                "SENDER_SELECTION_NOT_ELIGIBLE",
                "The selected sender ID is not one of the two comparable candidates in this scope.",
                isConflict: false);
        }

        if (current.State == PerspectiveStateV1.Confirmed
            && string.Equals(current.AccountSelfRawSenderId, confirmed.AccountSelfRawSenderId, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(request, new IdempotentResult(
                current.CalibrationVersion,
                current.AccountSelfRawSenderId!,
                current.ContactRawSenderId!,
                0));
        }

        var nextVersion = checked(current.CalibrationVersion + 1);
        var affectedMessageCount = await ConfirmAndReprojectAsync(
            connection,
            transaction,
            scope,
            confirmed,
            nextVersion,
            cancellationToken);

        await InsertCalibrationEventAsync(
            connection,
            transaction,
            current.Id,
            nextVersion,
            confirmed.AccountSelfRawSenderId!,
            confirmed.ContactRawSenderId!,
            idempotencyKey,
            affectedMessageCount,
            cancellationToken);
        await InsertPerspectiveOutboxEventsAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            nextVersion,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return ToResult(request, new IdempotentResult(
            nextVersion,
            confirmed.AccountSelfRawSenderId!,
            confirmed.ContactRawSenderId!,
            affectedMessageCount));
    }

    private static async Task<List<SourceScope>> ReadSourceScopesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT message_table, shard_fingerprint
            FROM copilot.message_sources
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND match_status IN ('Created', 'Matched');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("identity_namespace", identityNamespace);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var scopes = new List<SourceScope>();
        while (await reader.ReadAsync(cancellationToken))
        {
            scopes.Add(new SourceScope(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return scopes;
    }

    private static async Task<SenderCalibrationState?> ReadCalibrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderCalibrationScopeV1 scope,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, calibration_version, perspective_state, account_self_raw_sender_id, contact_raw_sender_id
            FROM copilot.sender_calibrations
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND message_table = @message_table
              AND shard_fingerprint = @shard_fingerprint
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SenderCalibrationState(
            reader.GetGuid(0),
            reader.GetInt64(1),
            Enum.Parse<PerspectiveStateV1>(reader.GetString(2), ignoreCase: false),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<IdempotentResult?> FindIdempotentResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid calibrationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT calibration_version, account_self_raw_sender_id, contact_raw_sender_id, affected_message_count
            FROM copilot.sender_calibration_events
            WHERE calibration_id = @calibration_id AND idempotency_key = @idempotency_key;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("calibration_id", calibrationId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IdempotentResult(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static async Task<List<ScopedSenderIdentityV1>> ReadObservedSendersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderCalibrationScopeV1 scope,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT raw_sender_id
            FROM copilot.message_sources
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND message_table = @message_table
              AND shard_fingerprint = @shard_fingerprint
              AND raw_sender_id IS NOT NULL
              AND match_status IN ('Created', 'Matched');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var identities = new List<ScopedSenderIdentityV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            identities.Add(new ScopedSenderIdentityV1(scope, reader.GetString(0)));
        }

        return identities;
    }

    private static async Task<long> ConfirmAndReprojectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderCalibrationScopeV1 scope,
        SenderPerspectiveV1 confirmed,
        long version,
        CancellationToken cancellationToken)
    {
        const string updateCalibrationSql = """
            UPDATE copilot.sender_calibrations
            SET account_self_raw_sender_id = @self_sender_id,
                contact_raw_sender_id = @contact_sender_id,
                calibration_version = @calibration_version,
                perspective_state = 'Confirmed',
                evidence = 'UserConfirmed',
                confirmed_at = now(),
                updated_at = now()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND message_table = @message_table
              AND shard_fingerprint = @shard_fingerprint;
            """;
        await using (var updateCalibration = new NpgsqlCommand(updateCalibrationSql, connection, transaction))
        {
            AddScope(updateCalibration, scope);
            updateCalibration.Parameters.AddWithValue("self_sender_id", confirmed.AccountSelfRawSenderId!);
            updateCalibration.Parameters.AddWithValue("contact_sender_id", confirmed.ContactRawSenderId!);
            updateCalibration.Parameters.AddWithValue("calibration_version", version);
            var updated = await updateCalibration.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new SenderCalibrationCommandException(
                    "CALIBRATION_STATE_UNAVAILABLE",
                    "The sender calibration changed while the request was being applied.",
                    isConflict: true);
            }
        }

        const string reprojectSql = """
            WITH scoped_senders AS (
                SELECT
                    message_id,
                    CASE WHEN count(DISTINCT raw_sender_id) = 1 THEN max(raw_sender_id) ELSE NULL END AS raw_sender_id
                FROM copilot.message_sources
                WHERE account_id = @account_id
                  AND conversation_id = @conversation_id
                  AND identity_namespace = @identity_namespace
                  AND message_table = @message_table
                  AND shard_fingerprint = @shard_fingerprint
                  AND message_id IS NOT NULL
                  AND match_status IN ('Created', 'Matched')
                GROUP BY message_id
            )
            UPDATE copilot.chat_messages AS message
            SET direction = CASE
                    WHEN scoped_senders.raw_sender_id = @self_sender_id THEN 'Outgoing'
                    WHEN scoped_senders.raw_sender_id = @contact_sender_id THEN 'Incoming'
                    ELSE 'Unknown'
                END,
                direction_evidence = CASE
                    WHEN scoped_senders.raw_sender_id IN (@self_sender_id, @contact_sender_id) THEN 'UserConfirmed'
                    ELSE 'Unresolved'
                END
            FROM scoped_senders
            WHERE message.id = scoped_senders.message_id
              AND message.account_id = @account_id;
            """;
        await using var reproject = new NpgsqlCommand(reprojectSql, connection, transaction);
        AddScope(reproject, scope);
        reproject.Parameters.AddWithValue("self_sender_id", confirmed.AccountSelfRawSenderId!);
        reproject.Parameters.AddWithValue("contact_sender_id", confirmed.ContactRawSenderId!);
        return await reproject.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCalibrationEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid calibrationId,
        long version,
        string selfSenderId,
        string contactSenderId,
        string idempotencyKey,
        long affectedMessageCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.sender_calibration_events(
                calibration_id,
                calibration_version,
                account_self_raw_sender_id,
                contact_raw_sender_id,
                idempotency_key,
                affected_message_count)
            VALUES (
                @calibration_id,
                @calibration_version,
                @self_sender_id,
                @contact_sender_id,
                @idempotency_key,
                @affected_message_count);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("calibration_id", calibrationId);
        command.Parameters.AddWithValue("calibration_version", version);
        command.Parameters.AddWithValue("self_sender_id", selfSenderId);
        command.Parameters.AddWithValue("contact_sender_id", contactSenderId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("affected_message_count", affectedMessageCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPerspectiveOutboxEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        long version,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.conversation_change_outbox(
                account_id, conversation_id, event_type, calibration_version)
            VALUES
                (@account_id, @conversation_id, 'conversation.perspective-changed', @version),
                (@account_id, @conversation_id, 'conversation.perspective-confirmed', @version);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddScope(NpgsqlCommand command, SenderCalibrationScopeV1 scope)
    {
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", scope.ShardFingerprint!);
    }

    private static SetWeChatAccountPerspectiveResultV1 ToResult(
        SetWeChatAccountPerspectiveRequestV1 request,
        IdempotentResult stored) => new()
    {
        AccountId = request.AccountId,
        ConversationId = request.ConversationId,
        IdentityNamespace = request.IdentityNamespace,
        CalibrationVersion = stored.Version,
        Mapping = new SenderMappingV1
        {
            AccountSelfRawSenderId = stored.AccountSelfRawSenderId,
            ContactRawSenderId = stored.ContactRawSenderId
        },
        Evidence = PerspectiveEvidenceV1.UserConfirmed,
        AffectedMessageCount = stored.AffectedMessageCount
    };

    private sealed record SourceScope(string? MessageTable, string? ShardFingerprint);
    private sealed record SenderCalibrationState(
        Guid Id,
        long CalibrationVersion,
        PerspectiveStateV1 State,
        string? AccountSelfRawSenderId,
        string? ContactRawSenderId);
    private sealed record IdempotentResult(
        long Version,
        string AccountSelfRawSenderId,
        string ContactRawSenderId,
        long AffectedMessageCount);
}

public sealed class SenderCalibrationCommandException(
    string code,
    string message,
    bool isConflict) : Exception(message)
{
    public string Code { get; } = code;
    public bool IsConflict { get; } = isConflict;
}
