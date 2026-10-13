using Npgsql;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

internal static class V6ScopeAccess
{
    public static async Task<(AuthorizedContextScopeV1? Scope,IResult? Error)> ResolveAsync(HttpContext context,IConfiguration config,IServiceProvider services,CancellationToken ct)
    {
        var db=services.GetService<NpgsqlDataSource>();
        if(db is null) return (null,Results.Json(new {error=new {code="DATABASE_NOT_CONFIGURED",message="数据库尚未准备。"}},statusCode:503));
        var target=await WebConversationScopeResolverV1.ResolveAsync(context,config,db,ct);
        if(target.Error is not null) return (null,target.Error);
        var scope=await services.GetRequiredService<IAgentRepositoryV1>().ResolveAuthorizedScopeAsync(new AgentWebConversationTargetV1(target.Target!.Provider,target.Target.AccountExternalId,target.Target.ConversationExternalId),ct);
        return scope is null ? (null,Results.NotFound(new {error=new {code="CONVERSATION_NOT_FOUND"}})) : (scope,null);
    }
}
