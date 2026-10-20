using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class UserEnteredMemoryContextPathTests
{
    [Fact]
    public async Task CommittedCreateAndUpdateProjectTextAndEvidenceIntoContextAndChangeRevision()
    {
        var scope = new MemoryConversationScopeV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var request = new ContextRequestV1
        {
            AccountId = scope.AccountId,
            ConversationId = scope.ConversationId,
            IncludeMemory = true
        };
        var messageOne = Guid.NewGuid();
        var messageTwo = Guid.NewGuid();
        var query = new StaticMessageQuery(ConfirmedSnapshot(scope, messageOne, messageTwo));
        var reader = new SnapshotMemoryReader();
        var builder = new ContextBuilderV1(query, reader);

        var beforeCreate = await builder.BuildAsync(request, new AuthorizedContextScopeV1(scope.AccountId, scope.ConversationId));
        var createRequest = new UserEnteredMemoryCreateRequestV1
        {
            SubjectType = ParticipantSubjectTypeV1.Contact,
            Text = "prefers a quiet cafe",
            Category = "preference",
            SourceMessageIds = [messageOne]
        };
        Assert.Empty(MemoryItemRequestValidatorV1.ValidateCreate(createRequest));
        var created = CommittedItem(scope, createRequest, memoryId: Guid.NewGuid(), version: 1, updatedOffsetMinutes: 0);
        reader.Items = [created];

        var afterCreate = await builder.BuildAsync(request, new AuthorizedContextScopeV1(scope.AccountId, scope.ConversationId));

        var projectedCreate = Assert.Single(afterCreate.MemoryItems);
        Assert.Equal(created.Text, projectedCreate.Text);
        Assert.Equal(MemoryOriginV1.UserEntered, projectedCreate.Origin);
        Assert.Equal(messageOne, Assert.Single(projectedCreate.SourceMessageIds));
        Assert.NotEqual(beforeCreate.ContextRevision, afterCreate.ContextRevision);

        var updateRequest = new UserEnteredMemoryUpdateRequestV1
        {
            ExpectedVersion = created.Version,
            Text = "prefers a quiet cafe with outdoor seating",
            Category = "preference",
            SourceMessageIds = [messageTwo]
        };
        Assert.Empty(MemoryItemRequestValidatorV1.ValidateUpdate(updateRequest));
        var updated = created with
        {
            Text = updateRequest.Text!,
            Category = updateRequest.Category,
            SourceMessageIds = updateRequest.SourceMessageIds!,
            Version = created.Version + 1,
            UpdatedAt = created.UpdatedAt.AddMinutes(1)
        };
        reader.Items = [updated];

        var afterUpdate = await builder.BuildAsync(request, new AuthorizedContextScopeV1(scope.AccountId, scope.ConversationId));

        var projectedUpdate = Assert.Single(afterUpdate.MemoryItems);
        Assert.Equal(2, projectedUpdate.Version);
        Assert.Equal(updateRequest.Text, projectedUpdate.Text);
        Assert.Equal(messageTwo, Assert.Single(projectedUpdate.SourceMessageIds));
        Assert.NotEqual(afterCreate.ContextRevision, afterUpdate.ContextRevision);
    }

    [Fact]
    public async Task ContextBuilder_KeepsOnlyCurrentAccountConversationAndValidAccountSubject()
    {
        var scope = new MemoryConversationScopeV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var validContactMemory = CreateMemory(scope, ParticipantSubjectTypeV1.Contact, scope.ContactId, scope.ConversationId, "contact fact");
        var validAccountNote = CreateMemory(scope, ParticipantSubjectTypeV1.WechatAccount, scope.AccountId, null, "account note");
        var otherAccountMemory = CreateMemory(
            scope with { AccountId = Guid.NewGuid() },
            ParticipantSubjectTypeV1.Contact,
            Guid.NewGuid(),
            scope.ConversationId,
            "other account");
        var otherConversationMemory = CreateMemory(
            scope with { ConversationId = Guid.NewGuid() },
            ParticipantSubjectTypeV1.Contact,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "other conversation");
        var mismatchedAccountSubject = CreateMemory(
            scope,
            ParticipantSubjectTypeV1.WechatAccount,
            Guid.NewGuid(),
            scope.ConversationId,
            "wrong self id");
        var reader = new SnapshotMemoryReader
        {
            Items = [validContactMemory, validAccountNote, otherAccountMemory, otherConversationMemory, mismatchedAccountSubject]
        };
        var builder = new ContextBuilderV1(new StaticMessageQuery(ConfirmedSnapshot(scope)), reader);

        var result = await builder.BuildAsync(
            new ContextRequestV1 { AccountId = scope.AccountId, ConversationId = scope.ConversationId },
            new AuthorizedContextScopeV1(scope.AccountId, scope.ConversationId));

        Assert.Equal(2, result.MemoryItems.Count);
        Assert.Contains(result.MemoryItems, item => item.MemoryId == validContactMemory.MemoryId);
        Assert.Contains(result.MemoryItems, item => item.MemoryId == validAccountNote.MemoryId);
        Assert.DoesNotContain(result.MemoryItems, item => item.MemoryId == otherAccountMemory.MemoryId);
        Assert.DoesNotContain(result.MemoryItems, item => item.MemoryId == otherConversationMemory.MemoryId);
        Assert.DoesNotContain(result.MemoryItems, item => item.MemoryId == mismatchedAccountSubject.MemoryId);
    }

    [Fact]
    public void ServerResolvedParticipantSubject_UsesOnlyTheAuthorizedAccountOrContactId()
    {
        var scope = new MemoryConversationScopeV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(
            scope.AccountId,
            MemoryParticipantScopeV1.ResolveSubjectId(scope, ParticipantSubjectTypeV1.WechatAccount));
        Assert.Equal(
            scope.ContactId,
            MemoryParticipantScopeV1.ResolveSubjectId(scope, ParticipantSubjectTypeV1.Contact));
        Assert.True(MemoryParticipantScopeV1.IsSubjectIdInScope(
            scope, ParticipantSubjectTypeV1.Contact, scope.ContactId));
        Assert.False(MemoryParticipantScopeV1.IsSubjectIdInScope(
            scope, ParticipantSubjectTypeV1.Contact, Guid.NewGuid()));
    }

    [Fact]
    public void EvidenceScopeGate_RejectsAnyMessageIdMissingFromTheAuthorizedConversationSet()
    {
        var currentMessage = Guid.NewGuid();
        var otherConversationMessage = Guid.NewGuid();

        Assert.True(MemoryEvidenceScopeValidatorV1.AllRequestedEvidenceIsAuthorized(
            [currentMessage], new HashSet<Guid> { currentMessage }));
        Assert.False(MemoryEvidenceScopeValidatorV1.AllRequestedEvidenceIsAuthorized(
            [currentMessage, otherConversationMessage], new HashSet<Guid> { currentMessage }));
    }

    private static MemoryItemV1 CommittedItem(
        MemoryConversationScopeV1 scope,
        UserEnteredMemoryCreateRequestV1 request,
        Guid memoryId,
        long version,
        int updatedOffsetMinutes)
    {
        var timestamp = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
        return new MemoryItemV1
        {
            MemoryId = memoryId,
            AccountId = scope.AccountId,
            SubjectType = request.SubjectType,
            SubjectId = MemoryParticipantScopeV1.ResolveSubjectId(scope, request.SubjectType),
            ConversationId = scope.ConversationId,
            Text = request.Text!,
            Category = request.Category,
            Origin = MemoryOriginV1.UserEntered,
            Status = MemoryItemStatusV1.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp.AddMinutes(updatedOffsetMinutes),
            ObservedAt = request.ObservedAt,
            SourceMessageIds = request.SourceMessageIds!,
            Version = version
        };
    }

    private static MemoryItemV1 CreateMemory(
        MemoryConversationScopeV1 scope,
        ParticipantSubjectTypeV1 subjectType,
        Guid subjectId,
        Guid? conversationId,
        string text) => new()
    {
        MemoryId = Guid.NewGuid(),
        AccountId = scope.AccountId,
        SubjectType = subjectType,
        SubjectId = subjectId,
        ConversationId = conversationId,
        Text = text,
        Origin = MemoryOriginV1.UserEntered,
        Status = MemoryItemStatusV1.Active,
        CreatedAt = DateTimeOffset.Parse("2026-10-09T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-10-09T00:00:00Z"),
        SourceMessageIds = []
    };

    private static MessageQuerySnapshotV1 ConfirmedSnapshot(
        MemoryConversationScopeV1 scope,
        params Guid[] messageIds) => new()
    {
        AccountId = scope.AccountId,
        ConversationId = scope.ConversationId,
        ConversationType = "Direct",
        IdentityNamespace = "namespace-current",
        MessageSetRevision = "message-revision-1",
        PerspectiveState = PerspectiveStateV1.Confirmed,
        CalibrationVersion = 3,
        HistoryCoverage = MessageHistoryCoverageV1.Complete,
        Messages = messageIds.Select((id, index) => new MessageQueryItemV1
        {
            MessageId = id,
            Text = $"message {index + 1}",
            Direction = MessageDirectionV1.Incoming,
            DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
        }).ToArray()
    };

    private sealed class StaticMessageQuery(MessageQuerySnapshotV1 snapshot) : IMessageQueryV1
    {
        public ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
    }

    private sealed class SnapshotMemoryReader : IMemoryContextReaderV1
    {
        public IReadOnlyList<MemoryItemV1> Items { get; set; } = Array.Empty<MemoryItemV1>();

        public ValueTask<MemoryContextSnapshotV1> ReadAsync(
            MemoryContextReadRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            var profileStatus = request.PerspectiveState == PerspectiveStateV1.Confirmed
                ? ProfileStatusV1.Current
                : ProfileStatusV1.WaitingForConfirmation;
            return ValueTask.FromResult(new MemoryContextSnapshotV1
            {
                AccountId = request.AccountId,
                ConversationId = request.ConversationId,
                Revision = MemoryContextRevisionV1.Compute(
                    request,
                    profileStatus,
                    Items,
                    Array.Empty<ParticipantProfileSnapshotV1>(),
                    unprofiledTextCharacters: 0,
                    consolidationSourceMessageCursor: null,
                    messageSetRevision: "memory-revision-1"),
                ProfileStatus = profileStatus,
                MemoryItems = Items
            });
        }
    }
}
