using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class ContextBuilderV1Tests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly Guid _contactId = Guid.NewGuid();

    [Fact]
    public async Task BuildAsync_WhenRequestDoesNotMatchAuthorizedScope_RejectsBeforeReading()
    {
        var messageQuery = new FakeMessageQuery(CreateMessageSnapshot());
        var memoryReader = new FakeMemoryReader(CreateMemorySnapshot());
        var builder = new ContextBuilderV1(messageQuery, memoryReader);
        var request = CreateRequest();
        var authorizedScope = new AuthorizedContextScopeV1(_accountId, Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => builder.BuildAsync(request, authorizedScope).AsTask());

        Assert.Equal(0, messageQuery.CallCount);
        Assert.Equal(0, memoryReader.CallCount);
    }

    [Fact]
    public async Task BuildAsync_WhenPerspectiveIsPending_ExcludesPerspectiveDataButKeepsUserEnteredMemory()
    {
        var evidenceMessageId = Guid.NewGuid();
        var messageQuery = new FakeMessageQuery(CreateMessageSnapshot(
            perspectiveState: PerspectiveStateV1.PendingConfirmation,
            calibrationVersion: 4,
            messages: [CreateMessage(evidenceMessageId, "manual fact evidence")]));
        var manualMemory = CreateMemoryItem(MemoryOriginV1.UserEntered, "manual note") with
        {
            SourceMessageIds = [evidenceMessageId]
        };
        var extractedMemory = CreateMemoryItem(
            MemoryOriginV1.MessageExtracted,
            "extracted note",
            calibrationVersion: 4,
            sourceMessageIds: [Guid.NewGuid()],
            sourceMessageCursor: "cursor-4");
        var memoryReader = new FakeMemoryReader(CreateMemorySnapshot(
            profileStatus: ProfileStatusV1.Current,
            unprofiledTextCharacters: 128,
            consolidationSourceMessageCursor: "cursor-4",
            participantProfiles: [CreateProfile(calibrationVersion: 4)],
            memoryItems: [manualMemory, extractedMemory]));
        var builder = new ContextBuilderV1(messageQuery, memoryReader);

        var result = await builder.BuildAsync(CreateRequest(), CreateAuthorizedScope());

        Assert.False(memoryReader.LastRequest!.MayReadPerspectiveBoundData);
        Assert.Equal(PerspectiveStateV1.PendingConfirmation, memoryReader.LastRequest.PerspectiveState);
        Assert.Equal(4, memoryReader.LastRequest.CalibrationVersion);
        Assert.Equal(ProfileStatusV1.WaitingForConfirmation, result.ProfileStatus);
        Assert.Equal(0, result.UnprofiledTextCharacters);
        Assert.Null(result.ConsolidationSourceMessageCursor);
        Assert.Empty(result.ParticipantProfiles);
        var retainedMemory = Assert.Single(result.MemoryItems);
        Assert.Equal(manualMemory.MemoryId, retainedMemory.MemoryId);
        Assert.Equal(MemoryOriginV1.UserEntered, retainedMemory.Origin);
        Assert.Equal(evidenceMessageId, Assert.Single(retainedMemory.SourceMessageIds));
        Assert.Contains(ContextOmissionCodeV1.PerspectiveNotConfirmed, OmissionCodes(result));
    }

    [Fact]
    public async Task BuildAsync_WhenPerspectiveIsConfirmed_FiltersProfilesAndExtractedMemoryByCalibrationVersion()
    {
        const long currentCalibrationVersion = 8;
        var messageQuery = new FakeMessageQuery(CreateMessageSnapshot(
            perspectiveState: PerspectiveStateV1.Confirmed,
            calibrationVersion: currentCalibrationVersion));
        var currentProfile = CreateProfile(
            subjectType: ParticipantSubjectTypeV1.WechatAccount,
            calibrationVersion: currentCalibrationVersion,
            profileRevision: 3);
        var staleProfile = CreateProfile(
            subjectType: ParticipantSubjectTypeV1.Contact,
            calibrationVersion: currentCalibrationVersion - 1,
            profileRevision: 5);
        var otherNamespaceProfile = CreateProfile(
            subjectType: ParticipantSubjectTypeV1.Contact,
            calibrationVersion: currentCalibrationVersion,
            profileRevision: 6) with { IdentityNamespace = "namespace-2" };
        var currentExtractedMemory = CreateMemoryItem(
            MemoryOriginV1.MessageExtracted,
            "current extracted memory",
            calibrationVersion: currentCalibrationVersion,
            sourceMessageIds: [Guid.NewGuid()],
            sourceMessageCursor: "current-cursor");
        var staleExtractedMemory = CreateMemoryItem(
            MemoryOriginV1.MessageExtracted,
            "stale extracted memory",
            calibrationVersion: currentCalibrationVersion - 1,
            sourceMessageIds: [Guid.NewGuid()],
            sourceMessageCursor: "stale-cursor");
        var manualMemory = CreateMemoryItem(MemoryOriginV1.UserEntered, "manual memory");
        var otherNamespaceMemory = CreateMemoryItem(
            MemoryOriginV1.MessageExtracted,
            "other namespace memory",
            calibrationVersion: currentCalibrationVersion,
            sourceMessageIds: [Guid.NewGuid()],
            sourceMessageCursor: "other-cursor") with { IdentityNamespace = "namespace-2" };
        var memoryReader = new FakeMemoryReader(CreateMemorySnapshot(
            profileStatus: ProfileStatusV1.Current,
            participantProfiles: [currentProfile, staleProfile, otherNamespaceProfile],
            memoryItems: [currentExtractedMemory, staleExtractedMemory, otherNamespaceMemory, manualMemory]));
        var builder = new ContextBuilderV1(messageQuery, memoryReader);

        var result = await builder.BuildAsync(CreateRequest(), CreateAuthorizedScope());

        var profile = Assert.Single(result.ParticipantProfiles);
        Assert.Equal(currentProfile.SubjectId, profile.SubjectId);
        Assert.Equal(currentCalibrationVersion, profile.CalibrationVersion);
        Assert.Equal(2, result.MemoryItems.Count);
        Assert.Contains(result.MemoryItems, item => item.MemoryId == currentExtractedMemory.MemoryId);
        Assert.Equal(
            Assert.Single(currentExtractedMemory.SourceMessageIds),
            Assert.Single(result.MemoryItems.Single(item => item.MemoryId == currentExtractedMemory.MemoryId).SourceMessageIds));
        Assert.Contains(result.MemoryItems, item => item.MemoryId == manualMemory.MemoryId);
        Assert.DoesNotContain(result.MemoryItems, item => item.MemoryId == staleExtractedMemory.MemoryId);
        Assert.DoesNotContain(result.MemoryItems, item => item.MemoryId == otherNamespaceMemory.MemoryId);
    }

    [Fact]
    public async Task BuildAsync_WhenTextBudgetIsExhausted_KeepsNewestMessageAndReportsTruncationAndOmission()
    {
        var oldestMessageId = Guid.NewGuid();
        var newestMessageId = Guid.NewGuid();
        var messageQuery = new FakeMessageQuery(CreateMessageSnapshot(
            perspectiveState: PerspectiveStateV1.Confirmed,
            calibrationVersion: 2,
            messages:
            [
                CreateMessage(oldestMessageId, "old"),
                CreateMessage(newestMessageId, "😀abcdef")
            ]));
        var memoryReader = new FakeMemoryReader(CreateMemorySnapshot(
            profileStatus: ProfileStatusV1.Current,
            participantProfiles: [CreateProfile(calibrationVersion: 2)],
            memoryItems: [CreateMemoryItem(MemoryOriginV1.UserEntered, "unused memory")]));
        var builder = new ContextBuilderV1(
            messageQuery,
            memoryReader,
            options: new ContextBuilderOptionsV1 { MaximumTotalTextCodePoints = 4 });

        var result = await builder.BuildAsync(CreateRequest(), CreateAuthorizedScope());

        var recentMessage = Assert.Single(result.RecentMessages);
        Assert.Equal(newestMessageId, recentMessage.MessageId);
        Assert.Equal("😀abc", recentMessage.Text);
        var omissions = OmissionCodes(result);
        Assert.Contains(ContextOmissionCodeV1.RecentMessageTextTruncated, omissions);
        Assert.Contains(ContextOmissionCodeV1.RecentMessagesOmitted, omissions);
        Assert.Contains(ContextOmissionCodeV1.MemoryItemsOmitted, omissions);
        Assert.Contains(ContextOmissionCodeV1.ParticipantProfilesOmitted, omissions);
    }

    [Fact]
    public async Task BuildAsync_WhenMemoryExceedsRemainingBudget_TruncatesTextAndEvidenceWithOmissions()
    {
        var sourceMessageIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var messageQuery = new FakeMessageQuery(CreateMessageSnapshot(
            perspectiveState: PerspectiveStateV1.Confirmed,
            calibrationVersion: 6,
            messages: [CreateMessage(Guid.NewGuid(), "hi")]));
        var memoryItem = CreateMemoryItem(
            MemoryOriginV1.MessageExtracted,
            "memory",
            calibrationVersion: 6,
            sourceMessageIds: sourceMessageIds,
            sourceMessageCursor: "cursor-6");
        var memoryReader = new FakeMemoryReader(CreateMemorySnapshot(
            profileStatus: ProfileStatusV1.Current,
            participantProfiles: [CreateProfile(calibrationVersion: 6)],
            memoryItems: [memoryItem]));
        var builder = new ContextBuilderV1(
            messageQuery,
            memoryReader,
            options: new ContextBuilderOptionsV1
            {
                MaximumTotalTextCodePoints = 4,
                MaximumMemoryEvidenceIdsPerItem = 1
            });

        var result = await builder.BuildAsync(CreateRequest(), CreateAuthorizedScope());

        var returnedMemory = Assert.Single(result.MemoryItems);
        Assert.Equal("me", returnedMemory.Text);
        Assert.Equal(sourceMessageIds[0], Assert.Single(returnedMemory.SourceMessageIds));
        var omissions = OmissionCodes(result);
        Assert.Contains(ContextOmissionCodeV1.MemoryItemTextTruncated, omissions);
        Assert.Contains(ContextOmissionCodeV1.MemoryEvidenceTruncated, omissions);
        Assert.Contains(ContextOmissionCodeV1.ParticipantProfilesOmitted, omissions);
    }

    [Fact]
    public async Task BuildAsync_WhenRequestedAnchorIsUnavailable_DoesNotSubstituteLatestMessages()
    {
        var snapshot = CreateMessageSnapshot(messages: []) with
        {
            AnchorMessageFound = false,
            HasMoreMessages = true
        };
        var messageQuery = new FakeMessageQuery(snapshot);
        var builder = new ContextBuilderV1(messageQuery, new FakeMemoryReader(CreateMemorySnapshot()));
        var request = CreateRequest() with { AnchorMessageId = Guid.NewGuid() };

        var result = await builder.BuildAsync(request, CreateAuthorizedScope());

        Assert.Empty(result.RecentMessages);
        Assert.Contains(ContextOmissionCodeV1.AnchorMessageUnavailable, OmissionCodes(result));
        Assert.DoesNotContain(result.RecentMessages, message => message.Text == "hello");
    }

    private ContextRequestV1 CreateRequest() => new()
    {
        AccountId = _accountId,
        ConversationId = _conversationId,
        IncludeMemory = true
    };

    private AuthorizedContextScopeV1 CreateAuthorizedScope() => new(_accountId, _conversationId);

    private MessageQuerySnapshotV1 CreateMessageSnapshot(
        PerspectiveStateV1 perspectiveState = PerspectiveStateV1.Confirmed,
        long calibrationVersion = 1,
        IReadOnlyList<MessageQueryItemV1>? messages = null) => new()
    {
        AccountId = _accountId,
        ConversationId = _conversationId,
        ConversationType = "Direct",
        IdentityNamespace = "namespace-1",
        MessageSetRevision = "message-revision-1",
        PerspectiveState = perspectiveState,
        CalibrationVersion = calibrationVersion,
        HistoryCoverage = MessageHistoryCoverageV1.Complete,
        Messages = messages ?? [CreateMessage(Guid.NewGuid(), "hello")]
    };

    private MemoryContextSnapshotV1 CreateMemorySnapshot(
        ProfileStatusV1 profileStatus = ProfileStatusV1.Current,
        int unprofiledTextCharacters = 0,
        string? consolidationSourceMessageCursor = null,
        IReadOnlyList<ParticipantProfileSnapshotV1>? participantProfiles = null,
        IReadOnlyList<MemoryItemV1>? memoryItems = null) => new()
    {
        AccountId = _accountId,
        ConversationId = _conversationId,
        Revision = "memory-revision-1",
        ProfileStatus = profileStatus,
        UnprofiledTextCharacters = unprofiledTextCharacters,
        ConsolidationSourceMessageCursor = consolidationSourceMessageCursor,
        ParticipantProfiles = participantProfiles ?? Array.Empty<ParticipantProfileSnapshotV1>(),
        MemoryItems = memoryItems ?? Array.Empty<MemoryItemV1>()
    };

    private static MessageQueryItemV1 CreateMessage(Guid messageId, string text) => new()
    {
        MessageId = messageId,
        Text = text,
        Direction = MessageDirectionV1.Incoming,
        DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
    };

    private MemoryItemV1 CreateMemoryItem(
        MemoryOriginV1 origin,
        string text,
        long? calibrationVersion = null,
        IReadOnlyList<Guid>? sourceMessageIds = null,
        string? sourceMessageCursor = null) => new()
    {
        MemoryId = Guid.NewGuid(),
        AccountId = _accountId,
        SubjectType = ParticipantSubjectTypeV1.Contact,
        SubjectId = _contactId,
        ConversationId = _conversationId,
        IdentityNamespace = origin == MemoryOriginV1.MessageExtracted ? "namespace-1" : null,
        Text = text,
        Origin = origin,
        Status = MemoryItemStatusV1.Active,
        UpdatedAt = DateTimeOffset.UtcNow,
        SourceMessageIds = sourceMessageIds ?? Array.Empty<Guid>(),
        CalibrationVersion = calibrationVersion,
        SourceMessageCursor = sourceMessageCursor,
        Version = 1
    };

    private ParticipantProfileSnapshotV1 CreateProfile(
        ParticipantSubjectTypeV1 subjectType = ParticipantSubjectTypeV1.Contact,
        long calibrationVersion = 1,
        long profileRevision = 1) => new()
    {
        AccountId = _accountId,
        ConversationId = _conversationId,
        IdentityNamespace = "namespace-1",
        SubjectType = subjectType,
        SubjectId = subjectType == ParticipantSubjectTypeV1.Contact ? _contactId : _accountId,
        ProfileRevision = profileRevision,
        CalibrationVersion = calibrationVersion,
        Status = ProfileStatusV1.Current,
        Summary = "profile summary",
        SourceMessageCursor = "profile-cursor",
        EvidenceMessageIds = [Guid.NewGuid()],
        GeneratedAt = DateTimeOffset.UtcNow
    };

    private static HashSet<ContextOmissionCodeV1> OmissionCodes(ContextBundleV1 context) =>
        context.Omissions.Select(omission => omission.Code).ToHashSet();

    private sealed class FakeMessageQuery(MessageQuerySnapshotV1 snapshot) : IMessageQueryV1
    {
        public int CallCount { get; private set; }
        public MessageQueryRequestV1? LastRequest { get; private set; }

        public ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class FakeMemoryReader(MemoryContextSnapshotV1 snapshot) : IMemoryContextReaderV1
    {
        public int CallCount { get; private set; }
        public MemoryContextReadRequestV1? LastRequest { get; private set; }

        public ValueTask<MemoryContextSnapshotV1> ReadAsync(
            MemoryContextReadRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(snapshot);
        }
    }
}
