using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class HistoryJobEndpointsV1
{
    public sealed record CreateHistoryJobV1(Guid AccountId,Guid ConversationId,DateTimeOffset? FromUtc,DateTimeOffset? ToUtc,int MaximumRows=5000,bool FullLocalHistory=false);
    public sealed record HistoryFailureV1(string ErrorCode);
    private static DateTimeOffset? RangeValue(NpgsqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetFieldValue<DateTimeOffset>(ordinal);
    public static IEndpointRouteBuilder MapHistoryJobEndpointsV1(this IEndpointRouteBuilder endpoints,IConfiguration config)
    {
        var web=endpoints.MapGroup("/api/history/jobs").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:WebBearerToken"));
        web.MapGet("",async(Guid accountId,Guid conversationId,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=context.Items[WebSessionServiceV1.IdentityItemKey] as WebSessionIdentityV1;
            if(identity is null||!identity.Allows(accountId,conversationId))return Results.StatusCode(403);
            var ids=new List<string>();
            await using(var cmd=db.CreateCommand("SELECT job_id FROM copilot.history_jobs WHERE account_id=@account AND conversation_id=@conversation AND requested_by_web ORDER BY started_at DESC LIMIT 20;")){
                cmd.Parameters.AddWithValue("account",accountId);cmd.Parameters.AddWithValue("conversation",conversationId);
                await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))ids.Add(reader.GetString(0));
            }
            var items=new List<object>();foreach(var id in ids){var job=await ReadJobAsync(db,id,identity,ct);if(job is not null)items.Add(job);}
            return Results.Ok(new{items});
        });
        web.MapPost("",async(CreateHistoryJobV1 request,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            var fullLocal=request.FullLocalHistory;
            DateTimeOffset? to=fullLocal?null:(request.ToUtc??DateTimeOffset.UtcNow);
            DateTimeOffset? from=fullLocal?null:(request.FromUtc??to!.Value.AddDays(-90));
            // The default row cap sizes a bounded window; a whole-store import needs the larger cap.
            var maximumRows=fullLocal&&request.MaximumRows==5000?200000:request.MaximumRows;
            if(maximumRows is <1 or >200000||(!fullLocal&&(from>to||to-from>TimeSpan.FromDays(90))))
                return Results.BadRequest(new{code="INVALID_HISTORY_RANGE",message=fullLocal?"全量导入单次最多 200,000 条文本记录。":"历史补录最多 90 天、5,000 条文本记录；要导入本机全部历史请勾选全量导入。"});
            var identity=context.Items[WebSessionServiceV1.IdentityItemKey] as WebSessionIdentityV1;
            if(identity is null||!identity.Allows(request.AccountId,request.ConversationId))return Results.StatusCode(403);
            await using(var check=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM copilot.conversations c JOIN copilot.monitored_conversation_targets t ON t.account_id=c.account_id AND t.conversation_id=c.id JOIN copilot.chat_accounts a ON a.id=c.account_id WHERE c.id=@conversation AND c.account_id=@account AND c.conversation_type='Direct' AND a.external_account_id=@external);") ){
                check.Parameters.AddWithValue("account",request.AccountId);check.Parameters.AddWithValue("conversation",request.ConversationId);check.Parameters.AddWithValue("external",config["Reader:AllowedAccountExternalId"]??"");
                if(!(bool)(await check.ExecuteScalarAsync(ct)??false))return Results.Conflict(new{code="READER_TARGET_NOT_AUTHORIZED",message="先授权监听当前账号的一对一会话，再同步历史消息。"});}
            var id=Guid.NewGuid().ToString("D");await using var cmd=db.CreateCommand("INSERT INTO copilot.history_jobs(job_id,account_id,conversation_id,requested_from_utc,requested_to_utc,status,maximum_rows,requested_by_web) VALUES(@id,@account,@conversation,@from,@to,'Pending',@max,true);");
            cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("account",request.AccountId);cmd.Parameters.AddWithValue("conversation",request.ConversationId);
            cmd.Parameters.Add(new("from",NpgsqlTypes.NpgsqlDbType.TimestampTz){Value=(object?)from??DBNull.Value});
            cmd.Parameters.Add(new("to",NpgsqlTypes.NpgsqlDbType.TimestampTz){Value=(object?)to??DBNull.Value});
            cmd.Parameters.AddWithValue("max",maximumRows);await cmd.ExecuteNonQueryAsync(ct);
            return Results.Accepted("/api/history/jobs/"+id,await ReadJobAsync(db,id,identity,ct));
        });
        web.MapGet("/{id:guid}",async(Guid id,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=context.Items[WebSessionServiceV1.IdentityItemKey] as WebSessionIdentityV1;if(identity is null)return Results.Unauthorized();
            var job=await ReadJobAsync(db,id.ToString("D"),identity,ct);return job is null?Results.NotFound():Results.Ok(job);
        });
        web.MapDelete("/{id:guid}",async(Guid id,HttpContext context,NpgsqlDataSource db,CancellationToken ct)=>{
            var identity=context.Items[WebSessionServiceV1.IdentityItemKey] as WebSessionIdentityV1;if(identity is null)return Results.Unauthorized();
            if(await ReadJobAsync(db,id.ToString("D"),identity,ct)is null)return Results.NotFound();
            await using var connection=await db.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
            await JobLockAsync(connection,tx,id.ToString("D"),ct);
            await using var cmd=new NpgsqlCommand("UPDATE copilot.history_jobs SET status='Cancelled',completed_at=now(),updated_at=now() WHERE job_id=@id AND requested_by_web AND status IN('Pending','Running');",connection,tx);cmd.Parameters.AddWithValue("id",id.ToString("D"));await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
            return Results.Ok(await ReadJobAsync(db,id.ToString("D"),identity,ct));
        });
        var reader=endpoints.MapGroup("/api/reader/history-jobs").AddEndpointFilter(new BearerTokenEndpointFilter(config,"Authentication:ReaderBearerToken"));
        reader.MapGet("/next",async(NpgsqlDataSource db,CancellationToken ct)=>{
            await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
            await using(var expire=new NpgsqlCommand("UPDATE copilot.history_jobs SET status='Failed',error_code='READER_JOB_LEASE_EXPIRED',updated_at=now(),completed_at=now() WHERE requested_by_web AND status='Running' AND updated_at<now()-interval '10 minutes';",conn,tx))await expire.ExecuteNonQueryAsync(ct);
            // Every accepted batch refreshes updated_at, so a reader that is alive but never finishes
            // would renew its lease forever and hold the single claim slot for all other conversations.
            await using(var deadline=new NpgsqlCommand("UPDATE copilot.history_jobs SET status='Failed',error_code='READER_JOB_DEADLINE_EXCEEDED',updated_at=now(),completed_at=now() WHERE requested_by_web AND status='Running' AND started_at<now()-CASE WHEN requested_from_utc IS NULL THEN interval '8 hours' ELSE interval '60 minutes' END;",conn,tx))await deadline.ExecuteNonQueryAsync(ct);
            await using var cmd=new NpgsqlCommand("""
                WITH selected AS(SELECT h.account_id,h.conversation_id,h.job_id FROM copilot.history_jobs h
                JOIN copilot.chat_accounts a ON a.id=h.account_id
                JOIN copilot.monitored_conversation_targets t ON t.account_id=h.account_id AND t.conversation_id=h.conversation_id
                WHERE h.requested_by_web AND h.status='Pending' AND a.external_account_id=@account ORDER BY h.started_at LIMIT 1 FOR UPDATE OF h SKIP LOCKED)
                UPDATE copilot.history_jobs h SET status='Running',claimed_at=now(),updated_at=now() FROM selected s,copilot.chat_accounts a,copilot.conversations c
                WHERE h.account_id=s.account_id AND h.conversation_id=s.conversation_id AND h.job_id=s.job_id AND a.id=h.account_id AND c.id=h.conversation_id
                RETURNING h.job_id,h.account_id,h.conversation_id,a.external_account_id,c.external_conversation_id,h.requested_from_utc,h.requested_to_utc,h.maximum_rows;
                """,conn,tx);cmd.Parameters.AddWithValue("account",config["Reader:AllowedAccountExternalId"]??"");
            string? jobId=null,accountExternalId=null,conversationExternalId=null;Guid scopeAccount=default,scopeConversation=default;int maximumRows=0;DateTimeOffset? fromUtc=null,toUtc=null;
            await using(var r=await cmd.ExecuteReaderAsync(ct)){if(await r.ReadAsync(ct)){jobId=r.GetString(0);scopeAccount=r.GetGuid(1);scopeConversation=r.GetGuid(2);accountExternalId=r.GetString(3);conversationExternalId=r.GetString(4);fromUtc=RangeValue(r,5);toUtc=RangeValue(r,6);maximumRows=r.GetInt32(7);}}
            var resumeCursors=new List<object>();
            if(jobId is not null)await using(var cursors=new NpgsqlCommand("SELECT message_table,last_local_id FROM copilot.history_scan_checkpoints WHERE account_id=@a AND conversation_id=@c AND window_key=@w ORDER BY message_table",conn,tx))
            {
                cursors.Parameters.AddWithValue("a",scopeAccount);cursors.Parameters.AddWithValue("c",scopeConversation);cursors.Parameters.AddWithValue("w",HistoryScanWindowV1.Key(fromUtc,toUtc));
                await using var reader=await cursors.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))resumeCursors.Add(new{messageTable=reader.GetString(0),lastLocalId=reader.GetInt64(1)});
            }
            object? job=jobId is null?null:new{jobId,accountExternalId,conversationExternalId,fromUtc,toUtc,maximumRows,fullLocalHistory=fromUtc is null,resumeCursors,batchSize=100};
            await tx.CommitAsync(ct);return job is null?Results.NoContent():Results.Ok(job);
        });
        reader.MapGet("/{id:guid}",async(Guid id,NpgsqlDataSource db,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("SELECT h.status FROM copilot.history_jobs h JOIN copilot.chat_accounts a ON a.id=h.account_id WHERE h.job_id=@id AND h.requested_by_web AND a.external_account_id=@account;");cmd.Parameters.AddWithValue("id",id.ToString("D"));cmd.Parameters.AddWithValue("account",config["Reader:AllowedAccountExternalId"]??"");var state=await cmd.ExecuteScalarAsync(ct) as string;return state is null?Results.NotFound():Results.Ok(new{status=state});
        });
        reader.MapPost("/{id:guid}/batches",async(Guid id,HistoryBatchV1 batch,NpgsqlDataSource db,CancellationToken ct)=>{
            if(batch.JobId!=id.ToString("D")||ContractValidatorV1.Validate(batch).Any(v=>!v.Path.StartsWith("items[",StringComparison.Ordinal)))return Results.BadRequest();
            await using var conn=await db.OpenConnectionAsync(ct);
            // A session advisory lock spans the ingestion service's transaction. Cancellation
            // acquires the same key, so a accepted batch cannot resurrect a cancelled job.
            await using var acquire=new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key,0));",conn);acquire.Parameters.AddWithValue("key","history:"+id);await acquire.ExecuteScalarAsync(ct);
            try{
                await using var check=new NpgsqlCommand("""
                    SELECT h.status,h.read_count,h.maximum_rows,h.requested_from_utc,h.requested_to_utc,
                    EXISTS(SELECT 1 FROM copilot.history_job_batches b WHERE b.account_id=h.account_id AND b.conversation_id=h.conversation_id AND b.job_id=h.job_id AND b.batch_key=@batchKey),
                    h.matched_count,h.created_count,h.needs_review_count,h.ignored_unsupported_count,h.failed_count,h.is_last_batch FROM copilot.history_jobs h
                    JOIN copilot.chat_accounts a ON a.id=h.account_id JOIN copilot.conversations c ON c.id=h.conversation_id
                    JOIN copilot.monitored_conversation_targets t ON t.account_id=h.account_id AND t.conversation_id=h.conversation_id
                    WHERE h.job_id=@id AND h.requested_by_web AND a.external_account_id=@account AND a.external_account_id=@batchAccount
                    AND c.external_conversation_id=@conversation;
                    """,conn);check.Parameters.AddWithValue("id",id.ToString("D"));check.Parameters.AddWithValue("account",config["Reader:AllowedAccountExternalId"]??"");check.Parameters.AddWithValue("batchAccount",batch.AccountExternalId??"");check.Parameters.AddWithValue("conversation",batch.ConversationExternalId??"");
                // This is the existing ingestion ledger's exact content hash, not a new client supplied counter.
                check.Parameters.AddWithValue("batchKey",Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(batch))).ToLowerInvariant());
                await using(var r=await check.ExecuteReaderAsync(ct)){
                    if(!await r.ReadAsync(ct))return Results.StatusCode(403);
                    if(r.GetBoolean(5))return Results.Ok(new HistoryBatchReceiptV1{JobId=id.ToString("D"),Status=Enum.Parse<HistoryJobStatusV1>(r.GetString(0)),Read=r.GetInt32(1),Matched=r.GetInt32(6),Created=r.GetInt32(7),NeedsReview=r.GetInt32(8),IgnoredUnsupported=r.GetInt32(9),Failed=r.GetInt32(10),IsLastBatch=r.GetBoolean(11)});
                    if(r.GetString(0)!="Running")return Results.Conflict(new{code="HISTORY_JOB_NOT_RUNNING"});
                    if(r.GetInt32(1)+(batch.Items?.Count??0)>r.GetInt32(2)||batch.RequestedRange?.FromUtc!=RangeValue(r,3)||batch.RequestedRange?.ToUtc!=RangeValue(r,4))return Results.BadRequest();}
                var receipt=await new PostgresMessageIngestionService(db).IngestHistoryBatchAsync(batch,ct);return Results.Ok(receipt);
            }finally{await using var release=new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0));",conn);release.Parameters.AddWithValue("key","history:"+id);await release.ExecuteScalarAsync(CancellationToken.None);}
        });
        reader.MapPost("/{id:guid}/fail",async(Guid id,HistoryFailureV1 request,NpgsqlDataSource db,CancellationToken ct)=>{
            if(string.IsNullOrWhiteSpace(request.ErrorCode)||request.ErrorCode.Length>80||request.ErrorCode.Any(ch=>!(char.IsAsciiLetterOrDigit(ch)||ch=='_')))return Results.BadRequest();
            await using var cmd=db.CreateCommand("UPDATE copilot.history_jobs h SET status='Failed',error_code=@code,updated_at=now(),completed_at=now() FROM copilot.chat_accounts a WHERE a.id=h.account_id AND a.external_account_id=@account AND h.job_id=@id AND h.requested_by_web AND h.status='Running';");cmd.Parameters.AddWithValue("id",id.ToString("D"));cmd.Parameters.AddWithValue("code",request.ErrorCode);cmd.Parameters.AddWithValue("account",config["Reader:AllowedAccountExternalId"]??"");await cmd.ExecuteNonQueryAsync(ct);return Results.Ok(new{accepted=true});
        });
        return endpoints;
    }
    private static async Task JobLockAsync(NpgsqlConnection conn,NpgsqlTransaction tx,string id,CancellationToken ct){await using var cmd=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",conn,tx);cmd.Parameters.AddWithValue("key","history:"+id);await cmd.ExecuteScalarAsync(ct);}
    private static async Task<object?> ReadJobAsync(NpgsqlDataSource db,string id,WebSessionIdentityV1 identity,CancellationToken ct){
        await using var cmd=db.CreateCommand("SELECT account_id,conversation_id,status,read_count,matched_count,created_count,needs_review_count,ignored_unsupported_count,failed_count,requested_from_utc,requested_to_utc,maximum_rows,error_code,started_at,updated_at FROM copilot.history_jobs WHERE job_id=@id AND requested_by_web;");cmd.Parameters.AddWithValue("id",id);
        Guid accountId=default,conversationId=default;string? status=null,errorCode=null;
        int read=0,matched=0,created=0,needsReview=0,ignoredUnsupported=0,failed=0,maximumRows=0;
        DateTimeOffset? rangeFrom=null,rangeTo=null;DateTimeOffset startedAt=default,updatedAt=default;
        await using(var r=await cmd.ExecuteReaderAsync(ct)){
            if(!await r.ReadAsync(ct)||!identity.Allows(r.GetGuid(0),r.GetGuid(1)))return null;
            accountId=r.GetGuid(0);conversationId=r.GetGuid(1);status=r.GetString(2);read=r.GetInt32(3);matched=r.GetInt32(4);created=r.GetInt32(5);
            needsReview=r.GetInt32(6);ignoredUnsupported=r.GetInt32(7);failed=r.GetInt32(8);rangeFrom=RangeValue(r,9);rangeTo=RangeValue(r,10);
            maximumRows=r.GetInt32(11);errorCode=r.IsDBNull(12)?null:r.GetString(12);startedAt=r.GetFieldValue<DateTimeOffset>(13);updatedAt=r.GetFieldValue<DateTimeOffset>(14);
        }
        // A resumed import shows the rows its earlier attempts already persisted, not just this job's tally.
        long resumedRows=0;
        await using(var checkpoint=db.CreateCommand("SELECT COALESCE(sum(scanned_rows),0) FROM copilot.history_scan_checkpoints WHERE account_id=@account AND conversation_id=@conversation AND window_key=@window;")){
            checkpoint.Parameters.AddWithValue("account",accountId);checkpoint.Parameters.AddWithValue("conversation",conversationId);checkpoint.Parameters.AddWithValue("window",HistoryScanWindowV1.Key(rangeFrom,rangeTo));
            resumedRows=(long)(await checkpoint.ExecuteScalarAsync(ct)??0L);
        }
        return new{jobId=id,accountId,conversationId,status,read,matched,created,needsReview,ignoredUnsupported,failed,
            rangeFromUtc=rangeFrom,rangeToUtc=rangeTo,maximumRows,fullLocalHistory=rangeFrom is null,resumedScannedRows=resumedRows,
            coverage="Unknown",errorCode,startedAtUtc=startedAt,updatedAtUtc=updatedAt};
    }
}
