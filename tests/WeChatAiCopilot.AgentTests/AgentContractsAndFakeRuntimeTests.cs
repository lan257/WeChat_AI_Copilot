using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

public sealed class AgentContractsAndFakeRuntimeTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();
    private const string ContextRevision = "context-r1";

    [Fact]
    public void AgentTaskRequestV6_AcceptsBothSpellingsOfTheIdentifiersAgentResponsesEmit()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        var thread = Guid.NewGuid();
        var batch = Guid.NewGuid();

        var undashed = JsonSerializer.Deserialize<AgentTaskRequestV6>(
            $$"""{"taskType":"FreeConsultation","executionProfileId":"Quick","threadId":"{{thread:N}}","previousBatchId":"{{batch:N}}","userInput":"hi"}""",
            options);
        var dashed = JsonSerializer.Deserialize<AgentTaskRequestV6>(
            $$"""{"taskType":"FreeConsultation","executionProfileId":"Quick","threadId":"{{thread:D}}","previousBatchId":"{{batch:D}}","userInput":"hi"}""",
            options);

        Assert.Equal(thread, undashed?.ThreadId);
        Assert.Equal(batch, undashed?.PreviousBatchId);
        Assert.Equal(thread, dashed?.ThreadId);
        Assert.Equal(batch, dashed?.PreviousBatchId);
    }

    [Fact]
    public void AgentTaskRequestV6_RejectsValuesThatAreNotGuids()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentTaskRequestV6>(
            """{"taskType":"FreeConsultation","threadId":"th-77","userInput":"hi"}""", options));
    }

    [Fact]
    public void ValidateScopedRequest_AcceptsMatchingAuthorizedScope()
    {
        var request = CreateReplyRequest();

        Assert.Empty(AgentContractValidatorV1.Validate(request));
    }

    [Fact]
    public void ValidateScopedRequest_RejectsContextFromAnotherConversation()
    {
        var request = CreateReplyRequest(context: CreateContext(conversationId: Guid.NewGuid()));

        Assert.Contains(
            AgentContractValidatorV1.Validate(request),
            violation => violation.Code == "CONTEXT_SCOPE_MISMATCH");
    }

    [Fact]
    public void ValidateScopedRequest_RejectsConsultationThreadBoundToAnotherAccount()
    {
        var task = new AgentTaskV1
        {
            TaskType = AgentTaskTypeV1.FreeConsultation,
            AccountId = _accountId,
            ConversationId = _conversationId,
            ThreadId = "thread-1",
            UserInput = "How should I respond?",
            ContextRevision = ContextRevision
        };
        var request = CreateRequest(
            task,
            CreateContext(),
            boundThread: new AgentConsultationThreadV1
            {
                ThreadId = "thread-1",
                AccountId = Guid.NewGuid(),
                ConversationId = _conversationId,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

        Assert.Contains(
            AgentContractValidatorV1.Validate(request),
            violation => violation.Code == "CONSULTATION_THREAD_SCOPE_MISMATCH");
    }

    [Fact]
    public void ValidateRuntimeResult_RejectsEvidenceOutsideAuthorizedContext()
    {
        var task = CreateConsultationTask();
        var context = CreateContext();
        var result = AgentRuntimeResultV1.CompletedConsultation(
            FakeAgentRuntime.RuntimeId,
            "A possible response.",
            ["message-outside-context"]);

        Assert.Contains(
            AgentContractValidatorV1.ValidateRuntimeResult(result, task, context),
            violation => violation.Code == "UNAVAILABLE_EVIDENCE_ID");
    }

    [Fact]
    public void ValidateRuntimeResult_AcceptsEvidenceFromAuthorizedContext()
    {
        var task = CreateConsultationTask();
        var context = CreateContext();
        var result = AgentRuntimeResultV1.CompletedConsultation(
            FakeAgentRuntime.RuntimeId,
            "A possible response.",
            ["message-1"]);

        Assert.Empty(AgentContractValidatorV1.ValidateRuntimeResult(result, task, context));
    }

    [Fact]
    public void RuntimeContracts_ExposeReadCapabilitiesButNoSendSurface()
    {
        Assert.All(
            Enum.GetNames<AgentReadCapabilityV1>(),
            name => Assert.DoesNotContain("send", name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ScopedAgentRequestV1).GetProperties(),
            property => property.Name.Contains("send", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IAgentRuntime).GetMethods(),
            method => method.Name.Contains("send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FakeRuntime_ReplyCandidatesReturnThreeContractValidDrafts()
    {
        var task = CreateReplyTask();
        var context = CreateContext();
        var request = CreateRequest(task, context);
        var runtime = new FakeAgentRuntime();

        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AgentRunStatusV1.Completed, result.Status);
        Assert.Equal(FakeAgentRuntime.RuntimeId, result.RuntimeName);
        Assert.Equal(3, result.Suggestions.Count);
        Assert.All(result.Suggestions, suggestion => Assert.False(string.IsNullOrWhiteSpace(suggestion.Text)));
        Assert.All(result.Suggestions, suggestion => Assert.Contains("FakeRuntime", suggestion.Text, StringComparison.Ordinal));
        Assert.Empty(AgentContractValidatorV1.ValidateRuntimeResult(result, task, context));
    }

    [Fact]
    public async Task FakeRuntime_ConsultationOutputUsesOnlySuppliedContext()
    {
        var task = CreateConsultationTask();
        var context = CreateContext(messages:
        [
            CreateContextMessage("message-1", "hello"),
            CreateContextMessage("message-2", "how are you?")
        ]);
        var runtime = new FakeAgentRuntime();

        var result = await runtime.ExecuteAsync(CreateRequest(task, context, CreateBoundThread()), CancellationToken.None);

        Assert.Equal(AgentRunStatusV1.Completed, result.Status);
        Assert.Contains("2 条近期消息", result.Answer);
        Assert.Empty(result.EvidenceMessageIds);
        Assert.Empty(AgentContractValidatorV1.ValidateRuntimeResult(result, task, context));
    }

    [Fact]
    public async Task FakeRuntime_InvalidScopeReturnsFailureWithoutGeneratedOutput()
    {
        var task = CreateReplyTask();
        var context = CreateContext(accountId: Guid.NewGuid());
        var runtime = new FakeAgentRuntime();

        var result = await runtime.ExecuteAsync(CreateRequest(task, context), CancellationToken.None);

        Assert.Equal(AgentRunStatusV1.Failed, result.Status);
        Assert.Equal("CONTEXT_SCOPE_MISMATCH", result.ErrorCode);
        Assert.Empty(result.Suggestions);
        Assert.Null(result.Answer);
        Assert.Empty(result.EvidenceMessageIds);
        Assert.Empty(AgentContractValidatorV1.ValidateRuntimeResult(result, task, context));
    }

    [Fact]
    public async Task FakeRuntime_CancelledExecutionReturnsCancelledWithoutOutput()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var runtime = new FakeAgentRuntime();

        var result = await runtime.ExecuteAsync(CreateReplyRequest(), source.Token);

        Assert.Equal(AgentRunStatusV1.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.ErrorCode);
        Assert.Empty(result.Suggestions);
        Assert.Null(result.Answer);
        Assert.Empty(result.EvidenceMessageIds);
    }

    [Fact]
    public async Task FakeRuntime_EmptyUsableContextFailsClosed()
    {
        var task = CreateReplyTask();
        var context = CreateContext(messages: []);
        var runtime = new FakeAgentRuntime();

        var result = await runtime.ExecuteAsync(CreateRequest(task, context), CancellationToken.None);

        Assert.Equal(AgentRunStatusV1.Failed, result.Status);
        Assert.Equal("CONTEXT_UNAVAILABLE", result.ErrorCode);
        Assert.Empty(result.Suggestions);
    }

    private ScopedAgentRequestV1 CreateReplyRequest(AgentContextSnapshotV1? context = null) =>
        CreateRequest(CreateReplyTask(), context ?? CreateContext());

    private AgentTaskV1 CreateReplyTask() => new()
    {
        TaskType = AgentTaskTypeV1.ReplyCandidates,
        AccountId = _accountId,
        ConversationId = _conversationId,
        PersonaPresetId = "persona-default",
        ContextRevision = ContextRevision
    };

    private AgentTaskV1 CreateConsultationTask() => new()
    {
        TaskType = AgentTaskTypeV1.FreeConsultation,
        AccountId = _accountId,
        ConversationId = _conversationId,
        ThreadId = "thread-1",
        UserInput = "How should I respond?",
        ContextRevision = ContextRevision
    };

    private AgentContextSnapshotV1 CreateContext(
        Guid? accountId = null,
        Guid? conversationId = null,
        IReadOnlyList<AgentContextMessageV1>? messages = null) => new()
    {
        AccountId = accountId ?? _accountId,
        ConversationId = conversationId ?? _conversationId,
        ContextRevision = ContextRevision,
        RecentMessages = messages ?? [CreateContextMessage("message-1", "hello")],
        Omissions = []
    };

    private static AgentContextMessageV1 CreateContextMessage(string id, string text) => new()
    {
        MessageId = id,
        Text = text,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        Direction = MessageDirectionV1.Incoming,
        DirectionEvidence = DirectionEvidenceV1.SourceConfirmed
    };

    private AgentConsultationThreadV1 CreateBoundThread() => new()
    {
        ThreadId = "thread-1",
        AccountId = _accountId,
        ConversationId = _conversationId,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ScopedAgentRequestV1 CreateRequest(
        AgentTaskV1 task,
        AgentContextSnapshotV1 context,
        AgentConsultationThreadV1? boundThread = null) => new()
    {
        Task = task,
        Context = context,
        BoundThread = boundThread,
        PersonaInstruction = task.TaskType == AgentTaskTypeV1.ReplyCandidates ? "Use a test-only style." : null,
        SkillVersion = "skill-v1",
        AllowedReadCapabilities = [AgentReadCapabilityV1.GetRecentMessages],
        Budget = new AgentExecutionBudgetV1
        {
            MaxDuration = TimeSpan.FromSeconds(10),
            MaxContextCharacters = 10_000,
            MaxToolCalls = 0
        }
    };
}
