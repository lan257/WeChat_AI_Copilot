using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Operations;

public static class SessionEndpointsV1
{
    private static readonly SemaphoreSlim BootstrapGate = new(1,1);
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        endpoints.MapPost("/api/session/bootstrap", async (HttpContext context, BootstrapRequest request, WebSessionServiceV1 sessions,CancellationToken ct) => {
            if (!WebSessionServiceV1.IsLoopback(context) || !WebSessionServiceV1.SameOrigin(context)) return Results.StatusCode(403);
            if (request.Grant is null || request.Grant.Length != 64) return Results.Unauthorized();
            var path = config["Web:BootstrapGrantPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WeChatAiCopilot","Formal","web-bootstrap.json");
            await BootstrapGate.WaitAsync(ct);
            try {
                if (!File.Exists(path)) return Results.Unauthorized();
                BootstrapGrant? grant;
                try { grant=JsonSerializer.Deserialize<BootstrapGrant>(await File.ReadAllTextAsync(path,ct),new JsonSerializerOptions{PropertyNameCaseInsensitive=true}); }
                catch (JsonException) { return Results.Unauthorized(); }
                if (grant is null || grant.ExpiresAtUtc < DateTimeOffset.UtcNow ||
                    !string.Equals(grant.TokenHash,WebSessionServiceV1.Hash(request.Grant),StringComparison.Ordinal)) return Results.Unauthorized();
                File.Delete(path);
                return Results.Ok(await sessions.CreateAsync(context,"本机浏览器",true,[],[],ct));
            } finally { BootstrapGate.Release(); }
        });
        endpoints.MapGet("/api/session",async (HttpContext context,IWebSessionValidatorV1 validator,CancellationToken ct) => {
            var identity=await validator.ValidateAsync(context,false,ct);
            context.Response.Headers.CacheControl="no-store";
            return identity is null ? Results.Unauthorized() : Results.Ok(new {authenticated=true,
                csrfToken=WebSessionServiceV1.Csrf(context.Request.Cookies[WebSessionServiceV1.CookieName]!),
                identity.ExpiresAtUtc,identity.AllImported,identity.SessionId,identity.AuthorizedAccountIds,identity.AuthorizedConversationIds});
        });
        var web=endpoints.MapGroup("/api").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        web.MapDelete("/session",async(HttpContext context,IWebSessionValidatorV1 validator,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=await validator.ValidateAsync(context,true,ct);
            if(identity is null)return Results.Unauthorized();
            await using var command=db.CreateCommand("UPDATE copilot.web_sessions SET revoked_at=now() WHERE session_id=@id;");
            command.Parameters.AddWithValue("id",identity.SessionId);await command.ExecuteNonQueryAsync(ct);
            context.Response.Cookies.Delete(WebSessionServiceV1.CookieName,new CookieOptions{Path="/"});return Results.NoContent();
        });
        web.MapGet("/devices",async(HttpContext context,IWebSessionValidatorV1 validator,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=await validator.ValidateAsync(context,false,ct);if(identity?.IsLocal != true)return Results.StatusCode(403);
            var rows=new List<object>();await using var cmd=db.CreateCommand("SELECT session_id,device_name,created_at,expires_at,revoked_at FROM copilot.web_sessions WHERE NOT is_local ORDER BY created_at DESC LIMIT 50;");
            await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new{sessionId=r.GetGuid(0),deviceName=r.GetString(1),createdAtUtc=r.GetFieldValue<DateTimeOffset>(2),expiresAtUtc=r.GetFieldValue<DateTimeOffset>(3),revokedAtUtc=r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4)});
            return Results.Ok(new{items=rows});
        });
        web.MapGet("/devices/certificate",async(HttpContext context,IWebSessionValidatorV1 validator,CancellationToken ct)=>{
            var identity=await validator.ValidateAsync(context,false,ct);if(identity?.IsLocal != true)return Results.StatusCode(403);
            var publicPath=config["Web:PublicCertificatePath"];
            if(!MobileConfigured(config)||string.IsNullOrWhiteSpace(publicPath)||Path.GetExtension(publicPath)!=".cer"||!File.Exists(publicPath))return Results.NotFound();
            return Results.File(await File.ReadAllBytesAsync(publicPath,ct),"application/pkix-cert","copilot-mobile.cer");
        });
        web.MapDelete("/devices/{id:guid}",async(Guid id,HttpContext context,IWebSessionValidatorV1 validator,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=await validator.ValidateAsync(context,true,ct);if(identity?.IsLocal != true)return Results.StatusCode(403);
            await using var cmd=db.CreateCommand("UPDATE copilot.web_sessions SET revoked_at=now() WHERE session_id=@id AND NOT is_local;");cmd.Parameters.AddWithValue("id",id);
            return await cmd.ExecuteNonQueryAsync(ct)>0?Results.NoContent():Results.NotFound();
        });
        web.MapPost("/devices/pairings",async(PairingRequest request,HttpContext context,IWebSessionValidatorV1 validator,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=await validator.ValidateAsync(context,true,ct);if(identity?.IsLocal != true)return Results.StatusCode(403);
            if(!MobileConfigured(config))return Results.Conflict(new{code="HTTPS_NOT_ENABLED",message="手机安全访问尚未启用；需要可信 HTTPS 地址和证书。"});
            if(string.IsNullOrWhiteSpace(request.DeviceName)||request.DeviceName.Length>80||request.AccountIds is null||request.ConversationIds is null||request.AccountIds.Length is <1 or >20||request.ConversationIds.Length is <1 or >20)return Results.BadRequest();
            await using(var check=db.CreateCommand("SELECT count(*) FROM copilot.conversations WHERE id=ANY(@conversations) AND account_id=ANY(@accounts) AND conversation_type='Direct';")){
                check.Parameters.AddWithValue("accounts",request.AccountIds);check.Parameters.AddWithValue("conversations",request.ConversationIds);
                if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))!=request.ConversationIds.Distinct().Count())return Results.BadRequest();}
            var grant=WebSessionServiceV1.NewToken();var expires=DateTimeOffset.UtcNow.AddMinutes(5);
            await using var cmd=db.CreateCommand("INSERT INTO copilot.device_pairing_grants(grant_hash,device_name,authorized_account_ids,authorized_conversation_ids,expires_at) VALUES(@hash,@name,@accounts,@conversations,@expires);");
            cmd.Parameters.AddWithValue("hash",WebSessionServiceV1.Hash(grant));cmd.Parameters.AddWithValue("name",request.DeviceName.Trim());cmd.Parameters.AddWithValue("accounts",request.AccountIds);cmd.Parameters.AddWithValue("conversations",request.ConversationIds);cmd.Parameters.AddWithValue("expires",expires);await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new{pairingUrl=config["Web:MobileOrigin"]+"/#pair="+grant,expiresAtUtc=expires});
        });
        endpoints.MapPost("/api/session/pair",async(BootstrapRequest request,HttpContext context,WebSessionServiceV1 sessions,NpgsqlDataSource db,CancellationToken ct)=>{
            if(!MobileConfigured(config)||!context.Request.IsHttps||!WebSessionServiceV1.SameOrigin(context)||!string.Equals(context.Request.Scheme+"://"+context.Request.Host,config["Web:MobileOrigin"],StringComparison.OrdinalIgnoreCase))return Results.StatusCode(403);
            if(request.Grant is null||request.Grant.Length!=64)return Results.Unauthorized();
            await using var cmd=db.CreateCommand("UPDATE copilot.device_pairing_grants SET consumed_at=now() WHERE grant_hash=@hash AND consumed_at IS NULL AND expires_at>now() RETURNING device_name,authorized_account_ids,authorized_conversation_ids;");cmd.Parameters.AddWithValue("hash",WebSessionServiceV1.Hash(request.Grant));
            string name;Guid[] accounts;Guid[] conversations;await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return Results.Unauthorized();name=r.GetString(0);accounts=r.GetFieldValue<Guid[]>(1);conversations=r.GetFieldValue<Guid[]>(2);}
            return Results.Ok(await sessions.CreateAsync(context,name,false,accounts,conversations,ct));
        });
        return endpoints;
    }
    public static bool MobileConfigured(IConfiguration config)=>bool.TryParse(config["Web:TrustedHttpsEnabled"],out var enabled)&&enabled&&Uri.TryCreate(config["Web:MobileOrigin"],UriKind.Absolute,out var uri)&&uri.Scheme=="https";
    public sealed record BootstrapRequest(string? Grant);
    private sealed record BootstrapGrant(string TokenHash,DateTimeOffset ExpiresAtUtc);
    public sealed record PairingRequest(string DeviceName,Guid[] AccountIds,Guid[] ConversationIds);
}
