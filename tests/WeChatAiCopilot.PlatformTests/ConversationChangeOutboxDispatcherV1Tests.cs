using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Platform.Outbox;

namespace WeChatAiCopilot.PlatformTests;

public sealed class ConversationChangeOutboxDispatcherV1Tests
{
    [Fact]
    public async Task DispatchNextAsync_ReturnsFalseWhenNothingCanBeClaimed()
    {
        var store = new FakeStore();
        var handler = new FakeHandler();
        var dispatcher = CreateDispatcher(store, handler);

        var didWork = await dispatcher.DispatchNextAsync();

        Assert.False(didWork);
        Assert.Equal(0, handler.CallCount);
        Assert.Null(store.CompletedClaim);
        Assert.Null(store.Retry);
    }

    [Theory]
    [InlineData(MemoryConsolidationEventOutcomeV1.NoOp)]
    [InlineData(MemoryConsolidationEventOutcomeV1.Accumulated)]
    [InlineData(MemoryConsolidationEventOutcomeV1.Consolidated)]
    public async Task DispatchNextAsync_AcknowledgesOnlySuccessfulOutcomes(
        MemoryConsolidationEventOutcomeV1 outcome)
    {
        var store = new FakeStore { Claim = CreateClaim(attempts: 1) };
        var handler = new FakeHandler
        {
            Result = new MemoryConsolidationEventResultV1(outcome)
        };
        var dispatcher = CreateDispatcher(store, handler);

        var didWork = await dispatcher.DispatchNextAsync();

        Assert.True(didWork);
        Assert.Equal(1, handler.CallCount);
        Assert.Same(store.Claim, store.CompletedClaim);
        Assert.Null(store.Retry);
    }

    [Theory]
    [InlineData(MemoryConsolidationEventOutcomeV1.Deferred, "calibration_version_changed", 2, 2)]
    [InlineData(MemoryConsolidationEventOutcomeV1.RebuildRequired, "scope_rebuild", 1, 1)]
    public async Task DispatchNextAsync_RetriesNonterminalOutcomesWithErrorCodeAndBackoff(
        MemoryConsolidationEventOutcomeV1 outcome,
        string detailCode,
        int attempts,
        int expectedDelaySeconds)
    {
        var store = new FakeStore { Claim = CreateClaim(attempts) };
        var handler = new FakeHandler
        {
            Result = new MemoryConsolidationEventResultV1(outcome, detailCode)
        };
        var dispatcher = CreateDispatcher(store, handler);

        var didWork = await dispatcher.DispatchNextAsync();

        Assert.True(didWork);
        Assert.Null(store.CompletedClaim);
        Assert.Equal(detailCode, store.Retry?.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), store.Retry?.Delay);
    }

    [Theory]
    [InlineData(MemoryConsolidationEventOutcomeV1.Deferred, "generator_unavailable")]
    [InlineData(MemoryConsolidationEventOutcomeV1.RebuildRequired, "generator_unavailable_reprocess_queued")]
    public async Task DispatchNextAsync_WaitsLongerForMissingCapabilityInsteadOfBackoffCycling(
        MemoryConsolidationEventOutcomeV1 outcome,
        string detailCode)
    {
        var store = new FakeStore { Claim = CreateClaim(attempts: 20) };
        var handler = new FakeHandler
        {
            Result = new MemoryConsolidationEventResultV1(outcome, detailCode)
        };
        var dispatcher = CreateDispatcher(store, handler);

        Assert.True(await dispatcher.DispatchNextAsync());

        Assert.Equal(detailCode, store.Retry?.ErrorCode);
        Assert.Equal(TimeSpan.FromMinutes(30), store.Retry?.Delay);
    }

    [Fact]
    public async Task DispatchNextAsync_TransientGeneratorFailureStillUsesShortBackoff()
    {
        var store = new FakeStore { Claim = CreateClaim(attempts: 2) };
        var handler = new FakeHandler
        {
            Result = new MemoryConsolidationEventResultV1(
                MemoryConsolidationEventOutcomeV1.Deferred,
                "generator_failed")
        };
        var dispatcher = CreateDispatcher(store, handler);

        Assert.True(await dispatcher.DispatchNextAsync());

        Assert.Equal(TimeSpan.FromSeconds(2), store.Retry?.Delay);
    }

    [Fact]
    public void Options_RejectCapabilityDelayShorterThanBackoffCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversationChangeOutboxDispatcherV1(
            new FakeStore(),
            new FakeHandler(),
            NullLogger<ConversationChangeOutboxDispatcherV1>.Instance,
            new OutboxDispatcherOptionsV1 { CapabilityRetryDelay = TimeSpan.FromMinutes(1) }));
    }

    [Fact]
    public async Task DispatchNextAsync_UsesStableCodeForExceptionsWithoutPersistingExceptionText()
    {
        var store = new FakeStore { Claim = CreateClaim(attempts: 1) };
        var handler = new FakeHandler { Exception = new InvalidOperationException("chat body must not leak") };
        var dispatcher = CreateDispatcher(store, handler);

        var didWork = await dispatcher.DispatchNextAsync();

        Assert.True(didWork);
        Assert.Equal("handler_exception", store.Retry?.ErrorCode);
        Assert.DoesNotContain("chat body", store.Retry?.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchNextAsync_ReleasesLeaseBestEffortWhenCancellationInterruptsHandler()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeStore { Claim = CreateClaim(attempts: 1) };
        var handler = new FakeHandler { Exception = new OperationCanceledException(cancellation.Token) };
        var dispatcher = CreateDispatcher(store, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchNextAsync(cancellation.Token));

        Assert.Same(store.Claim, store.ReleasedClaim);
        Assert.False(store.ReleaseCancellationToken.CanBeCanceled);
        Assert.Null(store.Retry);
    }

    [Fact]
    public async Task DispatchNextAsync_BlocksLaterEventsPerConversationButAllowsOtherConversations()
    {
        var firstMessage = CreateEvent(
            "00000000-0000-0000-0000-000000000003",
            "00000000-0000-0000-0000-000000000010",
            "00000000-0000-0000-0000-000000000020",
            MemoryConsolidationTriggerKindV1.MessageCreated);
        var laterConfirmation = CreateEvent(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000010",
            "00000000-0000-0000-0000-000000000020",
            MemoryConsolidationTriggerKindV1.PerspectiveConfirmed);
        var otherConversation = CreateEvent(
            "00000000-0000-0000-0000-000000000002",
            "00000000-0000-0000-0000-000000000010",
            "00000000-0000-0000-0000-000000000030",
            MemoryConsolidationTriggerKindV1.MessageCreated);
        var store = new OrderedFakeStore(
        [
            (firstMessage, 1L),
            (laterConfirmation, 3L),
            (otherConversation, 2L)
        ]);
        var handler = new FakeHandler
        {
            Results =
            [
                new(MemoryConsolidationEventOutcomeV1.Deferred, "initial_confirmation_pending"),
                new(MemoryConsolidationEventOutcomeV1.NoOp),
                new(MemoryConsolidationEventOutcomeV1.NoOp),
                new(MemoryConsolidationEventOutcomeV1.NoOp)
            ]
        };
        var dispatcher = CreateDispatcher(store, handler);

        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal(firstMessage.EventId, handler.HandledEventIds[0]);

        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal(otherConversation.EventId, handler.HandledEventIds[1]);

        Assert.False(await dispatcher.DispatchNextAsync());
        Assert.Equal(2, handler.HandledEventIds.Count);

        store.MakeRetryDue(firstMessage.EventId);
        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal(firstMessage.EventId, handler.HandledEventIds[2]);

        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal(laterConfirmation.EventId, handler.HandledEventIds[3]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(20, 300)]
    [InlineData(int.MaxValue, 300)]
    public void RetryPolicy_UsesExponentiallyIncreasingCappedDelay(int attempts, int expectedSeconds)
    {
        var delay = OutboxRetryPolicyV1.GetDelay(
            attempts,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void DiExtension_DoesNotRegisterHostedServiceWithoutFormalDatabaseProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryConsolidationEventHandlerV1>(new FakeHandler());

        services.AddConversationChangeOutboxDispatcher();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void DiExtension_DoesNotRegisterHostedServiceWithoutM3Handler()
    {
        var services = new ServiceCollection();
        using var dataSource = CreateUnopenedDataSource();
        services.AddSingleton(dataSource);

        services.AddConversationChangeOutboxDispatcher();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void DiExtensionRegistersHostedServiceWhenDatabaseAndHandlerAreAvailable()
    {
        var services = new ServiceCollection();
        using var dataSource = CreateUnopenedDataSource();
        services.AddSingleton(dataSource);
        services.AddSingleton<IMemoryConsolidationEventHandlerV1>(new FakeHandler());

        services.AddConversationChangeOutboxDispatcher();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IConversationChangeOutboxDeliveryStoreV1));
    }

    private static ConversationChangeOutboxDispatcherV1 CreateDispatcher(
        IConversationChangeOutboxDeliveryStoreV1 store,
        FakeHandler handler) =>
        new(store, handler, NullLogger<ConversationChangeOutboxDispatcherV1>.Instance, new OutboxDispatcherOptionsV1());

    private static OutboxDeliveryClaimV1 CreateClaim(int attempts) => new(
        new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.Parse("bfbc6448-5897-4e3f-943f-d44cb31575e1"),
            AccountId = Guid.Parse("731c31a7-7a90-4e16-98d9-4f2b7ce2c941"),
            ConversationId = Guid.Parse("a84f2d34-04a8-4a7b-aaab-a0d4b31e03e6"),
            Kind = MemoryConsolidationTriggerKindV1.MessageCreated,
            MessageCursor = "cursor-1",
            CalibrationVersion = 3
        },
        "message.created",
        Guid.NewGuid(),
        attempts);

    private static MemoryConsolidationOutboxEventV1 CreateEvent(
        string eventId,
        string accountId,
        string conversationId,
        MemoryConsolidationTriggerKindV1 kind) => new()
    {
        EventId = Guid.Parse(eventId),
        AccountId = Guid.Parse(accountId),
        ConversationId = Guid.Parse(conversationId),
        Kind = kind
    };

    private static NpgsqlDataSource CreateUnopenedDataSource() =>
        new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=outbox-tests;Username=test;Password=test").Build();

    private sealed class FakeStore : IConversationChangeOutboxDeliveryStoreV1
    {
        public OutboxDeliveryClaimV1? Claim { get; init; }
        public OutboxDeliveryClaimV1? CompletedClaim { get; private set; }
        public (OutboxDeliveryClaimV1 Claim, string ErrorCode, TimeSpan Delay)? Retry { get; private set; }
        public OutboxDeliveryClaimV1? ReleasedClaim { get; private set; }
        public CancellationToken ReleaseCancellationToken { get; private set; }

        public ValueTask<OutboxDeliveryClaimV1?> TryClaimNextAsync(
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Claim);

        public ValueTask<bool> CompleteAsync(
            OutboxDeliveryClaimV1 claim,
            CancellationToken cancellationToken = default)
        {
            CompletedClaim = claim;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ScheduleRetryAsync(
            OutboxDeliveryClaimV1 claim,
            string errorCode,
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            Retry = (claim, errorCode, delay);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ReleaseLeaseAsync(
            OutboxDeliveryClaimV1 claim,
            CancellationToken cancellationToken = default)
        {
            ReleasedClaim = claim;
            ReleaseCancellationToken = cancellationToken;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeHandler : IMemoryConsolidationEventHandlerV1
    {
        public MemoryConsolidationEventResultV1 Result { get; init; } = new(MemoryConsolidationEventOutcomeV1.NoOp);
        public Exception? Exception { get; init; }
        public IReadOnlyList<MemoryConsolidationEventResultV1>? Results { get; init; }
        public int CallCount { get; private set; }
        public List<Guid> HandledEventIds { get; } = [];

        public ValueTask<MemoryConsolidationEventResultV1> HandleAsync(
            MemoryConsolidationOutboxEventV1 message,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            HandledEventIds.Add(message.EventId);
            return Exception is null
                ? ValueTask.FromResult(Results is not null && CallCount <= Results.Count
                    ? Results[CallCount - 1]
                    : Result)
                : ValueTask.FromException<MemoryConsolidationEventResultV1>(Exception);
        }
    }

    private sealed class OrderedFakeStore : IConversationChangeOutboxDeliveryStoreV1
    {
        private readonly List<PendingEvent> _events;

        public OrderedFakeStore(IReadOnlyList<(MemoryConsolidationOutboxEventV1 Event, long EventSequence)> events)
        {
            _events = events.Select(item => new PendingEvent(item.Event, item.EventSequence)).ToList();
        }

        public ValueTask<OutboxDeliveryClaimV1?> TryClaimNextAsync(
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            var candidate = _events
                .OrderBy(item => item.EventSequence)
                .FirstOrDefault(item => !item.Completed
                    && !item.LeaseActive
                    && item.RetryDue
                    && !_events.Any(earlier => SameConversation(earlier.Event, item.Event)
                        && IsEarlier(earlier, item)
                        && !earlier.Completed));
            if (candidate is null)
            {
                return ValueTask.FromResult<OutboxDeliveryClaimV1?>(null);
            }

            candidate.Attempts++;
            candidate.LeaseId = Guid.NewGuid();
            candidate.LeaseActive = true;
            candidate.RetryDue = false;
            return ValueTask.FromResult<OutboxDeliveryClaimV1?>(new OutboxDeliveryClaimV1(
                candidate.Event,
                EventType(candidate.Event.Kind),
                candidate.LeaseId.Value,
                candidate.Attempts));
        }

        public ValueTask<bool> CompleteAsync(
            OutboxDeliveryClaimV1 claim,
            CancellationToken cancellationToken = default)
        {
            var pending = FindLease(claim);
            if (pending is null)
            {
                return ValueTask.FromResult(false);
            }

            pending.Completed = true;
            pending.LeaseActive = false;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ScheduleRetryAsync(
            OutboxDeliveryClaimV1 claim,
            string errorCode,
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            var pending = FindLease(claim);
            if (pending is null)
            {
                return ValueTask.FromResult(false);
            }

            pending.LeaseActive = false;
            pending.RetryDue = false;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ReleaseLeaseAsync(
            OutboxDeliveryClaimV1 claim,
            CancellationToken cancellationToken = default)
        {
            var pending = FindLease(claim);
            if (pending is null)
            {
                return ValueTask.FromResult(false);
            }

            pending.LeaseActive = false;
            pending.RetryDue = true;
            return ValueTask.FromResult(true);
        }

        public void MakeRetryDue(Guid eventId)
        {
            var pending = _events.Single(item => item.Event.EventId == eventId);
            pending.RetryDue = true;
        }

        private PendingEvent? FindLease(OutboxDeliveryClaimV1 claim) => _events.FirstOrDefault(item =>
            item.Event.EventId == claim.Event.EventId
            && item.LeaseActive
            && item.LeaseId == claim.LeaseId);

        private static bool SameConversation(
            MemoryConsolidationOutboxEventV1 left,
            MemoryConsolidationOutboxEventV1 right) =>
            left.AccountId == right.AccountId && left.ConversationId == right.ConversationId;

        private static bool IsEarlier(PendingEvent left, PendingEvent right) =>
            left.EventSequence < right.EventSequence;

        private static string EventType(MemoryConsolidationTriggerKindV1 kind) => kind switch
        {
            MemoryConsolidationTriggerKindV1.MessageCreated => "message.created",
            MemoryConsolidationTriggerKindV1.PerspectiveConfirmed => "conversation.perspective-confirmed",
            MemoryConsolidationTriggerKindV1.PerspectiveChanged => "conversation.perspective-changed",
            _ => "unsupported"
        };

        private sealed class PendingEvent(
            MemoryConsolidationOutboxEventV1 @event,
            long eventSequence)
        {
            public MemoryConsolidationOutboxEventV1 Event { get; } = @event;
            public long EventSequence { get; } = eventSequence;
            public int Attempts { get; set; }
            public Guid? LeaseId { get; set; }
            public bool LeaseActive { get; set; }
            public bool RetryDue { get; set; } = true;
            public bool Completed { get; set; }
        }
    }
}
