using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Modules.Skills;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

public sealed record QuickModelSelectionV6(string Route,string? ModelId,bool IsFallback);
public sealed class V6RoutingPolicy(NpgsqlDataSource db,IConfiguration config)
{
    public async Task<QuickModelSelectionV6> ReadQuickAsync(CancellationToken ct)
    {
        await using var q=db.CreateCommand("SELECT preference_json::text FROM copilot.application_preferences WHERE preference_key='user'");
        var json=await q.ExecuteScalarAsync(ct) as string;
        var route=json is null?"codex":JsonSerializer.Deserialize<OperationsEndpointsV1.PreferencesV1>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})?.QuickRoute??"codex";
        var model=route=="fast"?config["Agent:ExecutionProfiles:Quick:ModelId"]:null;
        if(model is not null&&(model.Length>80||model.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('-' or '_' or '.'))))throw new AgentModuleNotReadyException("QUICK_MODEL_INVALID","Configured Quick model is invalid.");
        return new(route,string.IsNullOrWhiteSpace(model)?null:model,route=="fast"&&string.IsNullOrWhiteSpace(model));
    }
}

// Watches only explicit monitoring targets. Model work is opt-in via persisted AutoPrep.
// Skill message-stage rules are inert preferences; trusted application code alone schedules work.
// The cursor is persisted because an in-memory one meant a restart saw every revision for the first
// time, treated that as "no change", and silently dropped messages that arrived while the app was down.
public sealed class ConversationLifecycleV6(NpgsqlDataSource db,IServiceScopeFactory scopes,AgentTaskQueueV6 queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){try{await PollAsync(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception){/* Fault is retried; never log user payloads. */}await Task.Delay(2000,ct);}
    }
    private async Task PollAsync(CancellationToken ct)
    {
        var items=new List<(AuthorizedContextScopeV1 Scope,long Revision,bool Known,long SeenRevision,bool NeedsPrep)>();
        await using(var watch=db.CreateCommand("""SELECT c.account_id,c.id,c.message_set_revision,s.last_seen_revision,s.needs_prep FROM copilot.conversations c JOIN copilot.monitored_conversation_targets t ON t.account_id=c.account_id AND t.conversation_id=c.id LEFT JOIN copilot.conversation_prep_state s ON s.account_id=c.account_id AND s.conversation_id=c.id ORDER BY c.id LIMIT 2000"""))
        {await using var reader=await watch.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))items.Add((new(reader.GetGuid(0),reader.GetGuid(1)),reader.GetInt64(2),!reader.IsDBNull(3),reader.IsDBNull(3)?0:reader.GetInt64(3),!reader.IsDBNull(4)&&reader.GetBoolean(4)));}

        // A conversation enters the cursor at its own current revision: history recorded before monitoring
        // started was never pending, so first sighting must not queue one generation per watched contact.
        await using(var enter=db.CreateCommand("INSERT INTO copilot.conversation_prep_state(account_id,conversation_id,last_seen_revision) VALUES(@a,@c,@r) ON CONFLICT DO NOTHING"))
        foreach(var item in items.Where(x=>!x.Known)){enter.Parameters.Clear();enter.Parameters.AddWithValue("a",item.Scope.AccountId);enter.Parameters.AddWithValue("c",item.Scope.ConversationId);enter.Parameters.AddWithValue("r",item.Revision);await enter.ExecuteNonQueryAsync(ct);}

        var changed=items.Where(x=>x.Known&&x.SeenRevision!=x.Revision).ToArray();
        await using(var mark=db.CreateCommand("UPDATE copilot.conversation_prep_state SET last_seen_revision=@r,needs_prep=true,updated_at=now() WHERE account_id=@a AND conversation_id=@c"))
        foreach(var item in changed){mark.Parameters.Clear();mark.Parameters.AddWithValue("r",item.Revision);mark.Parameters.AddWithValue("a",item.Scope.AccountId);mark.Parameters.AddWithValue("c",item.Scope.ConversationId);await mark.ExecuteNonQueryAsync(ct);}

        bool autoPrep=false;
        await using(var preference=db.CreateCommand("SELECT preference_json::text FROM copilot.application_preferences WHERE preference_key='user'")){var json=await preference.ExecuteScalarAsync(ct)as string;if(json is not null)autoPrep=JsonSerializer.Deserialize<OperationsEndpointsV1.PreferencesV1>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})?.AutoPrep==true;}
        using var container=scopes.CreateScope();var services=container.ServiceProvider;var skills=services.GetRequiredService<SkillResolutionService>();
        foreach(var item in changed)
        {
            var snapshot=await skills.ResolveAsync(item.Scope,null,ct);
            await skills.RecordStageUseAsync(item.Scope,"message",snapshot,ct);
        }
        if(autoPrep)
        {
            foreach(var item in changed.Concat(items.Where(x=>x.NeedsPrep)).GroupBy(x=>$"{x.Scope.AccountId}:{x.Scope.ConversationId}").Select(group=>group.First()))
            {
                try
                {
                    await using(var busy=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM copilot.agent_tasks WHERE account_id=@a AND conversation_id=@c AND task_type='ReplyCandidates' AND status IN('Queued','Running','CancelRequested'))")){busy.Parameters.AddWithValue("a",item.Scope.AccountId);busy.Parameters.AddWithValue("c",item.Scope.ConversationId);if(await busy.ExecuteScalarAsync(ct)is true)continue;}
                    var selected=await skills.ResolveAsync(item.Scope,null,ct);
                    var fingerprint=await queue.FingerprintAsync(services,item.Scope,selected.PersonaId,ct);
                    await queue.EnqueueAsync(item.Scope,new(){TaskType="ReplyCandidates",PersonaPresetId=selected.PersonaId},"pregen:"+fingerprint,ct);
                    await using var done=db.CreateCommand("UPDATE copilot.conversation_prep_state SET needs_prep=false,updated_at=now() WHERE account_id=@a AND conversation_id=@c");
                    done.Parameters.AddWithValue("a",item.Scope.AccountId);done.Parameters.AddWithValue("c",item.Scope.ConversationId);await done.ExecuteNonQueryAsync(ct);
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                // One conversation that cannot be scheduled must not stop the rest; its flag stays set and
                // the content-derived idempotency key keeps the next poll from duplicating model work.
                catch(Exception){continue;}
            }
        }
        // AutoPrep is read again every poll, so switching it off drops whatever was only waiting for it.
        if(!autoPrep){await using var clear=db.CreateCommand("UPDATE copilot.conversation_prep_state SET needs_prep=false,updated_at=now() WHERE needs_prep");await clear.ExecuteNonQueryAsync(ct);}
        await using(var prune=db.CreateCommand("DELETE FROM copilot.conversation_prep_state s WHERE NOT EXISTS(SELECT 1 FROM copilot.monitored_conversation_targets t WHERE t.account_id=s.account_id AND t.conversation_id=s.conversation_id)"))await prune.ExecuteNonQueryAsync(ct);
    }
}
