using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public static class MemoryConsolidationOutputValidatorV1
{
    public const int MaximumMemoriesPerBatch = 50;
    public const int MaximumEvidenceIdsPerOutput = 100;
    public const int MaximumTextCodePoints = 4000;
    public const int MaximumCategoryCodePoints = 100;

    public static IReadOnlyList<MemoryRequestViolationV1> Validate(
        MemoryConsolidationGenerationOutputV1? output,
        ConsolidationCaptureV1 capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (output is null)
        {
            return [new MemoryRequestViolationV1("output", "required")];
        }

        var violations = new List<MemoryRequestViolationV1>();
        if (capture.EligibleMessages is null)
        {
            return [new MemoryRequestViolationV1("capture.eligibleMessages", "required")];
        }

        if (capture.EligibleMessages.Any(message => message is null))
        {
            return [new MemoryRequestViolationV1("capture.eligibleMessages", "invalid_row")];
        }

        var availableDirections = new Dictionary<Guid, MessageDirectionV1>();
        foreach (var message in capture.EligibleMessages)
        {
            if (message.MessageId == Guid.Empty
                || message.Direction is not (MessageDirectionV1.Incoming or MessageDirectionV1.Outgoing)
                || !availableDirections.TryAdd(message.MessageId, message.Direction))
            {
                return [new MemoryRequestViolationV1("capture.eligibleMessages", "invalid_row")];
            }
        }
        var availableMessageIds = availableDirections.Keys.ToHashSet();

        if (output.ParticipantProfiles is null || output.MemoryItems is null)
        {
            return [new MemoryRequestViolationV1("output", "collections_required")];
        }

        if (output.ParticipantProfiles.Count > 2)
        {
            violations.Add(new MemoryRequestViolationV1("participantProfiles", "too_many"));
        }

        var profileSubjectTypes = new HashSet<ParticipantSubjectTypeV1>();
        for (var index = 0; index < output.ParticipantProfiles.Count; index++)
        {
            var profile = output.ParticipantProfiles[index];
            var path = $"participantProfiles[{index}]";
            if (profile is null)
            {
                violations.Add(new MemoryRequestViolationV1(path, "required"));
                continue;
            }

            if (!IsParticipant(profile.SubjectType))
            {
                violations.Add(new MemoryRequestViolationV1($"{path}.subjectType", "unsupported"));
            }
            else if (!profileSubjectTypes.Add(profile.SubjectType))
            {
                violations.Add(new MemoryRequestViolationV1($"{path}.subjectType", "duplicate_subject"));
            }

            ValidateText(profile.Summary, path + ".summary", violations);
            ValidateEvidence(profile.EvidenceMessageIds, availableMessageIds, path + ".evidenceMessageIds", violations);
            ValidateSubjectEvidenceDirection(profile.SubjectType, profile.EvidenceMessageIds, path, availableDirections, violations);
        }

        if (output.MemoryItems.Count > MaximumMemoriesPerBatch)
        {
            violations.Add(new MemoryRequestViolationV1("memoryItems", "too_many"));
        }

        for (var index = 0; index < output.MemoryItems.Count; index++)
        {
            var memory = output.MemoryItems[index];
            var path = $"memoryItems[{index}]";
            if (memory is null)
            {
                violations.Add(new MemoryRequestViolationV1(path, "required"));
                continue;
            }

            if (!IsParticipant(memory.SubjectType))
            {
                violations.Add(new MemoryRequestViolationV1($"{path}.subjectType", "unsupported"));
            }

            ValidateText(memory.Text, path + ".text", violations);
            if (memory.Category is { } category
                && (string.IsNullOrWhiteSpace(category) || CountCodePoints(category) > MaximumCategoryCodePoints))
            {
                violations.Add(new MemoryRequestViolationV1(path + ".category", "invalid"));
            }

            ValidateEvidence(memory.EvidenceMessageIds, availableMessageIds, path + ".evidenceMessageIds", violations);
            ValidateSubjectEvidenceDirection(memory.SubjectType, memory.EvidenceMessageIds, path, availableDirections, violations);
        }

        return violations;
    }

    private static bool IsParticipant(ParticipantSubjectTypeV1 value) => Enum.IsDefined(value);

    private static void ValidateText(string? text, string path, ICollection<MemoryRequestViolationV1> violations)
    {
        if (string.IsNullOrWhiteSpace(text) || CountCodePoints(text) > MaximumTextCodePoints)
        {
            violations.Add(new MemoryRequestViolationV1(path, "invalid"));
        }
    }

    private static void ValidateEvidence(
        IReadOnlyList<Guid>? evidenceIds,
        IReadOnlySet<Guid> availableMessageIds,
        string path,
        ICollection<MemoryRequestViolationV1> violations)
    {
        if (evidenceIds is null
            || evidenceIds.Count == 0
            || evidenceIds.Count > MaximumEvidenceIdsPerOutput
            || evidenceIds.Any(id => id == Guid.Empty)
            || evidenceIds.Distinct().Count() != evidenceIds.Count
            || evidenceIds.Any(id => !availableMessageIds.Contains(id)))
        {
            violations.Add(new MemoryRequestViolationV1(path, "invalid_or_out_of_range"));
        }
    }

    private static void ValidateSubjectEvidenceDirection(
        ParticipantSubjectTypeV1 subjectType,
        IReadOnlyList<Guid>? evidenceMessageIds,
        string path,
        IReadOnlyDictionary<Guid, MessageDirectionV1> availableDirections,
        ICollection<MemoryRequestViolationV1> violations)
    {
        if (!IsParticipant(subjectType) || evidenceMessageIds is null)
        {
            return;
        }

        var expectedDirection = subjectType == ParticipantSubjectTypeV1.WechatAccount
            ? MessageDirectionV1.Outgoing
            : MessageDirectionV1.Incoming;
        if (evidenceMessageIds.Any(messageId =>
                availableDirections.TryGetValue(messageId, out var direction)
                && direction != expectedDirection))
        {
            violations.Add(new MemoryRequestViolationV1(path + ".evidenceMessageIds", "direction_mismatch"));
        }
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
