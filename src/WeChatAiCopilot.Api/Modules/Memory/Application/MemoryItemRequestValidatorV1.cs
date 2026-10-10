using System.Text;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public static class MemoryItemRequestValidatorV1
{
    public const int MaximumTextCodePoints = 4000;
    public const int MaximumCategoryCodePoints = 100;
    public const int MaximumEvidenceMessageIds = 50;

    public static IReadOnlyList<MemoryRequestViolationV1> ValidateCreate(
        UserEnteredMemoryCreateRequestV1? request)
    {
        if (request is null)
        {
            return [new MemoryRequestViolationV1("body", "required")];
        }

        var violations = new List<MemoryRequestViolationV1>();
        if (!Enum.IsDefined(request.SubjectType))
        {
            violations.Add(new MemoryRequestViolationV1("subjectType", "unsupported"));
        }

        ValidateText(request.Text, violations);
        ValidateCategory(request.Category, violations);
        ValidateEvidence(request.SourceMessageIds, violations);
        return violations;
    }

    public static IReadOnlyList<MemoryRequestViolationV1> ValidateUpdate(
        UserEnteredMemoryUpdateRequestV1? request)
    {
        if (request is null)
        {
            return [new MemoryRequestViolationV1("body", "required")];
        }

        var violations = new List<MemoryRequestViolationV1>();
        if (request.ExpectedVersion <= 0)
        {
            violations.Add(new MemoryRequestViolationV1("expectedVersion", "must_be_positive"));
        }

        ValidateText(request.Text, violations);
        ValidateCategory(request.Category, violations);
        if (request.SourceMessageIds is null)
        {
            violations.Add(new MemoryRequestViolationV1("sourceMessageIds", "required"));
        }
        else
        {
            ValidateEvidence(request.SourceMessageIds, violations);
        }

        return violations;
    }

    private static void ValidateText(string? text, ICollection<MemoryRequestViolationV1> violations)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            violations.Add(new MemoryRequestViolationV1("text", "required"));
            return;
        }

        if (CountCodePoints(text) > MaximumTextCodePoints)
        {
            violations.Add(new MemoryRequestViolationV1("text", "too_long"));
        }
    }

    private static void ValidateCategory(string? category, ICollection<MemoryRequestViolationV1> violations)
    {
        if (category is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            violations.Add(new MemoryRequestViolationV1("category", "empty"));
        }
        else if (CountCodePoints(category) > MaximumCategoryCodePoints)
        {
            violations.Add(new MemoryRequestViolationV1("category", "too_long"));
        }
    }

    private static void ValidateEvidence(
        IReadOnlyList<Guid>? messageIds,
        ICollection<MemoryRequestViolationV1> violations)
    {
        if (messageIds is null)
        {
            return;
        }

        if (messageIds.Count > MaximumEvidenceMessageIds)
        {
            violations.Add(new MemoryRequestViolationV1("sourceMessageIds", "too_many"));
        }

        if (messageIds.Any(id => id == Guid.Empty))
        {
            violations.Add(new MemoryRequestViolationV1("sourceMessageIds", "empty_id"));
        }

        if (messageIds.Distinct().Count() != messageIds.Count)
        {
            violations.Add(new MemoryRequestViolationV1("sourceMessageIds", "duplicate_id"));
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
