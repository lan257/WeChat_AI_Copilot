using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace WeChatAiCopilot.Api.Platform.Migrations;

public static class MigrationRunner
{
    private const string ResourcePrefix = "WeChatAiCopilot.Api.Migrations.";
    private const string AdvisoryLockName = "wechat-ai-copilot.formal-schema-migrations";

    public static async Task ApplyPendingAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);

        try
        {
            await EnsureMigrationLedgerAsync(connection, cancellationToken);
            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);

            foreach (var migration in ReadEmbeddedMigrations())
            {
                var checksum = ComputeChecksum(migration.Sql);
                if (applied.TryGetValue(migration.Version, out var appliedChecksum))
                {
                    if (!string.Equals(checksum, appliedChecksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Applied migration '{migration.Version}' has changed. Add a new migration instead.");
                    }

                    continue;
                }

                await ApplyMigrationAsync(connection, migration, checksum, cancellationToken);
            }
        }
        finally
        {
            // ApplicationStopping is often already cancelled here. A session-level
            // advisory lock is not released by transaction rollback, so still make
            // the explicit unlock attempt before returning the connection to its pool.
            await ReleaseLockAsync(connection, CancellationToken.None);
        }
    }

    private static async Task EnsureMigrationLedgerAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS copilot;
            CREATE TABLE IF NOT EXISTS copilot.schema_migrations (
                version text PRIMARY KEY,
                checksum_sha256 text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT version, checksum_sha256 FROM copilot.schema_migrations ORDER BY version;";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0), reader.GetString(1));
        }

        return applied;
    }

    private static async Task ApplyMigrationAsync(
        NpgsqlConnection connection,
        EmbeddedMigration migration,
        string checksum,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationCommand = new NpgsqlCommand(migration.Sql, connection, transaction))
        {
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string recordSql = """
            INSERT INTO copilot.schema_migrations(version, checksum_sha256)
            VALUES (@version, @checksum);
            """;
        await using (var recordCommand = new NpgsqlCommand(recordSql, connection, transaction))
        {
            recordCommand.Parameters.AddWithValue("version", migration.Version);
            recordCommand.Parameters.AddWithValue("checksum", checksum);
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal static IReadOnlyList<EmbeddedMigration> ReadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var migrations = new List<EmbeddedMigration>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Migration resource '{resourceName}' was not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            var version = resourceName[ResourcePrefix.Length..^".sql".Length];
            migrations.Add(new EmbeddedMigration(version, sql));
        }

        return migrations;
    }

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext(@lock_name));",
            connection);
        command.Parameters.AddWithValue("lock_name", AdvisoryLockName);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ReleaseLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtext(@lock_name));",
            connection);
        command.Parameters.AddWithValue("lock_name", AdvisoryLockName);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    internal sealed record EmbeddedMigration(string Version, string Sql);
}
