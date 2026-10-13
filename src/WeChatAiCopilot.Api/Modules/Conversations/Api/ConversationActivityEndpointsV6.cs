using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class ConversationActivityEndpointsV6
{
    public sealed record ReadMarkerRequest(string MessageSetRevision);
    public static string ViewerKey(HttpContext context)=>context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 {IsLocal:false} identity?identity.SessionId.ToString("N"):"local";
    public static IEndpointRouteBuilder MapConversationActivityV6(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        endpoints.MapPost("/api/conversations/current/read",async(ReadMarkerRequest request,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;
            if(!long.TryParse(request.MessageSetRevision,out var revision)||revision<0)return Results.BadRequest(new{error=new{code="INVALID_MESSAGE_REVISION"}});
            await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
            await using var q=new NpgsqlCommand("SELECT message_set_revision FROM copilot.conversations WHERE account_id=@a AND id=@c FOR UPDATE",c,tx);q.Parameters.AddWithValue("a",access.Scope!.AccountId);q.Parameters.AddWithValue("c",access.Scope.ConversationId);
            var current=await q.ExecuteScalarAsync(ct);if(current is not long live)return Results.NotFound();if(live!=revision)return Results.Conflict(new{error=new{code="CONTEXT_REVISION_CHANGED"}});
            await using var mark=new NpgsqlCommand("INSERT INTO copilot.conversation_view_state(viewer_key,account_id,conversation_id,last_read_revision,last_seen_at) VALUES(@v,@a,@c,@r,now()) ON CONFLICT(viewer_key,account_id,conversation_id) DO UPDATE SET last_read_revision=@r,last_seen_at=now()",c,tx);
            mark.Parameters.AddWithValue("v",ViewerKey(context));mark.Parameters.AddWithValue("a",access.Scope.AccountId);mark.Parameters.AddWithValue("c",access.Scope.ConversationId);mark.Parameters.AddWithValue("r",revision);await mark.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return Results.Ok(new{read=true,messageSetRevision=revision.ToString()});
        }).AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        return endpoints;
    }
}
