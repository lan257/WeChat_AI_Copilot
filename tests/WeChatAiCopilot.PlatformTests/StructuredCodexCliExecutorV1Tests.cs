using System.Text.Json;
using System.ComponentModel;
using WeChatAiCopilot.Api.Platform.CodexCli;

namespace WeChatAiCopilot.PlatformTests;

public sealed class StructuredCodexCliExecutorV1Tests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"status\":{\"type\":\"string\"}}}";

    [Fact]
    public async Task ExecuteAsync_UsesFixedReadOnlyInvocationStdinAndCleansTemporaryFiles()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        await File.WriteAllTextAsync(executablePath, string.Empty);
        var runner = new FakeProcessRunner((request, cancellationToken) =>
        {
            File.WriteAllText(GetFollowingArgument(request.Arguments, "--output-last-message"), "{\"status\":\"ok\"}");
            return Task.FromResult(new CodexCliProcessResultV1(0, TimedOut: false, Cancelled: false));
        });
        var executor = new StructuredCodexCliExecutorV1(runner, executablePath);
        const string prompt = "synthetic prompt only";

        try
        {
            var result = await executor.ExecuteAsync(prompt, Schema, TimeSpan.FromSeconds(10));

            Assert.Equal(StructuredCodexCliExecutionStatusV1.Completed, result.Status);
            Assert.Equal("ok", result.Output!.Value.GetProperty("status").GetString());
            var request = Assert.IsType<CodexCliProcessRequestV1>(runner.LastRequest);
            Assert.Equal(prompt, request.StandardInput);
            Assert.DoesNotContain(request.Arguments, argument => string.Equals(argument, prompt, StringComparison.Ordinal));
            Assert.Equal("-", request.Arguments[^1]);
            Assert.Contains("--sandbox", request.Arguments);
            Assert.Contains("read-only", request.Arguments);
            Assert.Equal(1, CountArgumentPair(request.Arguments, "--disable", "shell_tool"));
            Assert.Equal(1, CountArgumentPair(request.Arguments, "--disable", "multi_agent"));
            Assert.Equal(1, CountArgumentPair(request.Arguments, "--disable", "remote_plugin"));
            Assert.Contains("mcp_servers={}", GetConfigValues(request.Arguments));
            Assert.Contains("web_search=\"disabled\"", GetConfigValues(request.Arguments));
            Assert.Contains("--ephemeral", request.Arguments);
            Assert.Contains("--ignore-user-config", request.Arguments);
            Assert.Equal(request.WorkingDirectory, GetFollowingArgument(request.Arguments, "--cd"));
            Assert.DoesNotContain("OPENAI_API_KEY", request.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("API_KEY", request.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.All(request.Environment.Keys, name => Assert.Contains(name,
                new[] { "PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "CODEX_HOME" },
                StringComparer.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(request.WorkingDirectory));
            Assert.False(File.Exists(runner.CapturedSchemaPath));
            Assert.False(File.Exists(runner.CapturedOutputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidInputsWithoutStartingProcess()
    {
        var runner = new FakeProcessRunner((_, _) => throw new InvalidOperationException("must not start"));
        var executor = new StructuredCodexCliExecutorV1(runner, "codex");

        var oversizedPrompt = await executor.ExecuteAsync(new string('x', 120_001), Schema, TimeSpan.FromSeconds(1));
        var invalidSchema = await executor.ExecuteAsync("synthetic", "not-json", TimeSpan.FromSeconds(1));
        var nonObjectSchema = await executor.ExecuteAsync("synthetic", "[]", TimeSpan.FromSeconds(1));
        var invalidTimeout = await executor.ExecuteAsync("synthetic", Schema, TimeSpan.Zero);

        Assert.Equal(StructuredCodexCliExecutionStatusV1.PromptTooLarge, oversizedPrompt.Status);
        Assert.Equal(StructuredCodexCliExecutionStatusV1.SchemaInvalid, invalidSchema.Status);
        Assert.Equal(StructuredCodexCliExecutionStatusV1.SchemaInvalid, nonObjectSchema.Status);
        Assert.Equal(StructuredCodexCliExecutionStatusV1.TimedOut, invalidTimeout.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_PassesServerConfiguredModelAsSeparateArgument()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var runner = new FakeProcessRunner((request, _) =>
        {
            File.WriteAllText(GetFollowingArgument(request.Arguments, "--output-last-message"), "{\"status\":\"ok\"}");
            return Task.FromResult(new CodexCliProcessResultV1(0, TimedOut: false, Cancelled: false));
        });
        try
        {
            var result = await new StructuredCodexCliExecutorV1(runner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2), modelId: "fast-model-v1");

            Assert.Equal(StructuredCodexCliExecutionStatusV1.Completed, result.Status);
            Assert.Equal(1, CountArgumentPair(runner.LastRequest!.Arguments, "--model", "fast-model-v1"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnsafeModelIdentifierBeforeStartingProcess()
    {
        var runner = new FakeProcessRunner((_, _) => throw new InvalidOperationException("must not start"));
        var result = await new StructuredCodexCliExecutorV1(runner, "codex")
            .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2), modelId: "model --malicious");

        Assert.Equal(StructuredCodexCliExecutionStatusV1.CliConfigurationInvalid, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsOnlyExplicitNonEmptyProxyVariablesAndKeepsCaseVariants()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var configuredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "synthetic-path",
            ["USERPROFILE"] = "synthetic-home",
            ["HTTP_PROXY"] = "http://proxy.example:8001",
            ["http_proxy"] = "http://lower-proxy.example:8002",
            ["HTTPS_PROXY"] = "   ",
            ["NO_PROXY"] = "localhost,127.0.0.1",
            ["ALL_PROXY"] = "socks5://proxy.example:8003",
            ["CUSTOM_VARIABLE"] = "must-not-pass",
            ["OPENAI_API_KEY"] = "must-not-pass"
        };
        string? GetVariable(string name) => configuredEnvironment.TryGetValue(name, out var value) ? value : null;
        var runner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new CodexCliProcessResultV1(9, TimedOut: false, Cancelled: false)));

        try
        {
            var result = await new StructuredCodexCliExecutorV1(runner, executablePath, GetVariable)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));

            Assert.Equal(StructuredCodexCliExecutionStatusV1.ProcessExitedNonZero, result.Status);
            var childEnvironment = runner.LastRequest!.Environment;
            Assert.Equal("http://proxy.example:8001", childEnvironment["HTTP_PROXY"]);
            Assert.Equal("http://lower-proxy.example:8002", childEnvironment["http_proxy"]);
            Assert.Equal("socks5://proxy.example:8003", childEnvironment["ALL_PROXY"]);
            Assert.Equal("localhost,127.0.0.1", childEnvironment["NO_PROXY"]);
            Assert.False(childEnvironment.ContainsKey("HTTPS_PROXY"));
            Assert.False(childEnvironment.ContainsKey("https_proxy"));
            Assert.False(childEnvironment.ContainsKey("CUSTOM_VARIABLE"));
            Assert.False(childEnvironment.ContainsKey("OPENAI_API_KEY"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotInventProxySettingsWhenTheyAreUnset()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var configuredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "synthetic-path",
            ["USERPROFILE"] = "synthetic-home"
        };
        string? GetVariable(string name) => configuredEnvironment.TryGetValue(name, out var value) ? value : null;
        var runner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new CodexCliProcessResultV1(9, TimedOut: false, Cancelled: false)));

        try
        {
            await new StructuredCodexCliExecutorV1(runner, executablePath, GetVariable)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(runner.LastRequest!.Environment.Keys, key =>
                key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase)
                || key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("synthetic-home", runner.LastRequest.Environment["HOME"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PreservesExplicitCodexHomeBeforeUserHomeFallbacks()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var configuredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = "synthetic-user-profile",
            ["HOME"] = "synthetic-home",
            ["CODEX_HOME"] = "synthetic-explicit-codex-home"
        };
        string? GetVariable(string name) => configuredEnvironment.TryGetValue(name, out var value) ? value : null;
        var runner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new CodexCliProcessResultV1(9, TimedOut: false, Cancelled: false)));

        try
        {
            await new StructuredCodexCliExecutorV1(runner, executablePath, GetVariable)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));

            Assert.Equal("synthetic-explicit-codex-home", runner.LastRequest!.Environment["CODEX_HOME"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DerivesCodexHomeFromUserProfileOrHomeFallback()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var preferredHome = OperatingSystem.IsWindows() ? "synthetic-user-profile" : "synthetic-home";
        var fallbackHome = OperatingSystem.IsWindows() ? "synthetic-home" : "synthetic-user-profile";
        var preferredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = "synthetic-user-profile",
            ["HOME"] = "synthetic-home"
        };
        string? GetPreferredVariable(string name) => preferredEnvironment.TryGetValue(name, out var value) ? value : null;
        var preferredRunner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new CodexCliProcessResultV1(9, TimedOut: false, Cancelled: false)));
        var fallbackEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OperatingSystem.IsWindows() ? "HOME" : "USERPROFILE"] = fallbackHome
        };
        string? GetFallbackVariable(string name) => fallbackEnvironment.TryGetValue(name, out var value) ? value : null;
        var fallbackRunner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new CodexCliProcessResultV1(9, TimedOut: false, Cancelled: false)));

        try
        {
            await new StructuredCodexCliExecutorV1(preferredRunner, executablePath, GetPreferredVariable)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            await new StructuredCodexCliExecutorV1(fallbackRunner, executablePath, GetFallbackVariable)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));

            Assert.Equal(Path.Combine(preferredHome, ".codex"), preferredRunner.LastRequest!.Environment["CODEX_HOME"]);
            Assert.Equal(Path.Combine(fallbackHome, ".codex"), fallbackRunner.LastRequest!.Environment["CODEX_HOME"]);
            Assert.Equal(fallbackHome, fallbackRunner.LastRequest.Environment["HOME"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWithoutCodexHomeOrUserHome()
    {
        var runner = new FakeProcessRunner((_, _) =>
            throw new InvalidOperationException("Process runner must not be called without an auth home."));
        var configuredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "synthetic-path"
        };
        string? GetVariable(string name) => configuredEnvironment.TryGetValue(name, out var value) ? value : null;

        var result = await new StructuredCodexCliExecutorV1(runner, "codex", GetVariable)
            .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));

        Assert.Equal(StructuredCodexCliExecutionStatusV1.ProcessStartFailed, result.Status);
        Assert.Null(result.Output);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFixedStartTimeoutAndCancellationStatuses()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        try
        {
            var startFailureRunner = new FakeProcessRunner((_, _) => throw new Win32Exception(2));
            var startFailure = await new StructuredCodexCliExecutorV1(startFailureRunner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(StructuredCodexCliExecutionStatusV1.ProcessStartFailed, startFailure.Status);

            var timeoutRunner = new FakeProcessRunner((_, _) =>
                Task.FromResult(new CodexCliProcessResultV1(1, TimedOut: true, Cancelled: false)));
            var timedOut = await new StructuredCodexCliExecutorV1(timeoutRunner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(StructuredCodexCliExecutionStatusV1.TimedOut, timedOut.Status);

            var cancelledRunner = new FakeProcessRunner((_, _) =>
                Task.FromResult(new CodexCliProcessResultV1(1, TimedOut: false, Cancelled: true)));
            var cancelled = await new StructuredCodexCliExecutorV1(cancelledRunner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(StructuredCodexCliExecutionStatusV1.Cancelled, cancelled.Status);

            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            var preCancelled = await new StructuredCodexCliExecutorV1(cancelledRunner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2), cancellationSource.Token);
            Assert.Equal(StructuredCodexCliExecutionStatusV1.Cancelled, preCancelled.Status);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData(7, true, StructuredCodexCliExecutionStatusV1.ProcessExitedNonZero)]
    [InlineData(0, false, StructuredCodexCliExecutionStatusV1.SchemaOutputMissing)]
    public async Task ExecuteAsync_ClassifiesProcessAndMissingOutputFailures(
        int exitCode,
        bool writeOutput,
        StructuredCodexCliExecutionStatusV1 expectedStatus)
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var runner = new FakeProcessRunner((request, _) =>
        {
            if (writeOutput)
            {
                File.WriteAllText(GetFollowingArgument(request.Arguments, "--output-last-message"), "{\"status\":\"ignored\"}");
            }

            return Task.FromResult(new CodexCliProcessResultV1(exitCode, TimedOut: false, Cancelled: false));
        });

        try
        {
            var result = await new StructuredCodexCliExecutorV1(runner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(expectedStatus, result.Status);
            Assert.Null(result.Output);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task ExecuteAsync_RejectsMalformedOrNonObjectOutput(string output)
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var runner = new FakeProcessRunner((request, _) =>
        {
            File.WriteAllText(GetFollowingArgument(request.Arguments, "--output-last-message"), output);
            return Task.FromResult(new CodexCliProcessResultV1(0, TimedOut: false, Cancelled: false));
        });

        try
        {
            var result = await new StructuredCodexCliExecutorV1(runner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(StructuredCodexCliExecutionStatusV1.SchemaOutputInvalid, result.Status);
            Assert.Null(result.Output);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWhenOutputExceedsBound()
    {
        var directory = CreateTemporaryDirectory();
        var executablePath = CreateFakeExecutable(directory);
        var runner = new FakeProcessRunner((request, _) =>
        {
            File.WriteAllText(GetFollowingArgument(request.Arguments, "--output-last-message"), "{\"x\":\"" + new string('x', 128 * 1024) + "\"}");
            return Task.FromResult(new CodexCliProcessResultV1(0, TimedOut: false, Cancelled: false));
        });

        try
        {
            var result = await new StructuredCodexCliExecutorV1(runner, executablePath)
                .ExecuteAsync("synthetic", Schema, TimeSpan.FromSeconds(2));
            Assert.Equal(StructuredCodexCliExecutionStatusV1.SchemaOutputInvalid, result.Status);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void Resolver_UsesNodeAndCodexJsInsteadOfPowerShellShim()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var shimPath = Path.Combine(directory, "codex.ps1");
            var nodePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
            var scriptPath = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            File.WriteAllText(shimPath, string.Empty);
            File.WriteAllText(nodePath, string.Empty);
            File.WriteAllText(scriptPath, string.Empty);

            var command = CodexCliExecutableResolverV1.Resolve(shimPath);

            Assert.Equal(nodePath, command.ExecutablePath);
            Assert.Equal(scriptPath, Assert.Single(command.PrefixArguments));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateFakeExecutable(string directory)
    {
        var executablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        File.WriteAllText(executablePath, string.Empty);
        return executablePath;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"structured-codex-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
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
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return string.Empty;
    }

    private sealed class FakeProcessRunner(
        Func<CodexCliProcessRequestV1, CancellationToken, Task<CodexCliProcessResultV1>> execute) : ICodexCliProcessRunnerV1
    {
        public CodexCliProcessRequestV1? LastRequest { get; private set; }
        public string? CapturedSchemaPath { get; private set; }
        public string? CapturedOutputPath { get; set; }

        public async Task<CodexCliProcessResultV1> RunAsync(
            CodexCliProcessRequestV1 request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            CapturedSchemaPath = GetFollowingArgument(request.Arguments, "--output-schema");
            CapturedOutputPath = GetFollowingArgument(request.Arguments, "--output-last-message");
            return await execute(request, cancellationToken);
        }
    }
}
