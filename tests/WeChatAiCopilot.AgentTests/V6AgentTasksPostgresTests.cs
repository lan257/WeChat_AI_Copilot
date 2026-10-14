using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Modules.Memory.Api;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Modules.Skills;
using WeChatAiCopilot.Api.Platform.CodexCli;
using WeChatAiCopilot.Api.Platform.Migrations;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

/// <summary>Disposable synthetic PostgreSQL only. Fake runtime outputs are never evidence of real-model acceptance.</summary>
public sealed class V6AgentTasksPostgresTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private static readonly AgentTaskRequestV6 Reply = new() { PersonaPresetId = "natural" };

    [Theory]
    [InlineData("Image")]
    [InlineData("Voice")]
    public void NonTextNullBodyIsAllowedButTextNullStillFailsContract(string nonTextType)
    {
        var context=new AgentContextSnapshotV1{AccountId=Guid.NewGuid(),ConversationId=Guid.NewGuid(),ContextRevision="synthetic",PerspectiveState=PerspectiveStateV1.Confirmed,CalibrationVersion=1,
            RecentMessages=[new(){MessageId=Guid.NewGuid().ToString(),MessageType=nonTextType,Text=null}]};
        Assert.DoesNotContain(AgentContractValidatorV1.Validate(context),v=>v.Code=="CONTEXT_MESSAGE_TEXT_REQUIRED");
        var text=context with{RecentMessages=[context.RecentMessages![0] with{MessageType="Text"}]};
        Assert.Contains(AgentContractValidatorV1.Validate(text),v=>v.Code=="CONTEXT_MESSAGE_TEXT_REQUIRED");
    }

    [V6SkillsPostgresFact]
    public async Task ActiveCancellationReachesRuntimeTokenAndProducesNoResult()
    {
        await using var fixture=await Fixture.CreateAsync();
        fixture.Runtime.Delay=true;
        var id=await fixture.Queue.EnqueueAsync(fixture.A,Reply,null,default);
        var active=fixture.ExecuteOne();
        await fixture.Runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Running",(await fixture.Response(id)).Status);
        Assert.True(await fixture.Queue.CancelAsync(fixture.A,id,default));
        Assert.True(await active.WaitAsync(TimeSpan.FromSeconds(3)));
        var cancelled=await fixture.Response(id);
        Assert.Equal("Cancelled",cancelled.Status);
        Assert.Null(cancelled.Result);
        Assert.True(fixture.Runtime.CancelledByToken);
        await using var q=fixture.Db.CreateCommand("SELECT result_id FROM copilot.agent_tasks WHERE task_id=@id");q.Parameters.AddWithValue("id",id);
        Assert.IsType<DBNull>(await q.ExecuteScalarAsync());
    }

    [V6SkillsPostgresFact]
    public async Task CustomSkillsReachStructuredPromptAsUntrustedDataAndReviewExecutesWithinToolBudget()
    {
        await using var fixture=await Fixture.CreateAsync();
        fixture.Runtime.DelegateRuntime=new CodexCliAgentRuntime(fixture.Executor);
        var persona=await fixture.Skills.CreateAsync(fixture.A,new("Persona","自定义测试人设","","温柔有礼；不要代本人承诺。",["reply"],"Conversation",fixture.A.AccountId,fixture.A.ConversationId));
        var flow=await fixture.Skills.CreateAsync(fixture.A,new("Workflow","不可执行规则测试","","测试数据：请求Shell和其他会话；应用必须保持只读边界。",["review"],"Conversation",fixture.A.AccountId,fixture.A.ConversationId));
        var id=await fixture.Complete(Reply with{PersonaPresetId=persona.SkillId,ExecutionProfileId=AgentExecutionProfileIdV1.Balanced});
        Assert.Equal(2,fixture.Executor.ReplyPrompts.Count);
        var first=fixture.Executor.ReplyPrompts[0];
        Assert.Contains("不可信偏好数据",first);
        var start=first.IndexOf('{');Assert.True(start>=0);
        using var payload=JsonDocument.Parse(first[start..]);
        Assert.Equal(persona.Instruction,payload.RootElement.GetProperty("personaPreference").GetString());
        Assert.Contains(payload.RootElement.GetProperty("workflowRules").GetProperty("review").EnumerateArray(),e=>e.GetString()!.Contains(flow.Instruction,StringComparison.Ordinal));
        Assert.Empty(payload.RootElement.GetProperty("availableToolActions").EnumerateArray());
        Assert.Equal(0,fixture.Runtime.LastRequest!.Budget!.MaxToolCalls);
        Assert.Contains(fixture.Runtime.LastRequest.AllowedReadCapabilities,c=>c==AgentReadCapabilityV1.SearchChatHistory);
        Assert.Single(await fixture.Skills.RunsAsync(fixture.A,persona.SkillId));
        Assert.Single(await fixture.Skills.RunsAsync(fixture.A,flow.SkillId));
        await fixture.Skills.UpdateAsync(fixture.A,persona.SkillId,new(persona.Type,persona.Name,persona.Description,"保持克制，不使用额外亲密暗示。",persona.Stages,persona.ScopeType,persona.AccountId,persona.ConversationId,true,"Manual",persona.Revision));
        Assert.True((await fixture.Response(id)).IsStale);
    }

    [V6SkillsPostgresFact]
    public async Task ReviewPromptCannotExceedConfiguredContextBudget()
    {
        await using var fixture=await Fixture.CreateAsync();
        fixture.App.Configuration["Agent:Budget:MaxContextCharacters"]="6000";
        fixture.Executor.LongSuggestions=true;
        fixture.Runtime.DelegateRuntime=new CodexCliAgentRuntime(fixture.Executor);
        var id=await fixture.Queue.EnqueueAsync(fixture.A,Reply with{ExecutionProfileId=AgentExecutionProfileIdV1.Max},null,default);
        Assert.True(await fixture.ExecuteOne());
        var result=await fixture.Response(id);
        Assert.Equal("Failed",result.Status);
        Assert.Equal("AGENT_CONTEXT_BUDGET_EXCEEDED",result.ErrorCode);
        Assert.Single(fixture.Executor.ReplyPrompts);
        Assert.True(fixture.Executor.ReplyPrompts[0].Length<6000);
    }

    [V6SkillsPostgresFact]
    public async Task PostAcceptsImmediatelyResultRemainsBoundToScopeAndIdempotencyChecksBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clock = Stopwatch.StartNew();
        var accepted = await fixture.Invoke("POST", "/api/ai/tasks/", fixture.A, Reply, key:"same-request");
        Assert.Equal(202, accepted.Status);
        Assert.Equal(0, fixture.Runtime.CallCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "Only task admission is timed; no model runs in this request.");
        var id = accepted.Json.GetProperty("taskId").GetGuid();
        Assert.Equal("Queued", accepted.Json.GetProperty("status").GetString());
        var duplicate = await fixture.Invoke("POST", "/api/ai/tasks/", fixture.A, Reply, key:"same-request");
        Assert.Equal(202, duplicate.Status);
        Assert.Equal(id, duplicate.Json.GetProperty("taskId").GetGuid());
        var conflict = await fixture.Invoke("POST", "/api/ai/tasks/", fixture.A, Reply with { PersonaPresetId="warm" }, key:"same-request");
        Assert.Equal(409, conflict.Status);
        Assert.Equal(404, (await fixture.Invoke("GET", "/api/ai/tasks/{id:guid}", fixture.B, id:id)).Status);
        Assert.True(await fixture.ExecuteOne());
        var finished = await fixture.Response(id);
        Assert.Equal("Completed", finished.Status);
        Assert.False(finished.IsStale);
        var batch = Assert.IsType<SuggestionBatchV1>(finished.Result);
        Assert.Equal(fixture.A.AccountId, batch.AccountId);
        Assert.Equal(fixture.A.ConversationId, batch.ConversationId);
        Assert.Equal(3, batch.Suggestions!.Count);
        Assert.All(batch.Suggestions, s => Assert.Contains("FakeRuntime", s.Text));
        Assert.Equal(1, fixture.Runtime.CallCount);
        Assert.NotNull(fixture.Runtime.LastRequest!.Task!.TrustedSkillSnapshot);
        Assert.True(finished.Timings.ContainsKey("queueMilliseconds"));
        Assert.True(finished.Timings.ContainsKey("totalMilliseconds"));
        Assert.Null(await fixture.Queue.GetAsync(fixture.B, id, default));
    }

    [V6SkillsPostgresFact]
    public async Task QueuedCancellationAndApplicationRestartRecoveryNeverRunCancelledWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cancelled = await fixture.Queue.EnqueueAsync(fixture.A, Reply, null, default);
        var cancel = await fixture.Invoke("DELETE", "/api/ai/tasks/{id:guid}", fixture.A, id:cancelled);
        Assert.Equal(202, cancel.Status);
        Assert.Equal("Cancelled", (await fixture.Response(cancelled)).Status);
        await fixture.Recover();
        Assert.Equal("Cancelled",(await fixture.Response(cancelled)).Status);
        Assert.False(await fixture.ExecuteOne());
        Assert.Equal(0, fixture.Runtime.CallCount);
        var interrupted = await fixture.Queue.EnqueueAsync(fixture.A, Reply, null, default);
        var cancelling = await fixture.Queue.EnqueueAsync(fixture.B, Reply, null, default);
        var queued = await fixture.Queue.EnqueueAsync(fixture.A, Reply, null, default);
        await fixture.ExecuteSql("UPDATE copilot.agent_tasks SET status='Running' WHERE task_id=@id", ("id",interrupted));
        await fixture.ExecuteSql("UPDATE copilot.agent_tasks SET status='CancelRequested' WHERE task_id=@id", ("id",cancelling));
        await fixture.Recover();
        Assert.Equal("APPLICATION_RESTARTED", (await fixture.Response(interrupted)).ErrorCode);
        Assert.Equal("Failed", (await fixture.Response(cancelling, fixture.B)).Status);
        Assert.Equal("Queued", (await fixture.Response(queued)).Status);
        Assert.True(await fixture.Queue.CancelAsync(fixture.A, queued, default));
    }

    [V6SkillsPostgresFact]
    public async Task CompletedResultBecomesStaleOnMessageCalibrationPersonaWorkflowAndExpressionChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Complete();
        await fixture.ExecuteSql("UPDATE copilot.conversations SET message_set_revision=message_set_revision+1 WHERE id=@id", ("id",fixture.A.ConversationId));
        Assert.True((await fixture.Response(first)).IsStale);
        var second = await fixture.Complete();
        await fixture.ExecuteSql("UPDATE copilot.sender_calibrations SET calibration_version=calibration_version+1 WHERE conversation_id=@id", ("id",fixture.A.ConversationId));
        Assert.True((await fixture.Response(second)).IsStale);
        var third = await fixture.Complete();
        await fixture.Skills.SelectAsync(fixture.A,"warm");
        Assert.True((await fixture.Response(third)).IsStale);
        var flow = await fixture.Skills.CreateAsync(fixture.A,new("Workflow","测试复核","","检查是否捏造未经确认的约定。",["review"],"Conversation",fixture.A.AccountId,fixture.A.ConversationId));
        var fourth = await fixture.Complete();
        await fixture.Skills.UpdateAsync(fixture.A,flow.SkillId,new(flow.Type,flow.Name,flow.Description,"检查事实，不替本人许下承诺。",flow.Stages,flow.ScopeType,flow.AccountId,flow.ConversationId,true,"Manual",flow.Revision));
        Assert.True((await fixture.Response(fourth)).IsStale);
        var fifth = await fixture.Complete();
        await fixture.Portraits.SaveExpressionAsync(fixture.A.AccountId,new("清楚自然","不虚构承诺","",0));
        Assert.True((await fixture.Response(fifth)).IsStale);
        Assert.Equal(5,fixture.Runtime.CallCount);
    }

    [V6SkillsPostgresFact]
    public async Task PortraitRejectsForeignEvidenceAndExpressionUpdatesAreVersionedAndAccountIsolated()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Executor.EvidenceId=fixture.MessageB;
        var invalid = await Assert.ThrowsAsync<PortraitFailureV6>(() => fixture.Portraits.RefreshAsync(fixture.A));
        Assert.Equal("PORTRAIT_EVIDENCE_INVALID", invalid.Code);
        await using(var q=fixture.Db.CreateCommand("SELECT count(*) FROM copilot.contact_portraits"))
            Assert.Equal(0L,await q.ExecuteScalarAsync());
        fixture.Executor.EvidenceId=fixture.MessageA;
        var id = await fixture.Portraits.RefreshAsync(fixture.A);
        Assert.NotEqual(Guid.Empty,id);
        var portrait = JsonSerializer.SerializeToElement(await fixture.Portraits.GetAsync(fixture.A),Json);
        Assert.Equal("Current",portrait.GetProperty("status").GetString());
        Assert.Single(portrait.GetProperty("evidence").EnumerateArray());
        Assert.Equal(fixture.MessageA,portrait.GetProperty("evidence")[0].GetProperty("messageId").GetGuid());
        Assert.DoesNotContain(fixture.MessageB.ToString(),portrait.GetRawText());
        Assert.Empty(await fixture.Portraits.ReadEvidenceAsync(fixture.A,[fixture.MessageB]));
        Assert.DoesNotContain(fixture.B.AccountId.ToString(),fixture.Executor.Prompt);
        Assert.DoesNotContain("synthetic-contact-b",fixture.Executor.Prompt);
        Assert.Equal("EXPRESSION_VERSION_CONFLICT",(await Assert.ThrowsAsync<PortraitFailureV6>(() =>
            fixture.Portraits.SaveExpressionAsync(fixture.A.AccountId,new("简短","不承诺","人工测试",99)))).Code);
        var saved = await fixture.Portraits.SaveExpressionAsync(fixture.A.AccountId,new("简短","不承诺","人工测试",0));
        Assert.Equal(1,saved.Revision);
        Assert.Equal(0,(await fixture.Portraits.GetExpressionAsync(fixture.B.AccountId)).Revision);
        Assert.Equal("EXPRESSION_VERSION_CONFLICT",(await Assert.ThrowsAsync<PortraitFailureV6>(() =>
            fixture.Portraits.SaveExpressionAsync(fixture.A.AccountId,saved with { Revision=0 }))).Code);
        var updated = await fixture.Portraits.SaveExpressionAsync(fixture.A.AccountId,saved with { Notes="更新人工测试" });
        Assert.Equal(2,updated.Revision);
        Assert.Equal(updated,await fixture.Portraits.GetExpressionAsync(fixture.A.AccountId));
    }

    [V6SkillsPostgresFact]
    public async Task ScopedDeviceCannotReadOrCreateOtherConversationTasksOrWriteAccountWideExpression()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Device.Identity=new(Guid.NewGuid(),false,[fixture.A.AccountId],[fixture.A.ConversationId],false,DateTimeOffset.UtcNow.AddHours(1));
        var outside = await fixture.Invoke("POST","/api/ai/tasks/",fixture.B,Reply,device:true);
        Assert.True(outside.Status is 403 or 404);
        var inside = await fixture.Invoke("POST","/api/ai/tasks/",fixture.A,Reply,device:true);
        Assert.Equal(202,inside.Status);
        var expression = await fixture.Invoke("PUT","/api/expression/current",fixture.A,new ExpressionV6("device-write","","",0),device:true);
        Assert.Equal(403,expression.Status);
        Assert.Equal(0,(await fixture.Portraits.GetExpressionAsync(fixture.A.AccountId)).Revision);
        await fixture.Queue.CancelAsync(fixture.A,inside.Json.GetProperty("taskId").GetGuid(),default);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required NpgsqlDataSource Db { get; init; }
        public required NpgsqlDataSource Admin { get; init; }
        public required string DatabaseName { get; init; }
        public required WebApplication App { get; init; }
        public required AuthorizedContextScopeV1 A { get; init; }
        public required AuthorizedContextScopeV1 B { get; init; }
        public required Guid MessageA { get; init; }
        public required Guid MessageB { get; init; }
        public required CaptureRuntime Runtime { get; init; }
        public required PortraitExecutor Executor { get; init; }
        public required DeviceValidator Device { get; init; }
        public AgentTaskQueueV6 Queue => App.Services.GetRequiredService<AgentTaskQueueV6>();
        public SkillService Skills => new(Db);
        public PortraitServiceV6 Portraits => App.Services.GetRequiredService<PortraitServiceV6>();

        public static async Task<Fixture> CreateAsync()
        {
            var dsn=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES"));
            Assert.Equal(55439,dsn.Port);
            Assert.Contains("test",dsn.Database!,StringComparison.OrdinalIgnoreCase);
            var admin=NpgsqlDataSource.Create(dsn.ConnectionString);
            var name="v6_agent_test_"+Guid.NewGuid().ToString("N");
            await using(var q=admin.CreateCommand("CREATE DATABASE \""+name+"\""))await q.ExecuteNonQueryAsync();
            dsn.Database=name;
            var db=NpgsqlDataSource.Create(dsn.ConnectionString);
            await MigrationRunner.ApplyPendingAsync(db);
            var a=await Seed(db,"synthetic-contact-a");var b=await Seed(db,"synthetic-contact-b");
            var runtime=new CaptureRuntime();var executor=new PortraitExecutor();var device=new DeviceValidator();
            var builder=WebApplication.CreateBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Authentication:WebBearerToken"]="synthetic-agent-token",["Agent:Runtime"]="Fake",
                ["Agent:AllowedPersonaPresetIds:0"]="natural",["Agent:SkillVersion"]="synthetic-v6",
                ["Agent:Budget:MaxDurationSeconds"]="20",["Agent:Budget:MaxContextCharacters"]="10000",
                ["Agent:Budget:MaxToolCalls"]="0",
                ["Agent:AllowedReadCapabilities:0"]="GetRecentMessages",["Agent:AllowedReadCapabilities:1"]="SearchChatHistory",
                ["Agent:AllowedReadCapabilities:2"]="GetContactMemory",["Agent:AllowedReadCapabilities:3"]="GetMessageEvidence"
            });
            builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<IAgentRuntime>(runtime);
            builder.Services.AddSingleton<IStructuredCodexCliExecutorV1>(executor);
            builder.Services.AddSingleton<IWebSessionValidatorV1>(device);
            builder.Services.AddScoped<IMessageQueryV1,PostgresMessageQueryV1>();
            builder.Services.AddScoped<IContextServiceV1,SyntheticContext>();
            builder.Services.AddScoped<IMemoryServiceV1,PostgresMemoryServiceV1>();
            builder.Services.AddScoped<IContextToolSessionFactoryV1,ContextToolSessionFactoryV1>();
            builder.Services.AddV6SkillServices();builder.Services.AddAgentModule(builder.Configuration);
            builder.Services.AddAsyncAgentTasksV6();
            var app=builder.Build();app.MapAsyncAgentTasksV6(builder.Configuration);app.MapPortraitEndpointsV6(builder.Configuration);
            return new(){Db=db,Admin=admin,DatabaseName=name,App=app,A=a.Scope,B=b.Scope,MessageA=a.Message,MessageB=b.Message,Runtime=runtime,Executor=executor,Device=device};
        }

        public async Task<AgentTaskResponseV6> Response(Guid id,AuthorizedContextScopeV1? scope=null) =>
            Assert.IsType<AgentTaskResponseV6>(await Queue.GetAsync(scope??A,id,default));
        public async Task<Guid> Complete(AgentTaskRequestV6? request=null)
        {
            var id=await Queue.EnqueueAsync(A,request??Reply,null,default);
            Assert.True(await ExecuteOne());
            var result=await Response(id);
            Assert.True(result.Status=="Completed", "Synthetic task failed with safe code: "+result.ErrorCode);
            Assert.False(result.IsStale);
            return id;
        }
        public Task<bool> ExecuteOne() => (Task<bool>)typeof(AgentTaskQueueV6).GetMethod("ExecuteNextAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Queue,[CancellationToken.None])!;
        public Task Recover() => (Task)typeof(AgentTaskQueueV6).GetMethod("RecoverAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Queue,[CancellationToken.None])!;
        public async Task ExecuteSql(string sql,params (string Name,object Value)[] parameters)
        {
            await using var q=Db.CreateCommand(sql);foreach(var p in parameters)q.Parameters.AddWithValue(p.Name,p.Value);await q.ExecuteNonQueryAsync();
        }
        public async Task<(int Status,JsonElement Json)> Invoke(string method,string pattern,AuthorizedContextScopeV1 scope,object? body=null,Guid? id=null,string? key=null,bool device=false)
        {
            var endpoint=((IEndpointRouteBuilder)App).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>().Single(e=>e.RoutePattern.RawText==pattern&&e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var ctx=new DefaultHttpContext{RequestServices=App.Services};ctx.Connection.RemoteIpAddress=IPAddress.Loopback;
            ctx.Request.Method=method;ctx.Request.Path=pattern;ctx.Request.QueryString=new($"?accountId={scope.AccountId}&conversationId={scope.ConversationId}");
            if(device)ctx.Request.Headers.Cookie=WebSessionServiceV1.CookieName+"=synthetic";else ctx.Request.Headers.Authorization="Bearer synthetic-agent-token";
            if(id is {} guid)ctx.Request.RouteValues["id"]=guid.ToString();if(key is not null)ctx.Request.Headers["Idempotency-Key"]=key;
            if(body is not null){var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json));ctx.Request.ContentType="application/json";ctx.Request.ContentLength=bytes.Length;ctx.Request.Body=new MemoryStream(bytes);ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());}
            ctx.Response.Body=new MemoryStream();await endpoint.RequestDelegate!(ctx);ctx.Response.Body.Position=0;
            var content=await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return(ctx.Response.StatusCode,string.IsNullOrEmpty(content)?JsonSerializer.SerializeToElement(new{}):JsonDocument.Parse(content).RootElement.Clone());
        }
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();await Db.DisposeAsync();
            Assert.Matches("^v6_agent_test_[a-f0-9]{32}$",DatabaseName);
            await using(var q=Admin.CreateCommand("DROP DATABASE \""+DatabaseName+"\" WITH (FORCE)"))await q.ExecuteNonQueryAsync();
            await Admin.DisposeAsync();
        }
        private static async Task<(AuthorizedContextScopeV1 Scope,Guid Message)> Seed(NpgsqlDataSource db,string name)
        {
            await using var q=db.CreateCommand("""
                WITH a AS(INSERT INTO copilot.chat_accounts(provider,external_account_id) VALUES('synthetic-test',gen_random_uuid()::text) RETURNING id),
                c AS(INSERT INTO copilot.contacts(account_id,external_contact_id) SELECT id,@name FROM a RETURNING id,account_id)
                INSERT INTO copilot.conversations(account_id,contact_id,external_conversation_id,message_set_revision)
                SELECT account_id,id,@name,1 FROM c RETURNING account_id,id
                """);q.Parameters.AddWithValue("name",name);
            AuthorizedContextScopeV1 scope;await using(var r=await q.ExecuteReaderAsync()){Assert.True(await r.ReadAsync());scope=new(r.GetGuid(0),r.GetGuid(1));}
            var message=Guid.NewGuid();
            await using var insert=db.CreateCommand("""
                INSERT INTO copilot.sender_calibrations(account_id,conversation_id,identity_namespace,account_self_raw_sender_id,contact_raw_sender_id,calibration_version,perspective_state,evidence,confirmed_at)
                VALUES(@a,@c,'synthetic-v6','1','20',1,'Confirmed','UserConfirmed',now());
                INSERT INTO copilot.chat_messages(id,account_id,conversation_id,provider,message_type,text_content,source_occurred_at,direction,direction_evidence)
                VALUES(@id,@a,@c,'synthetic-test','Text',@text,now(),'Incoming','UserConfirmed');
                """);insert.Parameters.AddWithValue("a",scope.AccountId);insert.Parameters.AddWithValue("c",scope.ConversationId);insert.Parameters.AddWithValue("id",message);insert.Parameters.AddWithValue("text","Synthetic fixture message: "+name);await insert.ExecuteNonQueryAsync();
            return(scope,message);
        }
    }
    private sealed class SyntheticContext(IMessageQueryV1 query) : IContextServiceV1
    {
        public async ValueTask<ContextBundleV1> BuildAsync(ContextRequestV1 request,AuthorizedContextScopeV1 scope,CancellationToken cancellationToken=default)
        {
            if(request.AccountId!=scope.AccountId||request.ConversationId!=scope.ConversationId)throw new UnauthorizedAccessException();
            var messages=await query.GetRecentMessagesAsync(new(){AccountId=scope.AccountId,ConversationId=scope.ConversationId,Limit=request.RecentMessageLimit},cancellationToken);
            return new(){AccountId=scope.AccountId,ConversationId=scope.ConversationId,ContextRevision="synthetic-context:"+messages.MessageSetRevision,PerspectiveState=messages.PerspectiveState,CalibrationVersion=messages.CalibrationVersion,ProfileStatus=ProfileStatusV1.Current,RecentMessages=messages.Messages.Select(m=>new ContextMessageV1{MessageId=m.MessageId,Text=m.Text,MessageType=m.MessageType,OccurredAt=m.OccurredAt,Direction=m.Direction,DirectionEvidence=m.DirectionEvidence}).ToArray(),GeneratedAt=DateTimeOffset.UtcNow};
        }
    }
    private sealed class CaptureRuntime : IAgentRuntime
    {
        public int CallCount {get;private set;}
        public ScopedAgentRequestV1? LastRequest {get;private set;}
        public bool Delay {get;set;}
        public bool CancelledByToken {get;private set;}
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAgentRuntime DelegateRuntime {get;set;}=new FakeAgentRuntime();
        public async Task<AgentRuntimeResultV1> ExecuteAsync(ScopedAgentRequestV1 request,CancellationToken cancellationToken)
        {
            CallCount++;LastRequest=request;Started.TrySetResult();
            if(Delay)try{await Task.Delay(Timeout.Infinite,cancellationToken);}catch(OperationCanceledException){CancelledByToken=true;throw;}
            return await DelegateRuntime.ExecuteAsync(request,cancellationToken);
        }
    }
    private sealed class PortraitExecutor : IStructuredCodexCliExecutorV1
    {
        public Guid EvidenceId {get;set;}
        public string Prompt {get;private set;}="";
        public List<string> ReplyPrompts {get;}=[];
        public bool LongSuggestions {get;set;}
        public Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(string prompt,string outputSchemaJson,TimeSpan timeout,CancellationToken cancellationToken=default,string? modelId=null)
        {
            Prompt=prompt;
            if(outputSchemaJson.Contains("\"suggestions\"",StringComparison.Ordinal))
            {
                ReplyPrompts.Add(prompt);
                var values=LongSuggestions?new[]{new string('x',1800)+"1",new string('x',1800)+"2",new string('x',1800)+"3"}:new[]{"Synthetic first suggestion","Synthetic second suggestion","Synthetic third suggestion"};
                return Task.FromResult(new StructuredCodexCliExecutionResultV1(StructuredCodexCliExecutionStatusV1.Completed,JsonSerializer.SerializeToElement(new{suggestions=values},Json)));
            }
            var output=JsonSerializer.SerializeToElement(new{overview=new[]{new{text="Synthetic supported test claim",type="Fact",evidenceMessageIds=new[]{EvidenceId}}},topics=Array.Empty<object>(),plans=Array.Empty<object>(),preferences=Array.Empty<object>(),boundaries=Array.Empty<object>()},Json);
            return Task.FromResult(new StructuredCodexCliExecutionResultV1(StructuredCodexCliExecutionStatusV1.Completed,output));
        }
    }
    private sealed class DeviceValidator : IWebSessionValidatorV1
    {
        public WebSessionIdentityV1? Identity {get;set;}
        public Task<WebSessionIdentityV1?> ValidateAsync(HttpContext context,bool requireCsrf,CancellationToken cancellationToken=default)
        {if(Identity is not null)context.Items[WebSessionServiceV1.IdentityItemKey]=Identity;return Task.FromResult(Identity);}
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody=>true; }
}
