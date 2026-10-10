using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Contracts;

public enum AgentTaskTypeV1
{
    ReplyCandidates,
    FreeConsultation
}

public enum AgentRunStatusV1
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum AgentReadCapabilityV1
{
    GetRecentMessages,
    SearchChatHistory,
    GetContactMemory,
    GetMessageEvidence
}

public sealed record AgentTaskV1
{
    public AgentTaskTypeV1 TaskType { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? PersonaPresetId { get; init; }
    public string? PreviousBatchId { get; init; }
    public string? ThreadId { get; init; }
    public string? UserInput { get; init; }
    public string? ContextRevision { get; init; }
}

/// <summary>
/// A limited projection of an already-authorized M3 context bundle. M4 does not
/// retrieve messages or memories itself; an M3 adapter supplies this snapshot.
/// </summary>
public sealed record AgentContextSnapshotV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? ContextRevision { get; init; }
    public PerspectiveStateV1 PerspectiveState { get; init; } = PerspectiveStateV1.Unresolved;
    public long CalibrationVersion { get; init; }
    public ProfileStatusV1 ProfileStatus { get; init; } = ProfileStatusV1.WaitingForConfirmation;
    public int UnprofiledTextCharacters { get; init; }
    public int RecentMessageLimit { get; init; } = 30;
    public bool IncludeMemory { get; init; } = true;
    public IReadOnlyList<AgentContextMessageV1>? RecentMessages { get; init; } = [];
    public IReadOnlyList<AgentContextProfileV1> ParticipantProfiles { get; init; } = [];
    public IReadOnlyList<AgentContextMemoryV1> MemoryItems { get; init; } = [];
    public IReadOnlyList<string> Omissions { get; init; } = [];
}

public sealed record AgentContextMessageV1
{
    public string? MessageId { get; init; }
    public string? Text { get; init; }
    public string? MessageType { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
    public MessageDirectionV1 Direction { get; init; } = MessageDirectionV1.Unknown;
    public DirectionEvidenceV1 DirectionEvidence { get; init; } = DirectionEvidenceV1.Unresolved;
}

public sealed record AgentContextProfileV1
{
    public string? IdentityNamespace { get; init; }
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public Guid SubjectId { get; init; }
    public ProfileStatusV1 Status { get; init; }
    public long CalibrationVersion { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> EvidenceMessageIds { get; init; } = [];
}

public sealed record AgentContextMemoryV1
{
    public string? MemoryId { get; init; }
    public string? IdentityNamespace { get; init; }
    public ParticipantSubjectTypeV1 SubjectType { get; init; }
    public Guid SubjectId { get; init; }
    public MemoryOriginV1 Origin { get; init; }
    public MemoryItemStatusV1 Status { get; init; } = MemoryItemStatusV1.Active;
    public long? CalibrationVersion { get; init; }
    public string? Category { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string> SourceMessageIds { get; init; } = [];
}

public sealed record AgentConsultationHistoryTurnV1
{
    public string? ThreadId { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? UserInput { get; init; }
    public string? Answer { get; init; }
    public IReadOnlyList<string> EvidenceMessageIds { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// Immutable binding for one consultation thread. Repositories must preserve
/// AccountId and ConversationId for the lifetime of the thread.
/// </summary>
public sealed record AgentConsultationThreadV1
{
    public string? ThreadId { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record AgentSuggestionV1
{
    public string? Id { get; init; }
    public string? Text { get; init; }
}

public sealed record AgentSuggestionDraftV1
{
    public string? Text { get; init; }
}

public sealed record SuggestionBatchV1
{
    public string? RunId { get; init; }
    public AgentTaskTypeV1 TaskType { get; init; } = AgentTaskTypeV1.ReplyCandidates;
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? PersonaPresetId { get; init; }
    public string? ContextRevision { get; init; }
    public string? BatchId { get; init; }
    public IReadOnlyList<AgentSuggestionV1>? Suggestions { get; init; }
    public AgentRunStatusV1 Status { get; init; } = AgentRunStatusV1.Completed;
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record ConsultationTurnV1
{
    public string? ThreadId { get; init; }
    public string? RunId { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? UserInput { get; init; }
    public string? Answer { get; init; }
    public IReadOnlyList<string> EvidenceMessageIds { get; init; } = [];
    public AgentRunStatusV1 Status { get; init; } = AgentRunStatusV1.Completed;
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// Execution limits captured at run start. These are configured limits, not
/// claims about actual token or tool usage.
/// </summary>
public sealed record AgentRunBudgetMetadataV1
{
    public long? MaxDurationMilliseconds { get; init; }
    public int? MaxContextCharacters { get; init; }
    public int? MaxToolCalls { get; init; }
}

/// <summary>
/// Minimal run audit metadata. It deliberately excludes user input, prompts,
/// chat text, model output, and raw exception details.
/// </summary>
public sealed record AgentRunV1
{
    public string? RunId { get; init; }
    public AgentTaskTypeV1 TaskType { get; init; }
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string? RuntimeName { get; init; }
    public string? SkillVersion { get; init; }
    public string? ContextRevision { get; init; }
    public AgentRunStatusV1 Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string? ErrorCode { get; init; }
    public AgentRunBudgetMetadataV1? BudgetMetadata { get; init; }
}

/// <summary>
/// An already-authorized, scope-bound request passed to a runtime. It contains
/// no credentials, database handles, filesystem paths, or arbitrary commands.
/// </summary>
public sealed record ScopedAgentRequestV1
{
    public AgentTaskV1? Task { get; init; }
    public AgentContextSnapshotV1? Context { get; init; }
    public AgentConsultationThreadV1? BoundThread { get; init; }
    public IContextToolSessionV1? ContextToolSession { get; init; }
    public IReadOnlyList<AgentConsultationHistoryTurnV1> ConsultationHistory { get; init; } = [];
    public bool ConsultationHistoryTruncated { get; init; }
    public string? PersonaInstruction { get; init; }
    public string? SkillVersion { get; init; }
    public IReadOnlyList<AgentReadCapabilityV1> AllowedReadCapabilities { get; init; } = [];
    public AgentExecutionBudgetV1? Budget { get; init; }
}

/// <summary>
/// Limits are supplied by the coordinator/configuration. M4 does not invent
/// deployment-specific thresholds here.
/// </summary>
public sealed record AgentExecutionBudgetV1
{
    public TimeSpan? MaxDuration { get; init; }
    public int? MaxContextCharacters { get; init; }
    public int? MaxToolCalls { get; init; }
}

/// <summary>
/// Runtime output contains content only. The coordinator assigns persisted IDs
/// and copies the authorized task scope into SuggestionBatchV1/ConsultationTurnV1.
/// </summary>
public sealed record AgentRuntimeResultV1
{
    public string RuntimeName { get; init; } = "unknown";
    public AgentRunStatusV1 Status { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<AgentSuggestionDraftV1> Suggestions { get; init; } = [];
    public string? Answer { get; init; }
    public IReadOnlyList<string> EvidenceMessageIds { get; init; } = [];
    /// <summary>Server-verified IDs returned by the bound M3 context tool session.</summary>
    public IReadOnlyList<string> VerifiedEvidenceMessageIds { get; init; } = [];

    public static AgentRuntimeResultV1 Failed(string runtimeName, string errorCode) => new()
    {
        RuntimeName = runtimeName,
        Status = AgentRunStatusV1.Failed,
        ErrorCode = errorCode
    };

    public static AgentRuntimeResultV1 Cancelled(string runtimeName) => new()
    {
        RuntimeName = runtimeName,
        Status = AgentRunStatusV1.Cancelled,
        ErrorCode = "CANCELLED"
    };

    public static AgentRuntimeResultV1 CompletedSuggestions(
        string runtimeName,
        IReadOnlyList<AgentSuggestionDraftV1> suggestions) => new()
    {
        RuntimeName = runtimeName,
        Status = AgentRunStatusV1.Completed,
        Suggestions = suggestions
    };

    public static AgentRuntimeResultV1 CompletedConsultation(
        string runtimeName,
        string answer,
        IReadOnlyList<string>? evidenceMessageIds = null,
        IReadOnlyList<string>? verifiedEvidenceMessageIds = null) => new()
    {
        RuntimeName = runtimeName,
        Status = AgentRunStatusV1.Completed,
        Answer = answer,
        EvidenceMessageIds = evidenceMessageIds ?? [],
        VerifiedEvidenceMessageIds = verifiedEvidenceMessageIds ?? []
    };
}
