using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static partial class ReaderManagementEndpointsV1
{
    private const int MaximumMonitoredTargets = 20;
    private static readonly TimeSpan ReaderHeartbeatLifetime = TimeSpan.FromSeconds(20);

    public static IEndpointRouteBuilder MapReaderManagementEndpointsV1(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var reader = endpoints.MapGroup("/api/reader")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:ReaderBearerToken"));
        reader.MapGet("/targets", GetTargetsAsync);
        reader.MapPost("/status", UpdateReaderStatusAsync);

        var web = endpoints.MapGroup("/api")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        web.MapGet("/reader/status", GetReaderStatusAsync);
        web.MapPut("/accounts/{accountId:guid}/reader-targets", ReplaceTargetsAsync);
        return endpoints;
    }

    private static async Task<IResult> GetTargetsAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        var provider = configuration["Reader:Provider"] ?? "local-wechat";
        var accountExternalId = configuration["Reader:AllowedAccountExternalId"];
        if (string.IsNullOrWhiteSpace(accountExternalId))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The authorized Reader account is not configured.", true);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            const string ensureAccountSql = """
                INSERT INTO copilot.chat_accounts (provider, external_account_id)
                VALUES (@provider, @account_external_id)
                ON CONFLICT (provider, external_account_id) DO NOTHING;
                """;
            await using (var ensureAccount = new NpgsqlCommand(ensureAccountSql, connection, transaction))
            {
                ensureAccount.Parameters.AddWithValue("provider", provider);
                ensureAccount.Parameters.AddWithValue("account_external_id", accountExternalId);
                await ensureAccount.ExecuteNonQueryAsync(cancellationToken);
            }

            const string ensureTargetSetSql = """
                INSERT INTO copilot.reader_target_sets (account_id, target_version)
                SELECT account.id, 0
                FROM copilot.chat_accounts AS account
                WHERE account.provider = @provider
                  AND account.external_account_id = @account_external_id
                ON CONFLICT (account_id) DO NOTHING;
                """;
            await using (var ensureTargetSet = new NpgsqlCommand(ensureTargetSetSql, connection, transaction))
            {
                ensureTargetSet.Parameters.AddWithValue("provider", provider);
                ensureTargetSet.Parameters.AddWithValue("account_external_id", accountExternalId);
                await ensureTargetSet.ExecuteNonQueryAsync(cancellationToken);
            }

            // The Reader caches its target list by version, so a target added here has to advance that
            // version as well; otherwise the Reader sees one version carrying two different target sets.
            const string initializeSql = """
                WITH seeded AS (
                    INSERT INTO copilot.monitored_conversation_targets (account_id, conversation_id)
                    SELECT conversation.account_id, conversation.id
                    FROM copilot.chat_accounts AS account
                    JOIN copilot.reader_target_sets AS target_set
                      ON target_set.account_id = account.id
                     AND target_set.target_version = 0
                    JOIN copilot.conversations AS conversation
                      ON conversation.account_id = account.id
                     AND conversation.external_conversation_id = @legacy_conversation_external_id
                     AND conversation.conversation_type = 'Direct'
                    WHERE @legacy_conversation_external_id <> ''
                    ON CONFLICT (account_id, conversation_id) DO NOTHING
                    RETURNING account_id
                )
                UPDATE copilot.reader_target_sets AS target_set
                SET target_version = target_set.target_version + 1
                WHERE target_set.account_id IN (SELECT account_id FROM seeded);
                """;
            await using (var initialize = new NpgsqlCommand(initializeSql, connection, transaction))
            {
                initialize.Parameters.AddWithValue("provider", provider);
                initialize.Parameters.AddWithValue("account_external_id", accountExternalId);
                initialize.Parameters.AddWithValue("legacy_conversation_external_id",
                    configuration["Reader:AllowedConversationExternalId"] ?? string.Empty);
                await initialize.ExecuteNonQueryAsync(cancellationToken);
            }

            const string readSql = """
                SELECT account.id,
                       target_set.target_version,
                       conversation.external_conversation_id
                FROM copilot.chat_accounts AS account
                JOIN copilot.reader_target_sets AS target_set ON target_set.account_id = account.id
                LEFT JOIN copilot.monitored_conversation_targets AS target ON target.account_id = account.id
                LEFT JOIN copilot.conversations AS conversation
                  ON conversation.id = target.conversation_id
                 AND conversation.account_id = target.account_id
                 AND conversation.conversation_type = 'Direct'
                WHERE account.provider = @provider
                  AND account.external_account_id = @account_external_id
                ORDER BY conversation.external_conversation_id;
                """;
            long version = 0;
            string? accountId = null;
            var targets = new List<string>();
            await using (var command = new NpgsqlCommand(readSql, connection, transaction))
            {
                command.Parameters.AddWithValue("provider", provider);
                command.Parameters.AddWithValue("account_external_id", accountExternalId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    accountId ??= reader.GetGuid(0).ToString("D");
                    version = reader.GetInt64(1);
                    if (!reader.IsDBNull(2)) targets.Add(reader.GetString(2));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            if (accountId is null)
            {
                return Error(context, StatusCodes.Status404NotFound,
                    "READER_ACCOUNT_NOT_FOUND", "The configured Reader account is not available.", false);
            }

            if (targets.Count > MaximumMonitoredTargets)
            {
                return Error(context, StatusCodes.Status409Conflict,
                    "READER_TARGET_LIMIT_EXCEEDED", "The monitored target set exceeds the supported limit.", false);
            }

            return Results.Ok(new
            {
                accountId,
                accountExternalId,
                targetVersion = version,
                conversationExternalIds = targets,
                refreshIntervalSeconds = 5,
                maximumTargets = MaximumMonitoredTargets
            });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> UpdateReaderStatusAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        ReaderStatusHeartbeatRequestV1? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ReaderStatusHeartbeatRequestV1>(cancellationToken);
        }
        catch (JsonException)
        {
            return Error(context, StatusCodes.Status400BadRequest, "INVALID_JSON", "The request body is not valid JSON.", false);
        }
        catch (InvalidOperationException)
        {
            return Error(context, StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE", "The request body must use application/json.", false);
        }

        var allowedAccount = configuration["Reader:AllowedAccountExternalId"];
        var provider = configuration["Reader:Provider"] ?? "local-wechat";
        if (request is null
            || request.ReaderInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(allowedAccount)
            || !string.Equals(request.AccountExternalId, allowedAccount, StringComparison.Ordinal)
            || request.State is not ("Starting" or "Listening" or "Idle" or "Error" or "Stopped")
            || request.TargetVersion < 0
            || request.ListeningTargetCount is < 0 or > MaximumMonitoredTargets
            || request.StartedAtUtc == default
            || (request.LastErrorCode is not null && !ErrorCodeRegex().IsMatch(request.LastErrorCode)))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_READER_STATUS", "The Reader heartbeat does not satisfy the status contract.", false);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            const string accountSql = """
                SELECT id
                FROM copilot.chat_accounts
                WHERE provider = @provider AND external_account_id = @account_external_id;
                """;
            Guid accountId;
            await using (var command = new NpgsqlCommand(accountSql, connection, transaction))
            {
                command.Parameters.AddWithValue("provider", provider);
                command.Parameters.AddWithValue("account_external_id", allowedAccount);
                var value = await command.ExecuteScalarAsync(cancellationToken);
                if (value is not Guid parsedAccountId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status404NotFound,
                        "READER_ACCOUNT_NOT_FOUND", "The configured Reader account is not available.", false);
                }

                accountId = parsedAccountId;
            }

            const string targetStateSql = """
                SELECT target_set.target_version,
                       COUNT(target.conversation_id)::integer
                FROM copilot.reader_target_sets AS target_set
                LEFT JOIN copilot.monitored_conversation_targets AS target
                  ON target.account_id = target_set.account_id
                WHERE target_set.account_id = @account_id
                GROUP BY target_set.target_version;
                """;
            await using (var command = new NpgsqlCommand(targetStateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var hasTargetSet = await reader.ReadAsync(cancellationToken);
                var targetVersionMatches = hasTargetSet && request.TargetVersion <= reader.GetInt64(0);
                var stateAndCountAgree = request.State switch
                {
                    "Listening" => request.ListeningTargetCount > 0,
                    "Idle" => request.ListeningTargetCount == 0,
                    "Stopped" => request.ListeningTargetCount == 0,
                    _ => true
                };
                if (!targetVersionMatches || !stateAndCountAgree)
                {
                    await reader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status409Conflict,
                        "READER_TARGET_STATE_STALE", "The heartbeat does not match the authorized target set.", true);
                }
            }

            const string currentSql = """
                SELECT reader_instance_id, heartbeat_at
                FROM copilot.reader_status
                WHERE account_id = @account_id
                FOR UPDATE;
                """;
            await using (var command = new NpgsqlCommand(currentSql, connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)
                    && reader.GetGuid(0) != request.ReaderInstanceId
                    && reader.GetDateTime(1) > DateTime.UtcNow - ReaderHeartbeatLifetime)
                {
                    await reader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status409Conflict,
                        "READER_INSTANCE_ALREADY_ACTIVE", "Another Reader process currently owns this account.", true);
                }
            }

            const string upsertSql = """
                INSERT INTO copilot.reader_status (
                    account_id, reader_instance_id, state, target_version,
                    listening_target_count, last_error_code, started_at, heartbeat_at)
                VALUES (@account_id, @reader_instance_id, @state, @target_version,
                    @listening_target_count, @last_error_code, @started_at, now())
                ON CONFLICT (account_id) DO UPDATE SET
                    reader_instance_id = EXCLUDED.reader_instance_id,
                    state = EXCLUDED.state,
                    target_version = EXCLUDED.target_version,
                    listening_target_count = EXCLUDED.listening_target_count,
                    last_error_code = EXCLUDED.last_error_code,
                    started_at = EXCLUDED.started_at,
                    heartbeat_at = now();
                """;
            await using (var command = new NpgsqlCommand(upsertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                command.Parameters.AddWithValue("reader_instance_id", request.ReaderInstanceId);
                command.Parameters.AddWithValue("state", request.State);
                command.Parameters.AddWithValue("target_version", request.TargetVersion);
                command.Parameters.AddWithValue("listening_target_count", request.ListeningTargetCount);
                command.Parameters.AddWithValue("last_error_code", (object?)request.LastErrorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("started_at", request.StartedAtUtc.ToUniversalTime());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { accepted = true });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetReaderStatusAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        const string sql = """
            SELECT account.id,
                   account.display_name,
                   account.provider,
                   status.state,
                   status.heartbeat_at,
                   status.target_version,
                   status.listening_target_count,
                   status.last_error_code,
                   (status.heartbeat_at >= now() - interval '20 seconds') AS is_online
            FROM copilot.chat_accounts AS account
            JOIN copilot.reader_status AS status ON status.account_id = account.id
            ORDER BY account.created_at, account.id;
            """;
        try
        {
            var accounts = new List<object>();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new
                {
                    accountId = reader.GetGuid(0),
                    displayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    provider = reader.GetString(2),
                    state = reader.GetBoolean(8) ? reader.GetString(3) : "Offline",
                    lastHeartbeatAtUtc = reader.GetFieldValue<DateTimeOffset>(4),
                    targetVersion = reader.GetInt64(5),
                    listeningTargetCount = reader.GetInt32(6),
                    lastErrorCode = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }

            return Results.Ok(new { accounts, heartbeatFreshnessSeconds = 20 });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> ReplaceTargetsAsync(
        Guid accountId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        ReplaceReaderTargetsRequestV1? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ReplaceReaderTargetsRequestV1>(cancellationToken);
        }
        catch (JsonException)
        {
            return Error(context, StatusCodes.Status400BadRequest, "INVALID_JSON", "The request body is not valid JSON.", false);
        }
        catch (InvalidOperationException)
        {
            return Error(context, StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE", "The request body must use application/json.", false);
        }

        var conversationIds = request?.ConversationIds;
        if (request is null
            || request.ExpectedVersion < 0
            || conversationIds is null
            || conversationIds.Count > MaximumMonitoredTargets
            || conversationIds.Any(id => id == Guid.Empty)
            || conversationIds.Distinct().Count() != conversationIds.Count)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_READER_TARGETS", "The monitored target list must contain up to 20 unique conversation UUIDs.", false);
        }

        var allowedAccount = configuration["Reader:AllowedAccountExternalId"];
        if (string.IsNullOrWhiteSpace(allowedAccount))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The active Reader account is not configured.", false);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            const string accountSql = """
                SELECT id FROM copilot.chat_accounts
                WHERE id = @account_id
                  AND provider = @provider
                  AND external_account_id = @account_external_id;
                """;
            await using (var command = new NpgsqlCommand(accountSql, connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                command.Parameters.AddWithValue("provider", configuration["Reader:Provider"] ?? "local-wechat");
                command.Parameters.AddWithValue("account_external_id", allowedAccount);
                if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status409Conflict,
                        "ACCOUNT_NOT_ACTIVE_FOR_READER", "Only the configured local Reader account can monitor conversations.", false);
                }
            }

            const string ensureVersionSql = """
                INSERT INTO copilot.reader_target_sets (account_id, target_version)
                VALUES (@account_id, 0)
                ON CONFLICT (account_id) DO NOTHING;
                """;
            await using (var command = new NpgsqlCommand(ensureVersionSql, connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            long currentVersion;
            await using (var command = new NpgsqlCommand(
                "SELECT target_version FROM copilot.reader_target_sets WHERE account_id = @account_id FOR UPDATE;",
                connection, transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                currentVersion = (long)(await command.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Reader target set was not created."));
            }
            if (currentVersion != request.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Error(context, StatusCodes.Status409Conflict,
                    "READER_TARGET_VERSION_CONFLICT", "The monitored target set changed. Reload it before saving.", false);
            }

            if (conversationIds.Count > 0)
            {
                const string validateSql = """
                    SELECT COUNT(*)::integer
                    FROM copilot.conversations
                    WHERE account_id = @account_id
                      AND id = ANY(@conversation_ids)
                      AND conversation_type = 'Direct';
                    """;
                await using var validate = new NpgsqlCommand(validateSql, connection, transaction);
                validate.Parameters.AddWithValue("account_id", accountId);
                validate.Parameters.AddWithValue("conversation_ids", conversationIds.ToArray());
                var validCount = (int)(await validate.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Target validation returned no count."));
                if (validCount != conversationIds.Count)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status404NotFound,
                        "READER_TARGET_NOT_FOUND", "One or more selected Direct conversations are unavailable.", false);
                }
            }

            await using (var delete = new NpgsqlCommand(
                "DELETE FROM copilot.monitored_conversation_targets WHERE account_id = @account_id;",
                connection, transaction))
            {
                delete.Parameters.AddWithValue("account_id", accountId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            if (conversationIds.Count > 0)
            {
                const string insertSql = """
                    INSERT INTO copilot.monitored_conversation_targets (account_id, conversation_id)
                    SELECT @account_id, selected_id FROM unnest(@conversation_ids) AS selected_id;
                    """;
                await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
                insert.Parameters.AddWithValue("account_id", accountId);
                insert.Parameters.AddWithValue("conversation_ids", conversationIds.ToArray());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            var nextVersion = currentVersion + 1;
            await using (var update = new NpgsqlCommand(
                "UPDATE copilot.reader_target_sets SET target_version = @version, updated_at = now() WHERE account_id = @account_id;",
                connection, transaction))
            {
                update.Parameters.AddWithValue("version", nextVersion);
                update.Parameters.AddWithValue("account_id", accountId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { accountId, targetVersion = nextVersion, conversationIds });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    [GeneratedRegex("^[A-Z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    private static IResult DatabaseNotConfigured(HttpContext context) => Error(
        context, StatusCodes.Status503ServiceUnavailable,
        "DATABASE_NOT_CONFIGURED", "The formal message store is not configured.", true);

    private static IResult DatabaseUnavailable(HttpContext context) => Error(
        context, StatusCodes.Status503ServiceUnavailable,
        "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", true);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);
}
