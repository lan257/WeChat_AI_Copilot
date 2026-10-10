using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

public sealed class AgentTaskCoordinatorV1Tests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private const string ConsultationThreadId = "104467f8a2e94a26928353b5b0ca28a3";
    private static readonly AuthorizedContextScopeV1 Scope = new(AccountId, ConversationId);

    [Fact]
    public async Task CreateSuggestionsAsync_RejectsTaskScopeMismatchBeforeBuildingContext()
    {
        var contextService = new FakeContextService(CreateContextBundle());
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedSuggestions(
            "test-runtime",
            CreateDrafts()));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, runtime, repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(accountId: Guid.NewGuid()),
            Scope));

        Assert.Equal(0, contextService.CallCount);
        Assert.Null(runtime.LastRequest);
        Assert.Null(repository.StartedRun);
    }

    [Fact]
    public async Task CreateConsultationTurnAsync_RejectsThreadForAnotherConversationBeforeBuildingContext()
    {
        var contextService = new FakeContextService(CreateContextBundle());
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedConsultation("test-runtime", "answer"));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, runtime, repository);
        var task = CreateConsultationTask();
        var thread = CreateThread(conversationId: Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.CreateConsultationTurnAsync(
            task,
            thread,
            Scope));

        Assert.Equal(0, contextService.CallCount);
        Assert.Null(runtime.LastRequest);
        Assert.Null(repository.StartedRun);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_MapsM3ContextAndReturnsThreeValidatedCandidates()
    {
        var bundle = CreateContextBundle(revision: "m3-context-r42");
        var contextService = new FakeContextService(bundle);
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedSuggestions(
            "test-runtime",
            CreateDrafts()));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, runtime, repository);
        var task = CreateReplyTask(contextRevision: "client-supplied-stale-revision");

        var batch = await coordinator.CreateSuggestionsAsync(task, Scope);

        Assert.Equal(1, contextService.CallCount);
        Assert.Equal(AccountId, contextService.LastRequest!.AccountId);
        Assert.Equal(ConversationId, contextService.LastRequest.ConversationId);
        Assert.Equal("ReplyCandidates", contextService.LastRequest.RequestPurpose);
        Assert.Equal(Scope, contextService.LastAuthorizedScope);

        var runtimeRequest = Assert.IsType<ScopedAgentRequestV1>(runtime.LastRequest);
        Assert.Equal(AccountId, runtimeRequest.Task!.AccountId);
        Assert.Equal(ConversationId, runtimeRequest.Task.ConversationId);
        Assert.Equal("m3-context-r42", runtimeRequest.Task.ContextRevision);
        Assert.Equal("m3-context-r42", runtimeRequest.Context!.ContextRevision);
        Assert.Equal(AccountId, runtimeRequest.Context.AccountId);
        Assert.Equal(ConversationId, runtimeRequest.Context.ConversationId);
        var mappedMessage = Assert.Single(runtimeRequest.Context.RecentMessages!);
        Assert.Equal(MessageId.ToString("D"), mappedMessage.MessageId);
        Assert.Equal("the authorized recent message", mappedMessage.Text);
        Assert.Equal(MessageDirectionV1.Incoming, mappedMessage.Direction);
        Assert.Equal(DirectionEvidenceV1.UserConfirmed, mappedMessage.DirectionEvidence);
        Assert.Equal(nameof(ContextOmissionCodeV1.HistoryCoveragePartial), Assert.Single(runtimeRequest.Context.Omissions));

        Assert.Equal("m3-context-r42", batch.ContextRevision);
        Assert.Equal(AccountId, batch.AccountId);
        Assert.Equal(ConversationId, batch.ConversationId);
        Assert.Equal(task.PersonaPresetId, batch.PersonaPresetId);
        Assert.Equal(AgentRunStatusV1.Completed, batch.Status);
        Assert.False(string.IsNullOrWhiteSpace(batch.RunId));
        Assert.False(string.IsNullOrWhiteSpace(batch.BatchId));
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<AgentSuggestionV1>>(batch.Suggestions);
        Assert.Equal(3, suggestions.Count);
        Assert.All(suggestions, suggestion =>
        {
            Assert.False(string.IsNullOrWhiteSpace(suggestion.Id));
            Assert.False(string.IsNullOrWhiteSpace(suggestion.Text));
        });
        Assert.Equal(3, suggestions.Select(suggestion => suggestion.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(AgentContractValidatorV1.Validate(batch, runtimeRequest.Task));
        Assert.Equal(batch.RunId, repository.StartedRun!.RunId);
        Assert.Equal(AgentRunStatusV1.Running, repository.StartedRun.Status);
        Assert.Equal(AgentTaskTypeV1.ReplyCandidates, repository.StartedRun.TaskType);
        Assert.Equal("test-skill-v1", repository.StartedRun.SkillVersion);
        Assert.Equal("m3-context-r42", repository.StartedRun.ContextRevision);
        Assert.Equal("test-runtime", repository.LastRuntimeName);
        Assert.Equal(5000, repository.StartedRun.BudgetMetadata!.MaxDurationMilliseconds);
        Assert.Equal(8000, repository.StartedRun.BudgetMetadata.MaxContextCharacters);
        Assert.Equal(0, repository.StartedRun.BudgetMetadata.MaxToolCalls);
    }

    [Fact]
    public async Task CreateConsultationTurnAsync_BindsOutputToAuthorizedThreadAndContextRevision()
    {
        var contextService = new FakeContextService(CreateContextBundle(revision: "m3-consult-r7"));
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedConsultation(
            "test-runtime",
            "A context-based answer.",
            [MessageId.ToString("D")]));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, runtime, repository);
        var task = CreateConsultationTask(contextRevision: "client-revision-to-ignore");
        var thread = CreateThread();

        var turn = await coordinator.CreateConsultationTurnAsync(task, thread, Scope);

        Assert.Equal("FreeConsultation", contextService.LastRequest!.RequestPurpose);
        Assert.Equal(thread, runtime.LastRequest!.BoundThread);
        Assert.Equal("m3-consult-r7", runtime.LastRequest.Task!.ContextRevision);
        Assert.Equal(thread.ThreadId, turn.ThreadId);
        Assert.Equal("m3-consult-r7", runtime.LastRequest.Context!.ContextRevision);
        Assert.Equal(AccountId, turn.AccountId);
        Assert.Equal(ConversationId, turn.ConversationId);
        Assert.Equal(task.UserInput, turn.UserInput);
        Assert.Equal("A context-based answer.", turn.Answer);
        Assert.Equal(MessageId.ToString("D"), Assert.Single(turn.EvidenceMessageIds));
        Assert.Equal(AgentRunStatusV1.Completed, turn.Status);
        Assert.False(string.IsNullOrWhiteSpace(turn.RunId));
        Assert.Empty(AgentContractValidatorV1.Validate(turn, runtime.LastRequest.Task, thread));
        Assert.Equal(turn.RunId, repository.StartedRun!.RunId);
        Assert.Equal(AgentTaskTypeV1.FreeConsultation, repository.StartedRun.TaskType);
        Assert.Equal(AgentRunStatusV1.Running, repository.StartedRun.Status);
    }

    [Fact]
    public async Task CreateConsultationTurnAsync_PassesPreviousSameThreadTurnAsBoundedHistory()
    {
        var previousTurn = CreateHistoryTurn();
        var repository = new FakeAgentRepository { TurnsToReturn = [previousTurn] };
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedConsultation("test-runtime", "second answer"));
        var contextService = new FakeContextService(CreateContextBundle());
        var coordinator = CreateCoordinator(contextService, runtime, repository);

        await coordinator.CreateConsultationTurnAsync(CreateConsultationTask(), CreateThread(), Scope);

        var runtimeRequest = Assert.IsType<ScopedAgentRequestV1>(runtime.LastRequest);
        var historyTurn = Assert.Single(runtimeRequest.ConsultationHistory);
        Assert.Equal(previousTurn.ThreadId, historyTurn.ThreadId);
        Assert.Equal(previousTurn.AccountId, historyTurn.AccountId);
        Assert.Equal(previousTurn.ConversationId, historyTurn.ConversationId);
        Assert.Equal(previousTurn.UserInput, historyTurn.UserInput);
        Assert.Equal(previousTurn.Answer, historyTurn.Answer);
        Assert.Equal(Scope, repository.LastHistoryScope);
        Assert.Equal(Guid.Parse(ConsultationThreadId), repository.LastHistoryThreadId);
        Assert.Equal(9, repository.LastHistoryLimit);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("account")]
    [InlineData("conversation")]
    public async Task CreateConsultationTurnAsync_RejectsHistoryFromAnotherThreadOrScope(string mismatch)
    {
        var invalidTurn = mismatch switch
        {
            "thread" => CreateHistoryTurn() with { ThreadId = Guid.NewGuid().ToString("N") },
            "account" => CreateHistoryTurn() with { AccountId = Guid.NewGuid() },
            _ => CreateHistoryTurn() with { ConversationId = Guid.NewGuid() }
        };
        var repository = new FakeAgentRepository { TurnsToReturn = [invalidTurn] };
        var contextService = new FakeContextService(CreateContextBundle());
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedConsultation("test-runtime", "answer"));
        var coordinator = CreateCoordinator(contextService, runtime, repository);

        var exception = await Assert.ThrowsAsync<AgentTaskContractException>(() => coordinator.CreateConsultationTurnAsync(
            CreateConsultationTask(),
            CreateThread(),
            Scope));

        Assert.Contains(exception.Violations, violation => violation.Code == "CONSULTATION_HISTORY_SCOPE_INVALID");
        Assert.Null(runtime.LastRequest);
        Assert.Equal(0, contextService.CallCount);
    }

    [Fact]
    public async Task CreateConsultationTurnAsync_LimitsHistoryByTurnsAndCharacters()
    {
        var turns = Enumerable.Range(1, 4)
            .Select(index => CreateHistoryTurn() with
            {
                RunId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                UserInput = $"Question {index}",
                Answer = new string((char)('a' + index), 90),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(index)
            })
            .ToArray();
        var repository = new FakeAgentRepository { TurnsToReturn = turns };
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedConsultation("test-runtime", "answer"));
        var coordinator = CreateCoordinator(
            new FakeContextService(CreateContextBundle()),
            runtime,
            repository,
            maxHistoryTurns: 2,
            maxHistoryCharacters: 180);

        await coordinator.CreateConsultationTurnAsync(CreateConsultationTask(), CreateThread(), Scope);

        var runtimeRequest = Assert.IsType<ScopedAgentRequestV1>(runtime.LastRequest);
        Assert.NotEmpty(runtimeRequest.ConsultationHistory);
        Assert.True(runtimeRequest.ConsultationHistory.Count <= 2);
        Assert.True(runtimeRequest.ConsultationHistoryTruncated);
        Assert.Equal("Question 4", runtimeRequest.ConsultationHistory[^1].UserInput);
        var contentCharacters = runtimeRequest.ConsultationHistory.Sum(turn =>
            (turn.UserInput?.Length ?? 0) + (turn.Answer?.Length ?? 0) + turn.EvidenceMessageIds.Sum(id => id.Length + 3) + 32);
        Assert.True(contentCharacters <= 180);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_IncludesScopedReadOnlyContextSessionForFutureBrokerIntegration()
    {
        var factory = new CapturingContextToolSessionFactory();
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedSuggestions("test-runtime", CreateDrafts()));
        var coordinator = CreateCoordinator(
            new FakeContextService(CreateContextBundle()),
            runtime,
            new FakeAgentRepository(),
            contextToolSessionFactory: factory);

        await coordinator.CreateSuggestionsAsync(CreateReplyTask(), Scope);

        Assert.Equal(Scope, factory.LastBoundScope);
        Assert.Same(factory.Session, runtime.LastRequest!.ContextToolSession);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_IncludesScopedCurrentProfileAndMemoryWithoutRequiringProfileForMemory()
    {
        var context = CreateContextBundle(
            perspectiveState: PerspectiveStateV1.Confirmed,
            calibrationVersion: 3,
            memories:
            [
                new MemoryItemV1
                {
                    MemoryId = Guid.NewGuid(),
                    AccountId = AccountId,
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    SubjectId = Guid.NewGuid(),
                    ConversationId = ConversationId,
                    IdentityNamespace = "namespace-current",
                    Text = "自动提取但有来源证据的事项",
                    Origin = MemoryOriginV1.MessageExtracted,
                    Status = MemoryItemStatusV1.Active,
                    CalibrationVersion = 3,
                    SourceMessageIds = [MessageId]
                },
                new MemoryItemV1
                {
                    MemoryId = Guid.NewGuid(),
                    AccountId = AccountId,
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    SubjectId = Guid.NewGuid(),
                    ConversationId = ConversationId,
                    IdentityNamespace = null,
                    Text = "用户手工备注",
                    Origin = MemoryOriginV1.UserEntered,
                    Status = MemoryItemStatusV1.Active
                },
                new MemoryItemV1
                {
                    MemoryId = Guid.NewGuid(),
                    AccountId = AccountId,
                    SubjectType = ParticipantSubjectTypeV1.Contact,
                    SubjectId = Guid.NewGuid(),
                    ConversationId = ConversationId,
                    IdentityNamespace = "namespace-current",
                    Text = "旧 calibration 不应传入",
                    Origin = MemoryOriginV1.MessageExtracted,
                    Status = MemoryItemStatusV1.Active,
                    CalibrationVersion = 2
                }
            ]);
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedSuggestions("test-runtime", CreateDrafts()));
        var coordinator = CreateCoordinator(new FakeContextService(context), runtime, new FakeAgentRepository());

        await coordinator.CreateSuggestionsAsync(CreateReplyTask(), Scope);

        var mappedContext = runtime.LastRequest!.Context!;
        Assert.Equal(PerspectiveStateV1.Confirmed, mappedContext.PerspectiveState);
        Assert.Equal(3, mappedContext.CalibrationVersion);
        Assert.Equal(2, mappedContext.MemoryItems.Count);
        Assert.Contains(mappedContext.MemoryItems, item => item.Origin == MemoryOriginV1.UserEntered);
        var extracted = Assert.Single(mappedContext.MemoryItems, item => item.Origin == MemoryOriginV1.MessageExtracted);
        Assert.Equal(3, extracted.CalibrationVersion);
        Assert.Equal(MessageId.ToString("D"), Assert.Single(extracted.SourceMessageIds));
    }

    [Fact]
    public async Task CreateSuggestionsAsync_RejectsRuntimeResultThatDoesNotContainThreeCandidates()
    {
        var contextService = new FakeContextService(CreateContextBundle());
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.CompletedSuggestions(
            "test-runtime",
            CreateDrafts().Take(2).ToArray()));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, runtime, repository);

        var exception = await Assert.ThrowsAsync<AgentTaskContractException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(),
            Scope));

        Assert.Contains(exception.Violations, violation => violation.Code == "SUGGESTION_COUNT_MUST_BE_THREE");
        Assert.Equal(AgentRunStatusV1.Failed, repository.LastRun!.Status);
        Assert.Equal("SUGGESTION_COUNT_MUST_BE_THREE", repository.LastRun.ErrorCode);
        Assert.NotNull(repository.LastRun.EndedAtUtc);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_PropagatesFakeRuntimeFailureInsteadOfReturningBatch()
    {
        var contextService = new FakeContextService(CreateContextBundle(messages: []));
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, new FakeAgentRuntime(), repository);

        var exception = await Assert.ThrowsAsync<AgentTaskExecutionException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(),
            Scope));

        Assert.Equal(AgentRunStatusV1.Failed, exception.Status);
        Assert.Equal("CONTEXT_UNAVAILABLE", exception.ErrorCode);
        Assert.Equal(FakeAgentRuntime.RuntimeId, exception.RuntimeName);
        Assert.Equal(AgentRunStatusV1.Failed, repository.LastRun!.Status);
        Assert.Equal("CONTEXT_UNAVAILABLE", repository.LastRun.ErrorCode);
        Assert.NotNull(repository.LastRun.EndedAtUtc);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_WhenRuntimeReturnsCancelled_RecordsCancelledRun()
    {
        var contextService = new FakeContextService(CreateContextBundle());
        var repository = new FakeAgentRepository();
        var runtime = new CapturingRuntime(AgentRuntimeResultV1.Cancelled("test-runtime"));
        var coordinator = CreateCoordinator(contextService, runtime, repository);

        var exception = await Assert.ThrowsAsync<AgentTaskExecutionException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(),
            Scope));

        Assert.Equal(AgentRunStatusV1.Cancelled, exception.Status);
        Assert.Equal(AgentRunStatusV1.Cancelled, repository.LastRun!.Status);
        Assert.Equal("CANCELLED", repository.LastRun.ErrorCode);
        Assert.NotNull(repository.LastRun.EndedAtUtc);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_RuntimeExceptionStoresOnlySanitizedErrorCode()
    {
        const string sensitiveText = "private chat text included in provider exception";
        var contextService = new FakeContextService(CreateContextBundle());
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(contextService, new ThrowingRuntime(sensitiveText), repository);

        var exception = await Assert.ThrowsAsync<AgentTaskExecutionException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(),
            Scope));

        Assert.Equal("RUNTIME_FAILURE", exception.ErrorCode);
        Assert.DoesNotContain(sensitiveText, exception.Message, StringComparison.Ordinal);
        Assert.Equal("RUNTIME_FAILURE", repository.LastRun!.ErrorCode);
        Assert.DoesNotContain(sensitiveText, repository.LastRun.ErrorCode, StringComparison.Ordinal);
        Assert.Equal(AgentRunStatusV1.Failed, repository.LastRun.Status);
        Assert.NotNull(repository.LastRun.EndedAtUtc);
    }

    [Fact]
    public async Task CreateSuggestionsAsync_RuntimeErrorCodeIsSanitizedBeforeAudit()
    {
        const string sensitiveText = "provider exception included private content";
        var repository = new FakeAgentRepository();
        var coordinator = CreateCoordinator(
            new FakeContextService(CreateContextBundle()),
            new CapturingRuntime(AgentRuntimeResultV1.Failed("unsafe runtime name", sensitiveText)),
            repository);

        var exception = await Assert.ThrowsAsync<AgentTaskExecutionException>(() => coordinator.CreateSuggestionsAsync(
            CreateReplyTask(),
            Scope));

        Assert.Equal("RUNTIME_FAILURE", exception.ErrorCode);
        Assert.DoesNotContain(sensitiveText, exception.Message, StringComparison.Ordinal);
        Assert.Equal("RUNTIME_FAILURE", repository.LastRun!.ErrorCode);
        Assert.DoesNotContain(sensitiveText, repository.LastRun.ErrorCode, StringComparison.Ordinal);
        Assert.Equal(nameof(CapturingRuntime), repository.LastRun.RuntimeName);
    }

    [Fact]
    public void AgentRunAuditContractContainsOnlyMetadataFields()
    {
        var propertyNames = typeof(AgentRunV1).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("UserInput", propertyNames);
        Assert.DoesNotContain("Prompt", propertyNames);
        Assert.DoesNotContain("ChatText", propertyNames);
        Assert.DoesNotContain("MessageText", propertyNames);
        Assert.DoesNotContain("Answer", propertyNames);
    }

    private static AgentTaskCoordinatorV1 CreateCoordinator(
        IContextServiceV1 contextService,
        IAgentRuntime runtime,
        IAgentRepositoryV1 repository,
        int maxHistoryTurns = 8,
        int maxHistoryCharacters = 4000,
        IContextToolSessionFactoryV1? contextToolSessionFactory = null) =>
        new(contextService, runtime, repository, new FakePresetCatalog("natural", "warm", "playful", "concise"), new AgentTaskCoordinatorOptionsV1
        {
            SkillVersion = "test-skill-v1",
            AllowedReadCapabilities = [AgentReadCapabilityV1.GetRecentMessages],
            Budget = new AgentExecutionBudgetV1
            {
                MaxDuration = TimeSpan.FromSeconds(5),
                MaxContextCharacters = 8000,
                MaxToolCalls = 0
            },
            MaxConsultationHistoryTurns = maxHistoryTurns,
            MaxConsultationHistoryCharacters = maxHistoryCharacters
        }, contextToolSessionFactory: contextToolSessionFactory);

    private static AgentTaskV1 CreateReplyTask(Guid? accountId = null, string? contextRevision = "client-revision") => new()
    {
        TaskType = AgentTaskTypeV1.ReplyCandidates,
        AccountId = accountId ?? AccountId,
        ConversationId = ConversationId,
        PersonaPresetId = "natural",
        PreviousBatchId = null,
        ContextRevision = contextRevision
    };

    private static AgentTaskV1 CreateConsultationTask(string? contextRevision = "client-revision") => new()
    {
        TaskType = AgentTaskTypeV1.FreeConsultation,
        AccountId = AccountId,
        ConversationId = ConversationId,
        ThreadId = ConsultationThreadId,
        UserInput = "How can I bring this up naturally?",
        ContextRevision = contextRevision
    };

    private static AgentConsultationThreadV1 CreateThread(Guid? conversationId = null) => new()
    {
        ThreadId = ConsultationThreadId,
        AccountId = AccountId,
        ConversationId = conversationId ?? ConversationId,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ContextBundleV1 CreateContextBundle(
        string revision = "m3-context-r1",
        IReadOnlyList<ContextMessageV1>? messages = null,
        PerspectiveStateV1 perspectiveState = PerspectiveStateV1.Unresolved,
        long calibrationVersion = 0,
        IReadOnlyList<MemoryItemV1>? memories = null,
        IReadOnlyList<ParticipantProfileSnapshotV1>? profiles = null) => new()
    {
        AccountId = AccountId,
        ConversationId = ConversationId,
        ContextRevision = revision,
        PerspectiveState = perspectiveState,
        CalibrationVersion = calibrationVersion,
        ProfileStatus = perspectiveState == PerspectiveStateV1.Confirmed ? ProfileStatusV1.Current : ProfileStatusV1.WaitingForConfirmation,
        RecentMessages = messages ??
        [
            new ContextMessageV1
            {
                MessageId = MessageId,
                Text = "the authorized recent message",
                OccurredAt = DateTimeOffset.UtcNow,
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.UserConfirmed
            }
        ],
        ParticipantProfiles = profiles ?? [],
        MemoryItems = memories ?? [],
        Omissions = [new ContextOmissionV1(ContextOmissionCodeV1.HistoryCoveragePartial)],
        GeneratedAt = DateTimeOffset.UtcNow
    };

    private static ConsultationTurnV1 CreateHistoryTurn() => new()
    {
        ThreadId = ConsultationThreadId,
        RunId = Guid.NewGuid().ToString("N"),
        AccountId = AccountId,
        ConversationId = ConversationId,
        UserInput = "First question",
        Answer = "First answer",
        EvidenceMessageIds = [MessageId.ToString("D")],
        Status = AgentRunStatusV1.Completed,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private static IReadOnlyList<AgentSuggestionDraftV1> CreateDrafts() =>
    [
        new() { Text = "First candidate" },
        new() { Text = "Second candidate" },
        new() { Text = "Third candidate" }
    ];

    private sealed class FakeContextService(ContextBundleV1 bundle) : IContextServiceV1
    {
        public int CallCount { get; private set; }
        public ContextRequestV1? LastRequest { get; private set; }
        public AuthorizedContextScopeV1? LastAuthorizedScope { get; private set; }

        public ValueTask<ContextBundleV1> BuildAsync(
            ContextRequestV1 request,
            AuthorizedContextScopeV1 authorizedScope,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            LastAuthorizedScope = authorizedScope;
            return ValueTask.FromResult(bundle);
        }
    }

    private sealed class CapturingRuntime(AgentRuntimeResultV1 result) : IAgentRuntime
    {
        public ScopedAgentRequestV1? LastRequest { get; private set; }

        public Task<AgentRuntimeResultV1> ExecuteAsync(
            ScopedAgentRequestV1 request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingRuntime(string message) : IAgentRuntime
    {
        public Task<AgentRuntimeResultV1> ExecuteAsync(
            ScopedAgentRequestV1 request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class FakeAgentRepository : IAgentRepositoryV1
    {
        public AgentRunV1? StartedRun { get; private set; }
        public AgentRunV1? LastRun { get; private set; }
        public string? LastRuntimeName { get; private set; }
        public IReadOnlyList<ConsultationTurnV1> TurnsToReturn { get; init; } = [];
        public AuthorizedContextScopeV1? LastHistoryScope { get; private set; }
        public Guid? LastHistoryThreadId { get; private set; }
        public int LastHistoryLimit { get; private set; }

        public Task<AuthorizedContextScopeV1?> ResolveAuthorizedScopeAsync(AgentWebConversationTargetV1 target, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorizedContextScopeV1?>(Scope);

        public Task StoreSuggestionBatchAsync(AuthorizedContextScopeV1 scope, SuggestionBatchV1 batch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SuggestionBatchV1?> GetSuggestionBatchAsync(AuthorizedContextScopeV1 scope, Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SuggestionBatchV1?>(null);

        public Task StoreConsultationThreadAsync(AuthorizedContextScopeV1 scope, AgentConsultationThreadV1 thread, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AgentConsultationThreadV1?> GetConsultationThreadAsync(AuthorizedContextScopeV1 scope, Guid threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentConsultationThreadV1?>(null);

        public Task StoreConsultationTurnAsync(AuthorizedContextScopeV1 scope, ConsultationTurnV1 turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConsultationTurnV1>> GetConsultationTurnsAsync(AuthorizedContextScopeV1 scope, Guid threadId, CancellationToken cancellationToken = default)
        {
            LastHistoryScope = scope;
            LastHistoryThreadId = threadId;
            return Task.FromResult(TurnsToReturn);
        }

        public Task<IReadOnlyList<ConsultationTurnV1>> GetRecentConsultationTurnsAsync(
            AuthorizedContextScopeV1 scope,
            Guid threadId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            LastHistoryScope = scope;
            LastHistoryThreadId = threadId;
            LastHistoryLimit = limit;
            return Task.FromResult<IReadOnlyList<ConsultationTurnV1>>(TurnsToReturn.TakeLast(limit).ToArray());
        }

        public Task StartAgentRunAsync(AuthorizedContextScopeV1 scope, AgentRunV1 run, CancellationToken cancellationToken = default)
        {
            StartedRun = run;
            LastRun = run;
            return Task.CompletedTask;
        }

        public Task UpdateAgentRunRuntimeNameAsync(AuthorizedContextScopeV1 scope, Guid runId, string runtimeName, CancellationToken cancellationToken = default)
        {
            LastRuntimeName = runtimeName;
            if (LastRun is { } run)
            {
                LastRun = run with { RuntimeName = runtimeName };
            }

            return Task.CompletedTask;
        }

        public Task FinishAgentRunAsync(
            AuthorizedContextScopeV1 scope,
            Guid runId,
            AgentRunStatusV1 status,
            string errorCode,
            DateTimeOffset endedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (LastRun is { } run)
            {
                LastRun = run with { Status = status, ErrorCode = errorCode, EndedAtUtc = endedAtUtc };
            }

            return Task.CompletedTask;
        }

        public Task<AgentRunV1?> GetAgentRunAsync(AuthorizedContextScopeV1 scope, Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(LastRun is { } run && Guid.Parse(run.RunId!) == runId ? run : null);
    }

    private sealed class FakePresetCatalog(params string[] presetIds) : IAgentPresetCatalogV1
    {
        private static readonly IReadOnlyDictionary<string, string> Instructions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natural"] = "Use natural wording.",
            ["warm"] = "Use warm wording.",
            ["playful"] = "Use playful wording.",
            ["concise"] = "Use concise wording."
        };
        private readonly HashSet<string> _presetIds = new(presetIds, StringComparer.Ordinal);

        public bool IsConfigured => _presetIds.Any(Instructions.ContainsKey);

        public bool Contains(string presetId) => _presetIds.Contains(presetId) && Instructions.ContainsKey(presetId);

        public IReadOnlyList<AgentPersonaPresetSummaryV1> GetAvailablePersonas() => _presetIds
            .Where(Instructions.ContainsKey)
            .Select(presetId => new AgentPersonaPresetSummaryV1(presetId, presetId))
            .ToArray();

        public bool TryGetInstruction(string presetId, out string instruction)
        {
            if (_presetIds.Contains(presetId) && Instructions.TryGetValue(presetId, out instruction!))
            {
                return true;
            }

            instruction = string.Empty;
            return false;
        }
    }

    private sealed class CapturingContextToolSessionFactory : IContextToolSessionFactoryV1
    {
        public AuthorizedContextScopeV1? LastBoundScope { get; private set; }
        public IContextToolSessionV1 Session { get; } = new EmptyContextToolSession();

        public IContextToolSessionV1 Bind(AuthorizedContextScopeV1 authorizedScope)
        {
            LastBoundScope = authorizedScope;
            return Session;
        }
    }

    private sealed class EmptyContextToolSession : IContextToolSessionV1
    {
        public ValueTask<ContextToolContextResultV1> GetContextAsync(ContextToolRequestV1 request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ContextToolContextResultV1());

        public ValueTask<HistorySearchResultV1> SearchHistoryAsync(HistorySearchRequestV1 request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HistorySearchResultV1());

        public ValueTask<MessageEvidenceResultV1> GetMessageEvidenceAsync(MessageEvidenceRequestV1 request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessageEvidenceResultV1());
    }
}
