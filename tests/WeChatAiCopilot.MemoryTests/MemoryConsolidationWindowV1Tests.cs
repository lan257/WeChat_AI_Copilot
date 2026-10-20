using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class MemoryConsolidationWindowV1Tests
{
    [Fact]
    public void NormalizeText_UsesLfAndCountsUnicodeCodePointsWithoutTrimming()
    {
        var normalized = MemoryConsolidationWindowV1.NormalizeText(" 😀\r\nx\ry ");

        Assert.Equal(" 😀\nx\ny ", normalized);
        Assert.Equal(7, MemoryConsolidationWindowV1.CountCodePoints(normalized));
    }

    [Fact]
    public async Task Collector_CapturesBoundedStablePagesAndFiltersIneligibleMessages()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(index => Message(
                index,
                index % 4 == 0 ? "image" : " chat \r\n😀",
                index % 4 == 0 ? "Image" : "Text",
                index % 6 == 0 ? DirectionEvidenceV1.DefaultSmallSenderId : DirectionEvidenceV1.SourceConfirmed))
            .ToArray();
        var source = new PagedMessageSource(messages);
        var collector = new ConsolidationMessageCollectorV1(source);

        var result = await collector.CaptureAllAsync(AccountId, ConversationId, expectedCalibrationVersion: 7);

        Assert.True(result.Succeeded);
        Assert.Equal(3, source.CallCount);
        Assert.Equal(250, result.Capture!.AllMessages.Count);
        Assert.Equal(Cursor(250), result.Capture.HighWaterMessageCursor);
        Assert.All(result.Capture.EligibleMessages, message => Assert.DoesNotContain('\r', message.Text));
        Assert.Equal(
            result.Capture.EligibleMessages.Sum(message => MemoryConsolidationWindowV1.CountCodePoints(message.Text)),
            result.Capture.EligibleTextCodePoints);
    }

    [Fact]
    public async Task Collector_FailsClosedWhenMessageSetChangesBetweenPages()
    {
        var source = new PagedMessageSource(
            Enumerable.Range(1, 250).Select(index => Message(index, "x")).ToArray(),
            changeRevisionAfterFirstPage: true);
        var collector = new ConsolidationMessageCollectorV1(source);

        var result = await collector.CaptureAllAsync(AccountId, ConversationId, expectedCalibrationVersion: 7);

        Assert.False(result.Succeeded);
        Assert.Equal(ConsolidationCaptureFailureCodeV1.MessageSetChangedDuringPagination, result.FailureCode);
    }

    [Fact]
    public async Task Collector_IdentifiesMessageBeforeCheckpointAsBackfillRequiringReprocess()
    {
        var source = new PagedMessageSource(
            Enumerable.Range(1, 5).Select(index => Message(index, "x")).ToArray());
        var collector = new ConsolidationMessageCollectorV1(source);

        var result = await collector.CaptureAfterCursorThroughEventAsync(
            AccountId,
            ConversationId,
            Cursor(3),
            Cursor(2),
            expectedCalibrationVersion: 7,
            IdentityNamespace);

        Assert.False(result.Succeeded);
        Assert.Equal(ConsolidationCaptureFailureCodeV1.CursorNotInNewerRange, result.FailureCode);
    }

    [Fact]
    public async Task Collector_ExcludesUnknownDirectionEvenWhenEvidenceValueIsUserConfirmed()
    {
        var unknown = Message(1, "uncertain", evidence: DirectionEvidenceV1.UserConfirmed) with
        {
            Direction = MessageDirectionV1.Unknown
        };
        var collector = new ConsolidationMessageCollectorV1(new PagedMessageSource([unknown]));

        var result = await collector.CaptureAllAsync(AccountId, ConversationId, expectedCalibrationVersion: 7);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Capture!.EligibleMessages);
    }

    [Fact]
    public async Task Collector_PreservesConfirmedIncomingAndOutgoingDirectionsForGenerator()
    {
        var incoming = Message(1, "synthetic contact text", direction: MessageDirectionV1.Incoming);
        var outgoing = Message(2, "synthetic account text", direction: MessageDirectionV1.Outgoing);
        var unresolved = Message(
            3,
            "synthetic unresolved text",
            direction: MessageDirectionV1.Outgoing,
            evidence: DirectionEvidenceV1.DefaultSmallSenderId);
        var collector = new ConsolidationMessageCollectorV1(new PagedMessageSource([incoming, outgoing, unresolved]));

        var result = await collector.CaptureAllAsync(AccountId, ConversationId, expectedCalibrationVersion: 7);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { MessageDirectionV1.Incoming, MessageDirectionV1.Outgoing },
            result.Capture!.EligibleMessages.Select(message => message.Direction));
    }

    [Fact]
    public async Task Collector_StopsAtOutboxCursorAndReturnsOnlyTheNewerRange()
    {
        var source = new PagedMessageSource(
            Enumerable.Range(1, 5).Select(index => Message(index, $"m{index}")).ToArray());
        var collector = new ConsolidationMessageCollectorV1(source);

        var result = await collector.CaptureAfterCursorThroughEventAsync(
            AccountId,
            ConversationId,
            Cursor(2),
            Cursor(4),
            expectedCalibrationVersion: 7,
            IdentityNamespace);

        Assert.True(result.Succeeded);
        Assert.Equal(Cursor(2), result.Capture!.RangeStartMessageCursor);
        Assert.Equal(Cursor(4), result.Capture.HighWaterMessageCursor);
        Assert.Equal(new[] { Cursor(3), Cursor(4) }, result.Capture.AllMessages.Select(item => item.MessageId.ToString("N")));
    }

    [Fact]
    public void LaterWindow_ExactlyFiveThousandDoesNotTriggerButFiveThousandAndOneDoes()
    {
        var state = State(unprofiledTextCharacters: 4999);
        var newMessages = Capture(Cursor(10), Cursor(11), "revision-2", 1);
        var pending = Capture(Cursor(4), Cursor(11), "revision-2", 5000);

        var atThreshold = MemoryConsolidationWindowV1.PlanMessageCreated(state, newMessages, pending, generatorAvailable: true);
        var overThreshold = MemoryConsolidationWindowV1.PlanMessageCreated(
            state with { UnprofiledTextCharacters = 5000 },
            newMessages,
            pending with { EligibleTextCodePoints = 5001 },
            generatorAvailable: true);

        Assert.Equal(MemoryConsolidationDecisionKindV1.RecordAccumulation, atThreshold.Kind);
        Assert.Equal(5000, atThreshold.NextState!.UnprofiledTextCharacters);
        Assert.Equal(MemoryConsolidationDecisionKindV1.GenerateIncremental, overThreshold.Kind);
    }

    [Fact]
    public void LaterWindow_WithoutGeneratorDoesNotAdvanceAnyCursor()
    {
        var state = State(unprofiledTextCharacters: 5000);
        var newMessages = Capture(Cursor(10), Cursor(11), "revision-2", 1);
        var pending = Capture(Cursor(4), Cursor(11), "revision-2", 5001);

        var decision = MemoryConsolidationWindowV1.PlanMessageCreated(
            state,
            newMessages,
            pending,
            generatorAvailable: false);

        Assert.Equal(MemoryConsolidationDecisionKindV1.DeferForGenerator, decision.Kind);
        Assert.Null(decision.NextState);
        Assert.Equal(Cursor(4), state.ConsolidationSourceMessageCursor);
        Assert.Equal(Cursor(10), state.ObservedMessageCursor);
    }

    [Fact]
    public void FirstConfirmedWindow_BypassesThresholdAndRequiresGeneratorWhenTextExists()
    {
        var capture = Capture(null, Cursor(11), "revision-1", 6500);

        var decision = MemoryConsolidationWindowV1.PlanPerspectiveConfirmed(capture, generatorAvailable: true);
        var deferred = MemoryConsolidationWindowV1.PlanPerspectiveConfirmed(capture, generatorAvailable: false);

        Assert.Equal(MemoryConsolidationDecisionKindV1.GenerateInitial, decision.Kind);
        Assert.Equal(MemoryConsolidationDecisionKindV1.DeferForGenerator, deferred.Kind);
        Assert.Null(deferred.NextState);
    }

    [Fact]
    public void FirstConfirmedWindow_WithNoEligibleTextEstablishesBaselineWithoutInventingOutput()
    {
        var capture = Capture(null, null, "revision-1", 0);

        var decision = MemoryConsolidationWindowV1.PlanPerspectiveConfirmed(capture, generatorAvailable: false);

        Assert.Equal(MemoryConsolidationDecisionKindV1.CompleteEmptyInitialWindow, decision.Kind);
        Assert.True(decision.NextState!.InitialConsolidationCompleted);
        Assert.Null(decision.NextState.ConsolidationSourceMessageCursor);
        Assert.Equal(ProfileStatusV1.Accumulating, decision.NextState.Status);
    }

    [Fact]
    public void OutputValidator_RejectsEvidenceOutsideTheCapturedConfirmedRange()
    {
        var capture = Capture(null, Cursor(4), "revision-1", 2) with
        {
            EligibleMessages = [new MemoryConsolidationInputMessageV1(Guid.Parse(Cursor(4)), "ok", null, MessageDirectionV1.Incoming)]
        };
        var output = new MemoryConsolidationGenerationOutputV1
        {
            MemoryItems =
            [
                new MessageExtractedMemoryDraftV1
                {
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    Text = "unsupported citation",
                    EvidenceMessageIds = [Guid.Parse(Cursor(9))]
                }
            ]
        };

        var violations = MemoryConsolidationOutputValidatorV1.Validate(output, capture);

        Assert.Contains(violations, violation => violation.Field.EndsWith("evidenceMessageIds", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputValidator_RejectsSelfProfileEvidenceFromContactDirection()
    {
        var incomingMessageId = Guid.Parse(Cursor(4));
        var capture = Capture(null, Cursor(4), "revision-1", 2) with
        {
            EligibleMessages =
            [
                new MemoryConsolidationInputMessageV1(
                    incomingMessageId,
                    "synthetic incoming text",
                    null,
                    MessageDirectionV1.Incoming)
            ]
        };
        var output = new MemoryConsolidationGenerationOutputV1
        {
            ParticipantProfiles =
            [
                new ParticipantProfileDraftV1
                {
                    SubjectType = ParticipantSubjectTypeV1.WechatAccount,
                    Summary = "synthetic self profile",
                    EvidenceMessageIds = [incomingMessageId]
                }
            ],
            MemoryItems = []
        };

        var violations = MemoryConsolidationOutputValidatorV1.Validate(output, capture);

        Assert.Contains(violations, violation =>
            violation.Field.EndsWith("evidenceMessageIds", StringComparison.Ordinal)
            && violation.Code == "direction_mismatch");
    }

    [Fact]
    public void OutputValidator_RejectsAccountMemoryEvidenceFromContactDirection()
    {
        var incomingMessageId = Guid.Parse(Cursor(4));
        var capture = Capture(null, Cursor(4), "revision-1", 2) with
        {
            EligibleMessages =
            [
                new MemoryConsolidationInputMessageV1(
                    incomingMessageId,
                    "synthetic incoming fact",
                    null,
                    MessageDirectionV1.Incoming)
            ]
        };
        var output = new MemoryConsolidationGenerationOutputV1
        {
            ParticipantProfiles = [],
            MemoryItems =
            [
                new MessageExtractedMemoryDraftV1
                {
                    SubjectType = ParticipantSubjectTypeV1.WechatAccount,
                    Text = "synthetic account memory",
                    EvidenceMessageIds = [incomingMessageId]
                }
            ]
        };

        var violations = MemoryConsolidationOutputValidatorV1.Validate(output, capture);

        Assert.Contains(violations, violation =>
            violation.Field.EndsWith("evidenceMessageIds", StringComparison.Ordinal)
            && violation.Code == "direction_mismatch");
    }

    [Fact]
    public void OutputValidator_RejectsMalformedNullCollectionsOrRows()
    {
        var capture = Capture(null, Cursor(4), "revision-1", 1) with
        {
            EligibleMessages = [new MemoryConsolidationInputMessageV1(Guid.Parse(Cursor(4)), "ok", null, MessageDirectionV1.Incoming)]
        };
        var nullCollections = new MemoryConsolidationGenerationOutputV1
        {
            ParticipantProfiles = null!,
            MemoryItems = []
        };
        var nullRows = new MemoryConsolidationGenerationOutputV1
        {
            ParticipantProfiles = [null!],
            MemoryItems = []
        };

        Assert.Contains(
            MemoryConsolidationOutputValidatorV1.Validate(nullCollections, capture),
            violation => violation.Code == "collections_required");
        Assert.Contains(
            MemoryConsolidationOutputValidatorV1.Validate(nullRows, capture),
            violation => violation.Code == "required");
    }

    private static Guid AccountId { get; } = Guid.NewGuid();
    private static Guid ConversationId { get; } = Guid.NewGuid();
    private const string IdentityNamespace = "wx:table-shard";

    private static MemoryConsolidationStateV1 State(int unprofiledTextCharacters) => new()
    {
        AccountId = AccountId,
        ConversationId = ConversationId,
        IdentityNamespace = IdentityNamespace,
        CalibrationVersion = 7,
        MessageSetRevision = "revision-1",
        ConsolidationSourceMessageCursor = Cursor(4),
        ObservedMessageCursor = Cursor(10),
        UnprofiledTextCharacters = unprofiledTextCharacters,
        InitialConsolidationCompleted = true,
        Status = ProfileStatusV1.Accumulating
    };

    private static ConsolidationCaptureV1 Capture(
        string? rangeStart,
        string? highWater,
        string revision,
        int codePoints) => new()
    {
        AccountId = AccountId,
        ConversationId = ConversationId,
        ConversationType = "Direct",
        IdentityNamespace = IdentityNamespace,
        CalibrationVersion = 7,
        MessageSetRevision = revision,
        RangeStartMessageCursor = rangeStart,
        HighWaterMessageCursor = highWater,
        EligibleTextCodePoints = codePoints
    };

    private static string Cursor(int index) => Guid.Parse($"00000000-0000-0000-0000-{index:D12}").ToString("N");

    private static MessageQueryItemV1 Message(
        int index,
        string text,
        string messageType = "Text",
        DirectionEvidenceV1 evidence = DirectionEvidenceV1.SourceConfirmed,
        MessageDirectionV1 direction = MessageDirectionV1.Incoming) => new()
    {
        MessageId = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
        Text = text,
        MessageType = messageType,
        Direction = direction,
        DirectionEvidence = evidence
    };

    private sealed class PagedMessageSource(
        IReadOnlyList<MessageQueryItemV1> messages,
        bool changeRevisionAfterFirstPage = false) : IConsolidationMessageSourceV1
    {
        public int CallCount { get; private set; }

        public ValueTask<ConsolidationMessagePageV1> GetPageAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var anchorIndex = request.AnchorMessageId is null
                ? messages.Count
                : IndexOf(request.AnchorMessageId.Value);
            var anchorFound = request.AnchorMessageId is null || anchorIndex >= 0;
            if (!anchorFound)
            {
                return ValueTask.FromResult(Page(Array.Empty<MessageQueryItemV1>(), false, false));
            }

            var pageEnd = anchorIndex;
            var pageStart = Math.Max(0, pageEnd - (request.Limit - (request.AnchorMessageId is null ? 0 : 1)));
            var page = messages.Skip(pageStart).Take(pageEnd - pageStart).ToArray();
            if (request.AnchorMessageId is { } requestedAnchor)
            {
                page = page.Append(messages[anchorIndex]).ToArray();
            }

            var hasMore = pageStart > 0;
            return ValueTask.FromResult(Page(page, hasMore, true));
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

        private ConsolidationMessagePageV1 Page(
            IReadOnlyList<MessageQueryItemV1> pageMessages,
            bool hasMore,
            bool anchorFound) => new()
        {
            AccountId = PagedMessageSource.AccountId,
            ConversationId = PagedMessageSource.ConversationId,
            ConversationType = "Direct",
            IdentityNamespace = MemoryConsolidationWindowV1Tests.IdentityNamespace,
            MessageSetRevision = changeRevisionAfterFirstPage && CallCount > 1 ? "revision-2" : "revision-1",
            PerspectiveState = PerspectiveStateV1.Confirmed,
            CalibrationVersion = 7,
            HasMoreMessages = hasMore,
            AnchorMessageFound = anchorFound,
            Messages = pageMessages
        };

        private static Guid AccountId => MemoryConsolidationWindowV1Tests.AccountId;
        private static Guid ConversationId => MemoryConsolidationWindowV1Tests.ConversationId;
    }
}
