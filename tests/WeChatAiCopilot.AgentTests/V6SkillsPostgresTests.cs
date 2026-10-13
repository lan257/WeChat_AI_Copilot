using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Skills;
using WeChatAiCopilot.Api.Platform.CodexCli;
using WeChatAiCopilot.Api.Platform.Migrations;
using Xunit;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WeChatAiCopilot.Api.Modules.Operations;

namespace WeChatAiCopilot.AgentTests;

public sealed class V6SkillsPostgresFactAttribute : FactAttribute
{
    public V6SkillsPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES")))
            Skip = "Requires a disposable test PostgreSQL database; never uses the user's Formal database.";
    }
}

public sealed class V6SkillsPostgresTests
{
    [V6SkillsPostgresFact]
    public async Task StageAuditHasRealLifecycleMetadataAndNoCrossConversationRuns()
    {
        await using var db = TestDatabase();
        await MigrationRunner.ApplyPendingAsync(db);
        var scope = await CreateScope(db);
        var other = await CreateScope(db);
        var store = new SkillService(db);
        var flow = await store.CreateAsync(scope, new("Workflow", "固定阶段测试", "", "只在固定阶段检查授权证据。",
            ["message", "context", "reply", "review", "consult", "memory"], "AllAuthorizedAccounts"));
        var resolver = new SkillResolutionService(store);
        var resolution = await resolver.ResolveAsync(scope);
        foreach (var stage in SkillPolicy.Stages) await resolver.RecordStageUseAsync(scope, stage, resolution);
        var runs = await store.RunsAsync(scope, flow.SkillId);
        Assert.Equal(6, runs.Count);
        Assert.Equal(SkillPolicy.Stages.Order(), runs.Select(r => r.Stage).Order());
        Assert.All(runs, r => Assert.Equal(resolution.RevisionDigest, r.RevisionDigest));
        Assert.Empty(await store.RunsAsync(other, flow.SkillId));
        var personaRuns = await store.RunsAsync(scope, "natural");
        Assert.Single(personaRuns);
        Assert.Equal("reply", personaRuns[0].Stage);
        await Assert.ThrowsAsync<SkillException>(() => resolver.RecordStageUseAsync(scope, "shell", resolution));
        var json = JsonSerializer.Serialize(runs);
        Assert.DoesNotContain(flow.Instruction, json);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        await store.DeleteAsync(scope, flow.SkillId, flow.Revision);
    }

    [V6SkillsPostgresFact]
    public async Task ScopedDeviceCanEditItsConversationButCannotAffectGlobalOrAccountSkills()
    {
        await using var db = TestDatabase();
        await MigrationRunner.ApplyPendingAsync(db);
        var scope = await CreateScope(db);
        var other = await CreateScope(db);
        var store = new SkillService(db);
        var global = await store.CreateAsync(scope, new("Persona", "全局设备测试", "", "保持简单，不制造额外约定。", ["reply"], "AllAuthorizedAccounts"));
        var identity = new WebSessionIdentityV1(Guid.NewGuid(), false, [scope.AccountId], [scope.ConversationId], false, DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Authentication:WebBearerToken"] = "synthetic-token" });
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<IWebSessionValidatorV1>(new DeviceValidator(identity));
        await using var app = builder.Build();
        app.MapV6SkillEndpoints(builder.Configuration);

        var request = new SkillWriteRequest("Persona", "设备专用人设", "", "只作用当前联系人，避免多余承诺。", ["reply"], "Conversation", scope.AccountId, scope.ConversationId);
        var localCreate = await Invoke(app, scope, "POST", "/api/skills/", request);
        Assert.Equal(200, localCreate.Status);
        Assert.Equal(403, (await Invoke(app, scope, "POST", "/api/skills/", request with
        { ScopeType = "AllAuthorizedAccounts", AccountId = null, ConversationId = null })).Status);
        Assert.Equal(403, (await Invoke(app, scope, "POST", "/api/skills/", request with
        { ScopeType = "Account", ConversationId = null })).Status);
        Assert.Equal(403, (await Invoke(app, scope, "PUT", "/api/skills/{id}", Write(global), global.SkillId)).Status);
        Assert.Equal(403, (await Invoke(app, scope, "DELETE", "/api/skills/{id}", null, global.SkillId)).Status);
        Assert.Equal(403, (await Invoke(app, other, "GET", "/api/skills/", null)).Status);
        // Reading and selecting a global expression template in an authorized conversation remains possible.
        Assert.Equal(200, (await Invoke(app, scope, "GET", "/api/skills/", null)).Status);
        Assert.Equal(200, (await Invoke(app, scope, "PUT", "/api/skills/selection", new SkillSelectionRequest(global.SkillId))).Status);
        await store.DeleteAsync(scope, global.SkillId, global.Revision);
    }
    [V6SkillsPostgresFact]
    public async Task RealVersionsDraftApprovalScopeIsolationRestoreAndSelectionAreDurable()
    {
        var dsn = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES"));
        Assert.True(dsn.Database!.Contains("test", StringComparison.OrdinalIgnoreCase)
            || dsn.Database.Contains("temp", StringComparison.OrdinalIgnoreCase));
        await using var db = NpgsqlDataSource.Create(dsn.ConnectionString);
        await MigrationRunner.ApplyPendingAsync(db);
        var scope = await CreateScope(db);
        var other = await CreateScope(db);
        var service = new SkillService(db);
        var resolver = new SkillResolutionService(service);
        var builtin = await service.GetAsync(scope, "natural");
        Assert.Equal("自然", builtin.Name);
        Assert.Equal("SKILL_BUILTIN_IMMUTABLE", (await Assert.ThrowsAsync<SkillException>(() =>
            service.DeleteAsync(scope, "natural", 1))).Code);

        var executor = new DraftExecutor();
        var draft = await service.DraftAsync(scope, new("Persona", "温柔但不擅自答应约定", "Conversation",
            scope.AccountId, scope.ConversationId), executor);
        Assert.DoesNotContain(scope.AccountId.ToString(), executor.Prompt);
        Assert.DoesNotContain(scope.ConversationId.ToString(), executor.Prompt);
        var malformed = new InvalidDraftExecutor();
        Assert.Equal("SKILL_DRAFT_INVALID", (await Assert.ThrowsAsync<SkillException>(() => service.DraftAsync(scope,
            new("Persona", "温柔但不擅自答应约定", "Conversation", scope.AccountId, scope.ConversationId), malformed))).Code);
        Assert.Equal("Draft", draft.ApprovalStatus);
        Assert.False(draft.Enabled);
        Assert.Throws<SkillException>(() => SkillPolicy.Resolve([draft], scope, draft.SkillId));
        Assert.Equal("SKILL_APPROVAL_REQUIRED", (await Assert.ThrowsAsync<SkillException>(() =>
            service.UpdateAsync(scope, draft.SkillId, Write(draft) with { Enabled = true }))).Code);
        await Assert.ThrowsAsync<SkillException>(() => service.GetAsync(other, draft.SkillId));
        await Assert.ThrowsAsync<SkillException>(() => service.VersionsAsync(other, draft.SkillId));

        var approved = await service.ApproveAsync(scope, draft.SkillId, new(draft.Revision));
        Assert.True(approved.Enabled);
        Assert.Equal(2, approved.Revision);
        await service.SelectAsync(scope, approved.SkillId);
        var selected = await resolver.ResolveAsync(scope);
        Assert.Equal(approved.SkillId, selected.PersonaId);
        await Assert.ThrowsAsync<SkillException>(() => service.SelectAsync(other, approved.SkillId));
        var edited = await service.UpdateAsync(scope, approved.SkillId, Write(approved) with { Instruction = "保持耐心，消息中不添加虚构约定。" });
        Assert.NotEqual(selected.RevisionDigest, await resolver.GetScopeRevisionDigestAsync(scope));
        Assert.Equal("SKILL_REVISION_CONFLICT", (await Assert.ThrowsAsync<SkillException>(() =>
            service.UpdateAsync(scope, approved.SkillId, Write(approved)))).Code);
        var deleted = await service.DeleteAsync(scope, edited.SkillId, edited.Revision);
        Assert.Equal("natural", (await resolver.ResolveAsync(scope)).PersonaId);
        var restored = await service.RestoreAsync(scope, deleted.SkillId, new(approved.Revision, deleted.Revision));
        Assert.Equal(5, restored.Revision);
        Assert.False(restored.Deleted);
        Assert.Equal(approved.Instruction, restored.Instruction);
        Assert.Equal(approved.SkillId, (await resolver.ResolveAsync(scope)).PersonaId);
        Assert.Equal(5, (await service.VersionsAsync(scope, draft.SkillId)).Count);

        var accountWide = await service.CreateAsync(scope, Write(restored) with
        { ScopeType = "Account", ConversationId = null, ExpectedRevision = null, Origin = "Manual" });
        Assert.Contains(await service.ListAsync(scope), s => s.SkillId == accountWide.SkillId);
        Assert.DoesNotContain(await service.ListAsync(other), s => s.SkillId == accountWide.SkillId);
        Assert.Equal("SKILL_SCOPE_INVALID", (await Assert.ThrowsAsync<SkillException>(() => service.CreateAsync(scope,
            Write(restored) with { AccountId = other.AccountId, ConversationId = other.ConversationId }))).Code);

        // Changing scope deliberately shares only the current approved instruction, never private historical versions.
        var shared = await service.UpdateAsync(scope, restored.SkillId, Write(restored) with
        { ScopeType = "AllAuthorizedAccounts", AccountId = null, ConversationId = null,
          Instruction = "这是跨账号公开的通用表达规则。" });
        var sharedHistory = await service.VersionsAsync(other, shared.SkillId);
        Assert.Single(sharedHistory);
        Assert.Equal(shared.Instruction, sharedHistory[0].Instruction);
        Assert.Equal("SKILL_VERSION_NOT_FOUND", (await Assert.ThrowsAsync<SkillException>(() =>
            service.RestoreAsync(other, shared.SkillId, new(approved.Revision, shared.Revision)))).Code);
        await service.UpdateAsync(scope, shared.SkillId, Write(shared) with
        { ScopeType = "Conversation", AccountId = scope.AccountId, ConversationId = scope.ConversationId });
        await Assert.ThrowsAsync<SkillException>(() => service.GetAsync(other, shared.SkillId));
    }

    private static SkillWriteRequest Write(SkillDto s) => new(s.Type,s.Name,s.Description,s.Instruction,s.Stages,
        s.ScopeType,s.AccountId,s.ConversationId,s.Enabled,s.Origin,s.Revision);

    private static NpgsqlDataSource TestDatabase()
    {
        var dsn = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES"));
        Assert.True(dsn.Database!.Contains("test", StringComparison.OrdinalIgnoreCase)
            || dsn.Database.Contains("temp", StringComparison.OrdinalIgnoreCase));
        return NpgsqlDataSource.Create(dsn.ConnectionString);
    }

    private static async Task<(int Status, string Body)> Invoke(WebApplication app, AuthorizedContextScopeV1 scope,
        string method, string pattern, object? body, string? id = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = method;
        ctx.Request.Path = pattern;
        ctx.Request.QueryString = new QueryString($"?accountId={scope.AccountId}&conversationId={scope.ConversationId}&expectedRevision=1");
        ctx.Request.Headers.Cookie = WebSessionServiceV1.CookieName + "=synthetic";
        if (id is not null) ctx.Request.RouteValues["id"] = id;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.Body = new MemoryStream(bytes);
        }
        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }
    private sealed class DeviceValidator(WebSessionIdentityV1 identity) : IWebSessionValidatorV1
    {
        public Task<WebSessionIdentityV1?> ValidateAsync(HttpContext context, bool requireCsrf, CancellationToken cancellationToken = default)
        {
            context.Items[WebSessionServiceV1.IdentityItemKey] = identity;
            return Task.FromResult<WebSessionIdentityV1?>(identity);
        }
    }

    private static async Task<AuthorizedContextScopeV1> CreateScope(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("""
            WITH a AS (INSERT INTO copilot.chat_accounts(provider,external_account_id) VALUES('synthetic-test',gen_random_uuid()::text) RETURNING id),
            c AS (INSERT INTO copilot.contacts(account_id,external_contact_id) SELECT id,gen_random_uuid()::text FROM a RETURNING id,account_id)
            INSERT INTO copilot.conversations(account_id,contact_id,external_conversation_id)
            SELECT account_id,id,gen_random_uuid()::text FROM c RETURNING account_id,id
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetGuid(0), reader.GetGuid(1));
    }
    private sealed class DraftExecutor : IStructuredCodexCliExecutorV1
    {
        public string Prompt { get; private set; } = "";
        public Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(string prompt, string outputSchemaJson,
            TimeSpan timeout, CancellationToken cancellationToken = default, string? modelId = null)
        {
            Prompt = prompt;
            using var document = JsonDocument.Parse("""
                {"name":"克制的温柔","description":"不虚构约定","instruction":"保持温柔，不为本人作出承诺。","stages":["reply","consult"]}
                """);
            return Task.FromResult(new StructuredCodexCliExecutionResultV1(
                StructuredCodexCliExecutionStatusV1.Completed, document.RootElement.Clone()));
        }
    }
    private sealed class InvalidDraftExecutor : IStructuredCodexCliExecutorV1
    {
        public Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(string prompt, string outputSchemaJson,
            TimeSpan timeout, CancellationToken cancellationToken = default, string? modelId = null)
        {
            using var document = JsonDocument.Parse("""
                {"name":"invalid","description":"","instruction":"short","stages":["shell"]}
                """);
            return Task.FromResult(new StructuredCodexCliExecutionResultV1(
                StructuredCodexCliExecutionStatusV1.Completed, document.RootElement.Clone()));
        }
    }
}
