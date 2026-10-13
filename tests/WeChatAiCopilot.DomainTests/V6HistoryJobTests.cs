using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Migrations;

namespace WeChatAiCopilot.DomainTests;

public sealed class V6HistoryJobTests
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};

    [V6SessionsPostgresFact]
    public async Task RealHistoryQueueEnforcesScopeCancellationCountersAndRepeatedBatchIdempotency()
    {
        await using var db=TestDatabase();await MigrationRunner.ApplyPendingAsync(db);
        var scope=await CreateScope(db);var other=await CreateScope(db);
        var token=await Session(db,scope);var app=Application(db,scope.AccountExternalId);
        await using var disposableApp=app;
        var from=DateTimeOffset.UtcNow.AddDays(-1);var to=DateTimeOffset.UtcNow;
        var create=new HistoryJobEndpointsV1.CreateHistoryJobV1(scope.AccountId,scope.ConversationId,from,to,5000);
        Assert.Equal(401,(await Invoke(app,"POST","/api/history/jobs",create,token,bodyCsrf:false)).Status);
        Assert.Equal(403,(await Invoke(app,"POST","/api/history/jobs",create with{AccountId=other.AccountId,ConversationId=other.ConversationId},token)).Status);
        var pending=await Invoke(app,"POST","/api/history/jobs",create,token);Assert.Equal(202,pending.Status);
        var id=JsonDocument.Parse(pending.Body).RootElement.GetProperty("jobId").GetString()!;
        Assert.Equal("Pending",JsonDocument.Parse(pending.Body).RootElement.GetProperty("status").GetString());
        var claimed=await Invoke(app,"GET","/api/reader/history-jobs/next",null,null);Assert.Equal(200,claimed.Status);
        Assert.Equal(id,JsonDocument.Parse(claimed.Body).RootElement.GetProperty("jobId").GetString());
        var cancelled=await Invoke(app,"DELETE","/api/history/jobs/{id:guid}",null,token,id);Assert.Equal(200,cancelled.Status);
        var cancelledJson=JsonDocument.Parse(cancelled.Body).RootElement;Assert.Equal("Cancelled",cancelledJson.GetProperty("status").GetString());Assert.Equal(0,cancelledJson.GetProperty("read").GetInt32());
        var batch=Batch(scope,id,from,to);
        Assert.Equal(409,(await Invoke(app,"POST","/api/reader/history-jobs/{id:guid}/batches",batch,null,id)).Status);

        var next=await Invoke(app,"POST","/api/history/jobs",create,token);var nextId=JsonDocument.Parse(next.Body).RootElement.GetProperty("jobId").GetString()!;
        var nextClaim=await Invoke(app,"GET","/api/reader/history-jobs/next",null,null);
        var claimedRange=JsonDocument.Parse(nextClaim.Body).RootElement;
        // Reader echoes the server's persisted microsecond range, never the original browser ticks.
        batch=Batch(scope,nextId,claimedRange.GetProperty("fromUtc").GetDateTimeOffset(),claimedRange.GetProperty("toUtc").GetDateTimeOffset());
        var completed=await Invoke(app,"POST","/api/reader/history-jobs/{id:guid}/batches",batch,null,nextId);
        Assert.Equal(200,completed.Status);var receipt=JsonSerializer.Deserialize<HistoryBatchReceiptV1>(completed.Body,JsonOptions)!;
        Assert.Equal(1,receipt.Read);Assert.Equal(1,receipt.Created);Assert.Equal(0,receipt.Failed);Assert.Equal(HistoryJobStatusV1.Completed,receipt.Status);
        var repeated=await Invoke(app,"POST","/api/reader/history-jobs/{id:guid}/batches",batch,null,nextId);
        Assert.Equal(200,repeated.Status);Assert.Equal(receipt,JsonSerializer.Deserialize<HistoryBatchReceiptV1>(repeated.Body,JsonOptions));
        var saved=await Invoke(app,"GET","/api/history/jobs/{id:guid}",null,token,nextId);Assert.Equal(200,saved.Status);
        Assert.Equal(1,JsonDocument.Parse(saved.Body).RootElement.GetProperty("created").GetInt32());
        Assert.Equal(200,(await Invoke(app,"GET","/api/history/jobs",null,token,query:$"?accountId={scope.AccountId}&conversationId={scope.ConversationId}")).Status);
        Assert.Equal(403,(await Invoke(app,"GET","/api/history/jobs",null,token,query:$"?accountId={other.AccountId}&conversationId={other.ConversationId}")).Status);
        await using var totals=db.CreateCommand("SELECT count(*) FROM copilot.chat_messages WHERE account_id=@account AND conversation_id=@conversation;");
        totals.Parameters.AddWithValue("account",scope.AccountId);totals.Parameters.AddWithValue("conversation",scope.ConversationId);Assert.Equal(1L,await totals.ExecuteScalarAsync());

        var mixedJob=await Invoke(app,"POST","/api/history/jobs",create,token);
        var mixedId=JsonDocument.Parse(mixedJob.Body).RootElement.GetProperty("jobId").GetString()!;
        var mixedClaim=JsonDocument.Parse((await Invoke(app,"GET","/api/reader/history-jobs/next",null,null)).Body).RootElement;
        var mixed=Batch(scope,mixedId,mixedClaim.GetProperty("fromUtc").GetDateTimeOffset(),mixedClaim.GetProperty("toUtc").GetDateTimeOffset());
        var existing=batch.Items![0];
        mixed=mixed with{Items=[existing,existing with{SourceIdentity=existing.SourceIdentity! with{LocalId=null,SortSeq=null}},existing with{Text=null}]};
        var partial=await Invoke(app,"POST","/api/reader/history-jobs/{id:guid}/batches",mixed,null,mixedId);
        Assert.Equal(200,partial.Status);var partialReceipt=JsonSerializer.Deserialize<HistoryBatchReceiptV1>(partial.Body,JsonOptions)!;
        Assert.Equal(HistoryJobStatusV1.Partial,partialReceipt.Status);Assert.Equal(3,partialReceipt.Read);
        Assert.Equal(1,partialReceipt.Matched);Assert.Equal(0,partialReceipt.Created);Assert.Equal(1,partialReceipt.NeedsReview);Assert.Equal(1,partialReceipt.Failed);
    }

    [V6SessionsPostgresFact]
    public async Task LocalBootstrapConsumesOneGrantAndRejectsWrongOriginsAndReplay()
    {
        await using var db=TestDatabase();await MigrationRunner.ApplyPendingAsync(db);
        var scope=await CreateScope(db);var grant=WebSessionServiceV1.NewToken();var temp=Path.Combine(Path.GetTempPath(),"copilot-synthetic-bootstrap-"+Guid.NewGuid()+".json");
        try{
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(new{tokenHash=WebSessionServiceV1.Hash(grant),expiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(2)}));
            await using var app=Application(db,scope.AccountExternalId,temp);
            Assert.Equal(403,(await Invoke(app,"POST","/api/session/bootstrap",new{grant},null,origin:"https://attacker.example")).Status);
            Assert.True(File.Exists(temp));
            Assert.Equal(401,(await Invoke(app,"POST","/api/session/bootstrap",new{grant=new string('0',64)},null)).Status);Assert.True(File.Exists(temp));
            var success=await Invoke(app,"POST","/api/session/bootstrap",new{grant},null);Assert.Equal(200,success.Status);
            Assert.Contains("httponly",success.SetCookie,StringComparison.OrdinalIgnoreCase);Assert.Contains("samesite=strict",success.SetCookie,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(grant,success.Body);Assert.False(File.Exists(temp));
            Assert.Equal(401,(await Invoke(app,"POST","/api/session/bootstrap",new{grant},null)).Status);
        }finally{File.Delete(temp);}
    }

    private static WebApplication Application(NpgsqlDataSource db,string externalAccount,string? bootstrapPath=null)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Authentication:WebBearerToken"]="synthetic-web-token",["Authentication:ReaderBearerToken"]="synthetic-reader-token",["Reader:AllowedAccountExternalId"]=externalAccount,["Web:BootstrapGrantPath"]=bootstrapPath});
        builder.Services.AddSingleton(db);builder.Services.AddOperationsModule();builder.Services.ConfigureHttpJsonOptions(options=>options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app=builder.Build();app.MapHistoryJobEndpointsV1(builder.Configuration);app.MapSessionEndpoints(builder.Configuration);return app;
    }
    private static async Task<(int Status,string Body,string SetCookie)> Invoke(WebApplication app,string method,string pattern,object? body,string? token,string? id=null,bool bodyCsrf=true,string origin="http://127.0.0.1:5080",string? query=null)
    {
        var endpoint=((IEndpointRouteBuilder)app).DataSources.SelectMany(source=>source.Endpoints).OfType<RouteEndpoint>().Single(endpoint=>endpoint.RoutePattern.RawText?.TrimEnd('/')==pattern.TrimEnd('/')&&endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var context=new DefaultHttpContext{RequestServices=app.Services};context.Connection.RemoteIpAddress=IPAddress.Loopback;context.Request.Scheme="http";context.Request.Host=new("127.0.0.1",5080);
        context.Request.Method=method;context.Request.Path=pattern;context.Request.Headers.Origin=origin;if(query is not null)context.Request.QueryString=new(query);
        if(token is not null){context.Request.Headers.Cookie=WebSessionServiceV1.CookieName+"="+token;if(bodyCsrf)context.Request.Headers[WebSessionServiceV1.CsrfHeader]=WebSessionServiceV1.Csrf(token);}
        else context.Request.Headers.Authorization="Bearer synthetic-reader-token";
        if(id is not null)context.Request.RouteValues["id"]=id;
        if(body is not null){var bytes=JsonSerializer.SerializeToUtf8Bytes(body,JsonOptions);context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());context.Request.ContentType="application/json";context.Request.ContentLength=bytes.Length;context.Request.Body=new MemoryStream(bytes);}
        context.Response.Body=new MemoryStream();await endpoint.RequestDelegate!(context);context.Response.Body.Position=0;
        return(context.Response.StatusCode,await new StreamReader(context.Response.Body).ReadToEndAsync(),context.Response.Headers.SetCookie.ToString());
    }
    private sealed record Scope(Guid AccountId,Guid ConversationId,string AccountExternalId,string ConversationExternalId);
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private static async Task<Scope> CreateScope(NpgsqlDataSource db)
    {
        await using var command=db.CreateCommand("""
            WITH a AS(INSERT INTO copilot.chat_accounts(provider,external_account_id) VALUES('local-wechat','synthetic-'||gen_random_uuid()::text) RETURNING id,external_account_id),
            c AS(INSERT INTO copilot.contacts(account_id,external_contact_id) SELECT id,'synthetic-contact-'||gen_random_uuid()::text FROM a RETURNING id,account_id,external_contact_id),
            v AS(INSERT INTO copilot.conversations(account_id,contact_id,external_conversation_id) SELECT account_id,id,external_contact_id FROM c RETURNING account_id,id,external_conversation_id),
            t AS(INSERT INTO copilot.monitored_conversation_targets(account_id,conversation_id) SELECT account_id,id FROM v)
            SELECT v.account_id,v.id,a.external_account_id,v.external_conversation_id FROM v JOIN a ON a.id=v.account_id;
            """);await using var r=await command.ExecuteReaderAsync();Assert.True(await r.ReadAsync());return new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3));
    }
    private static async Task<string> Session(NpgsqlDataSource db,Scope scope)
    {
        var token=WebSessionServiceV1.NewToken();await using var cmd=db.CreateCommand("INSERT INTO copilot.web_sessions(token_hash,device_name,all_imported,is_local,authorized_account_ids,authorized_conversation_ids,expires_at) VALUES(@hash,'synthetic',false,true,@accounts,@conversations,now()+interval '1 hour');");
        cmd.Parameters.AddWithValue("hash",WebSessionServiceV1.Hash(token));cmd.Parameters.AddWithValue("accounts",new[]{scope.AccountId});cmd.Parameters.AddWithValue("conversations",new[]{scope.ConversationId});await cmd.ExecuteNonQueryAsync();return token;
    }
    private static HistoryBatchV1 Batch(Scope scope,string id,DateTimeOffset from,DateTimeOffset to)=>new(){JobId=id,AccountExternalId=scope.AccountExternalId,ConversationExternalId=scope.ConversationExternalId,RequestedRange=new(){FromUtc=from,ToUtc=to},IsLastBatch=true,Items=[new(){Provider="local-wechat",SourceKind=SourceKindV1.HistoryBackfill,AccountExternalId=scope.AccountExternalId,ConversationExternalId=scope.ConversationExternalId,ConversationType="Direct",MessageType="Text",Text="synthetic integration fixture",RawSenderId="1",IdentityNamespace="synthetic-history",SourceOccurredAt=to.AddSeconds(-1),SourceIdentity=new(){LocalId="1",SortSeq="1",ServerId="0",ShardFingerprint="synthetic-shard",MessageTable="Msg_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]};
    private static NpgsqlDataSource TestDatabase(){var dsn=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES"));Assert.Contains("test",dsn.Database!,StringComparison.OrdinalIgnoreCase);return NpgsqlDataSource.Create(dsn.ConnectionString);}
}
