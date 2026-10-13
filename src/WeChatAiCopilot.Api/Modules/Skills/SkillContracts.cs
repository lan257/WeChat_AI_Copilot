using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Skills;

public sealed record SkillWriteRequest(
    string Type, string Name, string Description, string Instruction, string[] Stages,
    string ScopeType, Guid? AccountId = null, Guid? ConversationId = null,
    bool Enabled = true, string Origin = "Manual", long? ExpectedRevision = null);
public sealed record SkillDto(string SkillId, string Type, string Name, string Description,
    string Instruction, string[] Stages, string ScopeType, Guid? AccountId, Guid? ConversationId,
    bool Enabled, bool BuiltIn, string Origin, long Revision, string ApprovalStatus,
    DateTimeOffset? ApprovedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool Deleted = false);
public sealed record SkillDraftRequest(string Type, string Idea, string ScopeType,
    Guid? AccountId = null, Guid? ConversationId = null);
public sealed record SkillApproveRequest(long ExpectedRevision, bool Enabled = true);
public sealed record SkillRestoreRequest(long Revision, long ExpectedRevision);
public sealed record SkillSelectionRequest(string PersonaSkillId);
public sealed record SkillSelection(string PersonaSkillId, string RevisionDigest);
public sealed record SkillResolution(string PersonaId, string PersonaName, string PersonaInstruction,
    long PersonaRevision, IReadOnlyDictionary<string, IReadOnlyList<string>> WorkflowInstructions,
    IReadOnlyDictionary<string, long> WorkflowRevisions, string RevisionDigest)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> WorkflowStageSkillIds { get; init; }
        = new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>());
}
public sealed record SkillStageRun(string SkillId, long Revision, string Stage, DateTimeOffset OccurredAt, string RevisionDigest);
public sealed class SkillException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Only trusted code chooses stages and scope. Instructions are inert, untrusted content.</summary>
public static class SkillPolicy
{
    public static readonly string[] Stages = ["message", "context", "reply", "review", "consult", "memory"];
    public static SkillWriteRequest Validate(SkillWriteRequest request, AuthorizedContextScopeV1 scope)
    {
        var type = Canonical(request.Type, ["Persona", "Workflow"]);
        var scopeType = Canonical(request.ScopeType, ["AllAuthorizedAccounts", "Account", "Conversation"]);
        var origin = Canonical(request.Origin, ["Manual", "AiAssisted"]);
        var name = request.Name?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";
        var instruction = request.Instruction?.Trim() ?? "";
        if (name.Length is < 1 or > 34 || description.Length > 100 || instruction.Length is < 8 or > 2000)
            throw new SkillException("SKILL_TEXT_INVALID");
        if (request.Stages is null || request.Stages.Length is < 1 or > 6
            || request.Stages.Any(s => !Stages.Contains(s)) || request.Stages.Distinct().Count() != request.Stages.Length
            || (type == "Persona" && !request.Stages.Contains("reply")))
            throw new SkillException("SKILL_STAGES_INVALID");
        if (scope.AccountId == Guid.Empty || scope.ConversationId == Guid.Empty)
            throw new SkillException("SKILL_SCOPE_INVALID", 403);
        if (scopeType == "AllAuthorizedAccounts" && (request.AccountId is not null || request.ConversationId is not null)
            || scopeType == "Account" && (request.AccountId != scope.AccountId || request.ConversationId is not null)
            || scopeType == "Conversation" && (request.AccountId != scope.AccountId || request.ConversationId != scope.ConversationId))
            throw new SkillException("SKILL_SCOPE_INVALID", 403);
        return request with { Type = type, Name = name, Description = description, Instruction = instruction,
            ScopeType = scopeType, Origin = origin, Stages = Stages.Where(request.Stages.Contains).ToArray() };
    }
    public static bool Applies(SkillDto skill, AuthorizedContextScopeV1 scope) =>
        skill.ScopeType == "AllAuthorizedAccounts" || skill.AccountId == scope.AccountId
        && (skill.ScopeType == "Account" || skill.ScopeType == "Conversation" && skill.ConversationId == scope.ConversationId);
    public static bool Available(SkillDto skill, AuthorizedContextScopeV1 scope) =>
        Applies(skill, scope) && skill.Enabled && !skill.Deleted && skill.ApprovalStatus == "Approved";
    public static SkillResolution Resolve(IEnumerable<SkillDto> skills, AuthorizedContextScopeV1 scope, string personaId)
    {
        var active = skills.Where(s => Available(s, scope)).OrderBy(s => s.SkillId, StringComparer.Ordinal).ToArray();
        var persona = active.SingleOrDefault(s => s.SkillId == personaId && s.Type == "Persona")
            ?? throw new SkillException("SKILL_PERSONA_UNAVAILABLE", 409);
        var workflows = active.Where(s => s.Type == "Workflow").ToArray();
        var revisions = new ReadOnlyDictionary<string, long>(workflows.ToDictionary(s => s.SkillId, s => s.Revision));
        var digestInput = string.Join("|", new[] { $"{persona.SkillId}:{persona.Revision}" }
            .Concat(workflows.Select(s => $"{s.SkillId}:{s.Revision}")));
        return new SkillResolution(persona.SkillId, persona.Name, persona.Instruction, persona.Revision,
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(Stages.ToDictionary(stage => stage, stage => (IReadOnlyList<string>)Array.AsReadOnly(workflows
                .Where(s => s.Stages.Contains(stage)).Select(s => $"[{s.Name}] {s.Instruction}").ToArray()))),
            revisions, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant())
        {
            WorkflowStageSkillIds = new ReadOnlyDictionary<string, IReadOnlyList<string>>(Stages.ToDictionary(stage => stage,
                stage => (IReadOnlyList<string>)Array.AsReadOnly(workflows.Where(s => s.Stages.Contains(stage)).Select(s => s.SkillId).ToArray())))
        };
    }
    private static string Canonical(string? input, string[] values) =>
        values.FirstOrDefault(v => string.Equals(v, input, StringComparison.OrdinalIgnoreCase))
        ?? throw new SkillException("SKILL_ENUM_INVALID");
}
