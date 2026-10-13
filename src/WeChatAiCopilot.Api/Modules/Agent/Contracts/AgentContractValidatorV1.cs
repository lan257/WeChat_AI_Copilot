using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Agent.Contracts;

public sealed record AgentContractViolationV1(string Code, string Path, string Message);

public static class AgentContractValidatorV1
{
    public static IReadOnlyList<AgentContractViolationV1> Validate(AgentTaskV1? task)
    {
        if (task is null)
        {
            return [new("AGENT_TASK_REQUIRED", "$", "An agent task is required.")];
        }

        var violations = new List<AgentContractViolationV1>();
        if (!Enum.IsDefined(task.ExecutionProfileId))
        {
            violations.Add(new("INVALID_EXECUTION_PROFILE", "executionProfileId", "The execution profile is not supported."));
        }
        if (!Enum.IsDefined(task.TaskType))
        {
            violations.Add(new("INVALID_TASK_TYPE", "taskType", "The task type is not supported."));
        }

        if (task.AccountId == Guid.Empty)
        {
            violations.Add(new("ACCOUNT_ID_REQUIRED", "accountId", "A valid account ID is required."));
        }

        if (task.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONVERSATION_ID_REQUIRED", "conversationId", "A valid conversation ID is required."));
        }

        Require(task.ContextRevision, "contextRevision", violations);

        switch (task.TaskType)
        {
            case AgentTaskTypeV1.ReplyCandidates:
                Require(task.PersonaPresetId, "personaPresetId", violations);
                Forbid(task.ThreadId, "threadId", violations);
                Forbid(task.UserInput, "userInput", violations);
                ValidateOptionalId(task.PreviousBatchId, "previousBatchId", violations);
                break;

            case AgentTaskTypeV1.FreeConsultation:
                Require(task.ThreadId, "threadId", violations);
                Require(task.UserInput, "userInput", violations);
                Forbid(task.PersonaPresetId, "personaPresetId", violations);
                Forbid(task.PreviousBatchId, "previousBatchId", violations);
                break;
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(AgentContextSnapshotV1? context)
    {
        if (context is null)
        {
            return [new("CONTEXT_UNAVAILABLE", "context", "Authorized conversation context is unavailable.")];
        }

        var violations = new List<AgentContractViolationV1>();
        if (context.AccountId == Guid.Empty)
        {
            violations.Add(new("ACCOUNT_ID_REQUIRED", "context.accountId", "A valid account ID is required."));
        }

        if (context.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONVERSATION_ID_REQUIRED", "context.conversationId", "A valid conversation ID is required."));
        }

        Require(context.ContextRevision, "context.contextRevision", violations);
        if (!Enum.IsDefined(context.PerspectiveState))
        {
            violations.Add(new("INVALID_PERSPECTIVE_STATE", "context.perspectiveState", "The perspective state is not supported."));
        }

        if (context.CalibrationVersion < 0)
        {
            violations.Add(new("INVALID_CALIBRATION_VERSION", "context.calibrationVersion", "The calibration version must be non-negative."));
        }

        if (!Enum.IsDefined(context.ProfileStatus)
            || context.UnprofiledTextCharacters < 0
            || context.RecentMessageLimit is < 1 or > 100)
        {
            violations.Add(new("INVALID_PROFILE_CONTEXT_STATE", "context.profileStatus", "Profile context status and text count are invalid."));
        }

        if (context.Omissions is null)
        {
            violations.Add(new("CONTEXT_OMISSIONS_REQUIRED", "context.omissions", "The omissions collection is required."));
        }

        if (context.ParticipantProfiles is null)
        {
            violations.Add(new("CONTEXT_PROFILES_REQUIRED", "context.participantProfiles", "The participant profile collection is required."));
        }
        else
        {
            for (var index = 0; index < context.ParticipantProfiles.Count; index++)
            {
                var profile = context.ParticipantProfiles[index];
                if (profile is null)
                {
                    violations.Add(new("CONTEXT_PROFILE_REQUIRED", $"context.participantProfiles[{index}]", "A profile cannot be null."));
                    continue;
                }

                Require(profile.IdentityNamespace, $"context.participantProfiles[{index}].identityNamespace", violations);
                Require(profile.Summary, $"context.participantProfiles[{index}].summary", violations);
                if (profile.SubjectId == Guid.Empty || !Enum.IsDefined(profile.SubjectType))
                {
                    violations.Add(new("INVALID_CONTEXT_PROFILE_SUBJECT", $"context.participantProfiles[{index}]", "A profile must have a valid participant subject."));
                }

                if (profile.Status != ProfileStatusV1.Current
                    || profile.CalibrationVersion != context.CalibrationVersion
                    || context.PerspectiveState != PerspectiveStateV1.Confirmed)
                {
                    violations.Add(new("UNMATCHED_CONTEXT_PROFILE", $"context.participantProfiles[{index}]", "Only current profiles for the confirmed calibration may enter Agent context."));
                }

                ValidateEvidenceIds(profile.EvidenceMessageIds, $"context.participantProfiles[{index}].evidenceMessageIds", violations);
            }
        }

        if (context.MemoryItems is null)
        {
            violations.Add(new("CONTEXT_MEMORY_REQUIRED", "context.memoryItems", "The memory item collection is required."));
        }
        else
        {
            for (var index = 0; index < context.MemoryItems.Count; index++)
            {
                var memory = context.MemoryItems[index];
                if (memory is null)
                {
                    violations.Add(new("CONTEXT_MEMORY_ITEM_REQUIRED", $"context.memoryItems[{index}]", "A memory item cannot be null."));
                    continue;
                }

                Require(memory.MemoryId, $"context.memoryItems[{index}].memoryId", violations);
                Require(memory.Text, $"context.memoryItems[{index}].text", violations);
                if (memory.Status != MemoryItemStatusV1.Active)
                {
                    violations.Add(new("INACTIVE_CONTEXT_MEMORY", $"context.memoryItems[{index}].status", "Only active memory items may enter Agent context."));
                }

                if (!Enum.IsDefined(memory.SubjectType) || !Enum.IsDefined(memory.Origin))
                {
                    violations.Add(new("INVALID_CONTEXT_MEMORY_CLASSIFICATION", $"context.memoryItems[{index}]", "The memory item classification is not supported."));
                }

                if (memory.Origin == MemoryOriginV1.MessageExtracted
                    && (context.PerspectiveState != PerspectiveStateV1.Confirmed
                        || memory.CalibrationVersion != context.CalibrationVersion
                        || string.IsNullOrWhiteSpace(memory.IdentityNamespace)))
                {
                    violations.Add(new("UNMATCHED_CONTEXT_MEMORY", $"context.memoryItems[{index}]", "Message-extracted memory must match the confirmed calibration and identity namespace."));
                }

                ValidateEvidenceIds(memory.SourceMessageIds, $"context.memoryItems[{index}].sourceMessageIds", violations);
            }
        }

        if (context.RecentMessages is null)
        {
            violations.Add(new("CONTEXT_MESSAGES_UNAVAILABLE", "context.recentMessages", "Recent authorized messages are unavailable."));
            return violations;
        }

        for (var index = 0; index < context.RecentMessages.Count; index++)
        {
            var message = context.RecentMessages[index];
            if (message is null)
            {
                violations.Add(new("CONTEXT_MESSAGE_REQUIRED", $"context.recentMessages[{index}]", "A context message cannot be null."));
                continue;
            }

            Require(message.MessageId, $"context.recentMessages[{index}].messageId", violations);
            // Non-text WeChat messages have no text_content by design. Require text
            // only for text messages, while retaining non-text message metadata in context.
            if (string.Equals(message.MessageType, "Text", StringComparison.Ordinal)
                && message.Text is null)
            {
                violations.Add(new("CONTEXT_MESSAGE_TEXT_REQUIRED", $"context.recentMessages[{index}].text", "Context message text is required."));
            }

            if (!Enum.IsDefined(message.Direction))
            {
                violations.Add(new("INVALID_MESSAGE_DIRECTION", $"context.recentMessages[{index}].direction", "The message direction is not supported."));
            }

            if (!Enum.IsDefined(message.DirectionEvidence))
            {
                violations.Add(new("INVALID_DIRECTION_EVIDENCE", $"context.recentMessages[{index}].directionEvidence", "The direction evidence is not supported."));
            }

            if (message.OccurredAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            {
                violations.Add(new("TIME_MUST_BE_UTC", $"context.recentMessages[{index}].occurredAtUtc", "Context timestamps must use UTC."));
            }
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(ScopedAgentRequestV1? request)
    {
        if (request is null)
        {
            return [new("SCOPED_AGENT_REQUEST_REQUIRED", "$", "A scoped agent request is required.")];
        }

        var violations = new List<AgentContractViolationV1>();
        violations.AddRange(Validate(request.Task));
        violations.AddRange(ValidateThreadBinding(request.Task, request.BoundThread));
        violations.AddRange(Validate(request.Context));
        Require(request.SkillVersion, "skillVersion", violations);

        if (request.Task?.TaskType == AgentTaskTypeV1.ReplyCandidates)
        {
            Require(request.PersonaInstruction, "personaInstruction", violations);
        }
        else if (request.PersonaInstruction is not null)
        {
            violations.Add(new("PERSONA_INSTRUCTION_NOT_ALLOWED", "personaInstruction", "Only reply candidate tasks may include a persona instruction."));
        }

        if (request.ConsultationHistory is null)
        {
            violations.Add(new("CONSULTATION_HISTORY_REQUIRED", "consultationHistory", "The consultation history collection is required."));
        }
        else if (request.Task?.TaskType == AgentTaskTypeV1.FreeConsultation)
        {
            for (var index = 0; index < request.ConsultationHistory.Count; index++)
            {
                var turn = request.ConsultationHistory[index];
                if (turn is null)
                {
                    violations.Add(new("CONSULTATION_HISTORY_TURN_REQUIRED", $"consultationHistory[{index}]", "A history turn cannot be null."));
                    continue;
                }

                if (!string.Equals(turn.ThreadId, request.Task.ThreadId, StringComparison.Ordinal)
                    || turn.AccountId != request.Task.AccountId
                    || turn.ConversationId != request.Task.ConversationId)
                {
                    violations.Add(new("CONSULTATION_HISTORY_SCOPE_MISMATCH", $"consultationHistory[{index}]", "Every history turn must belong to the current thread and task scope."));
                }

                Require(turn.UserInput, $"consultationHistory[{index}].userInput", violations);
                Require(turn.Answer, $"consultationHistory[{index}].answer", violations);
                if (turn.CreatedAtUtc == default || turn.CreatedAtUtc.Offset != TimeSpan.Zero)
                {
                    violations.Add(new("INVALID_HISTORY_TIMESTAMP", $"consultationHistory[{index}].createdAtUtc", "History timestamps must be supplied in UTC."));
                }

                ValidateEvidenceIds(turn.EvidenceMessageIds, $"consultationHistory[{index}].evidenceMessageIds", violations);
            }
        }
        else if (request.ConsultationHistory?.Count > 0 || request.ConsultationHistoryTruncated)
        {
            violations.Add(new("CONSULTATION_HISTORY_NOT_ALLOWED", "consultationHistory", "Only consultation tasks may include consultation history."));
        }

        if (request.AllowedReadCapabilities is null)
        {
            violations.Add(new("READ_CAPABILITIES_REQUIRED", "allowedReadCapabilities", "The allowed read capability collection is required."));
        }
        else
        {
            var seenCapabilities = new HashSet<AgentReadCapabilityV1>();
            for (var index = 0; index < request.AllowedReadCapabilities.Count; index++)
            {
                var capability = request.AllowedReadCapabilities[index];
                if (!Enum.IsDefined(capability))
                {
                    violations.Add(new("UNSUPPORTED_READ_CAPABILITY", $"allowedReadCapabilities[{index}]", "The read capability is not supported."));
                }
                else if (!seenCapabilities.Add(capability))
                {
                    violations.Add(new("DUPLICATE_READ_CAPABILITY", $"allowedReadCapabilities[{index}]", "Read capabilities must not be duplicated."));
                }
            }
        }

        if (request.Budget is null)
        {
            violations.Add(new("AGENT_BUDGET_REQUIRED", "budget", "An execution budget must be supplied by the coordinator."));
        }
        else
        {
            if (request.Budget.MaxDuration is { } maxDuration && maxDuration <= TimeSpan.Zero)
            {
                violations.Add(new("INVALID_AGENT_BUDGET", "budget.maxDuration", "A configured maximum duration must be positive."));
            }

            if (request.Budget.MaxContextCharacters is < 0)
            {
                violations.Add(new("INVALID_AGENT_BUDGET", "budget.maxContextCharacters", "A configured context limit must be non-negative."));
            }

            if (request.Budget.MaxToolCalls is < 0)
            {
                violations.Add(new("INVALID_AGENT_BUDGET", "budget.maxToolCalls", "A configured tool call limit must be non-negative."));
            }
        }

        if (request.Task is not null && request.Context is not null)
        {
            if (request.Context.AccountId != request.Task.AccountId
                || request.Context.ConversationId != request.Task.ConversationId)
            {
                violations.Add(new("CONTEXT_SCOPE_MISMATCH", "context", "Context must match the task account and conversation."));
            }

            if (!string.Equals(request.Context.ContextRevision, request.Task.ContextRevision, StringComparison.Ordinal))
            {
                violations.Add(new("CONTEXT_REVISION_MISMATCH", "context.contextRevision", "Context must match the task revision."));
            }
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> ValidateThreadBinding(
        AgentTaskV1? task,
        AgentConsultationThreadV1? thread)
    {
        var violations = new List<AgentContractViolationV1>();
        if (task is null)
        {
            if (thread is not null)
            {
                violations.Add(new("UNEXPECTED_CONSULTATION_THREAD", "boundThread", "A consultation thread requires a valid task."));
            }

            return violations;
        }

        if (task.TaskType != AgentTaskTypeV1.FreeConsultation)
        {
            if (thread is not null)
            {
                violations.Add(new("UNEXPECTED_CONSULTATION_THREAD", "boundThread", "Only consultation tasks may include a bound thread."));
            }

            return violations;
        }

        if (thread is null)
        {
            return [new("CONSULTATION_THREAD_REQUIRED", "boundThread", "A bound consultation thread is required.")];
        }

        Require(thread.ThreadId, "boundThread.threadId", violations);
        if (thread.AccountId == Guid.Empty || thread.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONSULTATION_THREAD_SCOPE_REQUIRED", "boundThread", "The consultation thread must have a valid account and conversation scope."));
        }

        if (!string.Equals(task.ThreadId, thread.ThreadId, StringComparison.Ordinal)
            || task.AccountId != thread.AccountId
            || task.ConversationId != thread.ConversationId)
        {
            violations.Add(new("CONSULTATION_THREAD_SCOPE_MISMATCH", "boundThread", "The consultation thread is not bound to this task's account and conversation."));
        }

        if (thread.CreatedAtUtc == default || thread.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            violations.Add(new("INVALID_THREAD_TIMESTAMP", "boundThread.createdAtUtc", "Thread timestamps must be supplied in UTC."));
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(SuggestionBatchV1? batch)
    {
        if (batch is null)
        {
            return [new("SUGGESTION_BATCH_REQUIRED", "$", "A suggestion batch is required.")];
        }

        var violations = new List<AgentContractViolationV1>();
        Require(batch.RunId, "runId", violations);
        Require(batch.BatchId, "batchId", violations);
        Require(batch.PersonaPresetId, "personaPresetId", violations);
        Require(batch.ContextRevision, "contextRevision", violations);
        if (batch.AccountId == Guid.Empty)
        {
            violations.Add(new("ACCOUNT_ID_REQUIRED", "accountId", "A valid account ID is required."));
        }

        if (batch.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONVERSATION_ID_REQUIRED", "conversationId", "A valid conversation ID is required."));
        }

        if (batch.TaskType != AgentTaskTypeV1.ReplyCandidates)
        {
            violations.Add(new("INVALID_SUGGESTION_TASK_TYPE", "taskType", "Suggestion batches must belong to ReplyCandidates tasks."));
        }

        if (!Enum.IsDefined(batch.ExecutionProfileId))
        {
            violations.Add(new("INVALID_EXECUTION_PROFILE", "executionProfileId", "The execution profile is not supported."));
        }

        if (batch.Status != AgentRunStatusV1.Completed)
        {
            violations.Add(new("SUGGESTION_BATCH_NOT_COMPLETED", "status", "Only completed suggestion batches may be returned."));
        }

        ValidateSuggestions(batch.Suggestions, "suggestions", violations);
        if (batch.CreatedAtUtc == default || batch.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            violations.Add(new("INVALID_BATCH_TIMESTAMP", "createdAtUtc", "Batch timestamps must be supplied in UTC."));
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(SuggestionBatchV1? batch, AgentTaskV1? task)
    {
        var violations = Validate(batch).ToList();
        if (task is null)
        {
            violations.Add(new("AGENT_TASK_REQUIRED", "task", "A task is required to validate suggestion scope."));
            return violations;
        }

        if (batch is null)
        {
            return violations;
        }

        if (task.TaskType != AgentTaskTypeV1.ReplyCandidates
            || batch.TaskType != task.TaskType
            || batch.AccountId != task.AccountId
            || batch.ConversationId != task.ConversationId
            || batch.ExecutionProfileId != task.ExecutionProfileId
            || !string.Equals(batch.ContextRevision, task.ContextRevision, StringComparison.Ordinal)
            || !string.Equals(batch.PersonaPresetId, task.PersonaPresetId, StringComparison.Ordinal))
        {
            violations.Add(new("SUGGESTION_BATCH_SCOPE_MISMATCH", "$", "The batch task type, scope, persona, and context revision must match the originating task."));
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(
        ConsultationTurnV1? turn,
        AgentConsultationThreadV1? thread)
    {
        if (turn is null)
        {
            return [new("CONSULTATION_TURN_REQUIRED", "$", "A consultation turn is required.")];
        }

        var violations = new List<AgentContractViolationV1>();
        Require(turn.ThreadId, "threadId", violations);
        Require(turn.RunId, "runId", violations);
        Require(turn.UserInput, "userInput", violations);
        Require(turn.Answer, "answer", violations);
        if (turn.AccountId == Guid.Empty || turn.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONSULTATION_TURN_SCOPE_REQUIRED", "$", "A consultation turn must include a valid account and conversation scope."));
        }

        if (turn.Status != AgentRunStatusV1.Completed)
        {
            violations.Add(new("CONSULTATION_TURN_NOT_COMPLETED", "status", "Only completed consultation turns may be returned."));
        }

        if (thread is null)
        {
            violations.Add(new("CONSULTATION_TURN_SCOPE_MISMATCH", "threadId", "The turn must match its consultation thread's immutable scope."));
        }
        else
        {
            Require(thread.ThreadId, "thread.threadId", violations);
            if (thread.AccountId == Guid.Empty || thread.ConversationId == Guid.Empty)
            {
                violations.Add(new("CONSULTATION_THREAD_SCOPE_REQUIRED", "thread", "The consultation thread must have a valid account and conversation scope."));
            }

            if (thread.CreatedAtUtc == default || thread.CreatedAtUtc.Offset != TimeSpan.Zero)
            {
                violations.Add(new("INVALID_THREAD_TIMESTAMP", "thread.createdAtUtc", "Thread timestamps must be supplied in UTC."));
            }

            if (!string.Equals(turn.ThreadId, thread.ThreadId, StringComparison.Ordinal)
                || turn.AccountId != thread.AccountId
                || turn.ConversationId != thread.ConversationId)
            {
                violations.Add(new("CONSULTATION_TURN_SCOPE_MISMATCH", "threadId", "The turn must match its consultation thread's immutable scope."));
            }
        }

        if (turn.EvidenceMessageIds is null)
        {
            violations.Add(new("EVIDENCE_IDS_REQUIRED", "evidenceMessageIds", "The evidence ID collection is required."));
        }
        else if (turn.EvidenceMessageIds.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add(new("INVALID_EVIDENCE_ID", "evidenceMessageIds", "Evidence IDs must be non-empty."));
        }
        else if (turn.EvidenceMessageIds.Distinct(StringComparer.Ordinal).Count() != turn.EvidenceMessageIds.Count)
        {
            violations.Add(new("DUPLICATE_EVIDENCE_ID", "evidenceMessageIds", "Evidence message IDs must be unique."));
        }

        if (turn.CreatedAtUtc == default || turn.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            violations.Add(new("INVALID_TURN_TIMESTAMP", "createdAtUtc", "Turn timestamps must be supplied in UTC."));
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> Validate(
        ConsultationTurnV1? turn,
        AgentTaskV1? task,
        AgentConsultationThreadV1? thread)
    {
        var violations = Validate(turn, thread).ToList();
        if (task is null || task.TaskType != AgentTaskTypeV1.FreeConsultation)
        {
            violations.Add(new("AGENT_TASK_REQUIRED", "task", "A consultation task is required to validate turn scope."));
            return violations;
        }

        if (turn is null
            || !string.Equals(turn.ThreadId, task.ThreadId, StringComparison.Ordinal)
            || turn.AccountId != task.AccountId
            || turn.ConversationId != task.ConversationId
            || !string.Equals(turn.UserInput, task.UserInput, StringComparison.Ordinal))
        {
            violations.Add(new("CONSULTATION_TURN_SCOPE_MISMATCH", "$", "The turn thread, scope, and input must match the originating task."));
        }

        return violations;
    }

    public static IReadOnlyList<AgentContractViolationV1> ValidateRuntimeResult(
        AgentRuntimeResultV1? result,
        AgentTaskV1? task,
        AgentContextSnapshotV1? context)
    {
        if (result is null)
        {
            return [new("AGENT_RUNTIME_RESULT_REQUIRED", "$", "A runtime result is required.")];
        }

        var violations = new List<AgentContractViolationV1>();
        Require(result.RuntimeName, "runtimeName", violations);
        if (result.Suggestions is null
            || result.EvidenceMessageIds is null
            || result.VerifiedEvidenceMessageIds is null)
        {
            violations.Add(new("RUNTIME_OUTPUT_COLLECTIONS_REQUIRED", "$", "Runtime output collections are required."));
            return violations;
        }

        if (result.Status is AgentRunStatusV1.Failed or AgentRunStatusV1.Cancelled)
        {
            Require(result.ErrorCode, "errorCode", violations);
            if (result.Suggestions.Count != 0
                || result.Answer is not null
                || result.EvidenceMessageIds.Count != 0
                || result.VerifiedEvidenceMessageIds.Count != 0)
            {
                violations.Add(new("FAILED_RUNTIME_HAS_OUTPUT", "$", "Failed or cancelled runs cannot contain generated output."));
            }

            return violations;
        }

        if (result.Status != AgentRunStatusV1.Completed)
        {
            violations.Add(new("INVALID_RUNTIME_RESULT_STATUS", "status", "A runtime result must be completed, failed, or cancelled."));
            return violations;
        }

        if (!string.IsNullOrEmpty(result.ErrorCode))
        {
            violations.Add(new("COMPLETED_RUNTIME_HAS_ERROR", "errorCode", "A completed run cannot contain an error code."));
        }

        if (task?.TaskType == AgentTaskTypeV1.ReplyCandidates)
        {
            ValidateSuggestionDrafts(result.Suggestions, "suggestions", violations);
            if (result.Answer is not null
                || result.EvidenceMessageIds.Count != 0
                || result.VerifiedEvidenceMessageIds.Count != 0)
            {
                violations.Add(new("INVALID_SUGGESTION_RUNTIME_OUTPUT", "$", "Reply candidate output cannot include a consultation answer or evidence list."));
            }
        }
        else if (task?.TaskType == AgentTaskTypeV1.FreeConsultation)
        {
            Require(result.Answer, "answer", violations);
            if (result.Suggestions.Count != 0)
            {
                violations.Add(new("INVALID_CONSULTATION_RUNTIME_OUTPUT", "suggestions", "Consultation output cannot include reply candidates."));
            }

            var availableEvidenceIds = GetAvailableEvidenceIds(context);
            if (result.VerifiedEvidenceMessageIds.Any(string.IsNullOrWhiteSpace)
                || result.VerifiedEvidenceMessageIds.Distinct(StringComparer.Ordinal).Count() != result.VerifiedEvidenceMessageIds.Count)
            {
                violations.Add(new("INVALID_VERIFIED_EVIDENCE_IDS", "verifiedEvidenceMessageIds", "Server-verified evidence IDs must be non-empty and unique."));
            }

            foreach (var id in result.VerifiedEvidenceMessageIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    availableEvidenceIds.Add(id);
                }
            }

            if (result.EvidenceMessageIds.Any(id => string.IsNullOrWhiteSpace(id) || !availableEvidenceIds.Contains(id)))
            {
                violations.Add(new("UNAVAILABLE_EVIDENCE_ID", "evidenceMessageIds", "Evidence IDs must come from the authorized context."));
            }
        }
        else
        {
            violations.Add(new("AGENT_TASK_REQUIRED", "task", "A valid task is required to validate runtime output."));
        }

        return violations;
    }

    private static void ValidateSuggestions(
        IReadOnlyList<AgentSuggestionV1>? suggestions,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (suggestions is null)
        {
            violations.Add(new("SUGGESTIONS_REQUIRED", path, "A suggestion collection is required."));
            return;
        }

        if (suggestions.Count != 3)
        {
            violations.Add(new("SUGGESTION_COUNT_MUST_BE_THREE", path, "A completed batch must contain exactly three suggestions."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < suggestions.Count; index++)
        {
            var suggestion = suggestions[index];
            if (suggestion is null)
            {
                violations.Add(new("SUGGESTION_REQUIRED", $"{path}[{index}]", "A suggestion cannot be null."));
                continue;
            }

            Require(suggestion.Id, $"{path}[{index}].id", violations);
            Require(suggestion.Text, $"{path}[{index}].text", violations);
            if (!string.IsNullOrWhiteSpace(suggestion.Id) && !ids.Add(suggestion.Id))
            {
                violations.Add(new("DUPLICATE_SUGGESTION_ID", $"{path}[{index}].id", "Suggestion IDs must be unique within the batch."));
            }
        }
    }

    private static void ValidateSuggestionDrafts(
        IReadOnlyList<AgentSuggestionDraftV1>? suggestions,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (suggestions is null)
        {
            violations.Add(new("SUGGESTIONS_REQUIRED", path, "A suggestion collection is required."));
            return;
        }

        if (suggestions.Count != 3)
        {
            violations.Add(new("SUGGESTION_COUNT_MUST_BE_THREE", path, "A completed runtime result must contain exactly three suggestions."));
        }

        var uniqueTexts = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < suggestions.Count; index++)
        {
            var suggestion = suggestions[index];
            if (suggestion is null)
            {
                violations.Add(new("SUGGESTION_REQUIRED", $"{path}[{index}]", "A suggestion cannot be null."));
                continue;
            }

            Require(suggestion.Text, $"{path}[{index}].text", violations);
            if (!string.IsNullOrWhiteSpace(suggestion.Text))
            {
                if (suggestion.Text.Length > 2000)
                {
                    violations.Add(new("SUGGESTION_TEXT_TOO_LONG", $"{path}[{index}].text", "Suggestion text exceeds the supported length."));
                }

                if (!uniqueTexts.Add(suggestion.Text.Trim()))
                {
                    violations.Add(new("DUPLICATE_SUGGESTION_TEXT", $"{path}[{index}].text", "Suggestion texts must be distinct."));
                }
            }
        }
    }

    private static HashSet<string> GetAvailableEvidenceIds(AgentContextSnapshotV1? context)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (context is null)
        {
            return ids;
        }

        if (context.RecentMessages is not null)
        {
            foreach (var message in context.RecentMessages)
            {
                if (!string.IsNullOrWhiteSpace(message?.MessageId))
                {
                    ids.Add(message.MessageId);
                }
            }
        }

        foreach (var profile in context.ParticipantProfiles ?? [])
        {
            foreach (var id in profile?.EvidenceMessageIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        foreach (var memory in context.MemoryItems ?? [])
        {
            foreach (var id in memory?.SourceMessageIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static void ValidateEvidenceIds(
        IReadOnlyList<string>? evidenceIds,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (evidenceIds is null)
        {
            violations.Add(new("EVIDENCE_IDS_REQUIRED", path, "The evidence ID collection is required."));
            return;
        }

        if (evidenceIds.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add(new("INVALID_EVIDENCE_ID", path, "Evidence IDs must be non-empty."));
        }
        else if (evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Count)
        {
            violations.Add(new("DUPLICATE_EVIDENCE_ID", path, "Evidence message IDs must be unique."));
        }
    }

    private static void ValidateOptionalId(
        string? value,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            violations.Add(new("INVALID_OPTIONAL_ID", path, "An optional ID must be omitted or non-empty."));
        }
    }

    private static void Require(
        string? value,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(new("FIELD_REQUIRED", path, "This field is required."));
        }
    }

    private static void Forbid(
        string? value,
        string path,
        ICollection<AgentContractViolationV1> violations)
    {
        if (value is not null)
        {
            violations.Add(new("FIELD_NOT_ALLOWED", path, "This field is not valid for the selected task type."));
        }
    }
}
