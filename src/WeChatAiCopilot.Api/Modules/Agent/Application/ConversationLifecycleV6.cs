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
public sealed class ConversationLifecycleV6(NpgsqlDataSource db,IServiceScopeFactory scopes,AgentTaskQueueV6 queue) : BackgroundService
{
    private readonly Dictionary<AuthorizedContextScopeV1,long> _revisions=[];
    private readonly HashSet<AuthorizedContextScopeV1> _pending=[];
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){try{await PollAsync(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception){/* Fault is retried; never log user payloads. */}await Task.Delay(2000,ct);}
    }
    private async Task PollAsync(CancellationToken ct)
    {
        var items=new List<(AuthorizedContextScopeV1 Scope,long Revision)>();
        await using(var q=db.CreateCommand("SELECT c.account_id,c.id,c.message_set_revision FROM copilot.conversations c JOIN copilot.monitored_conversation_targets t ON t.account_id=c.account_id AND t.conversation_id=c.id ORDER BY c.id LIMIT 2000")){await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add((new(r.GetGuid(0),r.GetGuid(1)),r.GetInt64(2)));}
        bool autoPrep=false;
        await using(var q=db.CreateCommand("SELECT preference_json::text FROM copilot.application_preferences WHERE preference_key='user'")){var json=await q.ExecuteScalarAsync(ct)as string;if(json is not null)autoPrep=JsonSerializer.Deserialize<OperationsEndpointsV1.PreferencesV1>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})?.AutoPrep==true;}
        using var container=scopes.CreateScope();var services=container.ServiceProvider;var skills=services.GetRequiredService<SkillResolutionService>();
        foreach(var item in items){var changed=_revisions.TryGetValue(item.Scope,out var old)&&old!=item.Revision;_revisions[item.Scope]=item.Revision;
            if(changed){var snapshot=await skills.ResolveAsync(item.Scope,null,ct);await skills.RecordStageUseAsync(item.Scope,"message",snapshot,ct);_pending.Add(item.Scope);}
            if(!autoPrep||!_pending.Contains(item.Scope))continue;
            await using(var busy=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM copilot.agent_tasks WHERE account_id=@a AND conversation_id=@c AND task_type='ReplyCandidates' AND status IN('Queued','Running','CancelRequested'))")){busy.Parameters.AddWithValue("a",item.Scope.AccountId);busy.Parameters.AddWithValue("c",item.Scope.ConversationId);if(await busy.ExecuteScalarAsync(ct)is true)continue;}
            var selected=await skills.ResolveAsync(item.Scope,null,ct);var fingerprint=await queue.FingerprintAsync(services,item.Scope,selected.PersonaId,ct);
            await queue.EnqueueAsync(item.Scope,new(){TaskType="ReplyCandidates",PersonaPresetId=selected.PersonaId},"pregen:"+fingerprint,ct);_pending.Remove(item.Scope);
        }
        var active=items.Select(x=>x.Scope).ToHashSet();foreach(var old in _revisions.Keys.Where(x=>!active.Contains(x)).ToArray()){_revisions.Remove(old);_pending.Remove(old);}
        if(!autoPrep)_pending.Clear();
    }
}
