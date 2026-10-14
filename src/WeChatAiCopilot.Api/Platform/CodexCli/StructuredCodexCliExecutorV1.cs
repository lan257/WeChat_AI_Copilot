using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WeChatAiCopilot.Api.Platform.CodexCli;

internal sealed record CodexCliProcessRequestV1
{
    public string ExecutablePath { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public string StandardInput { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

internal enum CodexCliFailureCategoryV1
{
    Unknown,
    AuthenticationMissing,
    NetworkConnection,
    CliConfiguration,
    SchemaRejected
}

internal sealed record CodexCliProcessResultV1(
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    CodexCliFailureCategoryV1 FailureCategory = CodexCliFailureCategoryV1.Unknown,
    long? ProcessStartupMilliseconds = null,
    long? ExecutionMilliseconds = null);

internal interface ICodexCliProcessRunnerV1
{
    Task<CodexCliProcessResultV1> RunAsync(
        CodexCliProcessRequestV1 request,
        CancellationToken cancellationToken);
}

internal sealed class StructuredCodexCliExecutorV1 : IStructuredCodexCliExecutorV1
{
    private const int MaximumPromptCharacters = 120_000;
    private const int MaximumSchemaBytes = 64 * 1024;
    private const int MaximumOutputBytes = 128 * 1024;

    private readonly ICodexCliProcessRunnerV1 _processRunner;
    private readonly string _configuredExecutablePath;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public StructuredCodexCliExecutorV1(
        ICodexCliProcessRunnerV1 processRunner,
        string configuredExecutablePath,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _configuredExecutablePath = string.IsNullOrWhiteSpace(configuredExecutablePath)
            ? "codex"
            : configuredExecutablePath;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public async Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(
        string prompt,
        string outputSchemaJson,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? modelId = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Result(StructuredCodexCliExecutionStatusV1.Cancelled);
        }

        if (string.IsNullOrEmpty(prompt) || prompt.Length > MaximumPromptCharacters)
        {
            return Result(StructuredCodexCliExecutionStatusV1.PromptTooLarge);
        }

        if (string.IsNullOrWhiteSpace(outputSchemaJson)
            || Encoding.UTF8.GetByteCount(outputSchemaJson) > MaximumSchemaBytes)
        {
            return Result(StructuredCodexCliExecutionStatusV1.SchemaInvalid);
        }

        if (timeout <= TimeSpan.Zero)
        {
            return Result(StructuredCodexCliExecutionStatusV1.TimedOut);
        }

        if (modelId is not null && !IsSafeModelId(modelId))
        {
            return Result(StructuredCodexCliExecutionStatusV1.CliConfigurationInvalid);
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(outputSchemaJson);
            if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result(StructuredCodexCliExecutionStatusV1.SchemaInvalid);
            }
        }
        catch (JsonException)
        {
            return Result(StructuredCodexCliExecutionStatusV1.SchemaInvalid);
        }

        var processEnvironment = BuildWhitelistedEnvironment(_getEnvironmentVariable);
        if (processEnvironment is null)
        {
            return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"wechat-structured-codex-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var schemaPath = Path.Combine(tempDirectory, "output-schema.json");
            var outputPath = Path.Combine(tempDirectory, "last-message.json");
            await File.WriteAllTextAsync(
                schemaPath,
                outputSchemaJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            CodexCliCommandV1 command;
            try
            {
                command = CodexCliExecutableResolverV1.Resolve(_configuredExecutablePath);
            }
            catch (CodexCliConfigurationException exception)
            {
                return Result(exception.Status);
            }

            var processRequest = new CodexCliProcessRequestV1
            {
                ExecutablePath = command.ExecutablePath,
                Arguments = command.PrefixArguments.Concat(BuildArguments(tempDirectory, schemaPath, outputPath, modelId)).ToArray(),
                WorkingDirectory = tempDirectory,
                StandardInput = prompt,
                Timeout = timeout,
                Environment = processEnvironment
            };

            CodexCliProcessResultV1 processResult;
            try
            {
                processResult = await _processRunner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(StructuredCodexCliExecutionStatusV1.Cancelled);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
            }
            catch (FileNotFoundException)
            {
                return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
            }
            catch (Exception)
            {
                return Result(StructuredCodexCliExecutionStatusV1.ExecutionFailed);
            }

            if (processResult.Cancelled || cancellationToken.IsCancellationRequested)
            {
                return Result(StructuredCodexCliExecutionStatusV1.Cancelled);
            }

            if (processResult.TimedOut)
            {
                return Result(StructuredCodexCliExecutionStatusV1.TimedOut);
            }

            if (processResult.ExitCode is null)
            {
                return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
            }

            if (processResult.ExitCode != 0)
            {
                return Result(processResult.FailureCategory switch
                {
                    CodexCliFailureCategoryV1.AuthenticationMissing => StructuredCodexCliExecutionStatusV1.AuthenticationMissing,
                    CodexCliFailureCategoryV1.NetworkConnection => StructuredCodexCliExecutionStatusV1.NetworkConnectionFailed,
                    CodexCliFailureCategoryV1.CliConfiguration => StructuredCodexCliExecutionStatusV1.CliConfigurationInvalid,
                    CodexCliFailureCategoryV1.SchemaRejected => StructuredCodexCliExecutionStatusV1.SchemaRejected,
                    _ => StructuredCodexCliExecutionStatusV1.ProcessExitedNonZero
                });
            }

            if (!File.Exists(outputPath))
            {
                return Result(StructuredCodexCliExecutionStatusV1.SchemaOutputMissing);
            }

            var outputInfo = new FileInfo(outputPath);
            if (outputInfo.Length <= 0 || outputInfo.Length > MaximumOutputBytes)
            {
                return Result(StructuredCodexCliExecutionStatusV1.SchemaOutputInvalid);
            }

            try
            {
                using var outputDocument = JsonDocument.Parse(
                    await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
                if (outputDocument.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Result(StructuredCodexCliExecutionStatusV1.SchemaOutputInvalid);
                }

                return new StructuredCodexCliExecutionResultV1(
                    StructuredCodexCliExecutionStatusV1.Completed,
                    outputDocument.RootElement.Clone(),
                    processResult.ProcessStartupMilliseconds,
                    processResult.ExecutionMilliseconds);
            }
            catch (JsonException)
            {
                return Result(StructuredCodexCliExecutionStatusV1.SchemaOutputInvalid);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(StructuredCodexCliExecutionStatusV1.Cancelled);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
        }
        catch (FileNotFoundException)
        {
            return Result(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
        }
        catch (Exception)
        {
            return Result(StructuredCodexCliExecutionStatusV1.ExecutionFailed);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Raw prompt/output are not logged; any remaining temp files are OS-cleanup owned.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup errors never replace the fixed result status.
            }
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        string workingDirectory,
        string schemaPath,
        string outputPath,
        string? modelId)
    {
        var arguments = new List<string>
        {
            "exec",
            "--sandbox", "read-only",
            "--disable", "shell_tool",
            "--disable", "multi_agent",
            "--disable", "remote_plugin",
            "--config", "mcp_servers={}",
            "--config", "web_search=\"disabled\"",
            "--ephemeral",
            "--ignore-user-config",
            "--skip-git-repo-check",
            "--cd", workingDirectory,
            "--output-schema", schemaPath,
            "--output-last-message", outputPath
        };
        if (modelId is not null)
        {
            arguments.Add("--model");
            arguments.Add(modelId);
        }

        arguments.Add("-");
        return arguments;
    }

    private static bool IsSafeModelId(string modelId) =>
        modelId.Length is > 0 and <= 80
        && modelId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static IReadOnlyDictionary<string, string>? BuildWhitelistedEnvironment(
        Func<string, string?> getEnvironmentVariable)
    {
        var names = new[]
        {
            "PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME",
            "APPDATA", "LOCALAPPDATA",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "http_proxy", "https_proxy", "all_proxy", "no_proxy"
        };
        // Preserve both casing variants on Unix. Windows process environments are
        // case-insensitive, and the platform will collapse equivalent names there.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = getEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        var explicitCodexHome = getEnvironmentVariable("CODEX_HOME");
        var userHome = OperatingSystem.IsWindows()
            ? FirstNonWhitespace(values.GetValueOrDefault("USERPROFILE"), values.GetValueOrDefault("HOME"))
            : FirstNonWhitespace(values.GetValueOrDefault("HOME"), values.GetValueOrDefault("USERPROFILE"));

        if (!string.IsNullOrWhiteSpace(explicitCodexHome))
        {
            values["CODEX_HOME"] = explicitCodexHome;
        }
        else
        {
            if (userHome is null)
            {
                return null;
            }

            try
            {
                values["CODEX_HOME"] = Path.Combine(userHome, ".codex");
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        if (!values.ContainsKey("HOME") && userHome is not null)
        {
            values["HOME"] = userHome;
        }

        return values;
    }

    private static string? FirstNonWhitespace(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    private static StructuredCodexCliExecutionResultV1 Result(StructuredCodexCliExecutionStatusV1 status) => new(status, null);
}

internal sealed record CodexCliCommandV1(string ExecutablePath, IReadOnlyList<string> PrefixArguments);

internal sealed class CodexCliConfigurationException(
    StructuredCodexCliExecutionStatusV1 status) : Exception
{
    public StructuredCodexCliExecutionStatusV1 Status { get; } = status;
}

internal static class CodexCliExecutableResolverV1
{
    public static CodexCliCommandV1 Resolve(string configuredPath)
    {
        foreach (var candidate in ResolveCandidates(configuredPath))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var extension = Path.GetExtension(candidate);
            if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNpmShim(candidate);
            }

            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                var pairedPowerShellShim = Path.ChangeExtension(candidate, ".ps1");
                if (File.Exists(pairedPowerShellShim))
                {
                    return ResolveNpmShim(pairedPowerShellShim);
                }

                throw new CodexCliConfigurationException(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
            }

            if (string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexCliCommandV1(ResolveNodeExecutable(Path.GetDirectoryName(candidate)!), [candidate]);
            }

            return new CodexCliCommandV1(candidate, []);
        }

        throw new CodexCliConfigurationException(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
    }

    private static CodexCliCommandV1 ResolveNpmShim(string shimPath)
    {
        var shimDirectory = Path.GetDirectoryName(shimPath)!;
        var codexScript = Path.Combine(shimDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(codexScript))
        {
            throw new CodexCliConfigurationException(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
        }

        return new CodexCliCommandV1(ResolveNodeExecutable(shimDirectory), [codexScript]);
    }

    private static string ResolveNodeExecutable(string preferredDirectory)
    {
        var nodeFileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var adjacentNode = Path.Combine(preferredDirectory, nodeFileName);
        if (File.Exists(adjacentNode))
        {
            return adjacentNode;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, nodeFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new CodexCliConfigurationException(StructuredCodexCliExecutionStatusV1.ProcessStartFailed);
    }

    private static IEnumerable<string> ResolveCandidates(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            yield break;
        }

        var isRooted = Path.IsPathRooted(configuredPath);
        var hasExtension = Path.HasExtension(configuredPath);
        string[] names = hasExtension
            ? [configuredPath]
            : OperatingSystem.IsWindows()
                ? new[] { $"{configuredPath}.ps1", $"{configuredPath}.exe", $"{configuredPath}.cmd" }
                : new[] { configuredPath };

        if (isRooted)
        {
            foreach (var name in names)
            {
                yield return Path.GetFullPath(name);
            }

            yield break;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }
}

internal sealed class SystemCodexCliProcessRunnerV1 : ICodexCliProcessRunnerV1
{
    private const int StderrHeadBytes = 4 * 1024;
    private const int StderrTailBytes = 4 * 1024;

    public async Task<CodexCliProcessResultV1> RunAsync(CodexCliProcessRequestV1 request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startupTimestamp=Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            return new CodexCliProcessResultV1(null, TimedOut: false, Cancelled: false);
        }
        var startupMilliseconds=(long)Stopwatch.GetElapsedTime(startupTimestamp).TotalMilliseconds;
        var executionTimestamp=Stopwatch.GetTimestamp();

        var stdoutDrain = DrainAndDiscardAsync(process.StandardOutput.BaseStream);
        var stderrClassification = DrainAndClassifyStderrAsync(process.StandardError.BaseStream);
        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linkedSource.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The process may exit before reading stdin; output is discarded either way.
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrClassification).ConfigureAwait(false);
            return new CodexCliProcessResultV1(
                process.ExitCode,
                TimedOut: !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested,
                Cancelled: cancellationToken.IsCancellationRequested,
                FailureCategory: await stderrClassification.ConfigureAwait(false),
                ProcessStartupMilliseconds:startupMilliseconds,
                ExecutionMilliseconds:(long)Stopwatch.GetElapsedTime(executionTimestamp).TotalMilliseconds);
        }
        catch
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrClassification).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutDrain, stderrClassification).ConfigureAwait(false);
        return new CodexCliProcessResultV1(
            process.ExitCode,
            TimedOut: false,
            Cancelled: false,
            FailureCategory: await stderrClassification.ConfigureAwait(false),
            ProcessStartupMilliseconds:startupMilliseconds,
            ExecutionMilliseconds:(long)Stopwatch.GetElapsedTime(executionTimestamp).TotalMilliseconds);
    }

    private static async Task DrainAndDiscardAsync(Stream stream)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<CodexCliFailureCategoryV1> DrainAndClassifyStderrAsync(Stream stream)
    {
        // Codex prints retry noise before the decisive line, so "more than the capture bound" used to
        // mean "give up" and every real network failure left as an unhelpful generic exit code.
        // Keep a bounded head and a bounded rolling tail instead: still fixed memory, still raw text
        // never leaves this function.
        var head = new byte[StderrHeadBytes];
        var tail = new byte[StderrTailBytes];
        var buffer = new byte[2048];
        var headCount = 0;
        var tailCount = 0;
        var tailStart = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var chunk = buffer.AsSpan(0, read);
                var headRoom = head.Length - headCount;
                if (headRoom > 0)
                {
                    var take = Math.Min(headRoom, chunk.Length);
                    chunk.Slice(0, take).CopyTo(head.AsSpan(headCount));
                    headCount += take;
                }

                var firstPart = Math.Min(chunk.Length, tail.Length - tailStart);
                chunk.Slice(0, firstPart).CopyTo(tail.AsSpan(tailStart));
                if (chunk.Length > firstPart)
                {
                    chunk.Slice(firstPart).CopyTo(tail.AsSpan(0, chunk.Length - firstPart));
                }

                tailStart = (tailStart + chunk.Length) % tail.Length;
                tailCount = Math.Min(tailCount + chunk.Length, tail.Length);
            }

            var category = CodexCliStderrClassifierV1.Classify(head.AsSpan(0, headCount));
            if (category != CodexCliFailureCategoryV1.Unknown)
            {
                return category;
            }

            if (tailCount < tail.Length)
            {
                return CodexCliStderrClassifierV1.Classify(tail.AsSpan(0, tailCount));
            }

            var linear = new byte[tail.Length];
            tail.AsSpan(tailStart).CopyTo(linear);
            tail.AsSpan(0, tailStart).CopyTo(linear.AsSpan(tail.Length - tailStart));
            try
            {
                return CodexCliStderrClassifierV1.Classify(linear);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(linear);
            }
        }
        catch (IOException)
        {
            return CodexCliFailureCategoryV1.Unknown;
        }
        catch (ObjectDisposedException)
        {
            return CodexCliFailureCategoryV1.Unknown;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(head);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(tail);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
