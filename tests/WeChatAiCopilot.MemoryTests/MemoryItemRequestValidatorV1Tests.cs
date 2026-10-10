using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class MemoryItemRequestValidatorV1Tests
{
    [Fact]
    public void ValidateCreate_AcceptsFourThousandUnicodeCodePoints()
    {
        var request = new UserEnteredMemoryCreateRequestV1
        {
            SubjectType = ParticipantSubjectTypeV1.Contact,
            Text = string.Concat(new string('a', 3999), "😀"),
            SourceMessageIds = [Guid.NewGuid(), Guid.NewGuid()]
        };

        Assert.Empty(MemoryItemRequestValidatorV1.ValidateCreate(request));
    }

    [Fact]
    public void ValidateCreate_RejectsUnsupportedSubjectEmptyTextAndInvalidEvidenceIds()
    {
        var repeatedMessageId = Guid.NewGuid();
        var request = new UserEnteredMemoryCreateRequestV1
        {
            SubjectType = (ParticipantSubjectTypeV1)999,
            Text = "   ",
            SourceMessageIds = [Guid.Empty, repeatedMessageId, repeatedMessageId]
        };

        var codes = MemoryItemRequestValidatorV1.ValidateCreate(request)
            .Select(violation => violation.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("unsupported", codes);
        Assert.Contains("required", codes);
        Assert.Contains("empty_id", codes);
        Assert.Contains("duplicate_id", codes);
    }

    [Fact]
    public void ValidateUpdate_RequiresPositiveExpectedVersionAndBoundsInput()
    {
        var request = new UserEnteredMemoryUpdateRequestV1
        {
            ExpectedVersion = 0,
            Text = string.Concat(new string('a', 4000), "😀"),
            Category = new string('c', MemoryItemRequestValidatorV1.MaximumCategoryCodePoints + 1),
            SourceMessageIds = Enumerable.Range(0, MemoryItemRequestValidatorV1.MaximumEvidenceMessageIds + 1)
                .Select(_ => Guid.NewGuid())
                .ToArray()
        };

        var codes = MemoryItemRequestValidatorV1.ValidateUpdate(request)
            .Select(violation => violation.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("must_be_positive", codes);
        Assert.Contains("too_long", codes);
        Assert.Contains("too_many", codes);
    }

    [Fact]
    public void ValidateUpdate_RequiresExplicitEvidenceArray_ButAllowsExplicitClear()
    {
        var omittedEvidence = new UserEnteredMemoryUpdateRequestV1
        {
            ExpectedVersion = 1,
            Text = "updated text"
        };
        var clearEvidence = omittedEvidence with { SourceMessageIds = Array.Empty<Guid>() };

        Assert.Contains(
            MemoryItemRequestValidatorV1.ValidateUpdate(omittedEvidence),
            violation => violation.Field == "sourceMessageIds" && violation.Code == "required");
        Assert.Empty(MemoryItemRequestValidatorV1.ValidateUpdate(clearEvidence));
    }

    [Fact]
    public void ClientMemoryRequests_DoNotExposeAccountOrConversationScopeFields()
    {
        var properties = typeof(UserEnteredMemoryCreateRequestV1).GetProperties()
            .Concat(typeof(UserEnteredMemoryUpdateRequestV1).GetProperties())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("AccountId", properties);
        Assert.DoesNotContain("ConversationId", properties);
    }
}
