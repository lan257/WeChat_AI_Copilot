using System.Diagnostics;
using System.Collections.Concurrent;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

// Execution metadata only: never captures prompts, output, or conversation text.
public sealed class AgentTaskTelemetryV6
{
    public static readonly AsyncLocal<AgentTaskTelemetryV6?> Current = new();
    public string Phase { get; private set; } = "Preparing";
    private readonly ConcurrentDictionary<string, long> _milliseconds = new();
    public void SetPhase(string phase) => Phase = phase;
    public void Add(string name, long milliseconds) => _milliseconds.AddOrUpdate(name, Math.Max(0,milliseconds), (_, prior) => prior + Math.Max(0,milliseconds));
    public Dictionary<string,long> Snapshot() => new(_milliseconds);
    public IDisposable Measure(string name, string phase)
    {
        SetPhase(phase);
        return new Measurement(this,name);
    }
    private sealed class Measurement(AgentTaskTelemetryV6 owner,string name) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => owner.Add(name,(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
    }
}
