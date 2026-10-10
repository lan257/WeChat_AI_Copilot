namespace WeChatAiCopilot.Api.Modules.Agent.Application;

/// <summary>
/// Server-owned list of persona preset identifiers. Requests select an ID only;
/// they cannot submit an instruction, prompt, or file path.
/// </summary>
public interface IAgentPresetCatalogV1
{
    bool IsConfigured { get; }
    bool Contains(string presetId);
    IReadOnlyList<AgentPersonaPresetSummaryV1> GetAvailablePersonas();
    bool TryGetInstruction(string presetId, out string instruction);
}

public sealed record AgentPersonaPresetSummaryV1(string PersonaPresetId, string Label);
