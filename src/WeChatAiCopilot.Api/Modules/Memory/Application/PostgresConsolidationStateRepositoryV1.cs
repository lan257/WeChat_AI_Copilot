using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public sealed class MemoryConsolidationStateConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// PostgreSQL state store for M3's confirmed-perspective consolidation pipeline.
/// Each method uses short transactions; model generation happens between claim and commit.
/// </summary>
public sealed class PostgresConsolidationStateRepositoryV1(NpgsqlDataSource dataSource)
    : IConsolidationStateRepositoryV1
{
    public const int BatchLeaseMinutes = 10;

    public async ValueTask<MemoryConversationScopeV1?> ResolveScopeAsync(
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || conversationId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT account_id, id, contact_id
            FROM copilot.conversations
            WHERE account_id = @account_id AND id = @conversation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MemoryConversationScopeV1(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2))
            : null;
    }

    public async ValueTask<MemoryConsolidationStateV1?> GetStateAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(accountId, conversationId, identityNamespace, calibrationVersion);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            cancellationToken);
        var state = await ReadStateAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            identityNamespace,
            calibrationVersion,
            forUpdate: false,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    public async ValueTask<bool> IsEventProcessedAsync(
        MemoryConversationScopeV1 scope,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (eventId == Guid.Empty)
        {
            return false;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM copilot.memory_consolidation_event_receipts WHERE event_id = @event_id AND account_id = @account_id AND conversation_id = @conversation_id LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        var found = await command.ExecuteScalarAsync(cancellationToken) is not null;
        await transaction.CommitAsync(cancellationToken);
        return found;
    }

    public async ValueTask RecordAccumulationAsync(
        MemoryConsolidationStateV1 nextState,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateState(nextState);
        ValidateEventId(eventId);
        if (expectedStateVersion <= 0
            || nextState.StateVersion != expectedStateVersion + 1
            || !nextState.InitialConsolidationCompleted
            || nextState.UnprofiledTextCharacters < 0)
        {
            throw new ArgumentException("The accumulation state transition is invalid.", nameof(nextState));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            nextState.AccountId,
            nextState.ConversationId,
            cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE copilot.memory_consolidation_state
            SET message_set_revision = @message_set_revision,
                observed_message_cursor = @observed_cursor,
                unprofiled_text_characters = @unprofiled_text_characters,
                profile_status = 'Accumulating',
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND state_version = @expected_state_version
              AND initial_consolidation_completed = true
              AND active_batch_id IS NULL
              AND reprocess_required = false
              AND consolidation_source_message_cursor IS NOT DISTINCT FROM @source_cursor;
            """, connection, transaction);
        AddStateKey(command, nextState.AccountId, nextState.ConversationId, nextState.IdentityNamespace, nextState.CalibrationVersion);
        command.Parameters.AddWithValue("expected_state_version", expectedStateVersion);
        command.Parameters.AddWithValue("message_set_revision", nextState.MessageSetRevision);
        AddNullableText(command, "observed_cursor", nextState.ObservedMessageCursor);
        command.Parameters.AddWithValue("unprofiled_text_characters", nextState.UnprofiledTextCharacters);
        AddNullableText(command, "source_cursor", nextState.ConsolidationSourceMessageCursor);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new MemoryConsolidationStateConflictException("The consolidation window changed before accumulation could be recorded.");
        }

        await InsertEventReceiptAsync(
            connection,
            transaction,
            nextState.AccountId,
            nextState.ConversationId,
            nextState.IdentityNamespace,
            nextState.CalibrationVersion,
            eventId,
            "Accumulated",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask RecordEmptyInitialWindowAsync(
        MemoryConsolidationStateV1 nextState,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateState(nextState);
        ValidateEventId(eventId);
        if (expectedStateVersion != 0
            || !nextState.InitialConsolidationCompleted
            || nextState.UnprofiledTextCharacters != 0
            || nextState.Status != ProfileStatusV1.Accumulating
            || nextState.StateVersion != 0)
        {
            throw new ArgumentException("The empty initial baseline transition is invalid.", nameof(nextState));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            nextState.AccountId,
            nextState.ConversationId,
            cancellationToken);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO copilot.memory_consolidation_state (
                account_id, conversation_id, identity_namespace, calibration_version,
                message_set_revision, consolidation_source_message_cursor, observed_message_cursor,
                unprofiled_text_characters, initial_consolidation_completed, reprocess_required,
                profile_status, state_version, updated_at)
            VALUES (
                @account_id, @conversation_id, @identity_namespace, @calibration_version,
                @message_set_revision, @source_cursor, @observed_cursor,
                0, true, false, 'Accumulating', 1, clock_timestamp())
            ON CONFLICT (account_id, conversation_id, identity_namespace, calibration_version)
            DO NOTHING;
            """, connection, transaction))
        {
            AddStateKey(command, nextState.AccountId, nextState.ConversationId, nextState.IdentityNamespace, nextState.CalibrationVersion);
            command.Parameters.AddWithValue("message_set_revision", nextState.MessageSetRevision);
            AddNullableText(command, "source_cursor", nextState.ConsolidationSourceMessageCursor);
            AddNullableText(command, "observed_cursor", nextState.ObservedMessageCursor);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new MemoryConsolidationStateConflictException("The initial consolidation state already exists.");
            }
        }

        await InsertEventReceiptAsync(
            connection,
            transaction,
            nextState.AccountId,
            nextState.ConversationId,
            nextState.IdentityNamespace,
            nextState.CalibrationVersion,
            eventId,
            "NoOp",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<ConsolidationBatchClaimResultV1> TryBeginBatchAsync(
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        string messageSetRevision,
        string highWaterMessageCursor,
        MemoryConsolidationBatchKindV1 batchKind,
        Guid batchId,
        Guid eventId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ValidateKey(scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
        ValidateEventId(eventId);
        if (batchId == Guid.Empty
            || expectedStateVersion < 0
            || string.IsNullOrWhiteSpace(messageSetRevision)
            || string.IsNullOrWhiteSpace(highWaterMessageCursor))
        {
            throw new ArgumentException("The consolidation batch claim is invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);

        if (await EventExistsAsync(connection, transaction, scope, eventId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.EventAlreadyProcessed);
        }

        if (await BatchExistsAsync(
            connection,
            transaction,
            scope,
            identityNamespace,
            calibrationVersion,
            messageSetRevision,
            highWaterMessageCursor,
            cancellationToken))
        {
            await InsertEventReceiptAsync(
                connection,
                transaction,
                scope.AccountId,
                scope.ConversationId,
                identityNamespace,
                calibrationVersion,
                eventId,
                "NoOp",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.BatchAlreadyCommitted);
        }

        var state = await ReadStateAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            identityNamespace,
            calibrationVersion,
            forUpdate: true,
            cancellationToken);
        if (state is null)
        {
            if (expectedStateVersion != 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.StateConflict);
            }

            var status = batchKind == MemoryConsolidationBatchKindV1.Rebuild
                ? ProfileStatusV1.Rebuilding
                : ProfileStatusV1.Consolidating;
            await using var insert = new NpgsqlCommand("""
                INSERT INTO copilot.memory_consolidation_state (
                    account_id, conversation_id, identity_namespace, calibration_version,
                    message_set_revision, profile_status, state_version, active_batch_id,
                    active_lease_until, updated_at)
                VALUES (
                    @account_id, @conversation_id, @identity_namespace, @calibration_version,
                    @message_set_revision, @profile_status, 1, @batch_id,
                    clock_timestamp() + make_interval(mins => @lease_minutes), clock_timestamp())
                ON CONFLICT (account_id, conversation_id, identity_namespace, calibration_version)
                DO NOTHING;
                """, connection, transaction);
            AddStateKey(insert, scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
            insert.Parameters.AddWithValue("message_set_revision", messageSetRevision);
            insert.Parameters.AddWithValue("profile_status", status.ToString());
            insert.Parameters.AddWithValue("batch_id", batchId);
            insert.Parameters.AddWithValue("lease_minutes", BatchLeaseMinutes);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.StateConflict);
            }

            var insertedState = await ReadStateAsync(
                connection,
                transaction,
                scope.AccountId,
                scope.ConversationId,
                identityNamespace,
                calibrationVersion,
                forUpdate: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.Claimed, insertedState);
        }

        if (state.ActiveBatchId is not null
            && await HasLiveLeaseAsync(
                connection,
                transaction,
                scope.AccountId,
                scope.ConversationId,
                identityNamespace,
                calibrationVersion,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.Busy, state);
        }

        if (state.StateVersion != expectedStateVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.StateConflict, state);
        }

        var nextStatus = batchKind == MemoryConsolidationBatchKindV1.Rebuild
            ? ProfileStatusV1.Rebuilding
            : ProfileStatusV1.Consolidating;
        await using (var update = new NpgsqlCommand("""
            UPDATE copilot.memory_consolidation_state
            SET profile_status = @profile_status,
                active_batch_id = @batch_id,
                active_lease_until = clock_timestamp() + make_interval(mins => @lease_minutes),
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND state_version = @expected_state_version
              AND (active_batch_id IS NULL OR active_lease_until <= clock_timestamp());
            """, connection, transaction))
        {
            AddStateKey(update, scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
            update.Parameters.AddWithValue("profile_status", nextStatus.ToString());
            update.Parameters.AddWithValue("batch_id", batchId);
            update.Parameters.AddWithValue("lease_minutes", BatchLeaseMinutes);
            update.Parameters.AddWithValue("expected_state_version", expectedStateVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.StateConflict, state);
            }
        }

        var claimedState = await ReadStateAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            identityNamespace,
            calibrationVersion,
            forUpdate: false,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConsolidationBatchClaimResultV1(ConsolidationBatchClaimOutcomeV1.Claimed, claimedState);
    }

    public async ValueTask MarkReprocessRequiredAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        long expectedStateVersion,
        string observedMessageSetRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(accountId, conversationId, identityNamespace, calibrationVersion);
        if (expectedStateVersion <= 0 || string.IsNullOrWhiteSpace(observedMessageSetRevision))
        {
            throw new ArgumentException("A current state version and observed revision are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE copilot.memory_consolidation_state
            SET profile_status = 'Rebuilding',
                reprocess_required = true,
                reprocess_required_revision = @observed_revision,
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND state_version = @expected_state_version
              AND active_batch_id IS NULL;
            """, connection, transaction);
        AddStateKey(command, accountId, conversationId, identityNamespace, calibrationVersion);
        command.Parameters.AddWithValue("expected_state_version", expectedStateVersion);
        command.Parameters.AddWithValue("observed_revision", observedMessageSetRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new MemoryConsolidationStateConflictException("The consolidation state changed before reprocessing could be marked.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask MarkBatchFailedAsync(
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        Guid batchId,
        long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(accountId, conversationId, identityNamespace, calibrationVersion);
        if (batchId == Guid.Empty || expectedStateVersion <= 0)
        {
            throw new ArgumentException("The active batch and state version are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE copilot.memory_consolidation_state
            SET profile_status = 'Failed',
                active_batch_id = NULL,
                active_lease_until = NULL,
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND active_batch_id = @batch_id
              AND state_version = @expected_state_version;
            """, connection, transaction);
        AddStateKey(command, accountId, conversationId, identityNamespace, calibrationVersion);
        command.Parameters.AddWithValue("batch_id", batchId);
        command.Parameters.AddWithValue("expected_state_version", expectedStateVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new MemoryConsolidationStateConflictException("The active consolidation batch changed before failure could be recorded.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CommitGeneratedBatchAsync(
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateScope(commit.Scope);
        ValidateEventId(commit.EventId);
        if (commit.BatchId == Guid.Empty || commit.ExpectedStateVersion <= 0)
        {
            throw new ArgumentException("The generated batch claim is incomplete.", nameof(commit));
        }

        var capture = commit.Capture;
        var outputViolations = MemoryConsolidationOutputValidatorV1.Validate(commit.Output, capture);
        if (outputViolations.Count > 0)
        {
            throw new MemoryConsolidationOutputException(outputViolations);
        }

        ValidateCommitScope(commit);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            commit.Scope.AccountId,
            commit.Scope.ConversationId,
            cancellationToken);

        if (await EventExistsAsync(connection, transaction, commit.Scope, commit.EventId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var state = await ReadStateAsync(
            connection,
            transaction,
            commit.Scope.AccountId,
            commit.Scope.ConversationId,
            capture.IdentityNamespace,
            capture.CalibrationVersion,
            forUpdate: true,
            cancellationToken);
        if (state is null
            || state.ActiveBatchId != commit.BatchId
            || state.StateVersion != commit.ExpectedStateVersion)
        {
            throw new MemoryConsolidationStateConflictException("The consolidation batch claim is no longer current.");
        }

        var batchInserted = await InsertBatchAsync(connection, transaction, commit, cancellationToken);
        if (!batchInserted)
        {
            throw new MemoryConsolidationStateConflictException("A consolidation batch with this revision and high-water cursor already exists.");
        }

        if (commit.BatchKind == MemoryConsolidationBatchKindV1.Rebuild)
        {
            await SupersedeAllAutomaticOutputsAsync(
                connection,
                transaction,
                commit.Scope,
                capture.IdentityNamespace,
                capture.CalibrationVersion,
                cancellationToken);
        }

        await InsertProfilesAsync(connection, transaction, commit, cancellationToken);
        await InsertMemoryItemsAsync(connection, transaction, commit, cancellationToken);
        await UpdateStateAfterCommitAsync(connection, transaction, commit, cancellationToken);
        await InsertEventReceiptAsync(
            connection,
            transaction,
            commit.Scope.AccountId,
            commit.Scope.ConversationId,
            capture.IdentityNamespace,
            capture.CalibrationVersion,
            commit.EventId,
            "Consolidated",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<MemoryConsolidationStateV1?> ReadStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT message_set_revision,
                   consolidation_source_message_cursor,
                   observed_message_cursor,
                   unprofiled_text_characters,
                   initial_consolidation_completed,
                   reprocess_required,
                   reprocess_required_revision,
                   profile_status,
                   state_version,
                   active_batch_id,
                   active_lease_until
            FROM copilot.memory_consolidation_state
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddStateKey(command, accountId, conversationId, identityNamespace, calibrationVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemoryConsolidationStateV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            IdentityNamespace = identityNamespace,
            CalibrationVersion = calibrationVersion,
            MessageSetRevision = reader.GetString(0),
            ConsolidationSourceMessageCursor = reader.IsDBNull(1) ? null : reader.GetString(1),
            ObservedMessageCursor = reader.IsDBNull(2) ? null : reader.GetString(2),
            UnprofiledTextCharacters = reader.GetInt32(3),
            InitialConsolidationCompleted = reader.GetBoolean(4),
            ReprocessRequired = reader.GetBoolean(5),
            ReprocessRequiredRevision = reader.IsDBNull(6) ? null : reader.GetString(6),
            Status = Enum.Parse<ProfileStatusV1>(reader.GetString(7), ignoreCase: false),
            StateVersion = reader.GetInt64(8),
            ActiveBatchId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
            ActiveLeaseUntil = reader.IsDBNull(10) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc))
        };
    }

    private static async Task<bool> EventExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConversationScopeV1 scope,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1
            FROM copilot.memory_consolidation_event_receipts
            WHERE event_id = @event_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasLiveLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT active_batch_id IS NOT NULL AND active_lease_until > clock_timestamp()
            FROM copilot.memory_consolidation_state
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version;
            """, connection, transaction);
        AddStateKey(command, accountId, conversationId, identityNamespace, calibrationVersion);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> BatchExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        string messageSetRevision,
        string highWaterMessageCursor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1
            FROM copilot.memory_consolidation_batches
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND message_set_revision = @message_set_revision
              AND high_water_message_cursor = @high_water_cursor
            LIMIT 1;
            """, connection, transaction);
        AddStateKey(command, scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
        command.Parameters.AddWithValue("message_set_revision", messageSetRevision);
        command.Parameters.AddWithValue("high_water_cursor", highWaterMessageCursor);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> InsertBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.memory_consolidation_batches (
                id, account_id, conversation_id, identity_namespace, calibration_version,
                message_set_revision, high_water_message_cursor, batch_kind,
                eligible_message_count, eligible_text_code_points)
            VALUES (
                @id, @account_id, @conversation_id, @identity_namespace, @calibration_version,
                @message_set_revision, @high_water_cursor, @batch_kind,
                @eligible_message_count, @eligible_text_code_points)
            ON CONFLICT (
                account_id, conversation_id, identity_namespace, calibration_version,
                message_set_revision, high_water_message_cursor)
            DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", commit.BatchId);
        AddStateKey(command, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
        command.Parameters.AddWithValue("message_set_revision", commit.Capture.MessageSetRevision);
        command.Parameters.AddWithValue("high_water_cursor", commit.Capture.HighWaterMessageCursor!);
        command.Parameters.AddWithValue("batch_kind", commit.BatchKind.ToString());
        command.Parameters.AddWithValue("eligible_message_count", commit.Capture.EligibleMessages.Count);
        command.Parameters.AddWithValue("eligible_text_code_points", commit.Capture.EligibleTextCodePoints);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task SupersedeAllAutomaticOutputsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConversationScopeV1 scope,
        string identityNamespace,
        long calibrationVersion,
        CancellationToken cancellationToken)
    {
        const string memorySql = """
            UPDATE copilot.memory_items
            SET status = 'Superseded', version = version + 1, updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND origin = 'MessageExtracted'
              AND status = 'Active';
            """;
        await using (var command = new NpgsqlCommand(memorySql, connection, transaction))
        {
            AddStateKey(command, scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string profileSql = """
            UPDATE copilot.participant_profile_snapshots
            SET status = 'Superseded'
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND status = 'Current';
            """;
        await using var profileCommand = new NpgsqlCommand(profileSql, connection, transaction);
        AddStateKey(profileCommand, scope.AccountId, scope.ConversationId, identityNamespace, calibrationVersion);
        await profileCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken)
    {
        foreach (var profile in commit.Output.ParticipantProfiles)
        {
            var subjectId = ResolveSubjectId(commit.Scope, profile.SubjectType);
            await using (var supersede = new NpgsqlCommand("""
                UPDATE copilot.participant_profile_snapshots
                SET status = 'Superseded'
                WHERE account_id = @account_id
                  AND conversation_id = @conversation_id
                  AND identity_namespace = @identity_namespace
                  AND calibration_version = @calibration_version
                  AND subject_type = @subject_type
                  AND subject_id = @subject_id
                  AND status = 'Current';
                """, connection, transaction))
            {
                AddStateKey(supersede, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
                supersede.Parameters.AddWithValue("subject_type", profile.SubjectType.ToString());
                supersede.Parameters.AddWithValue("subject_id", subjectId);
                await supersede.ExecuteNonQueryAsync(cancellationToken);
            }

            const string getRevisionSql = """
                SELECT COALESCE(MAX(profile_revision), 0) + 1
                FROM copilot.participant_profile_snapshots
                WHERE account_id = @account_id
                  AND conversation_id = @conversation_id
                  AND identity_namespace = @identity_namespace
                  AND calibration_version = @calibration_version
                  AND subject_type = @subject_type
                  AND subject_id = @subject_id;
                """;
            long profileRevision;
            await using (var revisionCommand = new NpgsqlCommand(getRevisionSql, connection, transaction))
            {
                AddStateKey(revisionCommand, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
                revisionCommand.Parameters.AddWithValue("subject_type", profile.SubjectType.ToString());
                revisionCommand.Parameters.AddWithValue("subject_id", subjectId);
                profileRevision = (long)(await revisionCommand.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Could not assign a participant profile revision."));
            }

            var profileId = Guid.NewGuid();
            const string insertSql = """
                INSERT INTO copilot.participant_profile_snapshots (
                    id, account_id, conversation_id, identity_namespace, subject_type, subject_id,
                    profile_revision, calibration_version, status, summary, source_message_cursor)
                VALUES (
                    @id, @account_id, @conversation_id, @identity_namespace, @subject_type, @subject_id,
                    @profile_revision, @calibration_version, 'Current', @summary, @source_cursor);
                """;
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", profileId);
                AddStateKey(insert, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
                insert.Parameters.AddWithValue("subject_type", profile.SubjectType.ToString());
                insert.Parameters.AddWithValue("subject_id", subjectId);
                insert.Parameters.AddWithValue("profile_revision", profileRevision);
                insert.Parameters.AddWithValue("summary", profile.Summary!);
                insert.Parameters.AddWithValue("source_cursor", commit.Capture.HighWaterMessageCursor!);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertProfileEvidenceAsync(connection, transaction, profileId, commit.Scope, profile.EvidenceMessageIds, cancellationToken);
        }
    }

    private static async Task InsertProfileEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        MemoryConversationScopeV1 scope,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken)
    {
        if (evidenceIds.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO copilot.participant_profile_evidence (
                profile_id, account_id, conversation_id, message_id, ordinal)
            SELECT @profile_id, @account_id, @conversation_id,
                   evidence.message_id, (evidence.ordinality - 1)::integer
            FROM unnest(@message_ids) WITH ORDINALITY AS evidence(message_id, ordinality);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("profile_id", profileId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        AddGuidArray(command, "message_ids", evidenceIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMemoryItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken)
    {
        foreach (var memory in commit.Output.MemoryItems)
        {
            var memoryId = Guid.NewGuid();
            const string sql = """
                INSERT INTO copilot.memory_items (
                    id, account_id, subject_type, subject_id, conversation_id, identity_namespace,
                    text_content, category, origin, status, observed_at, calibration_version,
                    source_message_cursor, version)
                VALUES (
                    @id, @account_id, @subject_type, @subject_id, @conversation_id, @identity_namespace,
                    @text_content, @category, 'MessageExtracted', 'Active', @observed_at,
                    @calibration_version, @source_cursor, 1);
                """;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", memoryId);
                AddStateKey(command, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
                command.Parameters.AddWithValue("subject_type", memory.SubjectType.ToString());
                command.Parameters.AddWithValue("subject_id", ResolveSubjectId(commit.Scope, memory.SubjectType));
                command.Parameters.AddWithValue("text_content", memory.Text!);
                AddNullableText(command, "category", memory.Category);
                AddNullableTimestamp(command, "observed_at", memory.ObservedAt?.ToUniversalTime());
                command.Parameters.AddWithValue("source_cursor", commit.Capture.HighWaterMessageCursor!);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertMemoryEvidenceAsync(
                connection,
                transaction,
                memoryId,
                commit.Scope,
                memory.EvidenceMessageIds,
                cancellationToken);
        }
    }

    private static async Task InsertMemoryEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid memoryId,
        MemoryConversationScopeV1 scope,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.memory_evidence (
                memory_id, account_id, conversation_id, message_id, ordinal)
            SELECT @memory_id, @account_id, @conversation_id,
                   evidence.message_id, (evidence.ordinality - 1)::integer
            FROM unnest(@message_ids) WITH ORDINALITY AS evidence(message_id, ordinality);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("memory_id", memoryId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        AddGuidArray(command, "message_ids", evidenceIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateStateAfterCommitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConsolidationBatchCommitV1 commit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE copilot.memory_consolidation_state
            SET message_set_revision = @message_set_revision,
                consolidation_source_message_cursor = @source_cursor,
                observed_message_cursor = @observed_cursor,
                unprofiled_text_characters = 0,
                initial_consolidation_completed = true,
                reprocess_required = false,
                reprocess_required_revision = NULL,
                profile_status = @profile_status,
                state_version = @next_state_version,
                active_batch_id = NULL,
                active_lease_until = NULL,
                updated_at = clock_timestamp()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version
              AND active_batch_id = @batch_id
              AND state_version = @expected_state_version;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddStateKey(command, commit.Scope.AccountId, commit.Scope.ConversationId, commit.Capture.IdentityNamespace, commit.Capture.CalibrationVersion);
        command.Parameters.AddWithValue("message_set_revision", commit.Capture.MessageSetRevision);
        command.Parameters.AddWithValue("source_cursor", commit.Capture.HighWaterMessageCursor!);
        command.Parameters.AddWithValue("observed_cursor", commit.Capture.HighWaterMessageCursor!);
        command.Parameters.AddWithValue("profile_status", commit.NextState.Status.ToString());
        command.Parameters.AddWithValue("next_state_version", commit.NextState.StateVersion);
        command.Parameters.AddWithValue("batch_id", commit.BatchId);
        command.Parameters.AddWithValue("expected_state_version", commit.ExpectedStateVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new MemoryConsolidationStateConflictException("The active consolidation state changed during batch persistence.");
        }
    }

    private static async Task InsertEventReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion,
        Guid eventId,
        string outcome,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.memory_consolidation_event_receipts (
                event_id, account_id, conversation_id, identity_namespace, calibration_version, outcome)
            VALUES (
                @event_id, @account_id, @conversation_id, @identity_namespace, @calibration_version, @outcome)
            ON CONFLICT (event_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        AddStateKey(command, accountId, conversationId, identityNamespace, calibrationVersion);
        command.Parameters.AddWithValue("outcome", outcome);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid ResolveSubjectId(MemoryConversationScopeV1 scope, ParticipantSubjectTypeV1 subjectType)
    {
        try
        {
            return MemoryParticipantScopeV1.ResolveSubjectId(scope, subjectType);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new MemoryConsolidationOutputException([new MemoryRequestViolationV1("subjectType", "unsupported")]);
        }
    }

    private static void ValidateCommitScope(MemoryConsolidationBatchCommitV1 commit)
    {
        var capture = commit.Capture;
        var state = commit.NextState;
        if (capture.AccountId != commit.Scope.AccountId
            || capture.ConversationId != commit.Scope.ConversationId
            || state.AccountId != commit.Scope.AccountId
            || state.ConversationId != commit.Scope.ConversationId
            || state.CalibrationVersion != capture.CalibrationVersion
            || !string.Equals(state.IdentityNamespace, capture.IdentityNamespace, StringComparison.Ordinal)
            || !string.Equals(state.MessageSetRevision, capture.MessageSetRevision, StringComparison.Ordinal)
            || !string.Equals(state.ConsolidationSourceMessageCursor, capture.HighWaterMessageCursor, StringComparison.Ordinal)
            || !state.InitialConsolidationCompleted
            || state.UnprofiledTextCharacters != 0
            || state.ReprocessRequired
            || capture.CalibrationVersion <= 0
            || string.IsNullOrWhiteSpace(capture.IdentityNamespace)
            || string.IsNullOrWhiteSpace(capture.MessageSetRevision)
            || string.IsNullOrWhiteSpace(capture.HighWaterMessageCursor)
            || state.StateVersion != commit.ExpectedStateVersion + 1)
        {
            throw new ArgumentException("The generated batch and next consolidation state do not match.", nameof(commit));
        }
    }

    private static void ValidateScope(MemoryConversationScopeV1 scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AccountId == Guid.Empty || scope.ConversationId == Guid.Empty || scope.ContactId == Guid.Empty)
        {
            throw new ArgumentException("A complete server-resolved conversation scope is required.", nameof(scope));
        }
    }

    private static void ValidateState(MemoryConsolidationStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateKey(state.AccountId, state.ConversationId, state.IdentityNamespace, state.CalibrationVersion);
        if (string.IsNullOrWhiteSpace(state.MessageSetRevision) || state.StateVersion < 0)
        {
            throw new ArgumentException("The consolidation state is invalid.", nameof(state));
        }
    }

    private static void ValidateKey(Guid accountId, Guid conversationId, string identityNamespace, long calibrationVersion)
    {
        if (accountId == Guid.Empty
            || conversationId == Guid.Empty
            || string.IsNullOrWhiteSpace(identityNamespace)
            || calibrationVersion <= 0)
        {
            throw new ArgumentException("A complete account, conversation, identity namespace, and confirmed version are required.");
        }
    }

    private static void ValidateEventId(Guid eventId)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty M2 outbox event ID is required.", nameof(eventId));
        }
    }

    private static void AddStateKey(
        NpgsqlCommand command,
        Guid accountId,
        Guid conversationId,
        string identityNamespace,
        long calibrationVersion)
    {
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("identity_namespace", identityNamespace);
        command.Parameters.AddWithValue("calibration_version", calibrationVersion);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });

    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value is null ? DBNull.Value : value.Value
        });

    private static void AddGuidArray(NpgsqlCommand command, string name, IReadOnlyList<Guid> values) =>
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = values.ToArray()
        });
}

public sealed class MemoryConsolidationOutputException(
    IReadOnlyList<MemoryRequestViolationV1> violations) : Exception("Generated memory output failed validation.")
{
    public IReadOnlyList<MemoryRequestViolationV1> Violations { get; } = violations;
}
