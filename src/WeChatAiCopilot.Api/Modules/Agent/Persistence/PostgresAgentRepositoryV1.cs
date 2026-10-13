using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Persistence;

/// <summary>
/// PostgreSQL persistence for scoped M4 results. Every lookup includes both
/// account and conversation IDs; the database also enforces immutable thread scope.
/// </summary>
public sealed class PostgresAgentRepositoryV1(NpgsqlDataSource dataSource) : IAgentRepositoryV1
{
    public async Task<AuthorizedContextScopeV1?> ResolveAuthorizedScopeAsync(
        AgentWebConversationTargetV1 target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        const string sql = """
            SELECT account.id, conversation.id
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
            WHERE account.provider = @provider
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider", target.Provider);
        command.Parameters.AddWithValue("account_external_id", target.AccountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", target.ConversationExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AuthorizedContextScopeV1(reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    public async Task StoreSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        SuggestionBatchV1 batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(batch);
        ValidateBatchForStorage(scope, batch);

        var batchId = ParseId(batch.BatchId!, "batchId");
        var runId = ParseId(batch.RunId!, "runId");
        const string batchSql = """
            INSERT INTO copilot.agent_suggestion_batches (
                batch_id, run_id, account_id, conversation_id, persona_preset_id,
                context_revision, status, created_at, execution_profile_id)
            VALUES (
                @batch_id, @run_id, @account_id, @conversation_id, @persona_preset_id,
                @context_revision, @status, @created_at, @execution_profile_id);
            """;
        const string suggestionSql = """
            INSERT INTO copilot.agent_suggestions (
                suggestion_id, batch_id, account_id, conversation_id, ordinal, text_content)
            VALUES (@suggestion_id, @batch_id, @account_id, @conversation_id, @ordinal, @text_content);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(batchSql, connection, transaction))
        {
            command.Parameters.AddWithValue("batch_id", batchId);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("persona_preset_id", batch.PersonaPresetId!);
            command.Parameters.AddWithValue("context_revision", batch.ContextRevision!);
            command.Parameters.AddWithValue("status", batch.Status.ToString());
            command.Parameters.AddWithValue("created_at", batch.CreatedAtUtc);
            command.Parameters.AddWithValue("execution_profile_id", batch.ExecutionProfileId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < batch.Suggestions!.Count; index++)
        {
            var suggestion = batch.Suggestions[index];
            await using var command = new NpgsqlCommand(suggestionSql, connection, transaction);
            command.Parameters.AddWithValue("suggestion_id", ParseId(suggestion.Id!, "suggestionId"));
            command.Parameters.AddWithValue("batch_id", batchId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("ordinal", (short)(index + 1));
            command.Parameters.AddWithValue("text_content", suggestion.Text!);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CompleteAgentRunAsync(connection, transaction, scope, runId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SuggestionBatchV1?> GetSuggestionBatchAsync(
        AuthorizedContextScopeV1 scope,
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        const string batchSql = """
            SELECT run_id, batch_id, account_id, conversation_id, persona_preset_id,
                   context_revision, status, created_at, execution_profile_id
            FROM copilot.agent_suggestion_batches
            WHERE batch_id = @batch_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id;
            """;

        Guid runId;
        Guid foundBatchId;
        Guid accountId;
        Guid conversationId;
        string personaPresetId;
        string contextRevision;
        AgentRunStatusV1 status;
        DateTimeOffset createdAt;
        AgentExecutionProfileIdV1 executionProfileId;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(batchSql, connection))
        {
            command.Parameters.AddWithValue("batch_id", batchId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            runId = reader.GetGuid(0);
            foundBatchId = reader.GetGuid(1);
            accountId = reader.GetGuid(2);
            conversationId = reader.GetGuid(3);
            personaPresetId = reader.GetString(4);
            contextRevision = reader.GetString(5);
            status = ParseEnum<AgentRunStatusV1>(reader.GetString(6), "suggestion batch status");
            createdAt = reader.GetFieldValue<DateTimeOffset>(7);
            executionProfileId = ParseEnum<AgentExecutionProfileIdV1>(reader.GetString(8), "suggestion execution profile");
        }

        var suggestions = new List<AgentSuggestionV1>(3);
        const string suggestionsSql = """
            SELECT suggestion_id, text_content
            FROM copilot.agent_suggestions
            WHERE batch_id = @batch_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
            ORDER BY ordinal;
            """;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(suggestionsSql, connection))
        {
            command.Parameters.AddWithValue("batch_id", batchId);
            command.Parameters.AddWithValue("account_id", scope.AccountId);
            command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                suggestions.Add(new AgentSuggestionV1
                {
                    Id = reader.GetGuid(0).ToString("N"),
                    Text = reader.GetString(1)
                });
            }
        }

        if (suggestions.Count != 3)
        {
            throw new InvalidOperationException("The stored suggestion batch does not contain exactly three suggestions.");
        }

        return new SuggestionBatchV1
        {
            RunId = runId.ToString("N"),
            BatchId = foundBatchId.ToString("N"),
            TaskType = AgentTaskTypeV1.ReplyCandidates,
            ExecutionProfileId = executionProfileId,
            AccountId = accountId,
            ConversationId = conversationId,
            PersonaPresetId = personaPresetId,
            ContextRevision = contextRevision,
            Status = status,
            CreatedAtUtc = createdAt,
            Suggestions = suggestions
        };
    }

    public async Task StoreConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        AgentConsultationThreadV1 thread,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.AccountId != scope.AccountId || thread.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("The consultation thread is outside the authorized conversation scope.");
        }

        if (string.IsNullOrWhiteSpace(thread.ThreadId)
            || thread.CreatedAtUtc == default
            || thread.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A valid thread ID and UTC creation timestamp are required.", nameof(thread));
        }

        const string sql = """
            INSERT INTO copilot.agent_consultation_threads (
                thread_id, account_id, conversation_id, title, created_at)
            VALUES (@thread_id, @account_id, @conversation_id, @title, @created_at);
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", ParseId(thread.ThreadId, "threadId"));
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("title", (object?)thread.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", thread.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentConsultationThreadV1?> GetConsultationThreadAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        const string sql = """
            SELECT thread_id, account_id, conversation_id, title, created_at
            FROM copilot.agent_consultation_threads
            WHERE thread_id = @thread_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", threadId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AgentConsultationThreadV1
        {
            ThreadId = reader.GetGuid(0).ToString("N"),
            AccountId = reader.GetGuid(1),
            ConversationId = reader.GetGuid(2),
            Title = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(4)
        };
    }

    public async Task StoreConsultationTurnAsync(
        AuthorizedContextScopeV1 scope,
        ConsultationTurnV1 turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.AccountId != scope.AccountId || turn.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("The consultation turn is outside the authorized conversation scope.");
        }

        if (string.IsNullOrWhiteSpace(turn.ThreadId)
            || string.IsNullOrWhiteSpace(turn.RunId)
            || string.IsNullOrWhiteSpace(turn.UserInput)
            || string.IsNullOrWhiteSpace(turn.Answer)
            || turn.Status != AgentRunStatusV1.Completed
            || turn.CreatedAtUtc == default
            || turn.CreatedAtUtc.Offset != TimeSpan.Zero
            || turn.EvidenceMessageIds is null
            || turn.EvidenceMessageIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The consultation turn is incomplete or invalid.", nameof(turn));
        }

        const string sql = """
            INSERT INTO copilot.agent_consultation_turns (
                run_id, thread_id, account_id, conversation_id, user_input, answer,
                evidence_message_ids, status, created_at)
            VALUES (
                @run_id, @thread_id, @account_id, @conversation_id, @user_input, @answer,
                @evidence_message_ids, @status, @created_at);
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("run_id", ParseId(turn.RunId, "runId"));
        command.Parameters.AddWithValue("thread_id", ParseId(turn.ThreadId, "threadId"));
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("user_input", turn.UserInput);
        command.Parameters.AddWithValue("answer", turn.Answer);
        command.Parameters.Add("evidence_message_ids", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(turn.EvidenceMessageIds);
        command.Parameters.AddWithValue("status", turn.Status.ToString());
        command.Parameters.AddWithValue("created_at", turn.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteAgentRunAsync(
            connection,
            transaction,
            scope,
            ParseId(turn.RunId, "runId"),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsultationTurnV1>> GetConsultationTurnsAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        const string sql = """
            SELECT turn.run_id, turn.thread_id, turn.account_id, turn.conversation_id,
                   turn.user_input, turn.answer, turn.evidence_message_ids::text,
                   turn.status, turn.created_at
            FROM copilot.agent_consultation_turns AS turn
            JOIN copilot.agent_consultation_threads AS thread
              ON thread.thread_id = turn.thread_id
             AND thread.account_id = turn.account_id
             AND thread.conversation_id = turn.conversation_id
            WHERE turn.thread_id = @thread_id
              AND turn.account_id = @account_id
              AND turn.conversation_id = @conversation_id
            ORDER BY turn.created_at, turn.run_id;
            """;

        var turns = new List<ConsultationTurnV1>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", threadId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evidence = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [];
            turns.Add(new ConsultationTurnV1
            {
                RunId = reader.GetGuid(0).ToString("N"),
                ThreadId = reader.GetGuid(1).ToString("N"),
                AccountId = reader.GetGuid(2),
                ConversationId = reader.GetGuid(3),
                UserInput = reader.GetString(4),
                Answer = reader.GetString(5),
                EvidenceMessageIds = evidence,
                Status = ParseEnum<AgentRunStatusV1>(reader.GetString(7), "consultation turn status"),
                CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(8)
            });
        }

        return turns;
    }

    public async Task<IReadOnlyList<ConsultationTurnV1>> GetRecentConsultationTurnsAsync(
        AuthorizedContextScopeV1 scope,
        Guid threadId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (limit is < 1 or > 101)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Consultation history query limit must be between 1 and 101.");
        }

        const string sql = """
            WITH recent_turns AS (
                SELECT turn.run_id, turn.thread_id, turn.account_id, turn.conversation_id,
                       turn.user_input, turn.answer, turn.evidence_message_ids::text,
                       turn.status, turn.created_at
                FROM copilot.agent_consultation_turns AS turn
                JOIN copilot.agent_consultation_threads AS thread
                  ON thread.thread_id = turn.thread_id
                 AND thread.account_id = turn.account_id
                 AND thread.conversation_id = turn.conversation_id
                WHERE turn.thread_id = @thread_id
                  AND turn.account_id = @account_id
                  AND turn.conversation_id = @conversation_id
                ORDER BY turn.created_at DESC, turn.run_id DESC
                LIMIT @limit
            )
            SELECT run_id, thread_id, account_id, conversation_id,
                   user_input, answer, evidence_message_ids,
                   status, created_at
            FROM recent_turns
            ORDER BY created_at, run_id;
            """;

        var turns = new List<ConsultationTurnV1>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", threadId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evidence = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [];
            turns.Add(new ConsultationTurnV1
            {
                RunId = reader.GetGuid(0).ToString("N"),
                ThreadId = reader.GetGuid(1).ToString("N"),
                AccountId = reader.GetGuid(2),
                ConversationId = reader.GetGuid(3),
                UserInput = reader.GetString(4),
                Answer = reader.GetString(5),
                EvidenceMessageIds = evidence,
                Status = ParseEnum<AgentRunStatusV1>(reader.GetString(7), "consultation turn status"),
                CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(8)
            });
        }

        return turns;
    }

    public async Task StartAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        AgentRunV1 run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(run);
        if (run.AccountId != scope.AccountId || run.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("The Agent run is outside the authorized conversation scope.");
        }

        if (run.Status != AgentRunStatusV1.Running
            || run.EndedAtUtc is not null
            || run.ErrorCode is not null
            || run.StartedAtUtc == default
            || run.StartedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(run.RuntimeName)
            || string.IsNullOrWhiteSpace(run.SkillVersion)
            || string.IsNullOrWhiteSpace(run.ContextRevision)
            || run.BudgetMetadata is null)
        {
            throw new ArgumentException("The initial Agent run audit record is incomplete.", nameof(run));
        }

        const string sql = """
            INSERT INTO copilot.agent_runs (
                run_id, task_type, account_id, conversation_id, runtime_name,
                skill_version, context_revision, status, started_at, ended_at,
                error_code, budget_metadata)
            VALUES (
                @run_id, @task_type, @account_id, @conversation_id, @runtime_name,
                @skill_version, @context_revision, @status, @started_at, NULL,
                NULL, @budget_metadata);
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", ParseId(run.RunId!, "runId"));
        command.Parameters.AddWithValue("task_type", run.TaskType.ToString());
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        command.Parameters.AddWithValue("runtime_name", run.RuntimeName);
        command.Parameters.AddWithValue("skill_version", run.SkillVersion);
        command.Parameters.AddWithValue("context_revision", run.ContextRevision);
        command.Parameters.AddWithValue("status", AgentRunStatusV1.Running.ToString());
        command.Parameters.AddWithValue("started_at", run.StartedAtUtc);
        command.Parameters.Add("budget_metadata", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(run.BudgetMetadata);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAgentRunRuntimeNameAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        string runtimeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsSafeRuntimeName(runtimeName))
        {
            throw new ArgumentException("Runtime name contains unsupported characters.", nameof(runtimeName));
        }

        const string sql = """
            UPDATE copilot.agent_runs
            SET runtime_name = @runtime_name
            WHERE run_id = @run_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND status = 'Running';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("runtime_name", runtimeName);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Agent run is unavailable for runtime metadata update.");
        }
    }

    public async Task FinishAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        AgentRunStatusV1 status,
        string errorCode,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (status is not (AgentRunStatusV1.Failed or AgentRunStatusV1.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Only failed or cancelled runs can be finalized directly.");
        }

        if (!IsSafeErrorCode(errorCode) || endedAtUtc == default || endedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A safe error code and UTC end timestamp are required.");
        }

        const string sql = """
            UPDATE copilot.agent_runs
            SET status = @status,
                error_code = @error_code,
                ended_at = @ended_at
            WHERE run_id = @run_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND status = 'Running';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("error_code", errorCode);
        command.Parameters.AddWithValue("ended_at", endedAtUtc);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Agent run is unavailable for failure finalization.");
        }
    }

    public async Task<AgentRunV1?> GetAgentRunAsync(
        AuthorizedContextScopeV1 scope,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        const string sql = """
            SELECT run_id, task_type, account_id, conversation_id, runtime_name,
                   skill_version, context_revision, status, started_at, ended_at,
                   error_code, budget_metadata::text
            FROM copilot.agent_runs
            WHERE run_id = @run_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var budgetMetadata = JsonSerializer.Deserialize<AgentRunBudgetMetadataV1>(reader.GetString(11));
        return new AgentRunV1
        {
            RunId = reader.GetGuid(0).ToString("N"),
            TaskType = ParseEnum<AgentTaskTypeV1>(reader.GetString(1), "Agent run task type"),
            AccountId = reader.GetGuid(2),
            ConversationId = reader.GetGuid(3),
            RuntimeName = reader.GetString(4),
            SkillVersion = reader.GetString(5),
            ContextRevision = reader.GetString(6),
            Status = ParseEnum<AgentRunStatusV1>(reader.GetString(7), "Agent run status"),
            StartedAtUtc = reader.GetFieldValue<DateTimeOffset>(8),
            EndedAtUtc = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            ErrorCode = reader.IsDBNull(10) ? null : reader.GetString(10),
            ExecutionProfileId = budgetMetadata?.ExecutionProfileId ?? AgentExecutionProfileIdV1.Quick,
            BudgetMetadata = budgetMetadata
        };
    }

    private static async Task CompleteAgentRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedContextScopeV1 scope,
        Guid runId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE copilot.agent_runs
            SET status = 'Completed',
                ended_at = now(),
                error_code = NULL
            WHERE run_id = @run_id
              AND account_id = @account_id
              AND conversation_id = @conversation_id
              AND status = 'Running';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", scope.AccountId);
        command.Parameters.AddWithValue("conversation_id", scope.ConversationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Agent run is unavailable for completion.");
        }
    }

    private static void ValidateBatchForStorage(AuthorizedContextScopeV1 scope, SuggestionBatchV1 batch)
    {
        var violations = AgentContractValidatorV1.Validate(batch);
        if (batch.AccountId != scope.AccountId || batch.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("The suggestion batch is outside the authorized conversation scope.");
        }

        if (violations.Count != 0)
        {
            throw new ArgumentException("The suggestion batch failed contract validation.", nameof(batch));
        }

        _ = ParseId(batch.BatchId!, "batchId");
        _ = ParseId(batch.RunId!, "runId");
        if (!Enum.IsDefined(batch.ExecutionProfileId))
        {
            throw new ArgumentException("The suggestion execution profile is invalid.", nameof(batch));
        }
        foreach (var suggestion in batch.Suggestions!)
        {
            _ = ParseId(suggestion.Id!, "suggestionId");
        }
    }

    private static Guid ParseId(string value, string fieldName) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{fieldName} must be a UUID.", fieldName);

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Stored {fieldName} is invalid.");

    private static bool IsSafeErrorCode(string value) =>
        !string.IsNullOrWhiteSpace(value)
            && value.Length <= 80
            && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool IsSafeRuntimeName(string value) =>
        !string.IsNullOrWhiteSpace(value)
            && value.Length <= 100
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
