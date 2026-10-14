using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Domain;

[assembly: InternalsVisibleTo("WeChatAiCopilot.DomainTests")]

namespace WeChatAiCopilot.Api.Modules.Conversations.Application;

public sealed class PostgresMessageIngestionService(NpgsqlDataSource dataSource)
{
    internal const int MaxHistoryBatchItems = 500;

    public Task<IngestReceiptV1> IngestAsync(
        RawMessageEventV1 message,
        CancellationToken cancellationToken = default)
        => IngestCoreAsync(message, jobId: null, cancellationToken);

    public Task<IngestReceiptV1> IngestHistoryItemAsync(
        RawMessageEventV1 message,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A history job ID is required.", nameof(jobId));
        }

        return IngestCoreAsync(message, jobId, cancellationToken);
    }

    public async Task<HistoryBatchReceiptV1> IngestHistoryBatchAsync(
        HistoryBatchV1 batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Items is { } items)
        {
            EnsureHistoryBatchSize(items.Count);
        }

        var violations = ContractValidatorV1.Validate(batch);
        if (batch.Items is null
            || violations.Any(violation => !violation.Path.StartsWith("items[", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The history batch envelope is invalid.", nameof(batch));
        }

        var invalidItemIndexes = violations
            .Select(violation => ParseItemIndex(violation.Path))
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .ToHashSet();
        var failed = invalidItemIndexes.Count;
        var matched = 0;
        var created = 0;
        var needsReview = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        for (var index = 0; index < batch.Items.Count; index++)
        {
            if (invalidItemIndexes.Contains(index))
            {
                continue;
            }

            var receipt = await IngestWithinTransactionAsync(
                connection,
                transaction,
                batch.Items[index],
                batch.JobId!,
                cancellationToken);
            switch (receipt.MatchStatus)
            {
                case MatchStatusV1.Created:
                    created++;
                    break;
                case MatchStatusV1.Matched:
                    matched++;
                    break;
                case MatchStatusV1.NeedsReview:
                    needsReview++;
                    break;
            }
        }

        var batchReceipt = await RecordHistoryBatchAsync(
            connection,
            transaction,
            batch,
            read: batch.Items.Count,
            matched,
            created,
            needsReview,
            failed,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batchReceipt;
    }

    private async Task<IngestReceiptV1> IngestCoreAsync(
        RawMessageEventV1 message,
        string? jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var receipt = await IngestWithinTransactionAsync(
            connection,
            transaction,
            message,
            jobId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<IngestReceiptV1> IngestWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RawMessageEventV1 message,
        string? jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (ContractValidatorV1.Validate(message).Count != 0)
        {
            throw new ArgumentException("The event does not satisfy RawMessageEventV1.", nameof(message));
        }

        var candidateExternalId = ComputeLocalWechatExternalMessageId(message);
        var reviewCode = DetermineReviewCode(message, candidateExternalId);

        var accountId = await GetOrCreateAccountAsync(connection, transaction, message, cancellationToken);
        var contactId = await GetOrCreateContactAsync(connection, transaction, accountId, message, cancellationToken);
        var conversationId = await GetOrCreateConversationAsync(
            connection,
            transaction,
            accountId,
            contactId,
            message,
            cancellationToken);

        var sourceIdentity = message.SourceIdentity!;
        var calibrationScope = new SenderCalibrationScopeV1(
            accountId,
            conversationId,
            message.IdentityNamespace,
            sourceIdentity.MessageTable,
            sourceIdentity.ShardFingerprint);
        if (calibrationScope.IsComparable)
        {
            await AcquireScopeLockAsync(connection, transaction, calibrationScope, cancellationToken);
        }

        if (reviewCode is not null)
        {
            await RecordNeedsReviewAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                message,
                candidateExternalId,
                reviewCode,
                jobId,
                cancellationToken);
            return NeedsReview(reviewCode);
        }

        var externalMessageId = candidateExternalId!;
        var existing = await FindMessageByExternalIdAsync(
            connection,
            transaction,
            accountId,
            message.Provider!,
            externalMessageId,
            cancellationToken);

        if (existing is not null)
        {
            var conflict = await FindExistingSourceConflictAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                message,
                externalMessageId,
                existing,
                cancellationToken);
            if (conflict is not null)
            {
                await RecordNeedsReviewAsync(
                    connection,
                    transaction,
                    accountId,
                    conversationId,
                    message,
                    externalMessageId,
                    conflict,
                    jobId,
                    cancellationToken);
                return NeedsReview(conflict);
            }

            var matchedSourceAdded = await InsertResolvedSourceAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                existing.Id,
                message,
                externalMessageId,
                MatchStatusV1.Matched,
                jobId,
                cancellationToken);
            if (matchedSourceAdded)
            {
                await IncrementConversationRevisionAsync(connection, transaction, accountId, conversationId, cancellationToken);
            }
            return new IngestReceiptV1
            {
                MatchStatus = MatchStatusV1.Matched,
                MessageId = existing.Id,
                MessageCursor = ToCursor(existing.Id),
                Created = false
            };
        }

        var messageId = await InsertMessageAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            message,
            externalMessageId,
            cancellationToken);

        if (messageId is null)
        {
            existing = await FindMessageByExternalIdAsync(
                connection,
                transaction,
                accountId,
                message.Provider!,
                externalMessageId,
                cancellationToken);

            if (existing is null)
            {
                throw new InvalidOperationException("A message insert conflict occurred without a matching message.");
            }

            var conflict = await FindExistingSourceConflictAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                message,
                externalMessageId,
                existing,
                cancellationToken);
            if (conflict is not null)
            {
                await RecordNeedsReviewAsync(
                    connection,
                    transaction,
                    accountId,
                    conversationId,
                    message,
                    externalMessageId,
                    conflict,
                    jobId,
                    cancellationToken);
                return NeedsReview(conflict);
            }

            var raceSourceAdded = await InsertResolvedSourceAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                existing.Id,
                message,
                externalMessageId,
                MatchStatusV1.Matched,
                jobId,
                cancellationToken);
            if (raceSourceAdded)
            {
                await IncrementConversationRevisionAsync(connection, transaction, accountId, conversationId, cancellationToken);
            }
            return new IngestReceiptV1
            {
                MatchStatus = MatchStatusV1.Matched,
                MessageId = existing.Id,
                MessageCursor = ToCursor(existing.Id),
                Created = false
            };
        }

        var sourceAdded = await InsertResolvedSourceAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            messageId.Value,
            message,
            externalMessageId,
            MatchStatusV1.Created,
            jobId,
            cancellationToken);
        if (!sourceAdded)
        {
            throw new InvalidOperationException("The new message source was already associated with another record.");
        }

        var calibrationChange = await RefreshPerspectiveAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            message,
            cancellationToken);

        var cursor = ToCursor(messageId.Value);
        await UpdateConversationCursorAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            cursor,
            cancellationToken);
        await InsertOutboxEventAsync(
            connection,
            transaction,
            accountId,
            conversationId,
            "message.created",
            cursor,
            null,
            cancellationToken);

        if (calibrationChange is { Changed: true })
        {
            await InsertOutboxEventAsync(
                connection,
                transaction,
                accountId,
                conversationId,
                "conversation.perspective-changed",
                null,
                calibrationChange.Version,
                cancellationToken);
        }

        return new IngestReceiptV1
        {
            MatchStatus = MatchStatusV1.Created,
            MessageId = messageId,
            MessageCursor = cursor,
            Created = true
        };
    }

    private static string? DetermineReviewCode(RawMessageEventV1 message, string? computedExternalId)
    {
        if (!string.Equals(message.Provider, "local-wechat", StringComparison.Ordinal))
        {
            return "UNSUPPORTED_PROVIDER";
        }

        var sourceIdentity = message.SourceIdentity!;
        if (string.IsNullOrWhiteSpace(sourceIdentity.LocalId)
            || string.IsNullOrWhiteSpace(sourceIdentity.SortSeq)
            || string.IsNullOrWhiteSpace(sourceIdentity.MessageTable)
            || string.IsNullOrWhiteSpace(sourceIdentity.ShardFingerprint)
            || string.IsNullOrWhiteSpace(message.IdentityNamespace))
        {
            return "SOURCE_SCOPE_INCOMPLETE";
        }

        if (computedExternalId is null)
        {
            return "SOURCE_ID_UNAVAILABLE";
        }

        if (message.ExternalMessageId is not null
            && !string.Equals(message.ExternalMessageId, computedExternalId, StringComparison.Ordinal))
        {
            return "EXTERNAL_MESSAGE_ID_MISMATCH";
        }

        return null;
    }

    private static string? ComputeLocalWechatExternalMessageId(RawMessageEventV1 message)
    {
        var sourceIdentity = message.SourceIdentity!;
        if (string.IsNullOrWhiteSpace(message.AccountExternalId)
            || string.IsNullOrWhiteSpace(message.ConversationExternalId)
            || string.IsNullOrWhiteSpace(sourceIdentity.LocalId)
            || string.IsNullOrWhiteSpace(sourceIdentity.SortSeq))
        {
            return null;
        }

        var stableKey = string.Join(
            '|',
            message.AccountExternalId,
            message.ConversationExternalId,
            sourceIdentity.LocalId,
            sourceIdentity.SortSeq);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return "wx4:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static async Task<Guid> GetOrCreateAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RawMessageEventV1 message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.chat_accounts(provider, external_account_id)
            VALUES (@provider, @external_account_id)
            ON CONFLICT (provider, external_account_id)
            DO UPDATE SET external_account_id = EXCLUDED.external_account_id
            RETURNING id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("provider", message.Provider!);
        command.Parameters.AddWithValue("external_account_id", message.AccountExternalId!);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Account upsert returned no ID."));
    }

    private static async Task<Guid> GetOrCreateContactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        RawMessageEventV1 message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.contacts(account_id, external_contact_id)
            VALUES (@account_id, @external_contact_id)
            ON CONFLICT (account_id, external_contact_id)
            DO UPDATE SET external_contact_id = EXCLUDED.external_contact_id
            RETURNING id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("external_contact_id", message.ConversationExternalId!);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Contact upsert returned no ID."));
    }

    private static async Task<Guid> GetOrCreateConversationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid contactId,
        RawMessageEventV1 message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.conversations AS current_conversation(account_id, contact_id, external_conversation_id, conversation_type)
            VALUES (@account_id, @contact_id, @external_conversation_id, 'Direct')
            ON CONFLICT (account_id, external_conversation_id)
            DO UPDATE SET external_conversation_id = EXCLUDED.external_conversation_id
            WHERE current_conversation.contact_id = EXCLUDED.contact_id
            RETURNING id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("contact_id", contactId);
        command.Parameters.AddWithValue("external_conversation_id", message.ConversationExternalId!);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id
            ? id
            : throw new InvalidOperationException("Conversation scope conflicts with the existing contact mapping.");
    }

    private static async Task<Guid?> InsertMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        RawMessageEventV1 message,
        string externalMessageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.chat_messages(
                account_id,
                conversation_id,
                provider,
                message_type,
                text_content,
                source_occurred_at,
                direction,
                direction_evidence,
                external_message_id,
                matching_strategy_version)
            VALUES (
                @account_id,
                @conversation_id,
                @provider,
                @message_type,
                @text_content,
                @source_occurred_at,
                'Unknown',
                'Unresolved',
                @external_message_id,
                'wx4-legacy')
            ON CONFLICT (provider, account_id, external_message_id)
                WHERE external_message_id IS NOT NULL
            DO NOTHING
            RETURNING id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("provider", message.Provider!);
        command.Parameters.AddWithValue("message_type", message.MessageType!);
        AddNullableText(command, "text_content", message.MessageType == "Text" ? message.Text : null);
        AddNullableTimestamp(command, "source_occurred_at", message.SourceOccurredAt);
        command.Parameters.AddWithValue("external_message_id", externalMessageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : null;
    }

    private static async Task<ExistingMessage?> FindMessageByExternalIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        string provider,
        string externalMessageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, conversation_id, message_type, text_content
            FROM copilot.chat_messages
            WHERE account_id = @account_id
              AND provider = @provider
              AND external_message_id = @external_message_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("external_message_id", externalMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingMessage(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<string?> FindExistingSourceConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        RawMessageEventV1 message,
        string externalMessageId,
        ExistingMessage existing,
        CancellationToken cancellationToken)
    {
        var expectedText = message.MessageType == "Text" ? message.Text : null;
        if (existing.ConversationId != conversationId
            || !string.Equals(existing.MessageType, message.MessageType, StringComparison.Ordinal)
            || !string.Equals(existing.TextContent, expectedText, StringComparison.Ordinal))
        {
            return "SOURCE_CONTENT_OR_CONVERSATION_CONFLICT";
        }

        const string sql = """
            SELECT conversation_id, identity_namespace, message_table, shard_fingerprint, raw_sender_id, server_id, message_id
            FROM copilot.message_sources
            WHERE account_id = @account_id
              AND provider = @provider
              AND external_message_id = @external_message_id
              AND match_status <> 'NeedsReview';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("provider", message.Provider!);
        command.Parameters.AddWithValue("external_message_id", externalMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var foundSource = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            foundSource = true;
            if (reader.GetGuid(0) != conversationId
                || !string.Equals(reader.GetString(1), message.IdentityNamespace, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), message.SourceIdentity!.MessageTable, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), message.SourceIdentity.ShardFingerprint, StringComparison.Ordinal)
                || !string.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), message.RawSenderId, StringComparison.Ordinal)
                || HasConflictingKnownServerId(
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    message.SourceIdentity.ServerId)
                || reader.IsDBNull(6)
                || reader.GetGuid(6) != existing.Id)
            {
                return "EXTERNAL_MESSAGE_ID_SCOPE_COLLISION";
            }
        }

        return foundSource ? null : "EXTERNAL_MESSAGE_ID_WITHOUT_SOURCE";
    }

    private static bool HasConflictingKnownServerId(string? existingServerId, string? incomingServerId) =>
        existingServerId is not null
        && incomingServerId is not null
        && !string.Equals(existingServerId, incomingServerId, StringComparison.Ordinal);

    private static async Task<bool> InsertResolvedSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        Guid messageId,
        RawMessageEventV1 message,
        string externalMessageId,
        MatchStatusV1 matchStatus,
        string? jobId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.message_sources(
                message_id,
                account_id,
                conversation_id,
                provider,
                source_kind,
                external_message_id,
                server_id,
                local_id,
                sort_seq,
                shard_fingerprint,
                message_table,
                raw_sender_id,
                identity_namespace,
                job_id,
                match_status,
                source_occurred_at)
            VALUES (
                @message_id,
                @account_id,
                @conversation_id,
                @provider,
                @source_kind,
                @external_message_id,
                @server_id,
                @local_id,
                @sort_seq,
                @shard_fingerprint,
                @message_table,
                @raw_sender_id,
                @identity_namespace,
                @job_id,
                @match_status,
                @source_occurred_at)
            ON CONFLICT DO NOTHING
            RETURNING id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("provider", message.Provider!);
        command.Parameters.AddWithValue("source_kind", message.SourceKind.ToString());
        command.Parameters.AddWithValue("external_message_id", externalMessageId);
        AddNullableText(command, "server_id", message.SourceIdentity!.ServerId);
        AddNullableText(command, "local_id", message.SourceIdentity.LocalId);
        AddNullableText(command, "sort_seq", message.SourceIdentity.SortSeq);
        AddNullableText(command, "shard_fingerprint", message.SourceIdentity.ShardFingerprint);
        AddNullableText(command, "message_table", message.SourceIdentity.MessageTable);
        AddNullableText(command, "raw_sender_id", message.RawSenderId);
        AddNullableText(command, "identity_namespace", message.IdentityNamespace);
        AddNullableText(command, "job_id", jobId);
        command.Parameters.AddWithValue("match_status", matchStatus.ToString());
        AddNullableTimestamp(command, "source_occurred_at", message.SourceOccurredAt);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid;
    }

    private static async Task RecordNeedsReviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        RawMessageEventV1 message,
        string? candidateExternalId,
        string reasonCode,
        string? jobId,
        CancellationToken cancellationToken)
    {
        const string sourceSql = """
            INSERT INTO copilot.message_sources(
                account_id,
                conversation_id,
                provider,
                source_kind,
                external_message_id,
                server_id,
                local_id,
                sort_seq,
                shard_fingerprint,
                message_table,
                raw_sender_id,
                identity_namespace,
                job_id,
                match_status,
                source_occurred_at)
            VALUES (
                @account_id,
                @conversation_id,
                @provider,
                @source_kind,
                @external_message_id,
                @server_id,
                @local_id,
                @sort_seq,
                @shard_fingerprint,
                @message_table,
                @raw_sender_id,
                @identity_namespace,
                @job_id,
                'NeedsReview',
                @source_occurred_at)
            ON CONFLICT DO NOTHING;
            """;
        await using (var sourceCommand = new NpgsqlCommand(sourceSql, connection, transaction))
        {
            sourceCommand.Parameters.AddWithValue("account_id", accountId);
            sourceCommand.Parameters.AddWithValue("conversation_id", conversationId);
            sourceCommand.Parameters.AddWithValue("provider", message.Provider!);
            sourceCommand.Parameters.AddWithValue("source_kind", message.SourceKind.ToString());
            AddNullableText(sourceCommand, "external_message_id", candidateExternalId);
            AddNullableText(sourceCommand, "server_id", message.SourceIdentity!.ServerId);
            AddNullableText(sourceCommand, "local_id", message.SourceIdentity.LocalId);
            AddNullableText(sourceCommand, "sort_seq", message.SourceIdentity.SortSeq);
            AddNullableText(sourceCommand, "shard_fingerprint", message.SourceIdentity.ShardFingerprint);
            AddNullableText(sourceCommand, "message_table", message.SourceIdentity.MessageTable);
            AddNullableText(sourceCommand, "raw_sender_id", message.RawSenderId);
            AddNullableText(sourceCommand, "identity_namespace", message.IdentityNamespace);
            AddNullableText(sourceCommand, "job_id", jobId);
            AddNullableTimestamp(sourceCommand, "source_occurred_at", message.SourceOccurredAt);
            await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sourceRef = JsonSerializer.Serialize(new
        {
            message.Provider,
            message.SourceKind,
            jobId,
            message.AccountExternalId,
            message.ConversationExternalId,
            message.IdentityNamespace,
            message.RawSenderId,
            message.SourceIdentity,
            submittedExternalMessageId = message.ExternalMessageId,
            candidateExternalMessageId = candidateExternalId
        });
        var reviewKey = ComputeReviewKey(accountId, conversationId, message, candidateExternalId, reasonCode, jobId);
        const string reviewSql = """
            INSERT INTO copilot.import_reviews(account_id, conversation_id, review_key, job_id, source_ref, reason_code)
            VALUES (@account_id, @conversation_id, @review_key, @job_id, @source_ref, @reason_code)
            ON CONFLICT (account_id, review_key) DO NOTHING;
            """;
        await using var reviewCommand = new NpgsqlCommand(reviewSql, connection, transaction);
        reviewCommand.Parameters.AddWithValue("account_id", accountId);
        reviewCommand.Parameters.AddWithValue("conversation_id", conversationId);
        reviewCommand.Parameters.AddWithValue("review_key", reviewKey);
        AddNullableText(reviewCommand, "job_id", jobId);
        reviewCommand.Parameters.Add(new NpgsqlParameter("source_ref", NpgsqlDbType.Jsonb) { Value = sourceRef });
        reviewCommand.Parameters.AddWithValue("reason_code", reasonCode);
        await reviewCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CalibrationRefreshResult?> RefreshPerspectiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        RawMessageEventV1 message,
        CancellationToken cancellationToken)
    {
        var sourceIdentity = message.SourceIdentity!;
        var scope = new SenderCalibrationScopeV1(
            accountId,
            conversationId,
            message.IdentityNamespace,
            sourceIdentity.MessageTable,
            sourceIdentity.ShardFingerprint);
        if (!scope.IsComparable)
        {
            return null;
        }

        var observedSenders = await ReadObservedSendersAsync(
            connection,
            transaction,
            scope,
            cancellationToken);
        var defaultPerspective = SenderPerspectiveResolver.ResolveDefault(scope, observedSenders);
        var current = await ReadCalibrationAsync(connection, transaction, scope, cancellationToken);
        if (current is { State: PerspectiveStateV1.Confirmed })
        {
            await ReprojectMessagesAsync(connection, transaction, current, cancellationToken);
            return null;
        }

        var next = ChooseNextCalibration(current, defaultPerspective);
        var changed = current is null
            || current.State != next.State
            || !string.Equals(current.AccountSelfRawSenderId, next.AccountSelfRawSenderId, StringComparison.Ordinal)
            || !string.Equals(current.ContactRawSenderId, next.ContactRawSenderId, StringComparison.Ordinal)
            || current.Evidence != next.Evidence;
        if (changed)
        {
            await SaveCalibrationAsync(connection, transaction, next, cancellationToken);
        }

        await ReprojectMessagesAsync(connection, transaction, next, cancellationToken);
        return changed ? new CalibrationRefreshResult(true, next.CalibrationVersion) : null;
    }

    private async Task<HistoryBatchReceiptV1> RecordHistoryBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HistoryBatchV1 batch,
        int read,
        int matched,
        int created,
        int needsReview,
        int failed,
        CancellationToken cancellationToken)
    {
        var target = new RawMessageEventV1
        {
            Provider = "local-wechat",
            AccountExternalId = batch.AccountExternalId,
            ConversationExternalId = batch.ConversationExternalId,
            ConversationType = "Direct",
            MessageType = "Text",
            Text = string.Empty,
            IdentityNamespace = "history-job-target",
            SourceIdentity = new SourceIdentityV1()
        };

        var accountId = await GetOrCreateAccountAsync(connection, transaction, target, cancellationToken);
        var contactId = await GetOrCreateContactAsync(connection, transaction, accountId, target, cancellationToken);
        var conversationId = await GetOrCreateConversationAsync(
            connection,
            transaction,
            accountId,
            contactId,
            target,
            cancellationToken);

        var batchBytes = JsonSerializer.SerializeToUtf8Bytes(batch);
        var batchKey = Convert.ToHexString(SHA256.HashData(batchBytes)).ToLowerInvariant();
        var sourceStatus = failed > 0 || needsReview > 0
            ? HistoryJobStatusV1.Partial
            : HistoryJobStatusV1.Completed;
        var range = batch.RequestedRange!;

        const string ensureJobSql = """
            INSERT INTO copilot.history_jobs(
                job_id,
                account_id,
                conversation_id,
                requested_from_utc,
                requested_to_utc,
                status)
            VALUES (
                @job_id,
                @account_id,
                @conversation_id,
                @requested_from_utc,
                @requested_to_utc,
                'Running')
            ON CONFLICT (account_id, conversation_id, job_id)
            DO UPDATE SET updated_at = now()
            RETURNING requested_from_utc IS NOT DISTINCT FROM @requested_from_utc
                AND requested_to_utc IS NOT DISTINCT FROM @requested_to_utc;
            """;
        await using (var ensureJob = new NpgsqlCommand(ensureJobSql, connection, transaction))
        {
            ensureJob.Parameters.AddWithValue("job_id", batch.JobId!);
            ensureJob.Parameters.AddWithValue("account_id", accountId);
            ensureJob.Parameters.AddWithValue("conversation_id", conversationId);
            AddNullableTimestamp(ensureJob, "requested_from_utc", range.FromUtc);
            AddNullableTimestamp(ensureJob, "requested_to_utc", range.ToUtc);
            await using var reader = await ensureJob.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The history job upsert returned no row.");
            }

            EnsureHistoryJobRangeMatches(reader.GetBoolean(0));
        }

        const string insertBatchSql = """
            INSERT INTO copilot.history_job_batches(
                account_id,
                conversation_id,
                job_id,
                batch_key,
                status,
                read_count,
                matched_count,
                created_count,
                needs_review_count,
                ignored_unsupported_count,
                failed_count,
                is_last_batch)
            VALUES (
                @account_id,
                @conversation_id,
                @job_id,
                @batch_key,
                @status,
                @read_count,
                @matched_count,
                @created_count,
                @needs_review_count,
                0,
                @failed_count,
                @is_last_batch)
            ON CONFLICT (account_id, conversation_id, job_id, batch_key) DO NOTHING;
            """;
        await using (var insertBatch = new NpgsqlCommand(insertBatchSql, connection, transaction))
        {
            insertBatch.Parameters.AddWithValue("account_id", accountId);
            insertBatch.Parameters.AddWithValue("conversation_id", conversationId);
            insertBatch.Parameters.AddWithValue("job_id", batch.JobId!);
            insertBatch.Parameters.AddWithValue("batch_key", batchKey);
            insertBatch.Parameters.AddWithValue("status", sourceStatus.ToString());
            insertBatch.Parameters.AddWithValue("read_count", read);
            insertBatch.Parameters.AddWithValue("matched_count", matched);
            insertBatch.Parameters.AddWithValue("created_count", created);
            insertBatch.Parameters.AddWithValue("needs_review_count", needsReview);
            insertBatch.Parameters.AddWithValue("failed_count", failed);
            insertBatch.Parameters.AddWithValue("is_last_batch", batch.IsLastBatch);
            await insertBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        // The scan cursor only advances over rows this transaction has already persisted, so a reader
        // that dies mid-batch resumes at the last durable position instead of skipping unread messages.
        const string upsertCheckpointSql = """
            INSERT INTO copilot.history_scan_checkpoints(
                account_id,
                conversation_id,
                window_key,
                message_table,
                last_local_id,
                scanned_rows,
                last_job_id)
            VALUES (@account_id, @conversation_id, @window_key, @message_table, @last_local_id, @scanned_rows, @job_id)
            ON CONFLICT (account_id, conversation_id, window_key, message_table)
            DO UPDATE SET last_local_id = GREATEST(copilot.history_scan_checkpoints.last_local_id, EXCLUDED.last_local_id),
                scanned_rows = copilot.history_scan_checkpoints.scanned_rows + EXCLUDED.scanned_rows,
                last_job_id = EXCLUDED.last_job_id,
                updated_at = now();
            """;
        var windowKey = HistoryScanWindowV1.Key(range.FromUtc, range.ToUtc);
        foreach (var progress in (batch.Items ?? Array.Empty<RawMessageEventV1>())
                     .Select(item => (
                        item.SourceIdentity?.MessageTable,
                        LocalId: long.TryParse(item.SourceIdentity?.LocalId, out var localId) ? localId : (long?)null))
                     .Where(row => row.MessageTable is { Length: > 0 } && row.LocalId is not null)
                     .GroupBy(row => row.MessageTable!)
                     .Select(group => new
                     {
                         MessageTable = group.Key,
                         LastLocalId = group.Max(row => row.LocalId!.Value),
                         Rows = group.Count()
                     }))
        {
            await using var checkpoint = new NpgsqlCommand(upsertCheckpointSql, connection, transaction);
            checkpoint.Parameters.AddWithValue("account_id", accountId);
            checkpoint.Parameters.AddWithValue("conversation_id", conversationId);
            checkpoint.Parameters.AddWithValue("window_key", windowKey);
            checkpoint.Parameters.AddWithValue("message_table", progress.MessageTable);
            checkpoint.Parameters.AddWithValue("last_local_id", progress.LastLocalId);
            checkpoint.Parameters.AddWithValue("scanned_rows", progress.Rows);
            checkpoint.Parameters.AddWithValue("job_id", batch.JobId!);
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        const string totalsSql = """
            SELECT
                COALESCE(sum(read_count), 0),
                COALESCE(sum(matched_count), 0),
                COALESCE(sum(created_count), 0),
                COALESCE(sum(needs_review_count), 0),
                COALESCE(sum(ignored_unsupported_count), 0),
                COALESCE(sum(failed_count), 0),
                COALESCE(bool_or(is_last_batch), false)
            FROM copilot.history_job_batches
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND job_id = @job_id;
            """;
        int totalRead;
        int totalMatched;
        int totalCreated;
        int totalNeedsReview;
        int totalIgnored;
        int totalFailed;
        bool hasLastBatch;
        await using (var totals = new NpgsqlCommand(totalsSql, connection, transaction))
        {
            totals.Parameters.AddWithValue("account_id", accountId);
            totals.Parameters.AddWithValue("conversation_id", conversationId);
            totals.Parameters.AddWithValue("job_id", batch.JobId!);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalRead = reader.GetInt32(0);
            totalMatched = reader.GetInt32(1);
            totalCreated = reader.GetInt32(2);
            totalNeedsReview = reader.GetInt32(3);
            totalIgnored = reader.GetInt32(4);
            totalFailed = reader.GetInt32(5);
            hasLastBatch = reader.GetBoolean(6);
        }

        var status = !hasLastBatch
            ? HistoryJobStatusV1.Running
            : totalNeedsReview > 0 || totalFailed > 0
                ? HistoryJobStatusV1.Partial
                : HistoryJobStatusV1.Completed;
        const string updateJobSql = """
            UPDATE copilot.history_jobs
            SET status = @status,
                read_count = @read_count,
                matched_count = @matched_count,
                created_count = @created_count,
                needs_review_count = @needs_review_count,
                ignored_unsupported_count = @ignored_unsupported_count,
                failed_count = @failed_count,
                is_last_batch = @is_last_batch,
                completed_at = CASE WHEN @is_last_batch THEN COALESCE(completed_at, now()) ELSE completed_at END,
                updated_at = now()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND job_id = @job_id;
            """;
        await using (var updateJob = new NpgsqlCommand(updateJobSql, connection, transaction))
        {
            updateJob.Parameters.AddWithValue("status", status.ToString());
            updateJob.Parameters.AddWithValue("read_count", totalRead);
            updateJob.Parameters.AddWithValue("matched_count", totalMatched);
            updateJob.Parameters.AddWithValue("created_count", totalCreated);
            updateJob.Parameters.AddWithValue("needs_review_count", totalNeedsReview);
            updateJob.Parameters.AddWithValue("ignored_unsupported_count", totalIgnored);
            updateJob.Parameters.AddWithValue("failed_count", totalFailed);
            updateJob.Parameters.AddWithValue("is_last_batch", hasLastBatch);
            updateJob.Parameters.AddWithValue("account_id", accountId);
            updateJob.Parameters.AddWithValue("conversation_id", conversationId);
            updateJob.Parameters.AddWithValue("job_id", batch.JobId!);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }

        return new HistoryBatchReceiptV1
        {
            JobId = batch.JobId!,
            Read = totalRead,
            Matched = totalMatched,
            Created = totalCreated,
            NeedsReview = totalNeedsReview,
            IgnoredUnsupported = totalIgnored,
            Failed = totalFailed,
            Status = status,
            IsLastBatch = hasLastBatch
        };
    }

    private static int? ParseItemIndex(string path)
    {
        if (!path.StartsWith("items[", StringComparison.Ordinal))
        {
            return null;
        }

        var closingBracket = path.IndexOf(']');
        return closingBracket > "items[".Length
            && int.TryParse(path.AsSpan("items[".Length, closingBracket - "items[".Length), out var index)
                ? index
                : null;
    }

    internal static void EnsureHistoryJobRangeMatches(bool requestedRangeMatches)
    {
        if (!requestedRangeMatches)
        {
            throw new ArgumentException(
                "A history job ID cannot be reused with a different requested range.",
                "batch");
        }
    }

    internal static void EnsureHistoryBatchSize(int itemCount)
    {
        if (itemCount > MaxHistoryBatchItems)
        {
            throw new ArgumentException(
                $"A history batch cannot contain more than {MaxHistoryBatchItems} items.",
                "batch");
        }
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
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", scope.ShardFingerprint!);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var observed = new List<ScopedSenderIdentityV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            observed.Add(new ScopedSenderIdentityV1(scope, reader.GetString(0)));
        }

        return observed;
    }

    private static async Task<SenderPerspectiveV1?> ReadCalibrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderCalibrationScopeV1 scope,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT calibration_version, perspective_state, evidence, account_self_raw_sender_id, contact_raw_sender_id
            FROM copilot.sender_calibrations
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND message_table = @message_table
              AND shard_fingerprint = @shard_fingerprint
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", scope.ShardFingerprint!);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var version = reader.GetInt64(0);
        var state = Enum.Parse<PerspectiveStateV1>(reader.GetString(1), ignoreCase: false);
        var evidence = reader.IsDBNull(2)
            ? (PerspectiveEvidenceV1?)null
            : Enum.Parse<PerspectiveEvidenceV1>(reader.GetString(2), ignoreCase: false);
        return new SenderPerspectiveV1(
            scope,
            state,
            evidence,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            version);
    }

    private static SenderPerspectiveV1 ChooseNextCalibration(
        SenderPerspectiveV1? current,
        SenderPerspectiveV1 computed)
    {
        if (current is null)
        {
            return computed with { CalibrationVersion = 1 };
        }

        var sameMapping = current.State == computed.State
            && current.Evidence == computed.Evidence
            && string.Equals(current.AccountSelfRawSenderId, computed.AccountSelfRawSenderId, StringComparison.Ordinal)
            && string.Equals(current.ContactRawSenderId, computed.ContactRawSenderId, StringComparison.Ordinal);
        return sameMapping
            ? current
            : computed with { CalibrationVersion = current.CalibrationVersion + 1 };
    }

    private static async Task SaveCalibrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderPerspectiveV1 perspective,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.sender_calibrations(
                account_id,
                conversation_id,
                identity_namespace,
                message_table,
                shard_fingerprint,
                account_self_raw_sender_id,
                contact_raw_sender_id,
                calibration_version,
                perspective_state,
                evidence,
                confirmed_at)
            VALUES (
                @account_id,
                @conversation_id,
                @identity_namespace,
                @message_table,
                @shard_fingerprint,
                @self_sender_id,
                @contact_sender_id,
                @calibration_version,
                @perspective_state,
                @evidence,
                NULL)
            ON CONFLICT DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", perspective.Scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", perspective.Scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", perspective.Scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", perspective.Scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", perspective.Scope.ShardFingerprint!);
        AddNullableText(command, "self_sender_id", perspective.AccountSelfRawSenderId);
        AddNullableText(command, "contact_sender_id", perspective.ContactRawSenderId);
        command.Parameters.AddWithValue("calibration_version", perspective.CalibrationVersion);
        command.Parameters.AddWithValue("perspective_state", perspective.State.ToString());
        AddNullableText(command, "evidence", perspective.Evidence?.ToString());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 0)
        {
            await UpdateExistingCalibrationAsync(connection, transaction, perspective, cancellationToken);
        }
    }

    private static async Task UpdateExistingCalibrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderPerspectiveV1 perspective,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE copilot.sender_calibrations
            SET account_self_raw_sender_id = @self_sender_id,
                contact_raw_sender_id = @contact_sender_id,
                calibration_version = @calibration_version,
                perspective_state = @perspective_state,
                evidence = @evidence,
                confirmed_at = NULL,
                updated_at = now()
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND message_table = @message_table
              AND shard_fingerprint = @shard_fingerprint
              AND perspective_state <> 'Confirmed';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddNullableText(command, "self_sender_id", perspective.AccountSelfRawSenderId);
        AddNullableText(command, "contact_sender_id", perspective.ContactRawSenderId);
        command.Parameters.AddWithValue("calibration_version", perspective.CalibrationVersion);
        command.Parameters.AddWithValue("perspective_state", perspective.State.ToString());
        AddNullableText(command, "evidence", perspective.Evidence?.ToString());
        command.Parameters.AddWithValue("account_id", perspective.Scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", perspective.Scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", perspective.Scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", perspective.Scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", perspective.Scope.ShardFingerprint!);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReprojectMessagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderPerspectiveV1 perspective,
        CancellationToken cancellationToken)
    {
        const string sql = """
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
                    WHEN @perspective_state = 'Unresolved' OR scoped_senders.raw_sender_id IS NULL THEN 'Unknown'
                    WHEN scoped_senders.raw_sender_id = @self_sender_id THEN 'Outgoing'
                    WHEN scoped_senders.raw_sender_id = @contact_sender_id THEN 'Incoming'
                    ELSE 'Unknown'
                END,
                direction_evidence = CASE
                    WHEN @perspective_state = 'Unresolved' OR scoped_senders.raw_sender_id IS NULL THEN 'Unresolved'
                    WHEN scoped_senders.raw_sender_id NOT IN (@self_sender_id, @contact_sender_id) THEN 'Unresolved'
                    ELSE @direction_evidence
                END
            FROM scoped_senders
            WHERE message.id = scoped_senders.message_id
              AND message.account_id = @account_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", perspective.Scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", perspective.Scope.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", perspective.Scope.IdentityNamespace!);
        command.Parameters.AddWithValue("message_table", perspective.Scope.MessageTable!);
        command.Parameters.AddWithValue("shard_fingerprint", perspective.Scope.ShardFingerprint!);
        command.Parameters.AddWithValue("perspective_state", perspective.State.ToString());
        AddNullableText(command, "self_sender_id", perspective.AccountSelfRawSenderId);
        AddNullableText(command, "contact_sender_id", perspective.ContactRawSenderId);
        command.Parameters.AddWithValue("direction_evidence", perspective.Evidence switch
        {
            PerspectiveEvidenceV1.DefaultSmallSenderId => DirectionEvidenceV1.DefaultSmallSenderId.ToString(),
            PerspectiveEvidenceV1.UserConfirmed => DirectionEvidenceV1.UserConfirmed.ToString(),
            _ => DirectionEvidenceV1.Unresolved.ToString()
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateConversationCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string cursor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE copilot.conversations
            SET last_message_cursor = @cursor,
                message_set_revision = message_set_revision + 1
            WHERE id = @conversation_id AND account_id = @account_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("account_id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task IncrementConversationRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE copilot.conversations
            SET message_set_revision = message_set_revision + 1
            WHERE id = @conversation_id AND account_id = @account_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("account_id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        string eventType,
        string? messageCursor,
        long? calibrationVersion,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO copilot.conversation_change_outbox(
                account_id,
                conversation_id,
                event_type,
                message_cursor,
                calibration_version)
            VALUES (
                @account_id,
                @conversation_id,
                @event_type,
                @message_cursor,
                @calibration_version);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("event_type", eventType);
        AddNullableText(command, "message_cursor", messageCursor);
        AddNullableBigint(command, "calibration_version", calibrationVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ComputeReviewKey(
        Guid accountId,
        Guid conversationId,
        RawMessageEventV1 message,
        string? candidateExternalId,
        string reasonCode,
        string? jobId)
    {
        var identity = message.SourceIdentity!;
        var material = string.Join("|", new[]
        {
            accountId.ToString("N"),
            conversationId.ToString("N"),
            message.SourceKind.ToString(),
            jobId ?? string.Empty,
            reasonCode,
            candidateExternalId ?? string.Empty,
            message.IdentityNamespace ?? string.Empty,
            identity.MessageTable ?? string.Empty,
            identity.ShardFingerprint ?? string.Empty,
            identity.LocalId ?? string.Empty,
            identity.SortSeq ?? string.Empty,
            identity.ServerId ?? string.Empty,
            message.RawSenderId ?? string.Empty,
            message.SourceOccurredAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string ToCursor(Guid messageId) => messageId.ToString("N");

    internal static async Task AcquireScopeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SenderCalibrationScopeV1 scope,
        CancellationToken cancellationToken)
    {
        var namespaceValue = scope.IdentityNamespace!;
        var lockKey = string.Concat(
            scope.AccountId.ToString("N"),
            ":",
            scope.ConversationId.ToString("N"),
            ":",
            namespaceValue.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            namespaceValue);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope_key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_key", lockKey);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static IngestReceiptV1 NeedsReview(string reasonCode) => new()
    {
        MatchStatus = MatchStatusV1.NeedsReview,
        Created = false,
        ReviewCode = reasonCode
    };

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });
    }

    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value is null ? DBNull.Value : value.Value
        });

    private static void AddNullableBigint(NpgsqlCommand command, string name, long? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint)
        {
            Value = value is null ? DBNull.Value : value.Value
        });

    private sealed record ExistingMessage(
        Guid Id,
        Guid ConversationId,
        string MessageType,
        string? TextContent);

    private sealed record CalibrationRefreshResult(bool Changed, long Version);
}
