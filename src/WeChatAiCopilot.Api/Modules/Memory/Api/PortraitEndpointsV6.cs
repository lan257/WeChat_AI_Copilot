using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Memory.Api;

public static class PortraitEndpointsV6
{
    public sealed record EvidenceRequestV6(Guid[] MessageIds);
    public static IEndpointRouteBuilder MapPortraitEndpointsV6(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        var group=endpoints.MapGroup("/api").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        group.MapGet("/portraits/current",async(HttpContext context,PortraitServiceV6 service,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);return access.Error??Results.Ok(await service.GetAsync(access.Scope!,ct));});
        group.MapPost("/portraits/current/refresh",async(HttpContext context,AgentTaskQueueV6 queue,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;var id=await queue.EnqueueAsync(access.Scope!,new(){TaskType="Portrait"},context.Request.Headers["Idempotency-Key"].FirstOrDefault(),ct);return Results.Accepted($"/api/ai/tasks/{id}",await queue.GetAsync(access.Scope!,id,ct));});
        group.MapPost("/evidence/current",async(EvidenceRequestV6 request,HttpContext context,PortraitServiceV6 service,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;if(request.MessageIds is null||request.MessageIds.Length>100)return Results.BadRequest(new{error=new{code="EVIDENCE_LIMIT_EXCEEDED"}});return Results.Ok(await service.ReadEvidenceAsync(access.Scope!,request.MessageIds,ct));});
        group.MapGet("/expression/current",async(HttpContext context,NpgsqlDataSource db,PortraitServiceV6 service,CancellationToken ct)=>{var access=await ResolveAccountAsync(context,config,db,ct);return access.Error??Results.Ok(await service.GetExpressionAsync(access.AccountId,ct));});
        group.MapPut("/expression/current",async(ExpressionV6 request,HttpContext context,NpgsqlDataSource db,PortraitServiceV6 service,CancellationToken ct)=>{if(context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 {AllImported:false})return Results.Json(new{error=new{code="DEVICE_SCOPE_FORBIDDEN"}},statusCode:403);var access=await ResolveAccountAsync(context,config,db,ct);if(access.Error is not null)return access.Error;try{return Results.Ok(await service.SaveExpressionAsync(access.AccountId,request with{Voice=request.Voice??"",Boundary=request.Boundary??"",Notes=request.Notes??""},ct));}catch(PortraitFailureV6 ex){return Results.Json(new{error=new{code=ex.Code}},statusCode:ex.Code=="EXPRESSION_VERSION_CONFLICT"?409:400);}});
        return endpoints;
    }
    private static async Task<(Guid AccountId,IResult? Error)> ResolveAccountAsync(HttpContext context,IConfiguration config,NpgsqlDataSource db,CancellationToken ct)
    {
        Guid id;
        if(context.Request.Query.ContainsKey("accountId")){
            if(context.Request.Query["accountId"].Count!=1||!Guid.TryParse(context.Request.Query["accountId"],out id))return(default,Results.BadRequest());
        }else{
            await using var q=db.CreateCommand("SELECT id FROM copilot.chat_accounts WHERE provider=@p AND external_account_id=@a");q.Parameters.AddWithValue("p",config["Web:Provider"]??"local-wechat");q.Parameters.AddWithValue("a",config["Web:AllowedAccountExternalId"]??"");var value=await q.ExecuteScalarAsync(ct);if(value is not Guid guid)return(default,Results.NotFound());id=guid;
        }
        if(context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 identity&&!identity.AllowsAccount(id))return(default,Results.NotFound());
        await using var check=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM copilot.chat_accounts WHERE id=@a)");check.Parameters.AddWithValue("a",id);return await check.ExecuteScalarAsync(ct) is true?(id,null):(default,Results.NotFound());
    }
}
