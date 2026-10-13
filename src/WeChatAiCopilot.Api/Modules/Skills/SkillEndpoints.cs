using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Authentication;
using WeChatAiCopilot.Api.Platform.CodexCli;

namespace WeChatAiCopilot.Api.Modules.Skills;

public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapV6SkillEndpoints(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var group = endpoints.MapGroup("/api/skills")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        group.MapGet("", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) => Results.Ok(await store.ListAsync(scope, ctx.Request.Query["includeDeleted"] == "true", ct))));
        group.MapPost("", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                var body = await Body<SkillWriteRequest>(ctx, ct);
                GuardMutation(ctx, body.ScopeType);
                return Results.Ok(await store.CreateAsync(scope, body, ct));
            }));
        group.MapGet("/selection", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                var selection = await new SkillResolutionService(store).ResolveAsync(scope, null, ct);
                return Results.Ok(new SkillSelection(selection.PersonaId, selection.RevisionDigest));
            }));
        group.MapPut("/selection", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                await store.SelectAsync(scope, (await Body<SkillSelectionRequest>(ctx, ct)).PersonaSkillId, ct);
                var selection = await new SkillResolutionService(store).ResolveAsync(scope, null, ct);
                return Results.Ok(new SkillSelection(selection.PersonaId, selection.RevisionDigest));
            }));
        group.MapPost("/drafts", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                var executor = services.GetService<IStructuredCodexCliExecutorV1>()
                    ?? throw new SkillException("SKILL_AI_NOT_CONFIGURED", 503);
                var body = await Body<SkillDraftRequest>(ctx, ct);
                GuardMutation(ctx, body.ScopeType);
                return Results.Ok(await store.DraftAsync(scope, body, executor, ct));
            }));
        group.MapPost("/preview", (HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (_, scope, ct) =>
            {
                var valid = SkillPolicy.Validate(await Body<SkillWriteRequest>(ctx, ct), scope);
                return Results.Ok(new
                {
                    valid.Stages, valid.ScopeType, valid.Instruction,
                    impact = valid.Type == "Persona" ? "选为当前会话人设后，在所选阶段应用表达规则；一次只使用一个人设。" : "启用并审批后，在指定范围的所选固定阶段与其他流程规则组合。",
                    boundaries = new[] { "这是配置影响预览，没有执行模型或生成模拟回复。", "规则是自然语言数据，不能授予Shell、文件、网络或微信发送权限。", "作用域、阶段顺序、检索和时间预算由服务器控制。", "规则冲突时以系统安全边界及当前所选人设为先；流程规则按稳定技能ID顺序组合。" }
                });
            }));
        group.MapGet("/{id}", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) => Results.Ok(await store.GetAsync(scope, id, ct))));
        group.MapPut("/{id}", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                var old = await store.GetAsync(scope, id, ct);
                var body = await Body<SkillWriteRequest>(ctx, ct);
                GuardMutation(ctx, old.ScopeType);
                GuardMutation(ctx, body.ScopeType);
                return Results.Ok(await store.UpdateAsync(scope, id, body, ct));
            }));
        group.MapDelete("/{id}", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                GuardMutation(ctx, (await store.GetAsync(scope, id, ct)).ScopeType);
                if (!long.TryParse(ctx.Request.Query["expectedRevision"], out var expected)) throw new SkillException("SKILL_EXPECTED_REVISION_REQUIRED");
                await store.DeleteAsync(scope, id, expected, ct);
                return Results.NoContent();
            }));
        group.MapGet("/{id}/versions", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) => Results.Ok(await store.VersionsAsync(scope, id, ct))));
        group.MapGet("/{id}/runs", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) => Results.Ok(await store.RunsAsync(scope, id, ct))));
        group.MapPost("/{id}/restore", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                GuardMutation(ctx, (await store.GetAsync(scope, id, ct)).ScopeType);
                var body = await Body<SkillRestoreRequest>(ctx, ct);
                var version = (await store.VersionsAsync(scope, id, ct)).FirstOrDefault(v => v.Revision == body.Revision)
                    ?? throw new SkillException("SKILL_VERSION_NOT_FOUND", 404);
                GuardMutation(ctx, version.ScopeType);
                return Results.Ok(await store.RestoreAsync(scope, id, body, ct));
            }));
        group.MapPost("/{id}/approve", (string id, HttpContext ctx, IServiceProvider services) => Invoke(ctx, configuration, services,
            async (store, scope, ct) =>
            {
                GuardMutation(ctx, (await store.GetAsync(scope, id, ct)).ScopeType);
                return Results.Ok(await store.ApproveAsync(scope, id, await Body<SkillApproveRequest>(ctx, ct), ct));
            }));
        return endpoints;
    }

    private static async Task<IResult> Invoke(HttpContext context, IConfiguration configuration, IServiceProvider services,
        Func<SkillService, AuthorizedContextScopeV1, CancellationToken, Task<IResult>> action)
    {
        try
        {
            var ct = context.RequestAborted;
            var database = services.GetService<NpgsqlDataSource>() ?? throw new SkillException("DATABASE_NOT_CONFIGURED", 503);
            var resolution = await WebConversationScopeResolverV1.ResolveAsync(context, configuration, database, ct);
            if (resolution.Error is not null) return resolution.Error;
            var target = resolution.Target!;
            var scope = await new PostgresMemoryServiceV1(database).ResolveConfiguredConversationAsync(
                target.Provider, target.AccountExternalId, target.ConversationExternalId, ct)
                ?? throw new SkillException("SKILL_SCOPE_INVALID", 403);
            if (context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 identity
                && !identity.Allows(scope.AccountId, scope.ConversationId))
                throw new SkillException("SKILL_SCOPE_INVALID", 403);
            return await action(new SkillService(database), new AuthorizedContextScopeV1(scope.AccountId, scope.ConversationId), ct);
        }
        catch (SkillException e) { return Error(context, e.Status, e.Code); }
        catch (NpgsqlException) { return Error(context, 503, "DATABASE_UNAVAILABLE"); }
        catch (JsonException) { return Error(context, 400, "INVALID_JSON"); }
        catch (InvalidOperationException) { return Error(context, 415, "UNSUPPORTED_MEDIA_TYPE"); }
    }
    private static async Task<T> Body<T>(HttpContext context, CancellationToken ct) where T : class =>
        await context.Request.ReadFromJsonAsync<T>(ct) ?? throw new SkillException("INVALID_JSON");
    private static void GuardMutation(HttpContext context, string scopeType)
    {
        if (context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 { AllImported: false }
            && !string.Equals(scopeType, "Conversation", StringComparison.OrdinalIgnoreCase))
            throw new SkillException("SKILL_DEVICE_SCOPE_FORBIDDEN", 403);
    }
    private static IResult Error(HttpContext context, int status, string code) => Results.Json(
        new ApiErrorV1(new ApiErrorDetailV1(code, "The Skill operation could not be completed. " + code,
            status >= 500, context.TraceIdentifier)), statusCode: status);
}
