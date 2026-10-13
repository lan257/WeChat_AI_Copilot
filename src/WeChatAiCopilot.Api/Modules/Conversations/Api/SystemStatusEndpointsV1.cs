using Npgsql;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class SystemStatusEndpointsV1
{
    public static IEndpointRouteBuilder MapSystemStatusEndpointsV1(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        endpoints.MapGroup("/api")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"))
            .MapGet("/system/status", GetSystemStatusAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSystemStatusAsync(
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var databaseState = "NotConfigured";
        var readerState = "NotAvailable";
        var agentRunState = "NotAvailable";
        var readerAccounts = new List<object>();
        object? latestAgentRun = null;

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is not null)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using (var healthCommand = new NpgsqlCommand("SELECT 1;", connection))
                {
                    await healthCommand.ExecuteScalarAsync(cancellationToken);
                }

                databaseState = "Ready";
                readerState = await ReadReaderAccountsAsync(connection, readerAccounts, cancellationToken)
                    ? "Available"
                    : "Unavailable";
                (agentRunState, latestAgentRun) = await ReadLatestAgentRunAsync(connection, cancellationToken);
            }
            catch (NpgsqlException)
            {
                databaseState = "Unavailable";
                readerState = "Unavailable";
                agentRunState = "Unavailable";
            }
        }

        var configuredRuntime = configuration["Agent:Runtime"]?.Trim() switch
        {
            "CodexCli" => "CodexCli",
            "Fake" => "Fake",
            _ => "Disabled"
        };
        var memoryConsolidationRuntime = string.Equals(
            configuration["Memory:ConsolidationRuntime"],
            "CodexCli",
            StringComparison.OrdinalIgnoreCase)
            ? "CodexCli"
            : "Disabled";

        return Results.Ok(new
        {
            databaseState,
            readerState,
            readerAccounts,
            agentRunState,
            latestAgentRun,
            configuredRuntime,
            quickModelConfigured = !string.IsNullOrWhiteSpace(configuration["Agent:ExecutionProfiles:Quick:ModelId"]),
            memoryConsolidationRuntime,
            historyJobsState = "NotImplemented"
        });
    }

    private static async Task<bool> ReadReaderAccountsAsync(
        NpgsqlConnection connection,
        List<object> accounts,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT account.display_name,
                   account.provider,
                   CASE
                       WHEN status.heartbeat_at < now() - interval '20 seconds' THEN 'Offline'
                       ELSE status.state
                   END AS state,
                   status.heartbeat_at,
                   status.listening_target_count,
                   status.last_error_code
            FROM copilot.chat_accounts AS account
            JOIN copilot.reader_status AS status ON status.account_id = account.id
            ORDER BY account.created_at, account.id;
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new
                {
                    displayName = reader.IsDBNull(0) ? null : reader.GetString(0),
                    provider = reader.GetString(1),
                    state = reader.GetString(2),
                    lastHeartbeatAtUtc = reader.GetFieldValue<DateTimeOffset>(3),
                    listeningTargetCount = reader.GetInt32(4),
                    lastErrorCode = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }

            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static async Task<(string State, object? LatestRun)> ReadLatestAgentRunAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT task_type, runtime_name, status, started_at, ended_at, error_code
            FROM copilot.agent_runs
            ORDER BY started_at DESC, run_id DESC
            LIMIT 1;
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return ("Available", null);

            return ("Available", new
            {
                taskType = reader.GetString(0),
                runtimeName = reader.GetString(1),
                status = reader.GetString(2),
                startedAtUtc = reader.GetFieldValue<DateTimeOffset>(3),
                endedAtUtc = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4),
                errorCode = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }
        catch (NpgsqlException)
        {
            return ("Unavailable", null);
        }
    }
}
