using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static partial class ReaderDiscoveryEndpointsV1
{
    private const int MaximumDiscoveredSessions = 2000;

    public static IEndpointRouteBuilder MapReaderDiscoveryEndpointsV1(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var reader = endpoints.MapGroup("/api/reader")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:ReaderBearerToken"));
        reader.MapGet("/discovery-jobs/next", GetNextJobAsync);
        reader.MapPost("/discovery-jobs/{jobId:guid}/sessions", CompleteJobAsync);
        reader.MapPost("/discovery-jobs/{jobId:guid}/fail", FailJobAsync);

        var web = endpoints.MapGroup("/api/accounts")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        web.MapPost("/{accountId:guid}/discovery-jobs", CreateJobAsync);
        web.MapGet("/{accountId:guid}/discovery-jobs/{jobId:guid}", GetJobAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateJobAsync(
        Guid accountId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        var activeAccount = configuration["Reader:AllowedAccountExternalId"];
        var provider = configuration["Reader:Provider"] ?? "local-wechat";
        if (string.IsNullOrWhiteSpace(activeAccount))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The active Reader account is not configured.", true);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            const string accountSql = """
                SELECT id FROM copilot.chat_accounts
                WHERE id = @account_id AND provider = @provider AND external_account_id = @external_id;
                """;
            await using (var account = new NpgsqlCommand(accountSql, connection, transaction))
            {
                account.Parameters.AddWithValue("account_id", accountId);
                account.Parameters.AddWithValue("provider", provider);
                account.Parameters.AddWithValue("external_id", activeAccount);
                if (await account.ExecuteScalarAsync(cancellationToken) is not Guid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status409Conflict,
                        "ACCOUNT_NOT_ACTIVE_FOR_READER", "Only the active local Reader account can be scanned.", false);
                }
            }

            const string existingSql = """
                SELECT job_id FROM copilot.reader_directory_jobs
                WHERE account_id = @account_id AND status IN ('Pending', 'Running')
                ORDER BY created_at LIMIT 1;
                """;
            await using (var existing = new NpgsqlCommand(existingSql, connection, transaction))
            {
                existing.Parameters.AddWithValue("account_id", accountId);
                var existingId = await existing.ExecuteScalarAsync(cancellationToken);
                if (existingId is Guid jobId)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return Results.Accepted($"/api/accounts/{accountId:D}/discovery-jobs/{jobId:D}", new { jobId, status = "PendingOrRunning" });
                }
            }

            const string insertSql = """
                INSERT INTO copilot.reader_directory_jobs (account_id, status)
                VALUES (@account_id, 'Pending')
                RETURNING job_id;
                """;
            Guid newJobId;
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("account_id", accountId);
                newJobId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("The Reader discovery job was not created."));
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Accepted(
                $"/api/accounts/{accountId:D}/discovery-jobs/{newJobId:D}",
                new { jobId = newJobId, status = "Pending" });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetJobAsync(
        Guid accountId,
        Guid jobId,
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        const string sql = """
            SELECT status, discovered_count, error_code, created_at, started_at, completed_at
            FROM copilot.reader_directory_jobs
            WHERE account_id = @account_id AND job_id = @job_id;
            """;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.AddWithValue("job_id", jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Error(context, StatusCodes.Status404NotFound,
                    "READER_DISCOVERY_JOB_NOT_FOUND", "The discovery job is not available.", false);
            }

            return Results.Ok(new
            {
                jobId,
                status = reader.GetString(0),
                discoveredConversationCount = reader.GetInt32(1),
                errorCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                createdAtUtc = reader.GetFieldValue<DateTimeOffset>(3),
                startedAtUtc = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4),
                completedAtUtc = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5)
            });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetNextJobAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        var accountExternalId = configuration["Reader:AllowedAccountExternalId"];
        var provider = configuration["Reader:Provider"] ?? "local-wechat";
        if (string.IsNullOrWhiteSpace(accountExternalId))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The Reader account is not configured.", false);
        }

        const string sql = """
            WITH active_account AS (
                SELECT id, external_account_id
                FROM copilot.chat_accounts
                WHERE provider = @provider AND external_account_id = @account_external_id
            ), next_job AS (
                SELECT job.job_id, job.account_id
                FROM copilot.reader_directory_jobs AS job
                JOIN active_account AS account ON account.id = job.account_id
                WHERE job.status = 'Pending'
                ORDER BY job.created_at, job.job_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE copilot.reader_directory_jobs AS job
            SET status = 'Running', started_at = now()
            FROM next_job, active_account
            WHERE job.job_id = next_job.job_id
            RETURNING job.job_id, active_account.external_account_id;
            """;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("provider", provider);
            command.Parameters.AddWithValue("account_external_id", accountExternalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return Results.Ok(new { job = (object?)null });
            }

            var job = new { jobId = reader.GetGuid(0), accountExternalId = reader.GetString(1) };
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { job });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> CompleteJobAsync(
        Guid jobId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        ReaderDirectorySubmissionV1? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ReaderDirectorySubmissionV1>(cancellationToken);
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
        var sessions = request?.Sessions;
        if (request is null
            || string.IsNullOrWhiteSpace(allowedAccount)
            || !string.Equals(request.AccountExternalId, allowedAccount, StringComparison.Ordinal)
            || sessions is null
            || sessions.Count > MaximumDiscoveredSessions
            || sessions.Any(session => session is null
                || string.IsNullOrWhiteSpace(session.ConversationExternalId)
                || session.ConversationExternalId.Length > 512
                || session.ConversationExternalId.EndsWith("@chatroom", StringComparison.Ordinal)
                || session.ConversationExternalId.StartsWith("gh_", StringComparison.Ordinal)
                || session.ConversationExternalId is "filehelper" or "fmessage" or "newsapp" or "weixin" or "tmessage" or "brandsessionholder" or "notifymessage"
                || session.ConversationExternalId.Any(char.IsControl)
                || (session.DisplayName is not null && (session.DisplayName.Length > 200 || session.DisplayName.Any(char.IsControl))))
            || sessions.Select(session => session.ConversationExternalId).Distinct(StringComparer.Ordinal).Count() != sessions.Count)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_READER_DIRECTORY", "The local Reader directory contains invalid or unsupported sessions.", false);
        }

        var provider = configuration["Reader:Provider"] ?? "local-wechat";
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var accountId = await FindAccountIdAsync(connection, transaction, provider, allowedAccount, cancellationToken);
            if (accountId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Error(context, StatusCodes.Status404NotFound,
                    "READER_ACCOUNT_NOT_FOUND", "The configured Reader account is not available.", false);
            }

            const string jobSql = """
                SELECT status FROM copilot.reader_directory_jobs
                WHERE job_id = @job_id AND account_id = @account_id FOR UPDATE;
                """;
            await using (var jobCommand = new NpgsqlCommand(jobSql, connection, transaction))
            {
                jobCommand.Parameters.AddWithValue("job_id", jobId);
                jobCommand.Parameters.AddWithValue("account_id", accountId.Value);
                var status = await jobCommand.ExecuteScalarAsync(cancellationToken) as string;
                if (!string.Equals(status, "Running", StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Error(context, StatusCodes.Status409Conflict,
                        "READER_DISCOVERY_JOB_NOT_RUNNING", "The discovery job is no longer active.", false);
                }
            }

            foreach (var session in sessions)
            {
                const string contactSql = """
                    INSERT INTO copilot.contacts (account_id, external_contact_id, display_name)
                    VALUES (@account_id, @external_id, @display_name)
                    ON CONFLICT (account_id, external_contact_id) DO UPDATE
                    SET display_name = COALESCE(NULLIF(EXCLUDED.display_name, ''), copilot.contacts.display_name)
                    RETURNING id;
                    """;
                Guid contactId;
                await using (var contact = new NpgsqlCommand(contactSql, connection, transaction))
                {
                    contact.Parameters.AddWithValue("account_id", accountId.Value);
                    contact.Parameters.AddWithValue("external_id", session.ConversationExternalId);
                    contact.Parameters.AddWithValue("display_name", session.DisplayName ?? string.Empty);
                    contactId = (Guid)(await contact.ExecuteScalarAsync(cancellationToken)
                        ?? throw new InvalidOperationException("The discovered contact was not stored."));
                }

                const string conversationSql = """
                    INSERT INTO copilot.conversations (
                        account_id, contact_id, external_conversation_id, conversation_type)
                    VALUES (@account_id, @contact_id, @external_id, 'Direct')
                    ON CONFLICT (account_id, external_conversation_id) DO UPDATE
                    SET contact_id = EXCLUDED.contact_id;
                    """;
                await using var conversation = new NpgsqlCommand(conversationSql, connection, transaction);
                conversation.Parameters.AddWithValue("account_id", accountId.Value);
                conversation.Parameters.AddWithValue("contact_id", contactId);
                conversation.Parameters.AddWithValue("external_id", session.ConversationExternalId);
                await conversation.ExecuteNonQueryAsync(cancellationToken);
            }

            const string finishSql = """
                UPDATE copilot.reader_directory_jobs
                SET status = 'Completed', discovered_count = @discovered_count,
                    error_code = NULL, completed_at = now()
                WHERE job_id = @job_id AND account_id = @account_id;
                """;
            await using (var finish = new NpgsqlCommand(finishSql, connection, transaction))
            {
                finish.Parameters.AddWithValue("discovered_count", sessions.Count);
                finish.Parameters.AddWithValue("job_id", jobId);
                finish.Parameters.AddWithValue("account_id", accountId.Value);
                await finish.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { accepted = true, discoveredConversationCount = sessions.Count });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> FailJobAsync(
        Guid jobId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);
        ReaderDirectoryFailureRequestV1? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ReaderDirectoryFailureRequestV1>(cancellationToken);
        }
        catch (JsonException)
        {
            return Error(context, StatusCodes.Status400BadRequest, "INVALID_JSON", "The request body is not valid JSON.", false);
        }
        var allowedAccount = configuration["Reader:AllowedAccountExternalId"];
        if (request is null || string.IsNullOrWhiteSpace(allowedAccount) || !ErrorCodeRegex().IsMatch(request.ErrorCode ?? string.Empty))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_READER_DIRECTORY_FAILURE", "A sanitized Reader error code is required.", false);
        }

        const string sql = """
            UPDATE copilot.reader_directory_jobs AS job
            SET status = 'Failed', error_code = @error_code, completed_at = now()
            FROM copilot.chat_accounts AS account
            WHERE job.job_id = @job_id AND job.account_id = account.id
              AND account.provider = @provider AND account.external_account_id = @account_external_id
              AND job.status = 'Running';
            """;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("error_code", request.ErrorCode!);
            command.Parameters.AddWithValue("provider", configuration["Reader:Provider"] ?? "local-wechat");
            command.Parameters.AddWithValue("account_external_id", allowedAccount);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Results.Ok(new { accepted = true })
                : Error(context, StatusCodes.Status404NotFound,
                    "READER_DISCOVERY_JOB_NOT_FOUND", "The active discovery job is not available.", false);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<Guid?> FindAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string provider,
        string accountExternalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id FROM copilot.chat_accounts
            WHERE provider = @provider AND external_account_id = @external_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("external_id", accountExternalId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid accountId ? accountId : null;
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

public sealed record ReaderDirectorySessionV1(string ConversationExternalId, string? DisplayName);
public sealed record ReaderDirectorySubmissionV1(string? AccountExternalId, IReadOnlyList<ReaderDirectorySessionV1>? Sessions);
public sealed record ReaderDirectoryFailureRequestV1(string? ErrorCode);
