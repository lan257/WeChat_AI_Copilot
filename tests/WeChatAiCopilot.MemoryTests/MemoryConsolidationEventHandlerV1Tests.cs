using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class MemoryConsolidationEventHandlerV1Tests
{
    [Fact]
    public async Task PerspectiveConfirmedWithoutGenerator_DefersWithoutWritingStateOrCursor()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var source = new StaticMessageSource(accountId, conversationId, [new MessageQueryItemV1
        {
            MessageId = Guid.NewGuid(),
            Text = "confirmed text",
            MessageType = "Text",
            Direction = MessageDirectionV1.Incoming,
            DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
        }]);
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId);
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator: null);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.PerspectiveConfirmed,
            CalibrationVersion = 7
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.Deferred, result.Outcome);
        Assert.Equal("generator_unavailable", result.DetailCode);
        Assert.Equal(0, repository.WriteCount);
    }

    [Fact]
    public async Task MessageCreatedBeforeConfirmationOutboxEvent_BootstrapsInitialConfirmedWindow()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var source = new StaticMessageSource(accountId, conversationId, [new MessageQueryItemV1
        {
            MessageId = messageId,
            Text = "persisted before confirmation event was consumed",
            MessageType = "Text",
            Direction = MessageDirectionV1.Incoming,
            DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
        }]);
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId);
        var generator = new SyntheticConsolidationGenerator();
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.MessageCreated,
            MessageCursor = messageId.ToString("N"),
            CalibrationVersion = 7
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.Consolidated, result.Outcome);
        Assert.Equal(MemoryConsolidationBatchKindV1.Initial, Assert.Single(generator.Requests).BatchKind);
        Assert.Equal(messageId, Assert.Single(generator.Requests[0].Messages).MessageId);
        var profile = Assert.Single(repository.LastCommit!.Output.ParticipantProfiles);
        Assert.Equal(ParticipantSubjectTypeV1.Contact, profile.SubjectType);
        Assert.Equal(messageId, Assert.Single(profile.EvidenceMessageIds));
        Assert.Equal(1, repository.BatchCommitCount);
        Assert.Equal(0, repository.AccumulationWriteCount);
    }

    [Fact]
    public async Task MessageCreatedWhilePerspectiveIsPendingDoesNotGenerateOrUpdateMemoryState()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var source = new StaticMessageSource(accountId, conversationId,
        [new MessageQueryItemV1
        {
            MessageId = Guid.NewGuid(),
            Text = "synthetic pending perspective text",
            MessageType = "Text",
            Direction = MessageDirectionV1.Outgoing,
            DirectionEvidence = DirectionEvidenceV1.DefaultSmallSenderId
        }],
        perspectiveState: PerspectiveStateV1.PendingConfirmation);
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId);
        var generator = new SyntheticConsolidationGenerator();
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.MessageCreated,
            CalibrationVersion = 7
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.NoOp, result.Outcome);
        Assert.Equal(nameof(ConsolidationCaptureFailureCodeV1.PerspectiveNotConfirmed), result.DetailCode);
        Assert.Empty(generator.Requests);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(0, repository.BatchCommitCount);
    }

    [Fact]
    public async Task PerspectiveChanged_CapturesCurrentCalibrationAndCommitsAFullRebuild()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var incomingId = Guid.NewGuid();
        var outgoingId = Guid.NewGuid();
        var source = new StaticMessageSource(accountId, conversationId,
        [
            new MessageQueryItemV1
            {
                MessageId = incomingId,
                Text = "synthetic contact wording",
                MessageType = "Text",
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            },
            new MessageQueryItemV1
            {
                MessageId = outgoingId,
                Text = "synthetic account wording",
                MessageType = "Text",
                Direction = MessageDirectionV1.Outgoing,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            }
        ], calibrationVersion: 8, identityNamespace: "ns-after-switch");
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId);
        var generator = new SyntheticConsolidationGenerator();
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.PerspectiveChanged,
            CalibrationVersion = 8
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.Consolidated, result.Outcome);
        var generated = Assert.Single(generator.Requests);
        Assert.Equal(MemoryConsolidationBatchKindV1.Rebuild, generated.BatchKind);
        Assert.Equal(8, generated.CalibrationVersion);
        Assert.Equal("ns-after-switch", generated.IdentityNamespace);
        Assert.Equal(new[] { incomingId, outgoingId }, generated.Messages.Select(message => message.MessageId));
        Assert.Equal(new[] { MessageDirectionV1.Incoming, MessageDirectionV1.Outgoing },
            generated.Messages.Select(message => message.Direction));
        Assert.Equal(MemoryConsolidationBatchKindV1.Rebuild, repository.LastCommit!.BatchKind);
        Assert.Equal(8, repository.LastCommit.Capture.CalibrationVersion);
        Assert.Equal(2, repository.LastCommit.Output.ParticipantProfiles.Count);
        Assert.Contains(repository.LastCommit.Output.ParticipantProfiles, profile =>
            profile.SubjectType == ParticipantSubjectTypeV1.Contact
            && profile.EvidenceMessageIds.Contains(incomingId));
        Assert.Contains(repository.LastCommit.Output.ParticipantProfiles, profile =>
            profile.SubjectType == ParticipantSubjectTypeV1.WechatAccount
            && profile.EvidenceMessageIds.Contains(outgoingId));
    }

    [Theory]
    [InlineData(7, 8, MemoryConsolidationEventOutcomeV1.NoOp, "calibration_version_stale")]
    [InlineData(9, 8, MemoryConsolidationEventOutcomeV1.Deferred, "calibration_version_changed")]
    public async Task HandleAsync_ResolvesCalibrationBeforeClassifyingOutdatedEvents(
        long eventCalibrationVersion,
        long currentCalibrationVersion,
        MemoryConsolidationEventOutcomeV1 expectedOutcome,
        string expectedDetailCode)
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var source = new StaticMessageSource(
            accountId,
            conversationId,
            [new MessageQueryItemV1
            {
                MessageId = Guid.NewGuid(),
                Text = "synthetic calibration event",
                MessageType = "Text",
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            }],
            calibrationVersion: currentCalibrationVersion);
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId);
        var generator = new SyntheticConsolidationGenerator();
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.PerspectiveConfirmed,
            CalibrationVersion = eventCalibrationVersion
        });

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedDetailCode, result.DetailCode);
        Assert.Empty(generator.Requests);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(0, repository.BatchCommitCount);
    }

    [Fact]
    public async Task MessageCreated_OverThresholdWithoutGeneratorDoesNotAdvanceTheSharedCursor()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        var observedId = Guid.NewGuid();
        var eventMessageId = Guid.NewGuid();
        var source = new StaticMessageSource(accountId, conversationId,
        [
            new MessageQueryItemV1
            {
                MessageId = startId,
                Text = "already consolidated",
                MessageType = "Text",
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            },
            new MessageQueryItemV1
            {
                MessageId = observedId,
                Text = new string('x', 5000),
                MessageType = "Text",
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            },
            new MessageQueryItemV1
            {
                MessageId = eventMessageId,
                Text = "y",
                MessageType = "Text",
                Direction = MessageDirectionV1.Outgoing,
                DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
            }
        ]);
        var state = new MemoryConsolidationStateV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            IdentityNamespace = "ns-1",
            CalibrationVersion = 7,
            MessageSetRevision = "revision-1",
            ConsolidationSourceMessageCursor = startId.ToString("N"),
            ObservedMessageCursor = observedId.ToString("N"),
            UnprofiledTextCharacters = 5000,
            InitialConsolidationCompleted = true,
            Status = ProfileStatusV1.Accumulating,
            StateVersion = 3
        };
        var repository = new FakeConsolidationStateRepository(accountId, conversationId, contactId, state);
        var handler = new MemoryConsolidationEventHandlerV1(
            new ConsolidationMessageCollectorV1(source),
            repository,
            generator: null);

        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.MessageCreated,
            MessageCursor = eventMessageId.ToString("N"),
            CalibrationVersion = 7
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.Deferred, result.Outcome);
        Assert.Equal("generator_unavailable", result.DetailCode);
        Assert.Equal(0, repository.AccumulationWriteCount);
        Assert.Equal(0, repository.BatchCommitCount);
        Assert.Equal(startId.ToString("N"), state.ConsolidationSourceMessageCursor);
        Assert.Equal(observedId.ToString("N"), state.ObservedMessageCursor);
        Assert.Equal(5000, state.UnprofiledTextCharacters);
    }

    private sealed class StaticMessageSource(
        Guid accountId,
        Guid conversationId,
        IReadOnlyList<MessageQueryItemV1> messages,
        long calibrationVersion = 7,
        string identityNamespace = "ns-1",
        PerspectiveStateV1 perspectiveState = PerspectiveStateV1.Confirmed) : IConsolidationMessageSourceV1
    {
        public ValueTask<ConsolidationMessagePageV1> GetPageAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            var anchorIndex = request.AnchorMessageId is null
                ? messages.Count
                : IndexOf(request.AnchorMessageId.Value);
            var anchorFound = request.AnchorMessageId is null || anchorIndex >= 0;
            if (!anchorFound)
            {
                return ValueTask.FromResult(CreatePage([], hasMore: false, anchorFound: false));
            }

            var pageEnd = anchorIndex;
            var pageStart = Math.Max(0, pageEnd - (request.Limit - (request.AnchorMessageId is null ? 0 : 1)));
            var pageMessages = messages.Skip(pageStart).Take(pageEnd - pageStart).ToArray();
            if (request.AnchorMessageId is { } anchor)
            {
                pageMessages = pageMessages.Append(messages[anchorIndex]).ToArray();
            }

            return ValueTask.FromResult(CreatePage(pageMessages, pageStart > 0, anchorFound: true));
        }

        private int IndexOf(Guid messageId)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (messages[index].MessageId == messageId)
                {
                    return index;
                }
            }

            return -1;
        }

        private ConsolidationMessagePageV1 CreatePage(
            IReadOnlyList<MessageQueryItemV1> pageMessages,
            bool hasMore,
            bool anchorFound) => new()
        {
            AccountId = accountId,
            ConversationId = conversationId,
            ConversationType = "Direct",
            IdentityNamespace = identityNamespace,
            MessageSetRevision = "revision-1",
            PerspectiveState = perspectiveState,
            CalibrationVersion = calibrationVersion,
            HasMoreMessages = hasMore,
            AnchorMessageFound = anchorFound,
            Messages = pageMessages
        };

    }

    private sealed class FakeConsolidationStateRepository(
        Guid accountId,
        Guid conversationId,
        Guid contactId,
        MemoryConsolidationStateV1? initialState = null) : IConsolidationStateRepositoryV1
    {
        public int WriteCount { get; private set; }
        public int BatchCommitCount { get; private set; }
        public int AccumulationWriteCount { get; private set; }
        public MemoryConsolidationBatchCommitV1? LastCommit { get; private set; }

        public ValueTask<MemoryConversationScopeV1?> ResolveScopeAsync(
            Guid requestedAccountId,
            Guid requestedConversationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MemoryConversationScopeV1?>(
                requestedAccountId == accountId && requestedConversationId == conversationId
                    ? new MemoryConversationScopeV1(accountId, conversationId, contactId)
                    : null);

        public ValueTask<MemoryConsolidationStateV1?> GetStateAsync(
            Guid requestedAccountId,
            Guid requestedConversationId,
            string identityNamespace,
            long calibrationVersion,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                initialState is not null
                && requestedAccountId == accountId
                && requestedConversationId == conversationId
                && calibrationVersion == initialState.CalibrationVersion
                && string.Equals(identityNamespace, initialState.IdentityNamespace, StringComparison.Ordinal)
                    ? initialState
                    : null);

        public ValueTask<bool> IsEventProcessedAsync(
            MemoryConversationScopeV1 scope,
            Guid eventId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask RecordAccumulationAsync(
            MemoryConsolidationStateV1 nextState,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default)
        {
            AccumulationWriteCount++;
            return WriteNotExpected();
        }

        public ValueTask RecordEmptyInitialWindowAsync(
            MemoryConsolidationStateV1 nextState,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => WriteNotExpected();

        public ValueTask<ConsolidationBatchClaimResultV1> TryBeginBatchAsync(
            MemoryConversationScopeV1 scope,
            string identityNamespace,
            long calibrationVersion,
            string messageSetRevision,
            string highWaterMessageCursor,
            MemoryConsolidationBatchKindV1 batchKind,
            Guid batchId,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ConsolidationBatchClaimResultV1(
                ConsolidationBatchClaimOutcomeV1.Claimed,
                new MemoryConsolidationStateV1
                {
                    AccountId = scope.AccountId,
                    ConversationId = scope.ConversationId,
                    IdentityNamespace = identityNamespace,
                    CalibrationVersion = calibrationVersion,
                    MessageSetRevision = messageSetRevision,
                    Status = batchKind == MemoryConsolidationBatchKindV1.Rebuild
                        ? ProfileStatusV1.Rebuilding
                        : ProfileStatusV1.Consolidating,
                    StateVersion = expectedStateVersion + 1,
                    ActiveBatchId = batchId
                }));

        public ValueTask MarkReprocessRequiredAsync(
            Guid requestedAccountId,
            Guid requestedConversationId,
            string identityNamespace,
            long calibrationVersion,
            long expectedStateVersion,
            string observedMessageSetRevision,
            CancellationToken cancellationToken = default) => WriteNotExpected();

        public ValueTask MarkBatchFailedAsync(
            Guid requestedAccountId,
            Guid requestedConversationId,
            string identityNamespace,
            long calibrationVersion,
            Guid batchId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => WriteNotExpected();

        public ValueTask CommitGeneratedBatchAsync(
            MemoryConsolidationBatchCommitV1 commit,
            CancellationToken cancellationToken = default)
        {
            BatchCommitCount++;
            LastCommit = commit;
            return ValueTask.CompletedTask;
        }

        private ValueTask WriteNotExpected()
        {
            WriteCount++;
            throw new InvalidOperationException("No persistence write is expected when a generator is absent.");
        }
    }

    private sealed class SyntheticConsolidationGenerator : IMemoryConsolidationGeneratorV1
    {
        public List<MemoryConsolidationGenerationRequestV1> Requests { get; } = [];

        public ValueTask<MemoryConsolidationGenerationOutputV1> GenerateAsync(
            MemoryConsolidationGenerationRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var profiles = request.Messages
                .GroupBy(message => message.Direction)
                .Select(group => new ParticipantProfileDraftV1
                {
                    SubjectType = group.Key == MessageDirectionV1.Outgoing
                        ? ParticipantSubjectTypeV1.WechatAccount
                        : ParticipantSubjectTypeV1.Contact,
                    Summary = group.Key == MessageDirectionV1.Outgoing
                        ? "synthetic account writing evidence"
                        : "synthetic contact writing evidence",
                    EvidenceMessageIds = [group.First().MessageId]
                })
                .ToArray();
            return ValueTask.FromResult(new MemoryConsolidationGenerationOutputV1
            {
                ParticipantProfiles = profiles,
                MemoryItems = []
            });
        }
    }
}
