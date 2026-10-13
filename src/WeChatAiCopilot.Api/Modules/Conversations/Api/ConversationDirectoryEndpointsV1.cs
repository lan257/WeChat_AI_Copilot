using System.Globalization;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class ConversationDirectoryEndpointsV1
{
    public static IEndpointRouteBuilder MapConversationDirectoryEndpointsV1(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var directory = endpoints.MapGroup("/api/accounts")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        directory.MapGet("", ListAccountsAsync);
        directory.MapGet("/{accountId:guid}/conversations", ListConversationsAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAccountsAsync(
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
                   COUNT(conversation.id)::integer AS conversation_count,
                   reader_status.state,
                   reader_status.heartbeat_at,
                   (reader_status.heartbeat_at >= now() - interval '20 seconds') AS heartbeat_fresh
            FROM copilot.chat_accounts AS account
            LEFT JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
             AND conversation.conversation_type = 'Direct'
            LEFT JOIN copilot.reader_status AS reader_status
              ON reader_status.account_id = account.id
            GROUP BY account.id, account.display_name, account.provider, account.created_at,
                     reader_status.state, reader_status.heartbeat_at
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
                    conversationCount = reader.GetInt32(3),
                    availability = reader.IsDBNull(4)
                        ? "Unknown"
                        : !reader.GetBoolean(6)
                            ? "Offline"
                            : reader.GetString(4) == "Error" ? "Error" : "ReaderOnline",
                    readerState = reader.IsDBNull(4) || !reader.GetBoolean(6)
                        ? reader.IsDBNull(4) ? "Unknown" : "Offline"
                        : reader.GetString(4),
                    lastReaderHeartbeatAtUtc = reader.IsDBNull(5)
                        ? (DateTimeOffset?)null
                        : reader.GetFieldValue<DateTimeOffset>(5)
                });
            }

            return Results.Ok(new { accounts });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> ListConversationsAsync(
        Guid accountId,
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryReadLimit(context, out var limit))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_LIMIT", "The limit query parameter must be an integer from 1 to 2000.");
        }

        var query = context.Request.Query.TryGetValue("q", out var values) && values.Count == 1
            ? (values.FirstOrDefault() ?? string.Empty).Trim()
            : string.Empty;
        if (query.Length > 120)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_SEARCH", "The conversation search text is too long.");
        }

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null) return DatabaseNotConfigured(context);

        const string sql = """
            SELECT conversation.id,
                   contact.display_name,
                   conversation.conversation_type,
                   conversation.last_message_cursor,
                   conversation.message_set_revision,
                   COALESCE(target_set.target_version, 0) AS target_version,
                   (target.conversation_id IS NOT NULL) AS is_monitored,
                   CASE
                       WHEN target.conversation_id IS NULL THEN 'NotMonitored'
                       WHEN reader_status.account_id IS NULL THEN 'Unknown'
                       WHEN reader_status.heartbeat_at < now() - interval '20 seconds' THEN 'Offline'
                       WHEN reader_status.state = 'Error' THEN 'Error'
                       WHEN reader_status.target_version <> COALESCE(target_set.target_version, 0) THEN 'Starting'
                       ELSE reader_status.state
                   END AS capture_state
            FROM copilot.conversations AS conversation
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
            WHERE conversation.account_id = @account_id
              AND conversation.conversation_type = 'Direct'
              AND (@query = '' OR contact.display_name ILIKE '%' || @query || '%')
            ORDER BY contact.display_name NULLS LAST, conversation.id
            LIMIT @limit;
            """;
        try
        {
            var conversations = new List<object>();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.AddWithValue("query", query);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                conversations.Add(new
                {
                    accountId,
                    conversationId = reader.GetGuid(0),
                    displayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    conversationType = reader.GetString(2),
                    lastMessageCursor = reader.IsDBNull(3) ? null : reader.GetString(3),
                    messageSetRevision = reader.GetInt64(4).ToString(CultureInfo.InvariantCulture),
                    targetVersion = reader.GetInt64(5),
                    isMonitored = reader.GetBoolean(6),
                    captureState = reader.GetString(7)
                });
            }

            return Results.Ok(new { accountId, conversations });
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static bool TryReadLimit(HttpContext context, out int limit)
    {
        limit = 2000;
        return !context.Request.Query.TryGetValue("limit", out var values)
            || values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit is >= 1 and <= 2000;
    }

    private static IResult DatabaseNotConfigured(HttpContext context) => Error(
        context, StatusCodes.Status503ServiceUnavailable,
        "DATABASE_NOT_CONFIGURED", "The formal message store is not configured.", retryable: true);

    private static IResult DatabaseUnavailable(HttpContext context) => Error(
        context, StatusCodes.Status503ServiceUnavailable,
        "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable = false) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);
}
