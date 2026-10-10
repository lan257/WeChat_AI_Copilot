using System.Text.Json;
using System.ComponentModel;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Platform.CodexCli;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

public sealed class CodexCliAgentRuntimeTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ThreadGuid = Guid.NewGuid();

    [Fact]
    public async Task ExecuteAsync_UsesReadOnlyStdinInvocationAndScopeFreePrompt()
    {
        var runner = new FakeProcessRunner(
            """{"suggestions":["one","two","three"]}""");
        var (runtime, scriptPath, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Completed, result.Status);
            Assert.Equal(CodexCliAgentRuntime.RuntimeId, result.RuntimeName);
            Assert.Equal(3, result.Suggestions.Count);
            Assert.All(result.Suggestions, suggestion => Assert.False(string.IsNullOrWhiteSpace(suggestion.Text)));

            var process = Assert.IsType<CodexCliProcessRequestV1>(runner.LastRequest);
            Assert.DoesNotContain(process.Arguments, argument => string.Equals(argument, process.StandardInput, StringComparison.Ordinal));
            Assert.Equal("-", process.Arguments[^1]);
            Assert.Contains("--sandbox", process.Arguments);
            Assert.Contains("read-only", process.Arguments);
            Assert.Equal(1, CountArgumentPair(process.Arguments, "--disable", "shell_tool"));
            Assert.Equal(1, CountArgumentPair(process.Arguments, "--disable", "multi_agent"));
            Assert.Equal(1, CountArgumentPair(process.Arguments, "--disable", "remote_plugin"));
            Assert.Contains("mcp_servers={}", GetConfigValues(process.Arguments));
            Assert.Contains("web_search=\"disabled\"", GetConfigValues(process.Arguments));
            Assert.Contains("--ephemeral", process.Arguments);
            Assert.Contains("--ignore-user-config", process.Arguments);
            Assert.Contains("--skip-git-repo-check", process.Arguments);
            Assert.EndsWith("node.exe", process.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GetCodexScriptPath(scriptPath), process.Arguments[0]);
            Assert.DoesNotContain("-File", process.Arguments);
            Assert.DoesNotContain(process.Arguments, argument => argument.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(process.WorkingDirectory, GetFollowingArgument(process.Arguments, "--cd"));
            Assert.False(File.Exists(runner.SchemaPath!));
            using var schemaDocument = JsonDocument.Parse(runner.SchemaContents!);
            var suggestionsSchema = schemaDocument.RootElement.GetProperty("properties").GetProperty("suggestions");
            Assert.Equal("array", suggestionsSchema.GetProperty("type").GetString());
            Assert.Equal("string", suggestionsSchema.GetProperty("items").GetProperty("type").GetString());
            Assert.False(suggestionsSchema.TryGetProperty("uniqueItems", out _));
            Assert.False(suggestionsSchema.TryGetProperty("minItems", out _));
            Assert.False(suggestionsSchema.TryGetProperty("maxItems", out _));
            Assert.False(Directory.Exists(process.WorkingDirectory));

            Assert.Contains("profilePrivate-summary", process.StandardInput);
            Assert.Contains("authorized-user-note", process.StandardInput);
            Assert.Contains("authorized-message-memory", process.StandardInput);
            Assert.Contains("message-1", process.StandardInput);
            Assert.Contains("不可信数据", process.StandardInput);
            Assert.DoesNotContain(AccountId.ToString("D"), process.StandardInput);
            Assert.DoesNotContain(ConversationId.ToString("D"), process.StandardInput);
            Assert.DoesNotContain(ThreadGuid.ToString("N"), process.StandardInput);
            Assert.DoesNotContain("internal-identity-namespace", process.StandardInput);
            Assert.DoesNotContain("internal-subject-id", process.StandardInput);
            Assert.DoesNotContain("internal-memory-id", process.StandardInput);

            var allowedEnvironmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME",
                "APPDATA", "LOCALAPPDATA", "CODEX_HOME"
            };
            Assert.All(process.Environment.Keys, key => Assert.Contains(key, allowedEnvironmentNames));
            Assert.DoesNotContain("OPENAI_API_KEY", process.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("API_KEY", process.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_IncludesSameThreadHistoryButNotThreadScopeIdsInPrompt()
    {
        var runner = new FakeProcessRunner(
            """{"action":"final","answer":"A careful follow-up answer.","evidenceMessageIds":["message-1"]}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateConsultationRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Completed, result.Status);
            Assert.Equal("A careful follow-up answer.", result.Answer);
            Assert.Equal("message-1", Assert.Single(result.EvidenceMessageIds));
            var prompt = runner.LastRequest!.StandardInput;
            Assert.Contains("First question", prompt);
            Assert.Contains("First turn answer", prompt);
            Assert.Contains("Follow-up question", prompt);
            Assert.Contains("consultationHistoryTruncated", prompt);
            Assert.Contains("\"availableToolActions\":[]", prompt);
            Assert.DoesNotContain(AccountId.ToString("D"), prompt);
            Assert.DoesNotContain(ConversationId.ToString("D"), prompt);
            Assert.DoesNotContain(ThreadGuid.ToString("N"), prompt);
            Assert.Empty(AgentContractValidatorV1.ValidateRuntimeResult(result, CreateConsultationTask(), CreateContext()));
            Assert.False(Directory.Exists(runner.LastRequest.WorkingDirectory));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_SearchesHistoryThenUsesOnlyReturnedHitAsFinalEvidence()
    {
        var hitId = Guid.Parse("77ee7680-b8cb-454b-aa43-46a23a266020");
        var session = new FakeContextToolSession(
            [CreateToolContext()],
            searchResult: new HistorySearchResultV1
            {
                Status = HistorySearchStatusV1.Complete,
                MessageSetRevision = "message-set-r1",
                PerspectiveState = PerspectiveStateV1.Confirmed,
                CalibrationVersion = 4,
                Hits =
                [
                    new HistorySearchHitV1
                    {
                        Message = new ContextMessageV1
                        {
                            MessageId = hitId,
                            Text = "older history search hit",
                            MessageType = "Text",
                            OccurredAt = DateTimeOffset.UtcNow.AddDays(-1),
                            Direction = MessageDirectionV1.Incoming,
                            DirectionEvidence = DirectionEvidenceV1.UserConfirmed
                        }
                    }
                ]
            });
        var runner = new FakeProcessRunner(
        [
            """{"action":"searchHistory","query":"camping","limit":10}""",
            $$"""{"action":"final","answer":"The earlier chat supports this answer.","evidenceMessageIds":["{{hitId:D}}"]}"""
        ]);
        var (runtime, _, testDirectory) = CreateRuntime(runner);

        try
        {
            var request = CreateConsultationRequest(session, [AgentReadCapabilityV1.SearchChatHistory], maxToolCalls: 1);
            var result = await runtime.ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Completed, result.Status);
            Assert.Equal("The earlier chat supports this answer.", result.Answer);
            Assert.Equal(hitId.ToString("D"), Assert.Single(result.EvidenceMessageIds));
            Assert.Contains(hitId.ToString("D"), result.VerifiedEvidenceMessageIds);
            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(2, session.ContextRequests.Count);
            Assert.All(session.ContextRequests, contextRequest =>
            {
                Assert.Equal(30, contextRequest.RecentMessageLimit);
                Assert.True(contextRequest.IncludeMemory);
            });
            var searchRequest = Assert.IsType<HistorySearchRequestV1>(session.LastSearchRequest);
            Assert.Equal("camping", searchRequest.Query);
            Assert.Equal("message-set-r1", searchRequest.ExpectedMessageSetRevision);
            Assert.DoesNotContain(searchRequest.GetType().GetProperties(), property =>
                property.Name.Contains("AccountId", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ConversationId", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("older history search hit", runner.Requests[1].StandardInput);
            Assert.DoesNotContain(AccountId.ToString("D"), runner.Requests[1].StandardInput);
            Assert.DoesNotContain(ConversationId.ToString("D"), runner.Requests[1].StandardInput);
            Assert.False(Directory.Exists(runner.Requests[^1].WorkingDirectory));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, 2, "AGENT_TOOL_CAPABILITY_DENIED")]
    [InlineData(true, 0, "AGENT_TOOL_BUDGET_EXCEEDED")]
    public async Task ExecuteAsync_RefusesSearchWithoutCapabilityOrToolBudget(
        bool hasSearchCapability,
        int maxToolCalls,
        string expectedErrorCode)
    {
        var session = new FakeContextToolSession([CreateToolContext()]);
        var runner = new FakeProcessRunner("""{"action":"searchHistory","query":"unavailable"}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var capabilities = hasSearchCapability
                ? new[] { AgentReadCapabilityV1.SearchChatHistory }
                : new[] { AgentReadCapabilityV1.GetRecentMessages };
            var result = await runtime.ExecuteAsync(
                CreateConsultationRequest(session, capabilities, maxToolCalls),
                CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            Assert.Null(session.LastSearchRequest);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_MapsM3SearchRequestValidationFailureWithoutLeakingDetails()
    {
        const string privateQuery = "private chat phrase used only as a search term";
        var session = new FakeContextToolSession([CreateToolContext()], rejectSearchRequest: true);
        var runner = new FakeProcessRunner(
            $$"""{"action":"searchHistory","query":"{{privateQuery}}"}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(
                CreateConsultationRequest(session, [AgentReadCapabilityV1.SearchChatHistory], maxToolCalls: 1),
                CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("AGENT_TOOL_REQUEST_INVALID", result.ErrorCode);
            Assert.DoesNotContain(privateQuery, result.ErrorCode, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DropsAnswerWhenToolReportsRevisionChanged()
    {
        var session = new FakeContextToolSession(
            [CreateToolContext()],
            searchResult: new HistorySearchResultV1
            {
                Status = HistorySearchStatusV1.RevisionChanged,
                MessageSetRevision = "message-set-r1",
                ObservedMessageSetRevision = "message-set-r2",
                Hits = []
            });
        var runner = new FakeProcessRunner(
        [
            """{"action":"searchHistory","query":"changed"}""",
            """{"action":"final","answer":"stale answer","evidenceMessageIds":[]}"""
        ]);
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(
                CreateConsultationRequest(session, [AgentReadCapabilityV1.SearchChatHistory], maxToolCalls: 2),
                CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CONTEXT_REVISION_CHANGED", result.ErrorCode);
            Assert.Null(result.Answer);
            Assert.Empty(result.EvidenceMessageIds);
            Assert.Single(runner.Requests);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DropsFinalAnswerWhenEndContextRevisionChanges()
    {
        var session = new FakeContextToolSession(
        [
            CreateToolContext(),
            CreateToolContext(contextRevision: "context-r8")
        ]);
        var runner = new FakeProcessRunner(
            """{"action":"final","answer":"stale at final fence","evidenceMessageIds":[]}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(
                CreateConsultationRequest(session, [], maxToolCalls: 0),
                CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CONTEXT_REVISION_CHANGED", result.ErrorCode);
            Assert.Null(result.Answer);
            Assert.Empty(result.EvidenceMessageIds);
            Assert.Equal(2, session.ContextRequests.Count);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_RevalidatesPriorTurnEvidenceBeforeItCanBeCited()
    {
        var historyEvidenceId = Guid.Parse("f90eb1cd-c290-481c-98d0-9d949933c7e2");
        var session = new FakeContextToolSession(
            [CreateToolContext()],
            evidenceResult: new MessageEvidenceResultV1
            {
                Status = MessageEvidenceStatusV1.Complete,
                MessageSetRevision = "message-set-r1",
                PerspectiveState = PerspectiveStateV1.Confirmed,
                CalibrationVersion = 4,
                Evidence =
                [
                    new MessageEvidenceItemV1
                    {
                        Message = new ContextMessageV1
                        {
                            MessageId = historyEvidenceId,
                            Text = "revalidated prior evidence",
                            MessageType = "Text",
                            OccurredAt = DateTimeOffset.UtcNow.AddDays(-3),
                            Direction = MessageDirectionV1.Incoming,
                            DirectionEvidence = DirectionEvidenceV1.UserConfirmed
                        }
                    }
                ]
            });
        var runner = new FakeProcessRunner(
        [
            $$"""{"action":"getMessageEvidence","messageIds":["{{historyEvidenceId:D}}"]}""",
            $$"""{"action":"final","answer":"Revalidated history answer.","evidenceMessageIds":["{{historyEvidenceId:D}}"]}"""
        ]);
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var request = CreateConsultationRequest(
                session,
                [AgentReadCapabilityV1.GetMessageEvidence],
                maxToolCalls: 1,
                historyEvidenceIds: [historyEvidenceId.ToString("D")]);
            var result = await runtime.ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Completed, result.Status);
            Assert.Equal(historyEvidenceId.ToString("D"), Assert.Single(result.EvidenceMessageIds));
            Assert.Contains(historyEvidenceId.ToString("D"), result.VerifiedEvidenceMessageIds);
            Assert.Equal("message-set-r1", session.LastEvidenceRequest!.ExpectedMessageSetRevision);
            Assert.Contains(historyEvidenceId, session.LastEvidenceRequest.MessageIds);
            Assert.Contains("revalidated prior evidence", runner.Requests[1].StandardInput);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsEvidenceThatWasNotInAuthorizedContext()
    {
        var runner = new FakeProcessRunner(
            """{"action":"final","answer":"I cannot verify this.","evidenceMessageIds":["another-conversation-message"]}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateConsultationRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("UNAVAILABLE_EVIDENCE_ID", result.ErrorCode);
            Assert.Empty(result.EvidenceMessageIds);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_MapsMalformedJsonToExplicitOutputFailure()
    {
        var runner = new FakeProcessRunner("not-json");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CODEX_SCHEMA_OUTPUT_INVALID", result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsDuplicateCandidatesWithLocalContractValidation()
    {
        var runner = new FakeProcessRunner("""{"suggestions":["same","same","third"]}""");
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CODEX_SCHEMA_OUTPUT_INVALID", result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesProcessStartExitAndMissingStructuredOutput()
    {
        var (startRuntime, _, startDirectory) = CreateRuntime(new FakeProcessRunner("", failToStart: true));
        try
        {
            var startResult = await startRuntime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);
            Assert.Equal("CODEX_CLI_START_FAILED", startResult.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(startDirectory))
            {
                Directory.Delete(startDirectory, recursive: true);
            }
        }

        var (exitRuntime, _, exitDirectory) = CreateRuntime(new FakeProcessRunner("", exitCode: 7, writeOutput: false));
        try
        {
            var exitResult = await exitRuntime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);
            Assert.Equal("CODEX_CLI_EXIT_NONZERO", exitResult.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(exitDirectory))
            {
                Directory.Delete(exitDirectory, recursive: true);
            }
        }

        var (missingRuntime, _, missingDirectory) = CreateRuntime(new FakeProcessRunner("", writeOutput: false));
        try
        {
            var missingResult = await missingRuntime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);
            Assert.Equal("CODEX_SCHEMA_OUTPUT_MISSING", missingResult.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(missingDirectory))
            {
                Directory.Delete(missingDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("AuthenticationMissing", "CODEX_CLI_AUTH_REQUIRED")]
    [InlineData("NetworkConnection", "CODEX_CLI_NETWORK_FAILED")]
    [InlineData("CliConfiguration", "CODEX_CLI_CONFIG_INVALID")]
    [InlineData("SchemaRejected", "CODEX_SCHEMA_REJECTED")]
    [InlineData("Unknown", "CODEX_CLI_EXIT_NONZERO")]
    public async Task ExecuteAsync_MapsOnlyFixedStderrCategoriesToSafeErrorCodes(
        string failureCategoryName,
        string expectedErrorCode)
    {
        var failureCategory = Enum.Parse<CodexCliFailureCategoryV1>(failureCategoryName);
        var runner = new FakeProcessRunner(
            "",
            exitCode: 19,
            writeOutput: false,
            failureCategory: failureCategory);
        var (runtime, _, testDirectory) = CreateRuntime(runner);
        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_MapsTimeoutAndCancellationWithoutRuntimeOutput()
    {
        var timeoutRunner = new FakeProcessRunner("", timedOut: true);
        var (runtime, _, testDirectory) = CreateRuntime(timeoutRunner);
        try
        {
            var timeoutResult = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);
            Assert.Equal("RUNTIME_TIMEOUT", timeoutResult.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        var cancellationRunner = new FakeProcessRunner("", cancelled: true);
        var (cancelRuntime, _, cancelDirectory) = CreateRuntime(cancellationRunner);
        try
        {
            var cancellationResult = await cancelRuntime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);
            Assert.Equal(AgentRunStatusV1.Cancelled, cancellationResult.Status);
        }
        finally
        {
            if (Directory.Exists(cancelDirectory))
            {
                Directory.Delete(cancelDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCmdOnlyShimWithoutSafePowerShellEntryPoint()
    {
        var runner = new FakeProcessRunner("""{"suggestions":["one","two","three"]}""");
        var cmdOnlyDirectory = Path.Combine(Path.GetTempPath(), $"codex-cmd-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cmdOnlyDirectory);
        var cmdPath = Path.Combine(cmdOnlyDirectory, "codex.cmd");
        await File.WriteAllTextAsync(cmdPath, "@echo off");
        var runtime = new CodexCliAgentRuntime(new StructuredCodexCliExecutorV1(runner, cmdPath));

        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CODEX_CLI_START_FAILED", result.ErrorCode);
            Assert.Null(runner.LastRequest);
        }
        finally
        {
            if (Directory.Exists(cmdOnlyDirectory))
            {
                Directory.Delete(cmdOnlyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesPairedNpmCmdShimToNodeAndCodexScript()
    {
        var runner = new FakeProcessRunner("""{"suggestions":["one","two","three"]}""");
        var directory = Path.Combine(Path.GetTempPath(), $"codex-paired-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var cmdPath = Path.Combine(directory, "codex.cmd");
        var ps1Path = Path.ChangeExtension(cmdPath, ".ps1");
        var nodePath = Path.Combine(directory, "node.exe");
        var scriptPath = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(cmdPath, "@echo off");
        await File.WriteAllTextAsync(ps1Path, "throw 'must not execute this wrapper'");
        await File.WriteAllTextAsync(nodePath, string.Empty);
        await File.WriteAllTextAsync(scriptPath, string.Empty);
        var runtime = new CodexCliAgentRuntime(new StructuredCodexCliExecutorV1(runner, cmdPath));

        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Completed, result.Status);
            Assert.Equal(nodePath, runner.LastRequest!.ExecutablePath);
            Assert.Equal(scriptPath, runner.LastRequest.Arguments[0]);
            Assert.DoesNotContain("-File", runner.LastRequest.Arguments);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWhenNpmShimHasNoCodexJs()
    {
        var runner = new FakeProcessRunner("""{"suggestions":["one","two","three"]}""");
        var directory = Path.Combine(Path.GetTempPath(), $"codex-missing-js-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var ps1Path = Path.Combine(directory, "codex.ps1");
        await File.WriteAllTextAsync(ps1Path, string.Empty);
        var runtime = new CodexCliAgentRuntime(new StructuredCodexCliExecutorV1(runner, ps1Path));

        try
        {
            var result = await runtime.ExecuteAsync(CreateReplyRequest(), CancellationToken.None);

            Assert.Equal(AgentRunStatusV1.Failed, result.Status);
            Assert.Equal("CODEX_CLI_START_FAILED", result.ErrorCode);
            Assert.Null(runner.LastRequest);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static (CodexCliAgentRuntime Runtime, string ExecutablePath, string TestDirectory) CreateRuntime(FakeProcessRunner runner)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-agent-runtime-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var shimDirectory = Path.Combine(directory, "npm");
        Directory.CreateDirectory(shimDirectory);
        var executablePath = Path.Combine(shimDirectory, OperatingSystem.IsWindows() ? "codex.ps1" : "codex.ps1");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(Path.Combine(shimDirectory, "node.exe"), string.Empty);
        var codexScriptPath = GetCodexScriptPath(executablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(codexScriptPath)!);
        File.WriteAllText(codexScriptPath, string.Empty);
        return (
            new CodexCliAgentRuntime(new StructuredCodexCliExecutorV1(runner, executablePath)),
            executablePath,
            directory);
    }

    private static string GetCodexScriptPath(string shimPath) => Path.Combine(
        Path.GetDirectoryName(shimPath)!, "node_modules", "@openai", "codex", "bin", "codex.js");

    private static ScopedAgentRequestV1 CreateReplyRequest() => new()
    {
        Task = new AgentTaskV1
        {
            TaskType = AgentTaskTypeV1.ReplyCandidates,
            AccountId = AccountId,
            ConversationId = ConversationId,
            PersonaPresetId = "natural",
            ContextRevision = "context-r7"
        },
        Context = CreateContext(),
        PersonaInstruction = "Use the server selected natural preset.",
        SkillVersion = "reply-v1",
        AllowedReadCapabilities = [AgentReadCapabilityV1.GetRecentMessages],
        Budget = new AgentExecutionBudgetV1
        {
            MaxDuration = TimeSpan.FromSeconds(5),
            MaxContextCharacters = 8000,
            MaxToolCalls = 0
        }
    };

    private static ScopedAgentRequestV1 CreateConsultationRequest() => new()
    {
        Task = CreateConsultationTask(),
        Context = CreateContext(),
        BoundThread = new AgentConsultationThreadV1
        {
            ThreadId = ThreadGuid.ToString("N"),
            AccountId = AccountId,
            ConversationId = ConversationId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        },
        ConsultationHistory =
        [
            new AgentConsultationHistoryTurnV1
            {
                ThreadId = ThreadGuid.ToString("N"),
                AccountId = AccountId,
                ConversationId = ConversationId,
                UserInput = "First question",
                Answer = "First turn answer",
                EvidenceMessageIds = ["message-1"],
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
            }
        ],
        SkillVersion = "consult-v1",
        AllowedReadCapabilities = [AgentReadCapabilityV1.GetRecentMessages],
        Budget = new AgentExecutionBudgetV1
        {
            MaxDuration = TimeSpan.FromSeconds(5),
            MaxContextCharacters = 8000,
            MaxToolCalls = 0
        }
    };

    private static ScopedAgentRequestV1 CreateConsultationRequest(
        IContextToolSessionV1? session,
        IReadOnlyList<AgentReadCapabilityV1> capabilities,
        int maxToolCalls,
        IReadOnlyList<string>? historyEvidenceIds = null) => new()
    {
        Task = CreateConsultationTask(),
        Context = CreateContext(),
        BoundThread = new AgentConsultationThreadV1
        {
            ThreadId = ThreadGuid.ToString("N"),
            AccountId = AccountId,
            ConversationId = ConversationId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        },
        ContextToolSession = session,
        ConsultationHistory =
        [
            new AgentConsultationHistoryTurnV1
            {
                ThreadId = ThreadGuid.ToString("N"),
                AccountId = AccountId,
                ConversationId = ConversationId,
                UserInput = "First question",
                Answer = "First turn answer",
                EvidenceMessageIds = historyEvidenceIds ?? ["message-1"],
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
            }
        ],
        SkillVersion = "consult-v1",
        AllowedReadCapabilities = capabilities,
        Budget = new AgentExecutionBudgetV1
        {
            MaxDuration = TimeSpan.FromSeconds(5),
            MaxContextCharacters = 8000,
            MaxToolCalls = maxToolCalls
        }
    };

    private static AgentTaskV1 CreateConsultationTask() => new()
    {
        TaskType = AgentTaskTypeV1.FreeConsultation,
        AccountId = AccountId,
        ConversationId = ConversationId,
        ThreadId = ThreadGuid.ToString("N"),
        UserInput = "Follow-up question",
        ContextRevision = "context-r7"
    };

    private static AgentContextSnapshotV1 CreateContext() => new()
    {
        AccountId = AccountId,
        ConversationId = ConversationId,
        ContextRevision = "context-r7",
        PerspectiveState = PerspectiveStateV1.Confirmed,
        CalibrationVersion = 4,
        ProfileStatus = ProfileStatusV1.Current,
        RecentMessages =
        [
            new AgentContextMessageV1
            {
                MessageId = "message-1",
                Text = "private chat message content",
                MessageType = "Text",
                OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.UserConfirmed
            }
        ],
        ParticipantProfiles =
        [
            new AgentContextProfileV1
            {
                IdentityNamespace = "internal-identity-namespace",
                SubjectType = ParticipantSubjectTypeV1.Contact,
                SubjectId = Guid.NewGuid(),
                Status = ProfileStatusV1.Current,
                CalibrationVersion = 4,
                Summary = "profilePrivate-summary",
                EvidenceMessageIds = ["message-1"]
            }
        ],
        MemoryItems =
        [
            new AgentContextMemoryV1
            {
                MemoryId = "internal-memory-id",
                IdentityNamespace = null,
                SubjectType = ParticipantSubjectTypeV1.Contact,
                SubjectId = Guid.NewGuid(),
                Origin = MemoryOriginV1.UserEntered,
                Status = MemoryItemStatusV1.Active,
                Text = "authorized-user-note"
            },
            new AgentContextMemoryV1
            {
                MemoryId = "internal-message-memory-id",
                IdentityNamespace = "internal-identity-namespace",
                SubjectType = ParticipantSubjectTypeV1.Contact,
                SubjectId = Guid.NewGuid(),
                Origin = MemoryOriginV1.MessageExtracted,
                Status = MemoryItemStatusV1.Active,
                CalibrationVersion = 4,
                Text = "authorized-message-memory",
                SourceMessageIds = ["message-1"]
            }
        ],
        Omissions = []
    };

    private static ContextToolContextResultV1 CreateToolContext(
        string contextRevision = "context-r7",
        string messageSetRevision = "message-set-r1") => new()
    {
        ContextRevision = contextRevision,
        MessageSetRevision = messageSetRevision,
        PerspectiveState = PerspectiveStateV1.Confirmed,
        CalibrationVersion = 4,
        ProfileStatus = ProfileStatusV1.Current,
        RecentMessages =
        [
            new ContextMessageV1
            {
                MessageId = Guid.Parse("58912300-5e43-4504-a62b-52f8791ad901"),
                Text = "Most recent current context message",
                MessageType = "Text",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Direction = MessageDirectionV1.Incoming,
                DirectionEvidence = DirectionEvidenceV1.UserConfirmed
            }
        ],
        ParticipantProfiles =
        [
            new ContextToolParticipantProfileV1
            {
                SubjectType = ParticipantSubjectTypeV1.Contact,
                CalibrationVersion = 4,
                Status = ProfileStatusV1.Current,
                Summary = "Current contact profile",
                EvidenceMessageIds = [Guid.Parse("58912300-5e43-4504-a62b-52f8791ad901")]
            }
        ],
        MemoryItems =
        [
            new ContextToolMemoryItemV1
            {
                MemoryId = Guid.NewGuid(),
                SubjectType = ParticipantSubjectTypeV1.Contact,
                Text = "Current confirmed memory",
                Origin = MemoryOriginV1.MessageExtracted,
                Status = MemoryItemStatusV1.Active,
                CalibrationVersion = 4,
                SourceMessageIds = [Guid.Parse("58912300-5e43-4504-a62b-52f8791ad901")]
            }
        ],
        Omissions = []
    };

    private sealed class FakeContextToolSession : IContextToolSessionV1
    {
        private readonly Queue<ContextToolContextResultV1> _contexts;

        public FakeContextToolSession(
            IEnumerable<ContextToolContextResultV1> contexts,
            HistorySearchResultV1? searchResult = null,
            MessageEvidenceResultV1? evidenceResult = null,
            bool rejectSearchRequest = false)
        {
            _contexts = new Queue<ContextToolContextResultV1>(contexts);
            SearchResult = searchResult;
            EvidenceResult = evidenceResult;
            RejectSearchRequest = rejectSearchRequest;
        }

        public List<ContextToolRequestV1> ContextRequests { get; } = [];
        public List<HistorySearchRequestV1> SearchRequests { get; } = [];
        public List<MessageEvidenceRequestV1> EvidenceRequests { get; } = [];
        public HistorySearchRequestV1? LastSearchRequest => SearchRequests.LastOrDefault();
        public MessageEvidenceRequestV1? LastEvidenceRequest => EvidenceRequests.LastOrDefault();
        public HistorySearchResultV1? SearchResult { get; }
        public MessageEvidenceResultV1? EvidenceResult { get; }
        public bool RejectSearchRequest { get; }

        public ValueTask<ContextToolContextResultV1> GetContextAsync(
            ContextToolRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContextRequests.Add(request);
            if (_contexts.Count > 1)
            {
                return ValueTask.FromResult(_contexts.Dequeue());
            }

            return ValueTask.FromResult(_contexts.Count == 1
                ? _contexts.Peek()
                : throw new InvalidOperationException("The fake context session has no configured context."));
        }

        public ValueTask<HistorySearchResultV1> SearchHistoryAsync(
            HistorySearchRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchRequests.Add(request);
            if (RejectSearchRequest)
            {
                throw new ArgumentException("Synthetic M3 query validation error containing no user data.");
            }

            return ValueTask.FromResult(SearchResult ?? new HistorySearchResultV1
            {
                Status = HistorySearchStatusV1.Complete,
                MessageSetRevision = request.ExpectedMessageSetRevision
            });
        }

        public ValueTask<MessageEvidenceResultV1> GetMessageEvidenceAsync(
            MessageEvidenceRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvidenceRequests.Add(request);
            return ValueTask.FromResult(EvidenceResult ?? new MessageEvidenceResultV1
            {
                Status = MessageEvidenceStatusV1.Complete,
                MessageSetRevision = request.ExpectedMessageSetRevision
            });
        }
    }

    private static int CountArgumentPair(IReadOnlyList<string> arguments, string name, string value) =>
        Enumerable.Range(0, arguments.Count - 1)
            .Count(index => arguments[index] == name && arguments[index + 1] == value);

    private static IReadOnlyList<string> GetConfigValues(IReadOnlyList<string> arguments) =>
        Enumerable.Range(0, arguments.Count - 1)
            .Where(index => arguments[index] == "--config")
            .Select(index => arguments[index + 1])
            .ToArray();

    private static string GetFollowingArgument(IReadOnlyList<string> arguments, string option)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], option, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
    }

    private sealed class FakeProcessRunner : ICodexCliProcessRunnerV1
    {
        private readonly Queue<string> _outputs;
        private readonly bool _timedOut;
        private readonly bool _cancelled;
        private readonly int? _exitCode;
        private readonly bool _writeOutput;
        private readonly bool _failToStart;
        private readonly CodexCliFailureCategoryV1 _failureCategory;

        public FakeProcessRunner(
            string outputJson,
            bool timedOut = false,
            bool cancelled = false,
            int? exitCode = 0,
            bool writeOutput = true,
            bool failToStart = false,
            CodexCliFailureCategoryV1 failureCategory = CodexCliFailureCategoryV1.Unknown)
            : this([outputJson], timedOut, cancelled, exitCode, writeOutput, failToStart, failureCategory)
        {
        }

        public FakeProcessRunner(
            IEnumerable<string> outputJsons,
            bool timedOut = false,
            bool cancelled = false,
            int? exitCode = 0,
            bool writeOutput = true,
            bool failToStart = false,
            CodexCliFailureCategoryV1 failureCategory = CodexCliFailureCategoryV1.Unknown)
        {
            _outputs = new Queue<string>(outputJsons);
            _timedOut = timedOut;
            _cancelled = cancelled;
            _exitCode = exitCode;
            _writeOutput = writeOutput;
            _failToStart = failToStart;
            _failureCategory = failureCategory;
        }

        public CodexCliProcessRequestV1? LastRequest { get; private set; }
        public string? SchemaPath { get; private set; }
        public string? SchemaContents { get; private set; }
        public List<CodexCliProcessRequestV1> Requests { get; } = [];

        public async Task<CodexCliProcessResultV1> RunAsync(
            CodexCliProcessRequestV1 request,
            CancellationToken cancellationToken)
        {
            if (_failToStart)
            {
                throw new Win32Exception(2);
            }

            LastRequest = request;
            Requests.Add(request);
            var schemaPath = GetFollowingArgument(request.Arguments, "--output-schema");
            SchemaPath = schemaPath;
            SchemaContents = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            if (!_timedOut && !_cancelled && _exitCode == 0)
            {
                var outputPath = GetFollowingArgument(request.Arguments, "--output-last-message");
                var output = _outputs.Count > 0 ? _outputs.Dequeue() : string.Empty;
                if (_writeOutput)
                {
                    await File.WriteAllTextAsync(outputPath, output, cancellationToken);
                }

                return new CodexCliProcessResultV1(
                    _exitCode,
                    TimedOut: false,
                    Cancelled: false,
                    FailureCategory: _failureCategory);
            }

            return new CodexCliProcessResultV1(
                _exitCode,
                TimedOut: _timedOut,
                Cancelled: _cancelled,
                FailureCategory: _failureCategory);
        }
    }
}
