using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public sealed class PostgresMemoryServiceV1(NpgsqlDataSource dataSource) : IMemoryServiceV1
{
    public const int MaximumListLimit = 100;

    public async Task<MemoryConversationScopeV1?> ResolveConfiguredConversationAsync(
        string provider,
        string accountExternalId,
        string conversationExternalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(accountExternalId)
            || string.IsNullOrWhiteSpace(conversationExternalId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT a.id, c.id, c.contact_id
            FROM copilot.chat_accounts a
            JOIN copilot.conversations c ON c.account_id = a.id
            WHERE a.provider = @provider
              AND a.external_account_id = @account_external_id
              AND c.external_conversation_id = @conversation_external_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("account_external_id", accountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", conversationExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemoryConversationScopeV1(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2));
    }

    public async Task<IReadOnlyList<MemoryItemV1>> ListAsync(
        MemoryConversationScopeV1 scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (limit <= 0)
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.InvalidRequest, "invalid_limit");
        }

        limit = Math.Min(limit, MaximumListLimit);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);

        const string sql = """
            SELECT m.id,
                   m.account_id,
                   m.subject_type,
                   m.subject_id,
                   m.conversation_id,
                   m.text_content,
                   m.category,
                   m.origin,
                   m.status,
                   m.created_at,
                   m.updated_at,
                   m.observed_at,
                   m.calibration_version,
                   m.source_message_cursor,
                   m.version,
                   COALESCE(
                       array_agg(e.message_id ORDER BY e.ordinal)
                           FILTER (WHERE e.message_id IS NOT NULL),
                       ARRAY[]::uuid[])
            FROM copilot.memory_items m
            LEFT JOIN copilot.memory_evidence e
              ON e.memory_id = m.id
             AND e.account_id = m.account_id
             AND e.conversation_id = m.conversation_id
            WHERE m.account_id = @account_id
              AND m.conversation_id = @conversation_id
              AND (
                  (m.subject_type = 'WechatAccount' AND m.subject_id = @account_id)
                  OR (m.subject_type = 'Contact' AND m.subject_id = @contact_id)
              )
              AND m.origin = 'UserEntered'
              AND m.status = 'Active'
            GROUP BY m.id
            ORDER BY m.updated_at DESC, m.id
            LIMIT @limit;
            """;

        var items = new List<MemoryItemV1>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("contact_id", scope.ContactId);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMemoryItem(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public async Task<MemoryItemV1?> GetAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (memoryId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);

        const string sql = """
            SELECT m.id,
                   m.account_id,
                   m.subject_type,
                   m.subject_id,
                   m.conversation_id,
                   m.text_content,
                   m.category,
                   m.origin,
                   m.status,
                   m.created_at,
                   m.updated_at,
                   m.observed_at,
                   m.calibration_version,
                   m.source_message_cursor,
                   m.version,
                   COALESCE(
                       array_agg(e.message_id ORDER BY e.ordinal)
                           FILTER (WHERE e.message_id IS NOT NULL),
                       ARRAY[]::uuid[])
            FROM copilot.memory_items m
            LEFT JOIN copilot.memory_evidence e
              ON e.memory_id = m.id
             AND e.account_id = m.account_id
             AND e.conversation_id = m.conversation_id
            WHERE m.id = @memory_id
              AND m.account_id = @account_id
              AND m.conversation_id = @conversation_id
              AND (
                  (m.subject_type = 'WechatAccount' AND m.subject_id = @account_id)
                  OR (m.subject_type = 'Contact' AND m.subject_id = @contact_id)
              )
              AND m.origin = 'UserEntered'
              AND m.status = 'Active'
            GROUP BY m.id;
            """;

        MemoryItemV1? item;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("memory_id", memoryId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("contact_id", scope.ContactId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            item = await reader.ReadAsync(cancellationToken) ? ReadMemoryItem(reader) : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    public async Task<MemoryItemV1> CreateUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        UserEnteredMemoryCreateRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ThrowIfInvalid(MemoryItemRequestValidatorV1.ValidateCreate(request));

        var memoryId = Guid.NewGuid();
        var subjectId = MemoryParticipantScopeV1.ResolveSubjectId(scope, request.SubjectType);
        var sourceMessageIds = request.SourceMessageIds?.ToArray() ?? Array.Empty<Guid>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);
        await ValidateEvidenceAsync(connection, transaction, scope, sourceMessageIds, cancellationToken);

        const string sql = """
            INSERT INTO copilot.memory_items (
                id, account_id, subject_type, subject_id, conversation_id,
                text_content, category, origin, status, observed_at, version)
            VALUES (
                @id, @account_id, @subject_type, @subject_id, @conversation_id,
                @text_content, @category, 'UserEntered', 'Active', @observed_at, 1)
            RETURNING created_at, updated_at;
            """;
        DateTime createdAt;
        DateTime updatedAt;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", memoryId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("subject_type", request.SubjectType.ToString());
            command.Parameters.AddWithValue("subject_id", subjectId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("text_content", request.Text!);
            AddNullableTextParameter(command, "category", request.Category);
            AddNullableTimestampParameter(command, "observed_at", request.ObservedAt?.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The memory insert did not return its timestamps.");
            }

            createdAt = reader.GetDateTime(0);
            updatedAt = reader.GetDateTime(1);
        }

        await ReplaceEvidenceAsync(
            connection,
            transaction,
            memoryId,
            scope,
            sourceMessageIds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MemoryItemV1
        {
            MemoryId = memoryId,
            AccountId = scope.AccountId,
            SubjectType = request.SubjectType,
            SubjectId = subjectId,
            ConversationId = scope.ConversationId,
            Text = request.Text!,
            Category = request.Category,
            Origin = MemoryOriginV1.UserEntered,
            Status = MemoryItemStatusV1.Active,
            CreatedAt = AsUtcOffset(createdAt),
            UpdatedAt = AsUtcOffset(updatedAt),
            ObservedAt = request.ObservedAt?.ToUniversalTime(),
            SourceMessageIds = sourceMessageIds,
            Version = 1
        };
    }

    public async Task<MemoryItemV1> UpdateUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        UserEnteredMemoryUpdateRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ThrowIfInvalid(MemoryItemRequestValidatorV1.ValidateUpdate(request));
        if (memoryId == Guid.Empty)
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.MemoryNotFound);
        }

        var sourceMessageIds = request.SourceMessageIds!.ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);
        await ValidateEvidenceAsync(connection, transaction, scope, sourceMessageIds, cancellationToken);

        const string sql = """
            UPDATE copilot.memory_items
            SET text_content = @text_content,
                category = @category,
                observed_at = @observed_at,
                updated_at = clock_timestamp(),
                version = version + 1
            WHERE id = @memory_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND (
                  (subject_type = 'WechatAccount' AND subject_id = @account_id)
                  OR (subject_type = 'Contact' AND subject_id = @contact_id)
              )
              AND origin = 'UserEntered'
              AND status = 'Active'
              AND version = @expected_version
            RETURNING subject_type, subject_id, created_at, updated_at, version;
            """;

        ParticipantSubjectTypeV1 subjectType = default;
        Guid subjectId = Guid.Empty;
        DateTime createdAt = default;
        DateTime updatedAt = default;
        long version = 0;
        var rowFound = false;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("memory_id", memoryId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("contact_id", scope.ContactId);
            command.Parameters.AddWithValue("expected_version", request.ExpectedVersion);
            command.Parameters.AddWithValue("text_content", request.Text!);
            AddNullableTextParameter(command, "category", request.Category);
            AddNullableTimestampParameter(command, "observed_at", request.ObservedAt?.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            rowFound = await reader.ReadAsync(cancellationToken);
            if (rowFound)
            {
                subjectType = Enum.Parse<ParticipantSubjectTypeV1>(reader.GetString(0), ignoreCase: false);
                subjectId = reader.GetGuid(1);
                createdAt = reader.GetDateTime(2);
                updatedAt = reader.GetDateTime(3);
                version = reader.GetInt64(4);
            }
        }

        if (!rowFound)
        {
            await ThrowNotFoundOrConflictAsync(connection, transaction, scope, memoryId, cancellationToken);
            throw new InvalidOperationException("The memory conflict resolver returned unexpectedly.");
        }

        await ReplaceEvidenceAsync(
            connection,
            transaction,
            memoryId,
            scope,
            sourceMessageIds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MemoryItemV1
        {
            MemoryId = memoryId,
            AccountId = scope.AccountId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            ConversationId = scope.ConversationId,
            Text = request.Text!,
            Category = request.Category,
            Origin = MemoryOriginV1.UserEntered,
            Status = MemoryItemStatusV1.Active,
            CreatedAt = AsUtcOffset(createdAt),
            UpdatedAt = AsUtcOffset(updatedAt),
            ObservedAt = request.ObservedAt?.ToUniversalTime(),
            SourceMessageIds = sourceMessageIds,
            Version = version
        };
    }

    public async Task DeleteUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (memoryId == Guid.Empty)
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.MemoryNotFound);
        }

        if (expectedVersion <= 0)
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.InvalidRequest, "invalid_expected_version");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            scope.AccountId,
            scope.ConversationId,
            cancellationToken);

        const string sql = """
            UPDATE copilot.memory_items
            SET status = 'Deleted',
                updated_at = clock_timestamp(),
                version = version + 1
            WHERE id = @memory_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND (
                  (subject_type = 'WechatAccount' AND subject_id = @account_id)
                  OR (subject_type = 'Contact' AND subject_id = @contact_id)
              )
              AND origin = 'UserEntered'
              AND status = 'Active'
              AND version = @expected_version;
            """;
        int affected;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("memory_id", memoryId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("contact_id", scope.ContactId);
            command.Parameters.AddWithValue("expected_version", expectedVersion);
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (affected == 0)
        {
            await ThrowNotFoundOrConflictAsync(connection, transaction, scope, memoryId, cancellationToken);
            throw new InvalidOperationException("The memory conflict resolver returned unexpectedly.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static MemoryItemV1 ReadMemoryItem(NpgsqlDataReader reader)
    {
        return new MemoryItemV1
        {
            MemoryId = reader.GetGuid(0),
            AccountId = reader.GetGuid(1),
            SubjectType = Enum.Parse<ParticipantSubjectTypeV1>(reader.GetString(2), ignoreCase: false),
            SubjectId = reader.GetGuid(3),
            ConversationId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
            Text = reader.GetString(5),
            Category = reader.IsDBNull(6) ? null : reader.GetString(6),
            Origin = Enum.Parse<MemoryOriginV1>(reader.GetString(7), ignoreCase: false),
            Status = Enum.Parse<MemoryItemStatusV1>(reader.GetString(8), ignoreCase: false),
            CreatedAt = AsUtcOffset(reader.GetDateTime(9)),
            UpdatedAt = AsUtcOffset(reader.GetDateTime(10)),
            ObservedAt = reader.IsDBNull(11) ? null : AsUtcOffset(reader.GetDateTime(11)),
            CalibrationVersion = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            SourceMessageCursor = reader.IsDBNull(13) ? null : reader.GetString(13),
            Version = reader.GetInt64(14),
            SourceMessageIds = reader.GetFieldValue<Guid[]>(15)
        };
    }

    private static async Task ValidateEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConversationScopeV1 scope,
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        const string sql = """
            SELECT id
            FROM copilot.chat_messages
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND id = ANY(@message_ids)
            FOR KEY SHARE;
            """;
        var found = new HashSet<Guid>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = messageIds.ToArray()
        });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(reader.GetGuid(0));
            }
        }

        if (!MemoryEvidenceScopeValidatorV1.AllRequestedEvidenceIsAuthorized(messageIds, found))
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.EvidenceUnavailable);
        }
    }

    private static async Task ReplaceEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid memoryId,
        MemoryConversationScopeV1 scope,
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = new NpgsqlCommand(
            "DELETE FROM copilot.memory_evidence WHERE memory_id = @memory_id AND account_id = @account_id AND conversation_id = @conversation_id;",
            connection,
            transaction))
        {
            deleteCommand.Parameters.AddWithValue("memory_id", memoryId);
            deleteCommand.Parameters.AddWithValue("account_id", scope.AccountId);
            deleteCommand.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (messageIds.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO copilot.memory_evidence (
                memory_id, account_id, conversation_id, message_id, ordinal)
            SELECT @memory_id,
                   @account_id,
                   @conversation_id,
                   evidence.message_id,
                   (evidence.ordinality - 1)::integer
            FROM unnest(@message_ids) WITH ORDINALITY AS evidence(message_id, ordinality);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("memory_id", memoryId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = messageIds.ToArray()
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ThrowNotFoundOrConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM copilot.memory_items
            WHERE id = @memory_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND (
                  (subject_type = 'WechatAccount' AND subject_id = @account_id)
                  OR (subject_type = 'Contact' AND subject_id = @contact_id)
              )
              AND origin = 'UserEntered'
              AND status = 'Active'
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("memory_id", memoryId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("contact_id", scope.ContactId);
        var exists = await command.ExecuteScalarAsync(cancellationToken) is not null;
        throw new MemoryServiceException(exists
            ? MemoryServiceFailureCodeV1.VersionConflict
            : MemoryServiceFailureCodeV1.MemoryNotFound);
    }

    private static void AddNullableTextParameter(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });

    private static void AddNullableTimestampParameter(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value is null ? DBNull.Value : value.Value
        });

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void ValidateScope(MemoryConversationScopeV1 scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AccountId == Guid.Empty || scope.ConversationId == Guid.Empty || scope.ContactId == Guid.Empty)
        {
            throw new ArgumentException("A complete server-resolved conversation scope is required.", nameof(scope));
        }
    }

    private static void ThrowIfInvalid(IReadOnlyList<MemoryRequestViolationV1> violations)
    {
        if (violations.Count > 0)
        {
            throw new MemoryServiceException(MemoryServiceFailureCodeV1.InvalidRequest, violations[0].Code);
        }
    }
}

internal static class PostgresMemoryScopePolicy
{
    public static async Task SetTransactionScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT set_config('copilot.account_id', @account_id, true),
                   set_config('copilot.conversation_id', @conversation_id, true);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId.ToString("D"));
        command.Parameters.AddWithValue("conversation_id", conversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
