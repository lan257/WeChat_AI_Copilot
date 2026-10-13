using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Application;

/// <summary>
/// Coordinates one synchronous Agent task using an externally authorized,
/// immutable account/conversation scope. It owns no persistence or HTTP concerns.
/// </summary>
public sealed class AgentTaskCoordinatorV1 : IAgentTaskCoordinatorV1
{
    private readonly IContextServiceV1 _contextService;
    private readonly IAgentRuntime _runtime;
    private readonly IAgentRepositoryV1 _repository;
    private readonly IAgentPresetCatalogV1 _presetCatalog;
    private readonly IContextToolSessionFactoryV1? _contextToolSessionFactory;
    private readonly AgentTaskCoordinatorOptionsV1 _options;
    private readonly TimeProvider _timeProvider;

    public AgentTaskCoordinatorV1(
        IContextServiceV1 contextService,
        IAgentRuntime runtime,
        IAgentRepositoryV1 repository,
        IAgentPresetCatalogV1 presetCatalog,
        AgentTaskCoordinatorOptionsV1 options,
        TimeProvider? timeProvider = null,
        IContextToolSessionFactoryV1? contextToolSessionFactory = null)
    {
        _contextService = contextService ?? throw new ArgumentNullException(nameof(contextService));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _presetCatalog = presetCatalog ?? throw new ArgumentNullException(nameof(presetCatalog));
        _contextToolSessionFactory = contextToolSessionFactory;
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptions(options);
        _options = options with
        {
            AllowedReadCapabilities = options.AllowedReadCapabilities.ToArray(),
            ProfileModelIds = new Dictionary<AgentExecutionProfileIdV1, string>(options.ProfileModelIds)
        };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SuggestionBatchV1> CreateSuggestionsAsync(
        AgentTaskV1 task,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateAuthorizedScope(authorizedScope);
        EnsureTaskType(task, AgentTaskTypeV1.ReplyCandidates);
        EnsureTaskScope(task, authorizedScope);
        if (!_presetCatalog.TryGetInstruction(task.PersonaPresetId ?? string.Empty, out var personaInstruction))
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1("PERSONA_PRESET_UNAVAILABLE", "personaPresetId", "The requested persona is not available.")]);
        }

        var scopedTask = task with
        {
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            // The supplied value is deliberately discarded; the M3 bundle is authoritative.
            ContextRevision = "pending-context-revision"
        };
        var taskViolations = AgentContractValidatorV1.Validate(scopedTask);
        if (taskViolations.Count > 0)
        {
            throw new AgentTaskContractException(taskViolations);
        }

        var (runtimeTask, context, result, runId) = await ExecuteAsync(
            scopedTask,
            authorizedScope,
            boundThread: null,
            personaInstruction: personaInstruction,
            consultationHistory: [],
            consultationHistoryTruncated: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var batch = new SuggestionBatchV1
        {
            RunId = runId,
            TaskType = AgentTaskTypeV1.ReplyCandidates,
            ExecutionProfileId = runtimeTask.ExecutionProfileId,
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            PersonaPresetId = runtimeTask.PersonaPresetId,
            ContextRevision = context.ContextRevision,
            BatchId = NewId(),
            Suggestions = result.Suggestions.Select(suggestion => new AgentSuggestionV1
            {
                Id = NewId(),
                Text = suggestion.Text
            }).ToArray(),
            Status = AgentRunStatusV1.Completed,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        var outputViolations = AgentContractValidatorV1.Validate(batch, runtimeTask);
        if (outputViolations.Count > 0)
        {
            await FinishFailedRunAsync(authorizedScope, runId, outputViolations[0].Code).ConfigureAwait(false);
            throw new AgentTaskContractException(outputViolations, "The generated suggestion batch failed contract validation.");
        }

        return batch;
    }

    public async Task<ConsultationTurnV1> CreateConsultationTurnAsync(
        AgentTaskV1 task,
        AgentConsultationThreadV1 boundThread,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(boundThread);
        ValidateAuthorizedScope(authorizedScope);
        EnsureTaskType(task, AgentTaskTypeV1.FreeConsultation);
        EnsureTaskScope(task, authorizedScope);
        EnsureThreadScope(task, boundThread, authorizedScope);

        var scopedTask = task with
        {
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            // The supplied value is deliberately discarded; the M3 bundle is authoritative.
            ContextRevision = "pending-context-revision"
        };
        var taskViolations = AgentContractValidatorV1.Validate(scopedTask);
        if (taskViolations.Count > 0)
        {
            throw new AgentTaskContractException(taskViolations);
        }

        var threadViolations = AgentContractValidatorV1.ValidateThreadBinding(scopedTask, boundThread);
        if (threadViolations.Count > 0)
        {
            throw new AgentTaskContractException(threadViolations);
        }

        var (consultationHistory, consultationHistoryTruncated) = await LoadConsultationHistoryAsync(
            authorizedScope,
            boundThread,
            cancellationToken).ConfigureAwait(false);

        var (runtimeTask, context, result, runId) = await ExecuteAsync(
            scopedTask,
            authorizedScope,
            boundThread,
            personaInstruction: null,
            consultationHistory: consultationHistory,
            consultationHistoryTruncated: consultationHistoryTruncated,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var turn = new ConsultationTurnV1
        {
            ThreadId = boundThread.ThreadId,
            RunId = runId,
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            UserInput = runtimeTask.UserInput,
            Answer = result.Answer,
            EvidenceMessageIds = result.EvidenceMessageIds,
            Status = AgentRunStatusV1.Completed,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        var outputViolations = AgentContractValidatorV1.Validate(turn, runtimeTask, boundThread);
        if (outputViolations.Count > 0)
        {
            await FinishFailedRunAsync(authorizedScope, runId, outputViolations[0].Code).ConfigureAwait(false);
            throw new AgentTaskContractException(outputViolations, "The generated consultation turn failed contract validation.");
        }

        return turn;
    }

    private async Task<(IReadOnlyList<AgentConsultationHistoryTurnV1> Turns, bool IsTruncated)> LoadConsultationHistoryAsync(
        AuthorizedContextScopeV1 authorizedScope,
        AgentConsultationThreadV1 boundThread,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(boundThread.ThreadId, out var threadGuid))
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1("CONSULTATION_THREAD_INVALID_ID", "threadId", "The consultation thread ID is invalid.")]);
        }

        IReadOnlyList<ConsultationTurnV1> storedTurns;
        try
        {
            storedTurns = await _repository.GetRecentConsultationTurnsAsync(
                authorizedScope,
                threadGuid,
                checked(_options.MaxConsultationHistoryTurns + 1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AgentTaskExecutionException(
                AgentRunStatusV1.Failed,
                "CONSULTATION_HISTORY_UNAVAILABLE",
                "agent-history");
        }

        if (storedTurns is null)
        {
            throw new AgentTaskExecutionException(
                AgentRunStatusV1.Failed,
                "CONSULTATION_HISTORY_UNAVAILABLE",
                "agent-history");
        }

        if (storedTurns.Any(turn => turn is null))
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1(
                    "CONSULTATION_HISTORY_SCOPE_INVALID",
                    "consultationHistory",
                    "Stored consultation history failed scope or content validation.")]);
        }

        if (storedTurns.Count > _options.MaxConsultationHistoryTurns + 1)
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1(
                    "CONSULTATION_HISTORY_LIMIT_EXCEEDED",
                    "consultationHistory",
                    "The repository returned more history turns than the requested hard limit.")]);
        }

        var orderedTurns = storedTurns
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.RunId, StringComparer.Ordinal)
            .ToArray();

        foreach (var turn in orderedTurns)
        {
            if (turn is null
                || !string.Equals(turn.ThreadId, boundThread.ThreadId, StringComparison.Ordinal)
                || turn.AccountId != authorizedScope.AccountId
                || turn.ConversationId != authorizedScope.ConversationId
                || turn.Status != AgentRunStatusV1.Completed
                || string.IsNullOrWhiteSpace(turn.UserInput)
                || string.IsNullOrWhiteSpace(turn.Answer)
                || turn.CreatedAtUtc == default
                || turn.CreatedAtUtc.Offset != TimeSpan.Zero
                || turn.EvidenceMessageIds is null
                || turn.EvidenceMessageIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new AgentTaskContractException(
                    [new AgentContractViolationV1(
                        "CONSULTATION_HISTORY_SCOPE_INVALID",
                        "consultationHistory",
                        "Stored consultation history failed scope or content validation.")]);
            }
        }

        var selectedTurns = orderedTurns.TakeLast(_options.MaxConsultationHistoryTurns).ToArray();
        var boundedNewestFirst = new List<AgentConsultationHistoryTurnV1>();
        var remainingCharacters = Math.Min(
            _options.MaxConsultationHistoryCharacters,
            _options.Budget?.MaxContextCharacters ?? _options.MaxConsultationHistoryCharacters);
        var wasTruncated = selectedTurns.Length != orderedTurns.Length;

        foreach (var turn in selectedTurns.Reverse())
        {
            var historyTurn = new AgentConsultationHistoryTurnV1
            {
                ThreadId = turn.ThreadId,
                AccountId = turn.AccountId,
                ConversationId = turn.ConversationId,
                UserInput = turn.UserInput,
                Answer = turn.Answer,
                EvidenceMessageIds = turn.EvidenceMessageIds.Take(8).ToArray(),
                CreatedAtUtc = turn.CreatedAtUtc
            };
            if (turn.EvidenceMessageIds.Count > historyTurn.EvidenceMessageIds.Count)
            {
                wasTruncated = true;
            }

            var requiredCharacters = GetHistoryTurnCharacterCount(historyTurn);
            if (requiredCharacters > remainingCharacters)
            {
                var evidenceCharacterCount = historyTurn.EvidenceMessageIds.Sum(id => id.Length + 3);
                var fixedOverhead = 32 + evidenceCharacterCount;
                var textBudget = remainingCharacters - fixedOverhead;
                if (textBudget < 2)
                {
                    wasTruncated = true;
                    break;
                }

                var inputBudget = Math.Max(1, textBudget / 3);
                historyTurn = historyTurn with
                {
                    UserInput = TakeText(historyTurn.UserInput!, inputBudget),
                    Answer = TakeText(historyTurn.Answer!, Math.Max(1, textBudget - inputBudget))
                };
                requiredCharacters = GetHistoryTurnCharacterCount(historyTurn);
                wasTruncated = true;
            }

            if (requiredCharacters > remainingCharacters)
            {
                break;
            }

            boundedNewestFirst.Add(historyTurn);
            remainingCharacters -= requiredCharacters;
        }

        boundedNewestFirst.Reverse();
        return (boundedNewestFirst, wasTruncated);
    }

    private static int GetHistoryTurnCharacterCount(AgentConsultationHistoryTurnV1 turn) =>
        32
        + (turn.UserInput?.Length ?? 0)
        + (turn.Answer?.Length ?? 0)
        + turn.EvidenceMessageIds.Sum(id => id.Length + 3);

    private static string TakeText(string value, int maxUtf16Characters)
    {
        if (value.Length <= maxUtf16Characters)
        {
            return value;
        }

        var maxContentCharacters = Math.Max(0, maxUtf16Characters - 1);
        var builder = new System.Text.StringBuilder(maxContentCharacters + 1);
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxContentCharacters)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        builder.Append('…');
        return builder.ToString();
    }

    private async Task<(AgentTaskV1 Task, AgentContextSnapshotV1 Context, AgentRuntimeResultV1 Result, string RunId)> ExecuteAsync(
        AgentTaskV1 scopedTask,
        AuthorizedContextScopeV1 authorizedScope,
        AgentConsultationThreadV1? boundThread,
        string? personaInstruction,
        IReadOnlyList<AgentConsultationHistoryTurnV1> consultationHistory,
        bool consultationHistoryTruncated,
        CancellationToken cancellationToken)
    {
        var executionPolicy = ResolveExecutionPolicy(scopedTask.ExecutionProfileId);
        ContextBundleV1 contextBundle;
        try
        {
            contextBundle = await _contextService.BuildAsync(
                new ContextRequestV1
                {
                    AccountId = authorizedScope.AccountId,
                    ConversationId = authorizedScope.ConversationId,
                    RequestPurpose = scopedTask.TaskType.ToString(),
                    RecentMessageLimit = executionPolicy.RecentMessageLimit,
                    IncludeMemory = executionPolicy.IncludeMemory
                },
                authorizedScope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AgentTaskExecutionException(
                AgentRunStatusV1.Failed,
                "CONTEXT_UNAVAILABLE",
                "m3-context");
        }

        ValidateContextScope(contextBundle, authorizedScope);
        var context = MapContext(contextBundle, executionPolicy);
        var task = scopedTask with { ContextRevision = context.ContextRevision };
        var request = new ScopedAgentRequestV1
        {
            Task = task,
            ModelId = executionPolicy.ModelId,
            Context = context,
            BoundThread = boundThread,
            ContextToolSession = _contextToolSessionFactory?.Bind(authorizedScope),
            ConsultationHistory = consultationHistory,
            ConsultationHistoryTruncated = consultationHistoryTruncated,
            PersonaInstruction = personaInstruction,
            SkillVersion = _options.SkillVersion,
            AllowedReadCapabilities = executionPolicy.AllowedReadCapabilities,
            Budget = executionPolicy.Budget
        };

        var requestViolations = AgentContractValidatorV1.Validate(request);
        if (requestViolations.Count > 0)
        {
            throw new AgentTaskContractException(requestViolations, "The scoped runtime request failed contract validation.");
        }

        var runId = NewId();
        var runtimeName = SanitizeRuntimeName(_runtime.GetType().Name, "configured-runtime");
        var startedAt = _timeProvider.GetUtcNow();
        long? maxDurationMilliseconds = executionPolicy.Budget.MaxDuration is { } maxDuration
            ? (long?)checked((long)Math.Ceiling(maxDuration.TotalMilliseconds))
            : null;
        await _repository.StartAgentRunAsync(
            authorizedScope,
            new AgentRunV1
            {
                RunId = runId,
                TaskType = task.TaskType,
                ExecutionProfileId = task.ExecutionProfileId,
                AccountId = authorizedScope.AccountId,
                ConversationId = authorizedScope.ConversationId,
                RuntimeName = runtimeName,
                SkillVersion = _options.SkillVersion,
                ContextRevision = context.ContextRevision,
                Status = AgentRunStatusV1.Running,
                StartedAtUtc = startedAt,
                BudgetMetadata = new AgentRunBudgetMetadataV1
                {
                    ExecutionProfileId = task.ExecutionProfileId,
                    ModelId = executionPolicy.ModelId,
                    RecentMessageLimit = executionPolicy.RecentMessageLimit,
                    IncludeMemory = executionPolicy.IncludeMemory,
                    MaxDurationMilliseconds = maxDurationMilliseconds,
                    MaxContextCharacters = executionPolicy.Budget.MaxContextCharacters,
                    MaxToolCalls = executionPolicy.Budget.MaxToolCalls
                }
            },
            cancellationToken).ConfigureAwait(false);

        AgentRuntimeResultV1 result;
        try
        {
            result = await _runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishRunAsync(
                authorizedScope,
                runId,
                runtimeName,
                AgentRunStatusV1.Cancelled,
                "CANCELLED").ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await FinishRunAsync(
                authorizedScope,
                runId,
                runtimeName,
                AgentRunStatusV1.Failed,
                "RUNTIME_FAILURE").ConfigureAwait(false);
            throw new AgentTaskExecutionException(
                AgentRunStatusV1.Failed,
                "RUNTIME_FAILURE",
                runtimeName);
        }

        runtimeName = SanitizeRuntimeName(result?.RuntimeName, runtimeName);
        await _repository.UpdateAgentRunRuntimeNameAsync(
            authorizedScope,
            Guid.Parse(runId),
            runtimeName,
            CancellationToken.None).ConfigureAwait(false);

        var resultViolations = AgentContractValidatorV1.ValidateRuntimeResult(result, task, context);
        if (result is null || resultViolations.Count > 0)
        {
            var failureCode = result is null
                ? "RUNTIME_RESULT_REQUIRED"
                : SanitizeErrorCode(resultViolations[0].Code);
            await FinishRunAsync(
                authorizedScope,
                runId,
                runtimeName,
                AgentRunStatusV1.Failed,
                failureCode).ConfigureAwait(false);
            throw new AgentTaskContractException(resultViolations, "The Agent runtime result failed contract validation.");
        }

        if (result.Status != AgentRunStatusV1.Completed)
        {
            var terminalStatus = result.Status == AgentRunStatusV1.Cancelled
                ? AgentRunStatusV1.Cancelled
                : AgentRunStatusV1.Failed;
            var errorCode = SanitizeErrorCode(result.ErrorCode);
            await FinishRunAsync(
                authorizedScope,
                runId,
                runtimeName,
                terminalStatus,
                errorCode).ConfigureAwait(false);
            throw new AgentTaskExecutionException(result.Status, errorCode, runtimeName);
        }

        return (task, context, result, runId);
    }

    private async Task FinishFailedRunAsync(
        AuthorizedContextScopeV1 scope,
        string runId,
        string errorCode)
    {
        await _repository.FinishAgentRunAsync(
            scope,
            Guid.Parse(runId),
            AgentRunStatusV1.Failed,
            SanitizeErrorCode(errorCode),
            _timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FinishRunAsync(
        AuthorizedContextScopeV1 scope,
        string runId,
        string runtimeName,
        AgentRunStatusV1 status,
        string errorCode)
    {
        await _repository.UpdateAgentRunRuntimeNameAsync(
            scope,
            Guid.Parse(runId),
            SanitizeRuntimeName(runtimeName, _runtime.GetType().Name),
            CancellationToken.None).ConfigureAwait(false);
        await _repository.FinishAgentRunAsync(
            scope,
            Guid.Parse(runId),
            status,
            SanitizeErrorCode(errorCode),
            _timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string SanitizeErrorCode(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode)
            && errorCode.Length <= 80
            && errorCode.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                ? errorCode
                : "RUNTIME_FAILURE";

    private static string SanitizeRuntimeName(string? runtimeName, string fallback) =>
        !string.IsNullOrWhiteSpace(runtimeName)
            && runtimeName.Length <= 100
            && runtimeName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                ? runtimeName
                : fallback;

    private AgentContextSnapshotV1 MapContext(ContextBundleV1 bundle, AgentExecutionPolicyV1 executionPolicy)
    {
        var canUsePerspectiveBoundData = bundle.PerspectiveState == PerspectiveStateV1.Confirmed;
        ParticipantProfileSnapshotV1[] profiles = canUsePerspectiveBoundData
            ? bundle.ParticipantProfiles
                .Where(profile => profile.AccountId == bundle.AccountId
                    && profile.ConversationId == bundle.ConversationId
                    && profile.CalibrationVersion == bundle.CalibrationVersion
                    && profile.Status == ProfileStatusV1.Current
                    && !string.IsNullOrWhiteSpace(profile.IdentityNamespace)
                    && !string.IsNullOrWhiteSpace(profile.Summary))
                .ToArray()
            : Array.Empty<ParticipantProfileSnapshotV1>();

        var memories = bundle.MemoryItems
            .Where(memory => memory.AccountId == bundle.AccountId
                && (memory.ConversationId is null || memory.ConversationId == bundle.ConversationId)
                && memory.Status == MemoryItemStatusV1.Active
                && !string.IsNullOrWhiteSpace(memory.Text))
            .Where(memory => memory.Origin switch
            {
                MemoryOriginV1.UserEntered => true,
                MemoryOriginV1.MessageExtracted => canUsePerspectiveBoundData
                    && memory.ConversationId == bundle.ConversationId
                    && memory.CalibrationVersion == bundle.CalibrationVersion
                    && !string.IsNullOrWhiteSpace(memory.IdentityNamespace),
                _ => false
            })
            .ToArray();

        return new AgentContextSnapshotV1
        {
            AccountId = bundle.AccountId,
            ConversationId = bundle.ConversationId,
            ContextRevision = bundle.ContextRevision,
            PerspectiveState = bundle.PerspectiveState,
            CalibrationVersion = bundle.CalibrationVersion,
            ProfileStatus = bundle.ProfileStatus,
            UnprofiledTextCharacters = bundle.UnprofiledTextCharacters,
            RecentMessageLimit = executionPolicy.RecentMessageLimit,
            IncludeMemory = executionPolicy.IncludeMemory,
            RecentMessages = bundle.RecentMessages.Select(message => new AgentContextMessageV1
            {
                MessageId = message.MessageId.ToString("D"),
                Text = message.Text,
                MessageType = message.MessageType,
                OccurredAtUtc = message.OccurredAt?.ToUniversalTime(),
                Direction = message.Direction,
                DirectionEvidence = message.DirectionEvidence
            }).ToArray(),
            ParticipantProfiles = profiles.Select(profile => new AgentContextProfileV1
            {
                IdentityNamespace = profile.IdentityNamespace,
                SubjectType = profile.SubjectType,
                SubjectId = profile.SubjectId,
                Status = profile.Status,
                CalibrationVersion = profile.CalibrationVersion,
                Summary = profile.Summary,
                EvidenceMessageIds = profile.EvidenceMessageIds.Select(id => id.ToString("D")).ToArray()
            }).ToArray(),
            MemoryItems = memories.Select(memory => new AgentContextMemoryV1
            {
                MemoryId = memory.MemoryId.ToString("D"),
                IdentityNamespace = memory.IdentityNamespace,
                SubjectType = memory.SubjectType,
                SubjectId = memory.SubjectId,
                Origin = memory.Origin,
                Status = memory.Status,
                CalibrationVersion = memory.CalibrationVersion,
                Category = memory.Category,
                Text = memory.Text,
                SourceMessageIds = memory.SourceMessageIds.Select(id => id.ToString("D")).ToArray()
            }).ToArray(),
            Omissions = bundle.Omissions.Select(omission => omission.Code.ToString()).ToArray()
        };
    }

    private AgentExecutionPolicyV1 ResolveExecutionPolicy(AgentExecutionProfileIdV1 profileId)
    {
        var profile = AgentExecutionProfilesV1.Get(profileId);
        var configuredBudget = _options.Budget!;
        var profileContextLimit = profileId switch
        {
            AgentExecutionProfileIdV1.Quick => 8_000,
            AgentExecutionProfileIdV1.Balanced => 16_000,
            AgentExecutionProfileIdV1.Deep => 24_000,
            AgentExecutionProfileIdV1.Max => 32_000,
            _ => 16_000
        };
        int? contextLimit = configuredBudget.MaxContextCharacters is { } configuredContextLimit
            ? Math.Min(configuredContextLimit, profileContextLimit)
            : profileContextLimit;
        int? toolLimit = configuredBudget.MaxToolCalls is { } configuredToolLimit
            ? Math.Min(configuredToolLimit, profile.MaximumHistorySearches)
            : profile.MaximumHistorySearches;
        var allowedCapabilities = profile.MaximumHistorySearches == 0
            ? Array.Empty<AgentReadCapabilityV1>()
            : _options.AllowedReadCapabilities.ToArray();

        return new AgentExecutionPolicyV1(
            profile.RecentMessageLimit,
            profile.IncludeMemory,
            allowedCapabilities,
            new AgentExecutionBudgetV1
            {
                MaxDuration = configuredBudget.MaxDuration,
                MaxContextCharacters = contextLimit,
                MaxToolCalls = toolLimit
            },
            _options.ProfileModelIds.TryGetValue(profileId, out var modelId) ? modelId : null,
            profile.ReviewSuggestions);
    }

    private sealed record AgentExecutionPolicyV1(
        int RecentMessageLimit,
        bool IncludeMemory,
        IReadOnlyList<AgentReadCapabilityV1> AllowedReadCapabilities,
        AgentExecutionBudgetV1 Budget,
        string? ModelId,
        bool ReviewSuggestions);

    private static void ValidateAuthorizedScope(AuthorizedContextScopeV1 scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AccountId == Guid.Empty || scope.ConversationId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("A valid authorized account and conversation scope is required.");
        }
    }

    private static void EnsureTaskType(AgentTaskV1 task, AgentTaskTypeV1 expected)
    {
        if (task.TaskType != expected)
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1("INVALID_TASK_TYPE", "taskType", $"This operation requires {expected}.")]);
        }
    }

    private static void EnsureTaskScope(AgentTaskV1 task, AuthorizedContextScopeV1 scope)
    {
        if (task.AccountId != scope.AccountId || task.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("The task is outside the authorized account and conversation scope.");
        }
    }

    private static void EnsureThreadScope(
        AgentTaskV1 task,
        AgentConsultationThreadV1 thread,
        AuthorizedContextScopeV1 scope)
    {
        if (thread.AccountId != scope.AccountId
            || thread.ConversationId != scope.ConversationId
            || !string.Equals(task.ThreadId, thread.ThreadId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The consultation thread is outside the authorized account and conversation scope.");
        }
    }

    private static void ValidateContextScope(ContextBundleV1 bundle, AuthorizedContextScopeV1 scope)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.AccountId != scope.AccountId || bundle.ConversationId != scope.ConversationId)
        {
            throw new UnauthorizedAccessException("M3 returned context outside the authorized account and conversation scope.");
        }

        if (string.IsNullOrWhiteSpace(bundle.ContextRevision))
        {
            throw new AgentTaskContractException(
                [new AgentContractViolationV1("FIELD_REQUIRED", "context.contextRevision", "M3 must supply a context revision.")]);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static void ValidateOptions(AgentTaskCoordinatorOptionsV1 options)
    {
        if (string.IsNullOrWhiteSpace(options.SkillVersion))
        {
            throw new ArgumentException("A skill version must be configured.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.AllowedReadCapabilities);
        if (options.AllowedReadCapabilities.Any(capability => !Enum.IsDefined(capability))
            || options.AllowedReadCapabilities.Distinct().Count() != options.AllowedReadCapabilities.Count)
        {
            throw new ArgumentException("Read capabilities must be supported and unique.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.ProfileModelIds);
        if (options.ProfileModelIds.Any(pair => !Enum.IsDefined(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Value.Length > 80
                || pair.Value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
        {
            throw new ArgumentException("Execution profile model IDs must use the restricted model identifier format.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Budget);
        if (options.Budget.MaxDuration is { } duration && duration <= TimeSpan.Zero
            || options.Budget.MaxContextCharacters is < 0
            || options.Budget.MaxToolCalls is < 0)
        {
            throw new ArgumentException("Configured execution budget limits are invalid.", nameof(options));
        }

        if (options.RecentMessageLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Recent message limit must be positive.");
        }

        if (options.MaxConsultationHistoryTurns is < 1 or > 100
            || options.MaxConsultationHistoryCharacters is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Consultation history bounds exceed their supported hard limits.");
        }
    }
}

public sealed record AgentTaskCoordinatorOptionsV1
{
    public string SkillVersion { get; init; } = string.Empty;
    public IReadOnlyList<AgentReadCapabilityV1> AllowedReadCapabilities { get; init; } = [];
    public IReadOnlyDictionary<AgentExecutionProfileIdV1, string> ProfileModelIds { get; init; } = new Dictionary<AgentExecutionProfileIdV1, string>();
    public AgentExecutionBudgetV1? Budget { get; init; }
    public int RecentMessageLimit { get; init; } = 30;
    public bool IncludeMemory { get; init; } = true;
    public int MaxConsultationHistoryTurns { get; init; } = 8;
    public int MaxConsultationHistoryCharacters { get; init; } = 4000;
}

public sealed class AgentTaskContractException : Exception
{
    public AgentTaskContractException(
        IReadOnlyList<AgentContractViolationV1> violations,
        string? message = null)
        : base(message ?? (violations.Count == 0 ? "Agent task contract validation failed." : violations[0].Message))
    {
        Violations = violations;
    }

    public IReadOnlyList<AgentContractViolationV1> Violations { get; }
}

public sealed class AgentTaskExecutionException : Exception
{
    public AgentTaskExecutionException(AgentRunStatusV1 status, string errorCode, string runtimeName)
        : base($"Agent runtime '{runtimeName}' returned {status} ({errorCode}).")
    {
        Status = status;
        ErrorCode = errorCode;
        RuntimeName = runtimeName;
    }

    public AgentRunStatusV1 Status { get; }
    public string ErrorCode { get; }
    public string RuntimeName { get; }
}
