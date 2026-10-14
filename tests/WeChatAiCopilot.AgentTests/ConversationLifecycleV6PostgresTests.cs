using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Skills;
using WeChatAiCopilot.Api.Platform.Migrations;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

/// <summary>
/// Disposable synthetic PostgreSQL only. The pre-generation watcher keeps its cursor in the database,
/// so a process that was not running still schedules the messages that arrived during the gap.
/// </summary>
public sealed class ConversationLifecycleV6PostgresTests
{
    [V6SkillsPostgresFact]
    public async Task WatcherSchedulesRevisionsThatChangedWhileTheProcessWasDown()
    {
        var dsn = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES")!);
        Assert.Equal(55439, dsn.Port);
        Assert.Contains("test", dsn.Database!, StringComparison.OrdinalIgnoreCase);
        await using var admin = NpgsqlDataSource.Create(dsn.ConnectionString);
        var name = "v6_prep_test_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"""CREATE DATABASE "{name}" WITH TEMPLATE = template0""")) await create.ExecuteNonQueryAsync();
        dsn.Database = name;
        await using var db = NpgsqlDataSource.Create(dsn.ConnectionString);
        try
        {
            await MigrationRunner.ApplyPendingAsync(db);
            var scope = await CreateScopeAsync(db);
            await SetAutoPrepAsync(db, true);

            // First sighting only records the cursor: messages stored before monitoring were never pending,
            // so a fresh watch must not queue one model run per watched contact.
            await PollAsync(db);
            Assert.Equal(0, await PregenTaskCountAsync(db, scope));
            Assert.Equal(await RevisionAsync(db, scope), await CursorRevisionAsync(db, scope));
            Assert.False(await NeedsPrepAsync(db, scope));

            var pending = await RevisionAsync(db, scope) + 1;
            await SetRevisionAsync(db, scope, pending);

            // A brand new instance stands in for a restarted process: it has no memory of the previous
            // revision, so only the persisted cursor can tell that this revision still needs preparation.
            await PollAsync(db);
            Assert.Equal(1, await PregenTaskCountAsync(db, scope));
            Assert.False(await NeedsPrepAsync(db, scope));

            // The same revision must not spend model work twice.
            await PollAsync(db);
            Assert.Equal(1, await PregenTaskCountAsync(db, scope));
            // No Skill is installed in this fixture, so the message stage must not record phantom usage.
            Assert.Equal(0, await StageRunCountAsync(db, scope, "message"));
        }
        finally
        {
            await db.DisposeAsync();
            Assert.Matches("^v6_prep_test_[a-f0-9]{32}$", name);
            await using var drop = admin.CreateCommand($"""DROP DATABASE "{name}" WITH (FORCE)""");
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    [V6SkillsPostgresFact]
    public async Task TurningAutoPrepOffReleasesPendingFlagsWithoutQueueingWork()
    {
        var dsn = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES")!);
        Assert.Equal(55439, dsn.Port);
        Assert.Contains("test", dsn.Database!, StringComparison.OrdinalIgnoreCase);
        await using var admin = NpgsqlDataSource.Create(dsn.ConnectionString);
        var name = "v6_prep_test_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"""CREATE DATABASE "{name}" WITH TEMPLATE = template0""")) await create.ExecuteNonQueryAsync();
        dsn.Database = name;
        await using var db = NpgsqlDataSource.Create(dsn.ConnectionString);
        try
        {
            await MigrationRunner.ApplyPendingAsync(db);
            var scope = await CreateScopeAsync(db);
            await SetAutoPrepAsync(db, true);
            await PollAsync(db);
            await SetRevisionAsync(db, scope, await RevisionAsync(db, scope) + 1);

            // Disabled between polls: the pending flag is dropped instead of being carried into a burst
            // of model runs the moment the switch is turned back on.
            await SetAutoPrepAsync(db, false);
            await PollAsync(db);
            Assert.Equal(0, await PregenTaskCountAsync(db, scope));
            Assert.False(await NeedsPrepAsync(db, scope));
        }
        finally
        {
            await db.DisposeAsync();
            Assert.Matches("^v6_prep_test_[a-f0-9]{32}$", name);
            await using var drop = admin.CreateCommand($"""DROP DATABASE "{name}" WITH (FORCE)""");
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private static async Task PollAsync(NpgsqlDataSource db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddV6SkillServices();
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var lifecycle = new ConversationLifecycleV6(db, scopeFactory, new AgentTaskQueueV6(db, scopeFactory));
        var poll = typeof(ConversationLifecycleV6)
            .GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)poll.Invoke(lifecycle, [CancellationToken.None])!;
    }

    private static async Task<AuthorizedContextScopeV1> CreateScopeAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("""
            WITH a AS(INSERT INTO copilot.chat_accounts(provider,external_account_id) VALUES('local-wechat','synthetic-'||gen_random_uuid()::text) RETURNING id),
            c AS(INSERT INTO copilot.contacts(account_id,external_contact_id) SELECT id,'synthetic-contact-'||gen_random_uuid()::text FROM a RETURNING id,account_id),
            v AS(INSERT INTO copilot.conversations(account_id,contact_id,external_conversation_id,message_set_revision) SELECT account_id,id,'synthetic-direct',7 FROM c RETURNING account_id,id,external_conversation_id),
            t AS(INSERT INTO copilot.monitored_conversation_targets(account_id,conversation_id) SELECT account_id,id FROM v),
            s AS(INSERT INTO copilot.sender_calibrations(account_id,conversation_id,identity_namespace,account_self_raw_sender_id,contact_raw_sender_id,calibration_version,perspective_state,evidence,confirmed_at) SELECT account_id,id,'synthetic-prep','1','20',1,'Confirmed','UserConfirmed',now() FROM v)
            SELECT account_id,id FROM v;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new AuthorizedContextScopeV1(reader.GetGuid(0), reader.GetGuid(1));
    }

    private static async Task SetAutoPrepAsync(NpgsqlDataSource db, bool autoPrep)
    {
        await using var command = db.CreateCommand("""
            INSERT INTO copilot.application_preferences(preference_key,preference_json)
            VALUES('user',jsonb_build_object('autoPrep',@prep,'notifications',false,'quickRoute','codex','prioritizeSuggestions',false))
            ON CONFLICT(preference_key) DO UPDATE SET preference_json=EXCLUDED.preference_json,updated_at=now();
            """);
        command.Parameters.AddWithValue("prep", autoPrep);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> RevisionAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope)
    {
        await using var command = db.CreateCommand("SELECT message_set_revision FROM copilot.conversations WHERE account_id=@a AND id=@c;");
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetRevisionAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope, long revision)
    {
        await using var command = db.CreateCommand("UPDATE copilot.conversations SET message_set_revision=@r WHERE account_id=@a AND id=@c;");
        command.Parameters.AddWithValue("r", revision);
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> PregenTaskCountAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope)
    {
        await using var command = db.CreateCommand("""
            SELECT count(*) FROM copilot.agent_tasks WHERE account_id=@a AND conversation_id=@c AND task_type='ReplyCandidates' AND idempotency_key LIKE 'pregen:%';
            """);
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CursorRevisionAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope)
    {
        await using var command = db.CreateCommand("SELECT last_seen_revision FROM copilot.conversation_prep_state WHERE account_id=@a AND conversation_id=@c;");
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> NeedsPrepAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope)
    {
        await using var command = db.CreateCommand("SELECT needs_prep FROM copilot.conversation_prep_state WHERE account_id=@a AND conversation_id=@c;");
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> StageRunCountAsync(NpgsqlDataSource db, AuthorizedContextScopeV1 scope, string stage)
    {
        await using var command = db.CreateCommand("SELECT count(*) FROM copilot.skill_stage_runs WHERE account_id=@a AND conversation_id=@c AND stage=@stage;");
        command.Parameters.AddWithValue("a", scope.AccountId);
        command.Parameters.AddWithValue("c", scope.ConversationId);
        command.Parameters.AddWithValue("stage", stage);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
