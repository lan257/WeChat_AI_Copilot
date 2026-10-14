using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Skills;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Agent.Api;

public sealed record AgentTaskRequestV6
{
    public string TaskType {get;init;}="ReplyCandidates";
    public string? PersonaPresetId {get;init;}
    public AgentExecutionProfileIdV1 ExecutionProfileId {get;init;}=AgentExecutionProfileIdV1.Quick;
    [System.Text.Json.Serialization.JsonConverter(typeof(WeChatAiCopilot.Api.Modules.Agent.Contracts.LenientGuidJsonConverterV1))]
    public Guid? PreviousBatchId {get;init;}
    [System.Text.Json.Serialization.JsonConverter(typeof(WeChatAiCopilot.Api.Modules.Agent.Contracts.LenientGuidJsonConverterV1))]
    public Guid? ThreadId {get;init;}
    public string? UserInput {get;init;}
}
internal sealed record QueuedAgentTaskV6(Guid TaskId,AuthorizedContextScopeV1 Scope,string Status,string Phase,AgentTaskRequestV6 Request,string? Fingerprint,Guid? ResultId,string? ErrorCode,DateTimeOffset Created,DateTimeOffset? Started,DateTimeOffset? Ended,Dictionary<string,long> Timings);
public sealed record AgentTaskResponseV6(Guid TaskId,string TaskType,string Status,string Phase,DateTimeOffset CreatedAtUtc,DateTimeOffset? StartedAtUtc,DateTimeOffset? EndedAtUtc,string? ErrorCode,object? Result,bool IsStale,IReadOnlyDictionary<string,long> Timings);

public sealed class AgentTaskQueueV6(NpgsqlDataSource db,IServiceScopeFactory scopeFactory)
{
    private static readonly JsonSerializerOptions JsonOptions=CreateJsonOptions();
    private readonly ConcurrentDictionary<Guid,(CancellationTokenSource Cancellation,AgentTaskTelemetryV6 Telemetry)> _active=new();
    private static JsonSerializerOptions CreateJsonOptions(){var o=new JsonSerializerOptions(JsonSerializerDefaults.Web);o.Converters.Add(new JsonStringEnumConverter());return o;}

    public static string? Validate(AgentTaskRequestV6 request)
    {
        if(request.TaskType is not("ReplyCandidates" or "FreeConsultation" or "Portrait"))return "INVALID_TASK_TYPE";
        if(!Enum.IsDefined(request.ExecutionProfileId))return "INVALID_EXECUTION_PROFILE";
        if(request.TaskType=="ReplyCandidates"&&string.IsNullOrWhiteSpace(request.PersonaPresetId))return "PERSONA_REQUIRED";
        if(request.TaskType=="FreeConsultation"&&(string.IsNullOrWhiteSpace(request.UserInput)||request.UserInput.Length>4000))return "CONSULTATION_INPUT_INVALID";
        if(request.TaskType!="FreeConsultation"&&request.UserInput is not null)return "TASK_INPUT_NOT_ALLOWED";
        return null;
    }

    public async Task<Guid> EnqueueAsync(AuthorizedContextScopeV1 scope,AgentTaskRequestV6 request,string? key,CancellationToken ct)
    {
        if(Validate(request) is {} invalid)throw new PortraitFailureV6(invalid);
        if(key is not null&&(string.IsNullOrWhiteSpace(key)||key.Length>200))throw new PortraitFailureV6("IDEMPOTENCY_KEY_INVALID");
        var id=Guid.NewGuid();
        await using var q=db.CreateCommand("""
            INSERT INTO copilot.agent_tasks(task_id,account_id,conversation_id,task_type,payload,idempotency_key)
            VALUES(@id,@a,@c,@type,CAST(@payload AS jsonb),@key)
            ON CONFLICT(account_id,conversation_id,idempotency_key) DO NOTHING
            RETURNING task_id;
            """);
        q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("a",scope.AccountId);q.Parameters.AddWithValue("c",scope.ConversationId);q.Parameters.AddWithValue("type",request.TaskType);q.Parameters.AddWithValue("payload",JsonSerializer.Serialize(request,JsonOptions));q.Parameters.Add(new("key",NpgsqlTypes.NpgsqlDbType.Text){Value=(object?)key??DBNull.Value});
        var created=await q.ExecuteScalarAsync(ct);
        if(created is Guid taskId)return taskId;
        await using var prior=db.CreateCommand("SELECT task_id,payload::text FROM copilot.agent_tasks WHERE account_id=@a AND conversation_id=@c AND idempotency_key=@key");prior.Parameters.AddWithValue("a",scope.AccountId);prior.Parameters.AddWithValue("c",scope.ConversationId);prior.Parameters.AddWithValue("key",key!);await using var reader=await prior.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)||JsonSerializer.Deserialize<AgentTaskRequestV6>(reader.GetString(1),JsonOptions)!=request)throw new PortraitFailureV6("IDEMPOTENCY_KEY_REUSED");
        return reader.GetGuid(0);
    }

    public async Task<object?> GetAsync(AuthorizedContextScopeV1 scope,Guid id,CancellationToken ct)
    {
        var item=await ReadAsync(scope,id,ct);return item is null?null:await ToResponseAsync(item,ct);
    }
    public async Task<IReadOnlyList<object>> ListAsync(AuthorizedContextScopeV1 scope,CancellationToken ct)
    {
        var ids=new List<Guid>();await using(var q=db.CreateCommand("SELECT task_id FROM copilot.agent_tasks WHERE account_id=@a AND conversation_id=@c ORDER BY created_at DESC LIMIT 20")){q.Parameters.AddWithValue("a",scope.AccountId);q.Parameters.AddWithValue("c",scope.ConversationId);await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))ids.Add(r.GetGuid(0));}
        var items=new List<object>();foreach(var id in ids){var item=await GetAsync(scope,id,ct);if(item is not null)items.Add(item);}return items;
    }
    public async Task<bool> CancelAsync(AuthorizedContextScopeV1 scope,Guid id,CancellationToken ct)
    {
        await using var q=db.CreateCommand("""
            UPDATE copilot.agent_tasks SET status=CASE WHEN status='Queued' THEN 'Cancelled' ELSE 'CancelRequested' END,
            phase=CASE WHEN status='Queued' THEN 'Cancelled' ELSE 'Cancelling' END,
            ended_at=CASE WHEN status='Queued' THEN now() ELSE ended_at END
            WHERE task_id=@id AND account_id=@a AND conversation_id=@c AND status IN('Queued','Running','CancelRequested') RETURNING task_id;
            """);q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("a",scope.AccountId);q.Parameters.AddWithValue("c",scope.ConversationId);
        var found=await q.ExecuteScalarAsync(ct) is not null;if(found&&_active.TryGetValue(id,out var active))active.Cancellation.Cancel();return found;
    }

    internal async Task RecoverAsync(CancellationToken ct)
    {
        await using var q=db.CreateCommand("UPDATE copilot.agent_tasks SET status='Failed',phase='Failed',error_code='APPLICATION_RESTARTED',ended_at=now() WHERE status IN('Running','CancelRequested')");await q.ExecuteNonQueryAsync(ct);
    }
    internal async Task<bool> ExecuteNextAsync(CancellationToken stopping)
    {
        QueuedAgentTaskV6? item;
        // Fairness first, then age: with only two workers, a burst of tasks for one contact must not
        // make every other contact wait behind it. Oldest-wins still holds inside one conversation.
        await using(var q=db.CreateCommand("""
            UPDATE copilot.agent_tasks SET status='Running',phase='Preparing',started_at=now()
            WHERE task_id=(SELECT t.task_id FROM copilot.agent_tasks t WHERE t.status='Queued'
            ORDER BY (SELECT count(*) FROM copilot.agent_tasks p WHERE p.account_id=t.account_id AND p.conversation_id=t.conversation_id AND p.status IN('Queued','Running')),t.created_at,t.task_id
            FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING task_id,account_id,conversation_id,status,phase,payload::text,fingerprint,result_id,error_code,created_at,started_at,ended_at,timings::text;
            """)){await using var r=await q.ExecuteReaderAsync(stopping);item=await r.ReadAsync(stopping)?ReadRow(r):null;}
        if(item is null)return false;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping);cancel.CancelAfter(TimeSpan.FromMinutes(5));
        var trace=new AgentTaskTelemetryV6();_active[item.TaskId]=(cancel,trace);AgentTaskTelemetryV6.Current.Value=trace;
        var total=Stopwatch.GetTimestamp();trace.Add("queueMilliseconds",(long)((item.Started??DateTimeOffset.UtcNow)-item.Created).TotalMilliseconds);
        string status="Completed";string? error=null;Guid? resultId=null;string? fingerprint=null;
        try
        {
            if((await ReadAsync(item.Scope,item.TaskId,cancel.Token))?.Status=="CancelRequested")cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            using var container=scopeFactory.CreateScope();var services=container.ServiceProvider;
            fingerprint=await FingerprintAsync(services,item.Scope,item.Request.PersonaPresetId,cancel.Token);
            await using(var q=db.CreateCommand("UPDATE copilot.agent_tasks SET fingerprint=@f WHERE task_id=@id")){q.Parameters.AddWithValue("f",fingerprint);q.Parameters.AddWithValue("id",item.TaskId);await q.ExecuteNonQueryAsync(cancel.Token);}
            var app=services.GetRequiredService<IAgentTaskApplicationServiceV1>();
            if(item.Request.TaskType=="ReplyCandidates"){
                var batch=await app.CreateSuggestionsAsync(item.Scope,new(){PersonaPresetId=item.Request.PersonaPresetId,ExecutionProfileId=item.Request.ExecutionProfileId,PreviousBatchId=item.Request.PreviousBatchId},cancel.Token);resultId=Guid.Parse(batch.BatchId!);
            }else if(item.Request.TaskType=="FreeConsultation"){
                var threadId=item.Request.ThreadId;
                if(threadId is null){var thread=await app.CreateConsultationThreadAsync(item.Scope,cancel.Token);threadId=Guid.Parse(thread.ThreadId!);item=item with{Request=item.Request with{ThreadId=threadId}};await UpdatePayloadAsync(item,cancel.Token);}
                var turn=await app.CreateConsultationTurnAsync(item.Scope,threadId.Value,item.Request.UserInput!,cancel.Token,item.Request.ExecutionProfileId);resultId=Guid.Parse(turn.RunId!);
            }else resultId=await services.GetRequiredService<PortraitServiceV6>().RefreshAsync(item.Scope,cancel.Token);
        }
        catch(OperationCanceledException){status="Cancelled";error=stopping.IsCancellationRequested?"APPLICATION_STOPPED":"CANCELLED";}
        catch(AgentTaskContractException ex){status="Failed";error=SafeCode(ex.Violations.FirstOrDefault()?.Code);}
        catch(AgentTaskExecutionException ex){status="Failed";error=SafeCode(ex.ErrorCode);}
        catch(SkillException ex){status="Failed";error=SafeCode(ex.Code);}
        catch(PortraitFailureV6 ex){status="Failed";error=SafeCode(ex.Code);}
        catch(AgentResourceNotFoundException){status="Failed";error="AGENT_RESOURCE_NOT_FOUND";}
        catch(Exception){status="Failed";error="TASK_EXECUTION_FAILED";}
        finally
        {
            trace.Add("totalMilliseconds",(long)Stopwatch.GetElapsedTime(total).TotalMilliseconds);trace.SetPhase(status);
            await using var q=db.CreateCommand("UPDATE copilot.agent_tasks SET status=CASE WHEN status='CancelRequested' THEN 'Cancelled' ELSE @s END,phase=@s,error_code=@e,result_id=@r,timings=CAST(@t AS jsonb),ended_at=now() WHERE task_id=@id");
            q.Parameters.AddWithValue("s",status);q.Parameters.Add(new("e",NpgsqlTypes.NpgsqlDbType.Text){Value=(object?)error??DBNull.Value});q.Parameters.Add(new("r",NpgsqlTypes.NpgsqlDbType.Uuid){Value=(object?)resultId??DBNull.Value});q.Parameters.AddWithValue("t",JsonSerializer.Serialize(trace.Snapshot(),JsonOptions));q.Parameters.AddWithValue("id",item.TaskId);
            try{await q.ExecuteNonQueryAsync(CancellationToken.None);}finally{_active.TryRemove(item.TaskId,out _);AgentTaskTelemetryV6.Current.Value=null;}
        }
        return true;
    }
    private async Task UpdatePayloadAsync(QueuedAgentTaskV6 item,CancellationToken ct){await using var q=db.CreateCommand("UPDATE copilot.agent_tasks SET payload=CAST(@p AS jsonb) WHERE task_id=@id");q.Parameters.AddWithValue("p",JsonSerializer.Serialize(item.Request,JsonOptions));q.Parameters.AddWithValue("id",item.TaskId);await q.ExecuteNonQueryAsync(ct);}
    private async Task<QueuedAgentTaskV6?> ReadAsync(AuthorizedContextScopeV1 scope,Guid id,CancellationToken ct){await using var q=db.CreateCommand("SELECT task_id,account_id,conversation_id,status,phase,payload::text,fingerprint,result_id,error_code,created_at,started_at,ended_at,timings::text FROM copilot.agent_tasks WHERE task_id=@id AND account_id=@a AND conversation_id=@c");q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("a",scope.AccountId);q.Parameters.AddWithValue("c",scope.ConversationId);await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadRow(r):null;}
    private static QueuedAgentTaskV6 ReadRow(NpgsqlDataReader r)=>new(r.GetGuid(0),new(r.GetGuid(1),r.GetGuid(2)),r.GetString(3),r.GetString(4),JsonSerializer.Deserialize<AgentTaskRequestV6>(r.GetString(5),JsonOptions)!,r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetGuid(7),r.IsDBNull(8)?null:r.GetString(8),r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetFieldValue<DateTimeOffset>(11),JsonSerializer.Deserialize<Dictionary<string,long>>(r.GetString(12),JsonOptions)!);
    private async Task<object> ToResponseAsync(QueuedAgentTaskV6 item,CancellationToken ct)
    {
        using var container=scopeFactory.CreateScope();var services=container.ServiceProvider;object? result=null;var stale=false;
        if(item.Status=="Completed"&&item.ResultId is {} id){var app=services.GetRequiredService<IAgentTaskApplicationServiceV1>();if(item.Request.TaskType=="ReplyCandidates")result=await app.GetSuggestionBatchAsync(item.Scope,id,ct);else if(item.Request.TaskType=="FreeConsultation"&&item.Request.ThreadId is {} thread){var history=await app.GetConsultationHistoryAsync(item.Scope,thread,ct);result=history?.Turns.FirstOrDefault(x=>Guid.TryParse(x.RunId,out var rid)&&rid==id);}else result=new{portraitId=id};try{stale=item.Fingerprint!=await FingerprintAsync(services,item.Scope,item.Request.PersonaPresetId,ct);}catch(SkillException){stale=true;}}
        var timings=item.Timings;var phase=item.Phase;if(_active.TryGetValue(item.TaskId,out var active)){timings=active.Telemetry.Snapshot();phase=active.Telemetry.Phase;}
        return new AgentTaskResponseV6(item.TaskId,item.Request.TaskType,item.Status,phase,item.Created,item.Started,item.Ended,item.ErrorCode,result,stale,timings);
    }
    internal async Task<string> FingerprintAsync(IServiceProvider services,AuthorizedContextScopeV1 scope,string? personaId,CancellationToken ct)
    {
        await using var q=db.CreateCommand("SELECT message_set_revision::text,COALESCE((SELECT string_agg(identity_namespace||':'||calibration_version||':'||perspective_state,',' ORDER BY identity_namespace) FROM copilot.sender_calibrations WHERE account_id=@a AND conversation_id=@c),'none'),COALESCE((SELECT revision FROM copilot.account_expression WHERE account_id=@a),0) FROM copilot.conversations WHERE account_id=@a AND id=@c");q.Parameters.AddWithValue("a",scope.AccountId);q.Parameters.AddWithValue("c",scope.ConversationId);string revision;await using(var r=await q.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))throw new AgentResourceNotFoundException("conversation");revision=r.GetString(0)+":"+r.GetString(1)+":"+r.GetInt64(2);}
        var skills=services.GetRequiredService<SkillResolutionService>();var selected=await skills.GetScopeRevisionDigestAsync(scope,ct);var requested=await skills.ResolveAsync(scope,personaId,ct);return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision+":"+selected+":"+requested.RevisionDigest)));
    }
    private static string SafeCode(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=64&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')?value:"TASK_EXECUTION_FAILED";
}

public sealed class AgentTaskWorkerV6(AgentTaskQueueV6 queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.RecoverAsync(stoppingToken);
        await Task.WhenAll(Enumerable.Range(0,2).Select(_=>RunAsync(stoppingToken)));
    }
    private async Task RunAsync(CancellationToken ct){while(!ct.IsCancellationRequested){try{if(!await queue.ExecuteNextAsync(ct))await Task.Delay(500,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception){await Task.Delay(1000,ct);}}}
}

public static class AsyncAgentEndpointsV6
{
    public static IServiceCollection AddAsyncAgentTasksV6(this IServiceCollection services){services.AddSingleton<AgentTaskQueueV6>();services.AddHostedService<AgentTaskWorkerV6>();services.AddHostedService<ConversationLifecycleV6>();services.AddScoped<PortraitServiceV6>();services.AddScoped<V6RoutingPolicy>();return services;}
    public static IEndpointRouteBuilder MapAsyncAgentTasksV6(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        var group=endpoints.MapGroup("/api/ai/tasks").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        group.MapPost("",async(AgentTaskRequestV6 request,HttpContext context,AgentTaskQueueV6 queue,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;var invalid=AgentTaskQueueV6.Validate(request);if(invalid is not null)return Results.BadRequest(new{error=new{code=invalid}});try{var id=await queue.EnqueueAsync(access.Scope!,request,context.Request.Headers["Idempotency-Key"].FirstOrDefault(),ct);return Results.Accepted($"/api/ai/tasks/{id}",await queue.GetAsync(access.Scope!,id,ct));}catch(PortraitFailureV6 ex){return Results.Json(new{error=new{code=ex.Code}},statusCode:ex.Code=="IDEMPOTENCY_KEY_REUSED"?409:400);}});
        group.MapGet("",async(HttpContext context,AgentTaskQueueV6 queue,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);return access.Error??Results.Ok(await queue.ListAsync(access.Scope!,ct));});
        group.MapGet("/{id:guid}",async(Guid id,HttpContext context,AgentTaskQueueV6 queue,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;var task=await queue.GetAsync(access.Scope!,id,ct);return task is null?Results.NotFound():Results.Ok(task);});
        group.MapDelete("/{id:guid}",async(Guid id,HttpContext context,AgentTaskQueueV6 queue,CancellationToken ct)=>{var access=await V6ScopeAccess.ResolveAsync(context,config,context.RequestServices,ct);if(access.Error is not null)return access.Error;return await queue.CancelAsync(access.Scope!,id,ct)?Results.Accepted($"/api/ai/tasks/{id}"):Results.NotFound();});
        return endpoints;
    }
}
