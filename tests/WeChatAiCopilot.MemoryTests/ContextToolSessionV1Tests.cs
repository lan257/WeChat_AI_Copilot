using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class ContextToolSessionV1Tests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    [Fact]
    public async Task SearchHistoryAsync_UsesInclusiveAnchoredPagesWithoutReturningDuplicateMessages()
    {
        var oldestId = Guid.NewGuid();
        var overlapId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var oldest = CreateMessage(oldestId, "needle older");
        var overlap = CreateMessage(overlapId, "no match");
        var newest = CreateMessage(newestId, "needle newest");
        var query = new FakeMessageQuery(request => request.AnchorMessageId switch
        {
            null => CreateSnapshot([oldest, newest], hasMore: true),
            var anchor when anchor == oldestId => CreateSnapshot([oldest, overlap], hasMore: false),
            _ => throw new InvalidOperationException("Unexpected test anchor.")
        });
        var session = CreateSession(query, options: new ContextToolOptionsV1 { PageSize = 2 });

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1
        {
            Query = "needle",
            Limit = 10
        });

        Assert.Equal(HistorySearchStatusV1.Complete, result.Status);
        Assert.Equal("messages-1", result.MessageSetRevision);
        Assert.Equal(new[] { newestId, oldestId }, result.Hits.Select(hit => hit.Message.MessageId));
        Assert.Equal(2, result.PagesRead);
        Assert.Equal(3, result.MessagesScanned);
        Assert.Equal(new Guid?[] { null, oldestId }, query.Requests.Select(request => request.AnchorMessageId));
        Assert.All(query.Requests, request =>
        {
            Assert.Equal(_accountId, request.AccountId);
            Assert.Equal(_conversationId, request.ConversationId);
        });
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenAnchorIsMissing_ReturnsAnchorNotFoundWithoutFallback()
    {
        var query = new FakeMessageQuery(request => CreateSnapshot(
            [],
            anchorFound: request.AnchorMessageId is null,
            hasMore: false));
        var session = CreateSession(query);
        var anchorId = Guid.NewGuid();

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1
        {
            Query = "needle",
            AnchorMessageId = anchorId
        });

        Assert.Equal(HistorySearchStatusV1.AnchorNotFound, result.Status);
        Assert.Empty(result.Hits);
        Assert.Single(query.Requests);
        Assert.Equal(anchorId, query.Requests[0].AnchorMessageId);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenMessageSetRevisionChangesAcrossPages_DiscardsAllHits()
    {
        var oldestId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var query = new FakeMessageQuery(request => request.AnchorMessageId is null
            ? CreateSnapshot(
                [CreateMessage(oldestId, "needle first page"), CreateMessage(newestId, "needle latest")],
                hasMore: true,
                revision: "messages-1")
            : CreateSnapshot(
                [CreateMessage(oldestId, "needle changed page"), CreateMessage(middleId, "needle older")],
                hasMore: false,
                revision: "messages-2"));
        var session = CreateSession(query, options: new ContextToolOptionsV1 { PageSize = 2 });

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1 { Query = "needle" });

        Assert.Equal(HistorySearchStatusV1.RevisionChanged, result.Status);
        Assert.Empty(result.Hits);
        Assert.Equal("messages-1", result.MessageSetRevision);
        Assert.Equal("messages-2", result.ObservedMessageSetRevision);
        Assert.Equal(2, result.PagesRead);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenExpectedMessageSetRevisionIsStale_ReturnsNoHits()
    {
        var query = new FakeMessageQuery(_ => CreateSnapshot(
            [CreateMessage(Guid.NewGuid(), "needle")],
            revision: "messages-current"));
        var session = CreateSession(query);

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1
        {
            Query = "needle",
            ExpectedMessageSetRevision = "messages-old"
        });

        Assert.Equal(HistorySearchStatusV1.RevisionChanged, result.Status);
        Assert.Empty(result.Hits);
        Assert.Null(result.MessageSetRevision);
        Assert.Equal("messages-current", result.ObservedMessageSetRevision);
        Assert.Equal(0, result.MessagesScanned);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenPageOrResultBoundIsReached_ReportsTruncationAndContinuationAnchor()
    {
        var oldestId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var query = new FakeMessageQuery(_ => CreateSnapshot(
            [CreateMessage(oldestId, "needle oldest"), CreateMessage(newestId, "needle newest")],
            hasMore: true));
        var session = CreateSession(query, options: new ContextToolOptionsV1
        {
            PageSize = 2,
            MaximumPages = 1
        });

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1
        {
            Query = "needle"
        });

        Assert.Equal(HistorySearchStatusV1.Truncated, result.Status);
        Assert.True(result.HasMoreMessages);
        Assert.Equal(oldestId, result.NextAnchorMessageId);
        Assert.True(result.NextPageExcludesAnchor);
        Assert.Equal(1, result.PagesRead);
        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenResultLimitIsReachedAcrossPages_ContinuesAfterLastReturnedHit()
    {
        var oldestReturnedId = Guid.NewGuid();
        var middleReturnedId = Guid.NewGuid();
        var olderHitId = Guid.NewGuid();
        var query = new FakeMessageQuery(request => request.AnchorMessageId is null
            ? CreateSnapshot(
                [CreateMessage(oldestReturnedId, "needle oldest returned"), CreateMessage(middleReturnedId, "needle middle")],
                hasMore: true)
            : CreateSnapshot(
                [CreateMessage(olderHitId, "needle next page"), CreateMessage(oldestReturnedId, "needle overlap")],
                hasMore: false));
        var session = CreateSession(query, options: new ContextToolOptionsV1
        {
            PageSize = 2,
            MaximumSearchResults = 2
        });

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1
        {
            Query = "needle",
            Limit = 2
        });

        Assert.Equal(HistorySearchStatusV1.Truncated, result.Status);
        Assert.Equal(new[] { middleReturnedId, oldestReturnedId }, result.Hits.Select(hit => hit.Message.MessageId));
        Assert.Equal(oldestReturnedId, result.NextAnchorMessageId);
        Assert.True(result.NextPageExcludesAnchor);
        Assert.Equal(2, result.PagesRead);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenMessageTextExceedsSearchBudget_ReportsIncompleteSearch()
    {
        var messageId = Guid.NewGuid();
        var query = new FakeMessageQuery(_ => CreateSnapshot(
            [CreateMessage(messageId, "prefix needle appears later")],
            hasMore: false));
        var session = CreateSession(query, options: new ContextToolOptionsV1
        {
            MaximumSearchTextCodePointsPerMessage = 6
        });

        var result = await session.SearchHistoryAsync(new HistorySearchRequestV1 { Query = "needle" });

        Assert.Equal(HistorySearchStatusV1.Truncated, result.Status);
        Assert.True(result.SearchTextTruncated);
        Assert.Empty(result.Hits);
        Assert.Equal(1, result.MessagesScanned);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenM2ReturnsAnotherScope_RejectsThePage()
    {
        var query = new FakeMessageQuery(_ => CreateSnapshot(
            [CreateMessage(Guid.NewGuid(), "needle")],
            accountId: Guid.NewGuid()));
        var session = CreateSession(query);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SearchHistoryAsync(new HistorySearchRequestV1 { Query = "needle" }).AsTask());
    }

    [Fact]
    public async Task GetMessageEvidenceAsync_ReturnsOnlyRequestedInScopeIdsAndCountsMissingIdsWithoutEchoingThem()
    {
        var requestedId = Guid.NewGuid();
        var otherPageMessageId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var query = new FakeMessageQuery(request => request.AnchorMessageId == requestedId
            ? CreateSnapshot(
                [CreateMessage(otherPageMessageId, "must not be returned"), CreateMessage(requestedId, "requested evidence")])
            : CreateSnapshot([], anchorFound: false));
        var session = CreateSession(query);

        var result = await session.GetMessageEvidenceAsync(new MessageEvidenceRequestV1
        {
            MessageIds = [requestedId, missingId]
        });

        Assert.Equal(MessageEvidenceStatusV1.Partial, result.Status);
        Assert.Equal(1, result.OmittedCount);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(requestedId, evidence.Message.MessageId);
        Assert.Equal("requested evidence", evidence.Message.Text);
        Assert.DoesNotContain(result.Evidence, item => item.Message.MessageId == missingId);
        Assert.DoesNotContain(result.Evidence, item => item.Message.MessageId == otherPageMessageId);
        Assert.All(query.Requests, request => Assert.Equal(1, request.Limit));
    }

    [Fact]
    public async Task GetMessageEvidenceAsync_WhenExpectedMessageSetRevisionIsStale_ReturnsNoEvidence()
    {
        var messageId = Guid.NewGuid();
        var query = new FakeMessageQuery(_ => CreateSnapshot(
            [CreateMessage(messageId, "evidence")],
            revision: "messages-current"));
        var session = CreateSession(query);

        var result = await session.GetMessageEvidenceAsync(new MessageEvidenceRequestV1
        {
            MessageIds = [messageId],
            ExpectedMessageSetRevision = "messages-old"
        });

        Assert.Equal(MessageEvidenceStatusV1.RevisionChanged, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Null(result.MessageSetRevision);
        Assert.Equal("messages-current", result.ObservedMessageSetRevision);
    }

    [Fact]
    public async Task GetMessageEvidenceAsync_WhenMoreThanTwentyIdsAreRequested_IsBoundedAndMarkedTruncated()
    {
        var requestedIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        var query = new FakeMessageQuery(request => CreateSnapshot(
            [CreateMessage(request.AnchorMessageId!.Value, "evidence")]));
        var session = CreateSession(query);

        var result = await session.GetMessageEvidenceAsync(new MessageEvidenceRequestV1
        {
            MessageIds = requestedIds
        });

        Assert.Equal(MessageEvidenceStatusV1.Truncated, result.Status);
        Assert.Equal(1, result.OmittedCount);
        Assert.Equal(20, result.Evidence.Count);
        Assert.Equal(20, query.Requests.Count);
        Assert.DoesNotContain(result.Evidence, item => item.Message.MessageId == requestedIds[20]);
    }

    [Fact]
    public async Task SearchHistoryAsync_WhenCancelledDuringPaginationStopsBeforeTheNextPage()
    {
        using var cancellation = new CancellationTokenSource();
        var query = new FakeMessageQuery((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(CreateSnapshot(
                [CreateMessage(Guid.NewGuid(), "needle")],
                hasMore: true));
        });
        var session = CreateSession(query, options: new ContextToolOptionsV1 { PageSize = 2 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SearchHistoryAsync(
            new HistorySearchRequestV1 { Query = "needle" },
            cancellation.Token).AsTask());

        Assert.Single(query.Requests);
    }

    [Fact]
    public async Task GetContextAsync_ReturnsScopeFreeProjectionFromTheFixedBoundScope()
    {
        var anchorMessageId = Guid.NewGuid();
        var context = new ContextBundleV1
        {
            AccountId = _accountId,
            ConversationId = _conversationId,
            ContextRevision = "context-revision-1",
            PerspectiveState = PerspectiveStateV1.Confirmed,
            CalibrationVersion = 3,
            ProfileStatus = ProfileStatusV1.Current,
            RecentMessages = [new ContextMessageV1 { MessageId = Guid.NewGuid(), Text = "recent" }],
            ParticipantProfiles =
            [
                new ParticipantProfileSnapshotV1
                {
                    AccountId = _accountId,
                    ConversationId = _conversationId,
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    SubjectId = Guid.NewGuid(),
                    Summary = "profile",
                    CalibrationVersion = 3,
                    Status = ProfileStatusV1.Current,
                    EvidenceMessageIds = [Guid.NewGuid()]
                }
            ],
            MemoryItems =
            [
                new MemoryItemV1
                {
                    MemoryId = Guid.NewGuid(),
                    AccountId = _accountId,
                    ConversationId = _conversationId,
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    SubjectId = Guid.NewGuid(),
                    Text = "manual memory",
                    Origin = MemoryOriginV1.UserEntered,
                    Status = MemoryItemStatusV1.Active
                }
            ]
        };
        var contextService = new FakeContextService(context);
        var messageQuery = new FakeMessageQuery(_ => CreateSnapshot([]));
        var session = CreateSession(messageQuery, contextService: contextService);

        var result = await session.GetContextAsync(new ContextToolRequestV1
        {
            RecentMessageLimit = 37,
            AnchorMessageId = anchorMessageId
        });

        Assert.Equal("context-revision-1", result.ContextRevision);
        Assert.Equal("messages-1", result.MessageSetRevision);
        Assert.Equal("recent", Assert.Single(result.RecentMessages).Text);
        Assert.Equal("profile", Assert.Single(result.ParticipantProfiles).Summary);
        Assert.Equal("manual memory", Assert.Single(result.MemoryItems).Text);
        Assert.Equal(_accountId, contextService.LastRequest!.AccountId);
        Assert.Equal(_conversationId, contextService.LastRequest.ConversationId);
        Assert.Equal(anchorMessageId, contextService.LastRequest.AnchorMessageId);
        Assert.Equal(37, contextService.LastRequest.RecentMessageLimit);
        Assert.Equal(2, messageQuery.Requests.Count);
        Assert.All(messageQuery.Requests, queryRequest =>
        {
            Assert.Equal(_accountId, queryRequest.AccountId);
            Assert.Equal(_conversationId, queryRequest.ConversationId);
            Assert.Equal(anchorMessageId, queryRequest.AnchorMessageId);
            Assert.Equal(37, queryRequest.Limit);
        });
    }

    [Fact]
    public async Task GetContextAsync_WhenMessageSetRevisionChangesDuringBuild_FailsWithoutReturningContext()
    {
        var callCount = 0;
        var query = new FakeMessageQuery(_ =>
        {
            callCount++;
            return CreateSnapshot([], revision: callCount == 1 ? "messages-before" : "messages-after");
        });
        var contextService = new FakeContextService(new ContextBundleV1
        {
            AccountId = _accountId,
            ConversationId = _conversationId,
            ContextRevision = "mixed-context-must-not-escape"
        });
        var session = CreateSession(query, contextService: contextService);

        var exception = await Assert.ThrowsAsync<ContextRevisionChangedException>(
            () => session.GetContextAsync(new ContextToolRequestV1()).AsTask());

        Assert.Equal(ContextRevisionChangedException.SafeErrorCode, exception.ErrorCode);
        Assert.Equal(ContextSnapshotChangeReasonV1.MessageSetRevisionChanged, exception.Reason);
        Assert.Equal("messages-before", exception.BeforeMessageSetRevision);
        Assert.Equal("messages-after", exception.AfterMessageSetRevision);
        Assert.Equal(1, contextService.CallCount);
        Assert.Equal(2, query.Requests.Count);
    }

    [Fact]
    public async Task GetContextAsync_WhenAnchorAvailabilityChangesDuringBuild_FailsClosed()
    {
        var callCount = 0;
        var query = new FakeMessageQuery(_ =>
        {
            callCount++;
            return CreateSnapshot([], anchorFound: callCount == 1, revision: "messages-stable");
        });
        var contextService = new FakeContextService(new ContextBundleV1
        {
            AccountId = _accountId,
            ConversationId = _conversationId,
            ContextRevision = "anchor-state-must-be-stable"
        });
        var session = CreateSession(query, contextService: contextService);

        var exception = await Assert.ThrowsAsync<ContextRevisionChangedException>(() => session.GetContextAsync(
            new ContextToolRequestV1 { AnchorMessageId = Guid.NewGuid() }).AsTask());

        Assert.Equal(ContextSnapshotChangeReasonV1.PageSnapshotChanged, exception.Reason);
        Assert.Equal("messages-stable", exception.BeforeMessageSetRevision);
        Assert.Equal("messages-stable", exception.AfterMessageSetRevision);
        Assert.Equal(2, query.Requests.Count);
    }

    [Fact]
    public async Task GetContextAsync_WhenM2ReturnsAnotherScope_RejectsBeforeBuildingContext()
    {
        var query = new FakeMessageQuery(_ => CreateSnapshot([], conversationId: Guid.NewGuid()));
        var contextService = new FakeContextService(new ContextBundleV1
        {
            AccountId = _accountId,
            ConversationId = _conversationId,
            ContextRevision = "should-not-be-built"
        });
        var session = CreateSession(query, contextService: contextService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetContextAsync(new ContextToolRequestV1()).AsTask());

        Assert.Equal(0, contextService.CallCount);
        Assert.Single(query.Requests);
    }

    [Fact]
    public void ToolContracts_DoNotExposeAccountConversationOrParticipantSubjectIds()
    {
        var scopeIdPropertyNames = new[] { "AccountId", "ConversationId", "SubjectId", "RawSenderId", "IdentityNamespace" };
        var toolContractTypes = new[]
        {
            typeof(ContextToolRequestV1),
            typeof(ContextToolContextResultV1),
            typeof(ContextToolParticipantProfileV1),
            typeof(ContextToolMemoryItemV1),
            typeof(HistorySearchRequestV1),
            typeof(HistorySearchHitV1),
            typeof(HistorySearchResultV1),
            typeof(MessageEvidenceRequestV1),
            typeof(MessageEvidenceItemV1),
            typeof(MessageEvidenceResultV1)
        };

        foreach (var contractType in toolContractTypes)
        {
            foreach (var propertyName in scopeIdPropertyNames)
            {
                Assert.Null(contractType.GetProperty(propertyName));
            }
        }
    }

    private IContextToolSessionV1 CreateSession(
        FakeMessageQuery query,
        ContextToolOptionsV1? options = null,
        FakeContextService? contextService = null)
    {
        var factory = new ContextToolSessionFactoryV1(
            query,
            contextService ?? new FakeContextService(new ContextBundleV1
            {
                AccountId = _accountId,
                ConversationId = _conversationId,
                ContextRevision = "context-revision",
                GeneratedAt = DateTimeOffset.UtcNow
            }),
            options);
        return factory.Bind(new AuthorizedContextScopeV1(_accountId, _conversationId));
    }

    private MessageQuerySnapshotV1 CreateSnapshot(
        IReadOnlyList<MessageQueryItemV1> messages,
        bool hasMore = false,
        bool anchorFound = true,
        string revision = "messages-1",
        Guid? accountId = null,
        Guid? conversationId = null) => new()
    {
        AccountId = accountId ?? _accountId,
        ConversationId = conversationId ?? _conversationId,
        ConversationType = "Direct",
        IdentityNamespace = "namespace-1",
        MessageSetRevision = revision,
        PerspectiveState = PerspectiveStateV1.Confirmed,
        CalibrationVersion = 3,
        HistoryCoverage = MessageHistoryCoverageV1.Complete,
        HasMoreMessages = hasMore,
        AnchorMessageFound = anchorFound,
        Messages = messages
    };

    private static MessageQueryItemV1 CreateMessage(Guid messageId, string text) => new()
    {
        MessageId = messageId,
        Text = text,
        OccurredAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z"),
        Direction = MessageDirectionV1.Incoming,
        DirectionEvidence = DirectionEvidenceV1.UserConfirmed
    };

    private sealed class FakeMessageQuery : IMessageQueryV1
    {
        private readonly Func<MessageQueryRequestV1, CancellationToken, ValueTask<MessageQuerySnapshotV1>> _asyncHandler;

        public FakeMessageQuery(Func<MessageQueryRequestV1, MessageQuerySnapshotV1> handler)
        {
            _asyncHandler = (request, _) => ValueTask.FromResult(handler(request));
        }

        public FakeMessageQuery(Func<MessageQueryRequestV1, CancellationToken, ValueTask<MessageQuerySnapshotV1>> asyncHandler)
        {
            _asyncHandler = asyncHandler;
        }

        public List<MessageQueryRequestV1> Requests { get; } = [];

        public ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _asyncHandler(request, cancellationToken);
        }
    }

    private sealed class FakeContextService(ContextBundleV1 context) : IContextServiceV1
    {
        public ContextRequestV1? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public ValueTask<ContextBundleV1> BuildAsync(
            ContextRequestV1 request,
            AuthorizedContextScopeV1 authorizedScope,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(context);
        }
    }
}
