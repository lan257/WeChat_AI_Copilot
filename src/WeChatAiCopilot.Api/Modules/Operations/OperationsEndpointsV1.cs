using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Platform.Authentication;
using WeChatAiCopilot.Api.Platform.CodexCli;

namespace WeChatAiCopilot.Api.Modules.Operations;

public static class OperationsEndpointsV1
{
    public static IServiceCollection AddOperationsModule(this IServiceCollection services)
    {
        services.AddSingleton<WebSessionServiceV1>();
        services.AddSingleton<IWebSessionValidatorV1>(provider=>provider.GetRequiredService<WebSessionServiceV1>());
        return services;
    }
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        var group=endpoints.MapGroup("/api").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        group.MapGet("/operations/status",GetStatusAsync);
        group.MapPost("/operations/diagnostics",DiagnoseAsync);
        group.MapGet("/preferences",async(NpgsqlDataSource db,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("SELECT preference_json::text FROM copilot.application_preferences WHERE preference_key='user';");
            var json=await cmd.ExecuteScalarAsync(ct) as string;
            return Results.Ok(json is null?new PreferencesV1(false,false,"codex",true):JsonSerializer.Deserialize<PreferencesV1>(json));
        });
        group.MapPut("/preferences",async(PreferencesV1 request,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            if(!IsLocalIdentity(context))return Results.StatusCode(403);
            if(request.QuickRoute is not("fast" or "codex")||!request.PrioritizeSuggestions)return Results.BadRequest();
            await using var cmd=db.CreateCommand("INSERT INTO copilot.application_preferences(preference_key,preference_json) VALUES('user',@json::jsonb) ON CONFLICT(preference_key) DO UPDATE SET preference_json=EXCLUDED.preference_json,updated_at=now();");
            cmd.Parameters.AddWithValue("json",JsonSerializer.Serialize(request));await cmd.ExecuteNonQueryAsync(ct);return Results.Ok(request);
        });
        return endpoints;
    }
    public static bool IsLocalIdentity(HttpContext context)=>context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1{IsLocal:true};
    public sealed record PreferencesV1(bool AutoPrep,bool Notifications,string QuickRoute,bool PrioritizeSuggestions);
    public sealed record DiagnosticRequestV1(int Step);

    private static async Task<IResult> GetStatusAsync(HttpContext context,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)
    {
        var accounts=new List<object>();string databaseState="Unavailable";string readerState="Unavailable";object? latestAgentRun=null;
        var identity=context.Items[WebSessionServiceV1.IdentityItemKey] as WebSessionIdentityV1;
        try{
            await using var health=db.CreateCommand("SELECT 1;");await health.ExecuteScalarAsync(ct);databaseState="Ready";
            await using(var cmd=db.CreateCommand("""
                SELECT a.id,a.display_name,a.provider,CASE WHEN s.heartbeat_at<now()-interval '20 seconds' THEN 'Offline' ELSE s.state END,
                s.heartbeat_at,s.listening_target_count,s.last_error_code FROM copilot.chat_accounts a JOIN copilot.reader_status s ON s.account_id=a.id
                WHERE @all OR a.id=ANY(@accounts) ORDER BY a.created_at;
                """)){
                cmd.Parameters.AddWithValue("all",identity?.AllImported??false);cmd.Parameters.AddWithValue("accounts",identity?.AuthorizedAccountIds.ToArray()??[]);
                await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))accounts.Add(new{accountId=r.GetGuid(0),displayName=r.IsDBNull(1)?null:r.GetString(1),provider=r.GetString(2),state=r.GetString(3),lastHeartbeatAtUtc=r.GetFieldValue<DateTimeOffset>(4),listeningTargetCount=r.GetInt32(5),lastErrorCode=r.IsDBNull(6)?null:r.GetString(6)});
            }
            readerState=accounts.Count>0?"Available":"Unavailable";
            await using(var cmd=db.CreateCommand("SELECT task_type,runtime_name,status,started_at,ended_at,error_code FROM copilot.agent_runs WHERE @all OR (account_id=ANY(@accounts) AND conversation_id=ANY(@conversations)) ORDER BY started_at DESC LIMIT 1;")){
                cmd.Parameters.AddWithValue("all",identity?.AllImported??false);cmd.Parameters.AddWithValue("accounts",identity?.AuthorizedAccountIds.ToArray()??[]);
                cmd.Parameters.AddWithValue("conversations",identity?.AuthorizedConversationIds.ToArray()??[]);
                await using var r=await cmd.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))latestAgentRun=new{taskType=r.GetString(0),runtimeName=r.GetString(1),status=r.GetString(2),startedAtUtc=r.GetFieldValue<DateTimeOffset>(3),endedAtUtc=r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4),errorCode=r.IsDBNull(5)?null:r.GetString(5)};
            }
        }catch(NpgsqlException){databaseState="Unavailable";}
        var enabled=SessionEndpointsV1.MobileConfigured(config);
        return Results.Ok(new{databaseState,readerState,readerAccounts=accounts,latestAgentRun,
            configuredRuntime=config["Agent:Runtime"]??"Disabled",quickModelConfigured=!string.IsNullOrWhiteSpace(config["Agent:ExecutionProfiles:Quick:ModelId"]),
            memoryConsolidationRuntime=config["Memory:ConsolidationRuntime"]??"Disabled",historyJobsState="Available",
            mobileAccess=new{state=enabled?"Configured":"NotEnabled",url=enabled?config["Web:MobileOrigin"]:null,message=enabled?"请先信任本机证书，再使用五分钟配对链接；设备会话一小时后到期。":"手机安全访问尚未启用，需要配置可信 HTTPS 证书；电脑本机地址不能用于手机访问。"}});
    }

    private static async Task<IResult> DiagnoseAsync(DiagnosticRequestV1 request,HttpContext context,NpgsqlDataSource db,IConfiguration config,IServiceProvider services,CancellationToken ct)
    {
        if(!IsLocalIdentity(context))return Results.StatusCode(403);
        if(request.Step is <0 or >3)return Results.BadRequest();
        var checks=new List<object>();bool pass=true;var timer=Stopwatch.StartNew();
        void Add(string name,bool ready,string message,string recovery){pass&=ready;checks.Add(new{name,state=ready?"Ready":"Unavailable",message,recovery=ready?null:recovery});}
        if(request.Step==0){
            try{await using var cmd=db.CreateCommand("SELECT count(*) FROM copilot.schema_migrations;");var count=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));Add("database",true,"本机消息库可以连接。","");Add("migrations",count>0,$"数据库迁移已检查（已登记 ${count} 项）。","关闭并重新启动应用以应用迁移。");}catch(NpgsqlException){Add("database",false,"本机消息库未就绪。","使用关闭系统入口后重新启动；保留本机数据目录。");}
            var root=config["Operations:StateRoot"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WeChatAiCopilot","Formal");
            try{var probe=Path.Combine(root,"diagnostic-"+Guid.NewGuid().ToString("N")+".tmp");Directory.CreateDirectory(root);await File.WriteAllTextAsync(probe,"permission-probe",ct);File.Delete(probe);Add("storage",true,"本机应用目录可以写入。","");}catch(IOException){Add("storage",false,"本机应用目录无法写入。","确认当前 Windows 用户有本机应用目录权限。");}catch(UnauthorizedAccessException){Add("storage",false,"本机应用目录访问被拒绝。","以安装应用的 Windows 用户启动。");}
        }else if(request.Step==1){
            var running=OperatingSystem.IsWindows()&&Process.GetProcessesByName("Weixin").Length>0;Add("wechat",running,running?"微信客户端正在运行；登录状态以 Reader 心跳为准。":"没有检测到微信客户端。","打开微信并登录，然后重新检测。");
            await using var cmd=db.CreateCommand("SELECT state,heartbeat_at,last_error_code FROM copilot.reader_status WHERE heartbeat_at>now()-interval '20 seconds' ORDER BY heartbeat_at DESC LIMIT 1;");
            await using var r=await cmd.ExecuteReaderAsync(ct);bool found=await r.ReadAsync(ct);var ready=found&&r.GetString(0) is("Listening" or "Idle");Add("reader",ready,ready?"只读读取器最近心跳正常。":"读取器离线或有故障。","确认微信登录后，通过关闭与启动入口恢复读取器。");
        }else if(request.Step==2){
            var executor=services.GetService<IStructuredCodexCliExecutorV1>();
            if(executor is null||config["Agent:Runtime"]!="CodexCli")Add("ai",false,"尚未配置真实 AI 执行器。","在本机完成 Codex CLI 安装和认证后重新启动。");
            else{
                const string schema="""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";
                var result=await executor.ExecuteAsync("Synthetic connection test. Return the JSON object {\"ok\":true}. No personal data is included. Do not use tools.",schema,TimeSpan.FromSeconds(30),ct);
                var ok=result.Status==StructuredCodexCliExecutionStatusV1.Completed&&result.Output is{} output&&output.TryGetProperty("ok",out var property)&&property.ValueKind==JsonValueKind.True;
                Add("ai",ok,ok?"AI 已完成不含私聊的连接测试。":"AI 连接测试失败："+result.Status,"完成 Codex 登录或修复网络连接后重试。");
            }
        }else Add("mobile",SessionEndpointsV1.MobileConfigured(config),SessionEndpointsV1.MobileConfigured(config)?"已配置可信 HTTPS 安全配对。":"手机安全访问尚未启用，可跳过。","本机配置有效 HTTPS 证书与局域网地址，再启用设备配对。");
        return Results.Ok(new{step=request.Step,state=pass?"Ready":"Unavailable",checks,checkedAtUtc=DateTimeOffset.UtcNow,durationMilliseconds=timer.ElapsedMilliseconds,privateChatUsed=false});
    }
}
