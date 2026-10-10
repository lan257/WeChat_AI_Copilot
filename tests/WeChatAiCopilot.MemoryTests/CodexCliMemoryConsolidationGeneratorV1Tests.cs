using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using WeChatAiCopilot.Api.Platform.CodexCli;
using Xunit;

namespace WeChatAiCopilot.MemoryTests;

public sealed class CodexCliMemoryConsolidationGeneratorV1Tests
{
    [Fact]
    public async Task GenerateAsync_UsesScopeFreeIndexesAndMapsEvidenceToEligibleMessageIds()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var incomingId = Guid.NewGuid();
        var outgoingId = Guid.NewGuid();
        var fakeExecutor = new FakeStructuredCodexCliExecutorV1(Completed(
            """
            {
              "participantProfiles": [
                { "subjectType": "WechatAccount", "summary": "synthetic account profile", "evidenceIndexes": [1] },
                { "subjectType": "Contact", "summary": "synthetic contact profile", "evidenceIndexes": [0] }
              ],
              "memoryItems": [
                { "subjectType": "Contact", "text": "synthetic appointment detail", "category": "plan", "observedAt": "", "evidenceIndexes": [0] }
              ]
            }
            """));
        var generator = new CodexCliMemoryConsolidationGeneratorV1(fakeExecutor);
        var request = Request(accountId, conversationId,
        [
            new MemoryConsolidationInputMessageV1(
                incomingId,
                "synthetic incoming chat text",
                DateTimeOffset.Parse("2026-09-10T12:00:00Z"),
                MessageDirectionV1.Incoming),
            new MemoryConsolidationInputMessageV1(
                outgoingId,
                "synthetic outgoing chat text",
                DateTimeOffset.Parse("2026-09-10T12:01:00Z"),
                MessageDirectionV1.Outgoing)
        ]);

        var output = await generator.GenerateAsync(request);

        Assert.Equal(new[] { outgoingId, incomingId }, output.ParticipantProfiles
            .Select(profile => profile.EvidenceMessageIds.Single()));
        Assert.Equal(incomingId, output.MemoryItems.Single().EvidenceMessageIds.Single());
        Assert.Equal("plan", output.MemoryItems.Single().Category);
        Assert.Null(output.MemoryItems.Single().ObservedAt);

        var prompt = Assert.IsType<string>(fakeExecutor.LastPrompt);
        Assert.Contains("\"batchKind\":\"Incremental\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"index\":0", prompt, StringComparison.Ordinal);
        Assert.Contains("\"direction\":\"Incoming\"", prompt, StringComparison.Ordinal);
        Assert.Contains("synthetic incoming chat text", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(accountId.ToString("D"), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(conversationId.ToString("D"), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(incomingId.ToString("D"), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outgoingId.ToString("D"), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(request.IdentityNamespace, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(request.HighWaterMessageCursor!, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("accountId", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationId", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("identityNamespace", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("highWaterMessageCursor", prompt, StringComparison.Ordinal);

        var schema = Assert.IsType<string>(fakeExecutor.LastSchema);
        Assert.DoesNotContain("enum", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"required\": [\"participantProfiles\", \"memoryItems\"]", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RejectsOutOfRangeEvidenceIndexWithoutReturningModelContent()
    {
        var fakeExecutor = new FakeStructuredCodexCliExecutorV1(Completed(
            """
            {
              "participantProfiles": [],
              "memoryItems": [
                { "subjectType": "Contact", "text": "synthetic private output", "category": "", "observedAt": "", "evidenceIndexes": [1] }
              ]
            }
            """));
        var generator = new CodexCliMemoryConsolidationGeneratorV1(fakeExecutor);
        var request = Request(Guid.NewGuid(), Guid.NewGuid(),
        [new MemoryConsolidationInputMessageV1(
            Guid.NewGuid(), "synthetic eligible message", null, MessageDirectionV1.Incoming)]);

        var exception = await Assert.ThrowsAsync<MemoryConsolidationGenerationExceptionV1>(
            async () => await generator.GenerateAsync(request));

        Assert.Equal("structured_output_invalid", exception.ErrorCode);
        Assert.DoesNotContain("synthetic private output", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RejectsEvidenceWithDirectionThatDoesNotMatchItsSubject()
    {
        var fakeExecutor = new FakeStructuredCodexCliExecutorV1(Completed(
            """
            {
              "participantProfiles": [
                { "subjectType": "WechatAccount", "summary": "synthetic wrong direction", "evidenceIndexes": [0] }
              ],
              "memoryItems": []
            }
            """));
        var generator = new CodexCliMemoryConsolidationGeneratorV1(fakeExecutor);
        var request = Request(Guid.NewGuid(), Guid.NewGuid(),
        [new MemoryConsolidationInputMessageV1(
            Guid.NewGuid(), "synthetic incoming message", null, MessageDirectionV1.Incoming)]);

        var exception = await Assert.ThrowsAsync<MemoryConsolidationGenerationExceptionV1>(
            async () => await generator.GenerateAsync(request));

        Assert.Equal("structured_output_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_MapsExecutorFailureToStableCodeWithoutDiagnostics()
    {
        var fakeExecutor = new FakeStructuredCodexCliExecutorV1(new StructuredCodexCliExecutionResultV1(
            StructuredCodexCliExecutionStatusV1.ProcessExitedNonZero,
            null));
        var generator = new CodexCliMemoryConsolidationGeneratorV1(fakeExecutor);
        var request = Request(Guid.NewGuid(), Guid.NewGuid(),
        [new MemoryConsolidationInputMessageV1(
            Guid.NewGuid(), "synthetic eligible message", null, MessageDirectionV1.Incoming)]);

        var exception = await Assert.ThrowsAsync<MemoryConsolidationGenerationExceptionV1>(
            async () => await generator.GenerateAsync(request));

        Assert.Equal("codex_exit_nonzero", exception.ErrorCode);
        Assert.DoesNotContain("stderr", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic eligible message", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredFactory_IsFailClosedUnlessCodexCliIsExplicitlySelectedAndExecutorExists()
    {
        var disabledServices = new ServiceCollection();
        disabledServices.AddSingleton<IConfiguration>(Configuration());
        disabledServices.AddSingleton<IStructuredCodexCliExecutorV1>(new FakeStructuredCodexCliExecutorV1(
            Completed("{\"participantProfiles\":[],\"memoryItems\":[]}")));
        using var disabledProvider = disabledServices.BuildServiceProvider();
        Assert.Null(CodexCliMemoryConsolidationGeneratorV1.TryCreateConfigured(disabledProvider));

        var missingExecutorServices = new ServiceCollection();
        missingExecutorServices.AddSingleton<IConfiguration>(Configuration("CodexCli"));
        using var missingExecutorProvider = missingExecutorServices.BuildServiceProvider();
        Assert.Null(CodexCliMemoryConsolidationGeneratorV1.TryCreateConfigured(missingExecutorProvider));

        var configuredServices = new ServiceCollection();
        configuredServices.AddSingleton<IConfiguration>(Configuration("CodexCli"));
        configuredServices.AddSingleton<IStructuredCodexCliExecutorV1>(new FakeStructuredCodexCliExecutorV1(
            Completed("{\"participantProfiles\":[],\"memoryItems\":[]}")));
        using var configuredProvider = configuredServices.BuildServiceProvider();
        Assert.IsType<CodexCliMemoryConsolidationGeneratorV1>(
            CodexCliMemoryConsolidationGeneratorV1.TryCreateConfigured(configuredProvider));
    }

    [Fact]
    public async Task MemoryServiceRegistration_UsesConfiguredCodexGeneratorForConfirmedInitialWindow()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var source = new SyntheticMessageSource(new ConsolidationMessagePageV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            ConversationType = "Direct",
            IdentityNamespace = "synthetic-namespace",
            MessageSetRevision = "synthetic-revision",
            PerspectiveState = PerspectiveStateV1.Confirmed,
            CalibrationVersion = 3,
            Messages =
            [
                new MessageQueryItemV1
                {
                    MessageId = messageId,
                    Text = "synthetic incoming conversation fact",
                    MessageType = "Text",
                    Direction = MessageDirectionV1.Incoming,
                    DirectionEvidence = DirectionEvidenceV1.UserConfirmed
                }
            ]
        });
        var repository = new SyntheticConsolidationStateRepository(
            new MemoryConversationScopeV1(accountId, conversationId, contactId));
        var executor = new FakeStructuredCodexCliExecutorV1(Completed(
            """
            {
              "participantProfiles": [
                { "subjectType": "Contact", "summary": "synthetic contact summary", "evidenceIndexes": [0] }
              ],
              "memoryItems": []
            }
            """));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration("CodexCli"));
        services.AddSingleton<IStructuredCodexCliExecutorV1>(executor);
        services.AddSingleton<IConsolidationMessageSourceV1>(source);
        services.AddSingleton<IConsolidationStateRepositoryV1>(repository);
        services.AddPostgresMemoryServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IMemoryConsolidationEventHandlerV1>();
        var result = await handler.HandleAsync(new MemoryConsolidationOutboxEventV1
        {
            EventId = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Kind = MemoryConsolidationTriggerKindV1.PerspectiveConfirmed,
            CalibrationVersion = 3
        });

        Assert.Equal(MemoryConsolidationEventOutcomeV1.Consolidated, result.Outcome);
        Assert.Equal(1, executor.CallCount);
        Assert.NotNull(repository.LastCommit);
        Assert.Equal(messageId,
            repository.LastCommit!.Output.ParticipantProfiles.Single().EvidenceMessageIds.Single());
        Assert.Equal("synthetic-revision", repository.LastCommit.Capture.MessageSetRevision);
    }

    [Fact]
    public async Task GenerateAsync_ForwardsCancellationTokenWithoutCallingAnyExternalProcess()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var fakeExecutor = new FakeStructuredCodexCliExecutorV1(Completed(
            "{\"participantProfiles\":[],\"memoryItems\":[]}"));
        var generator = new CodexCliMemoryConsolidationGeneratorV1(fakeExecutor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await generator.GenerateAsync(
                Request(Guid.NewGuid(), Guid.NewGuid(),
                [new MemoryConsolidationInputMessageV1(
                    Guid.NewGuid(), "synthetic eligible message", null, MessageDirectionV1.Incoming)]),
                cancellation.Token);
        });

        Assert.Equal(0, fakeExecutor.CallCount);
    }

    private static MemoryConsolidationGenerationRequestV1 Request(
        Guid accountId,
        Guid conversationId,
        IReadOnlyList<MemoryConsolidationInputMessageV1> messages) => new()
    {
        AccountId = accountId,
        ConversationId = conversationId,
        IdentityNamespace = "synthetic-namespace-4e79e4d9",
        CalibrationVersion = 9,
        HighWaterMessageCursor = "synthetic-cursor-2817d8f6",
        BatchKind = MemoryConsolidationBatchKindV1.Incremental,
        Messages = messages
    };

    private static IConfiguration Configuration(string? runtime = null)
    {
        var values = new Dictionary<string, string?>();
        if (runtime is not null)
        {
            values[CodexCliMemoryConsolidationGeneratorV1.RuntimeSetting] = runtime;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static StructuredCodexCliExecutionResultV1 Completed(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new StructuredCodexCliExecutionResultV1(
            StructuredCodexCliExecutionStatusV1.Completed,
            document.RootElement.Clone());
    }

    private sealed class FakeStructuredCodexCliExecutorV1(StructuredCodexCliExecutionResultV1 result)
        : IStructuredCodexCliExecutorV1
    {
        public string? LastPrompt { get; private set; }
        public string? LastSchema { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public int CallCount { get; private set; }

        public Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(
            string prompt,
            string outputSchemaJson,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = prompt;
            LastSchema = outputSchemaJson;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }

    private sealed class SyntheticMessageSource(ConsolidationMessagePageV1 page) : IConsolidationMessageSourceV1
    {
        public ValueTask<ConsolidationMessagePageV1> GetPageAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(page);
    }

    private sealed class SyntheticConsolidationStateRepository(MemoryConversationScopeV1 scope)
        : IConsolidationStateRepositoryV1
    {
        public MemoryConsolidationBatchCommitV1? LastCommit { get; private set; }

        public ValueTask<MemoryConversationScopeV1?> ResolveScopeAsync(
            Guid accountId,
            Guid conversationId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<MemoryConversationScopeV1?>(scope);

        public ValueTask<MemoryConsolidationStateV1?> GetStateAsync(
            Guid accountId,
            Guid conversationId,
            string identityNamespace,
            long calibrationVersion,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<MemoryConsolidationStateV1?>(null);

        public ValueTask<bool> IsEventProcessedAsync(
            MemoryConversationScopeV1 requestScope,
            Guid eventId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask RecordAccumulationAsync(
            MemoryConsolidationStateV1 nextState,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask RecordEmptyInitialWindowAsync(
            MemoryConsolidationStateV1 nextState,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConsolidationBatchClaimResultV1> TryBeginBatchAsync(
            MemoryConversationScopeV1 requestScope,
            string identityNamespace,
            long calibrationVersion,
            string messageSetRevision,
            string highWaterMessageCursor,
            MemoryConsolidationBatchKindV1 batchKind,
            Guid batchId,
            Guid eventId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new ConsolidationBatchClaimResultV1(
                    ConsolidationBatchClaimOutcomeV1.Claimed,
                    new MemoryConsolidationStateV1
                    {
                        AccountId = requestScope.AccountId,
                        ConversationId = requestScope.ConversationId,
                        IdentityNamespace = identityNamespace,
                        CalibrationVersion = calibrationVersion,
                        MessageSetRevision = messageSetRevision,
                        StateVersion = expectedStateVersion + 1,
                        ActiveBatchId = batchId
                    }));

        public ValueTask MarkReprocessRequiredAsync(
            Guid accountId,
            Guid conversationId,
            string identityNamespace,
            long calibrationVersion,
            long expectedStateVersion,
            string observedMessageSetRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask MarkBatchFailedAsync(
            Guid accountId,
            Guid conversationId,
            string identityNamespace,
            long calibrationVersion,
            Guid batchId,
            long expectedStateVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask CommitGeneratedBatchAsync(
            MemoryConsolidationBatchCommitV1 commit,
            CancellationToken cancellationToken = default)
        {
            LastCommit = commit;
            return ValueTask.CompletedTask;
        }
    }
}
