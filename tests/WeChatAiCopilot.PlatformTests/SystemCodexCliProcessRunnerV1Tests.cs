using WeChatAiCopilot.Api.Platform.CodexCli;
using System.Text;
using System.Diagnostics;

namespace WeChatAiCopilot.PlatformTests;

public sealed class SystemCodexCliProcessRunnerV1Tests
{
    [Fact]
    public async Task RunAsync_WritesMultilineUnicodeInputAsUtf8AndClosesInput()
    {
        var workingDirectory = CreateTemporaryDirectory();
        var capturedInputPath = Path.Combine(workingDirectory, "captured-input.bin");
        const string prompt = "Synthetic test prompt.\n第二行含有中文和标点。\n第三行。";
        try
        {
            var request = CreateInputCaptureRequest(workingDirectory, capturedInputPath, prompt);
            var result = await new SystemCodexCliProcessRunnerV1().RunAsync(request, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Equal(Encoding.UTF8.GetBytes(prompt), await File.ReadAllBytesAsync(capturedInputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("SYNTHETIC_SECRET_MARKER not logged in", "AuthenticationMissing")]
    [InlineData("SYNTHETIC_SECRET_MARKER ECONNREFUSED connection refused", "NetworkConnection")]
    [InlineData("SYNTHETIC_SECRET_MARKER unknown config key", "CliConfiguration")]
    [InlineData("SYNTHETIC_SECRET_MARKER invalid JSON schema", "SchemaRejected")]
    [InlineData("SYNTHETIC_SECRET_MARKER unrelated child diagnostic", "Unknown")]
    [InlineData("SYNTHETIC_SECRET_MARKER Error: error sending request for url (https://model.invalid/v1)", "NetworkConnection")]
    [InlineData("SYNTHETIC_SECRET_MARKER failed to refresh available models: request timed out", "NetworkConnection")]
    public async Task RunAsync_ClassifiesBoundedStderrWithoutReturningRawText(
        string stderrText,
        string expectedCategoryName)
    {
        var expectedCategory = Enum.Parse<CodexCliFailureCategoryV1>(expectedCategoryName);
        var workingDirectory = CreateTemporaryDirectory();
        try
        {
            var result = await new SystemCodexCliProcessRunnerV1().RunAsync(
                CreateRequest(workingDirectory, stderrText),
                CancellationToken.None);

            Assert.Equal(19, result.ExitCode);
            Assert.Equal(expectedCategory, result.FailureCategory);
            Assert.DoesNotContain("SYNTHETIC_SECRET_MARKER", result.ToString());
            Assert.DoesNotContain(typeof(CodexCliProcessResultV1).GetProperties(), property =>
                property.Name.Contains("stderr", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("diagnosticText", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("AuthenticationMissing")]
    [InlineData("NetworkConnection")]
    public async Task RunAsync_ClassifiesPhraseAtEitherEndOfLongStderrWithoutReturningRawText(
        string expectedCategoryName)
    {
        var expectedCategory = Enum.Parse<CodexCliFailureCategoryV1>(expectedCategoryName);
        var noise = new string('x', 12 * 1024);
        // A chatty CLI puts the decisive line either first or last. Both must still be classified:
        // bailing out on long output turned every real network failure into a useless generic code.
        var stderrText = expectedCategoryName == "AuthenticationMissing"
            ? "SYNTHETIC_SECRET_MARKER not logged in" + noise
            : noise + "SYNTHETIC_SECRET_MARKER ECONNREFUSED connection refused";
        var workingDirectory = CreateTemporaryDirectory();
        try
        {
            var result = await new SystemCodexCliProcessRunnerV1().RunAsync(
                CreateRequest(workingDirectory, stderrText),
                CancellationToken.None);

            Assert.Equal(19, result.ExitCode);
            Assert.Equal(expectedCategory, result.FailureCategory);
            Assert.DoesNotContain("SYNTHETIC_SECRET_MARKER", result.ToString());
        }
        finally
        {
            DeleteTemporaryDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_KeepsLongUnrelatedStderrUnknownWithoutLeakingText()
    {
        var workingDirectory = CreateTemporaryDirectory();
        try
        {
            var result = await new SystemCodexCliProcessRunnerV1().RunAsync(
                CreateRequest(workingDirectory, "SYNTHETIC_SECRET_MARKER " + new string('x', 12 * 1024)),
                CancellationToken.None);

            Assert.Equal(19, result.ExitCode);
            Assert.Equal(CodexCliFailureCategoryV1.Unknown, result.FailureCategory);
            Assert.DoesNotContain("SYNTHETIC_SECRET_MARKER", result.ToString());
        }
        finally
        {
            DeleteTemporaryDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsFakeChildProcessTree()
    {
        var workingDirectory = CreateTemporaryDirectory();
        var childPidPath = Path.Combine(workingDirectory, "fake-child.pid");
        var childProcessId = 0;
        try
        {
            var result = await new SystemCodexCliProcessRunnerV1().RunAsync(
                CreateSleepingProcessTreeRequest(workingDirectory, childPidPath),
                CancellationToken.None);

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.True(File.Exists(childPidPath));
            Assert.True(int.TryParse(await File.ReadAllTextAsync(childPidPath), out childProcessId));

            await WaitUntilProcessExitsAsync(childProcessId, TimeSpan.FromSeconds(3));
            Assert.False(IsProcessRunning(childProcessId));
        }
        finally
        {
            if (childProcessId > 0 && IsProcessRunning(childProcessId))
            {
                try
                {
                    using var child = Process.GetProcessById(childProcessId);
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(3000);
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            DeleteTemporaryDirectory(workingDirectory);
        }
    }

    private static CodexCliProcessRequestV1 CreateRequest(string workingDirectory, string stderrText)
    {
        string executablePath;
        IReadOnlyList<string> arguments;
        if (OperatingSystem.IsWindows())
        {
            executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var escapedText = stderrText.Replace("'", "''", StringComparison.Ordinal);
            arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $"[Console]::Error.Write('{escapedText}'); exit 19"];
        }
        else
        {
            executablePath = "/bin/sh";
            arguments = ["-c", $"printf '%s' '{stderrText}' >&2; exit 19"];
        }

        return new CodexCliProcessRequestV1
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = string.Empty,
            Timeout = TimeSpan.FromSeconds(10),
            Environment = GetTestEnvironment()
        };
    }

    private static CodexCliProcessRequestV1 CreateInputCaptureRequest(
        string workingDirectory,
        string capturedInputPath,
        string prompt)
    {
        string executablePath;
        IReadOnlyList<string> arguments;
        if (OperatingSystem.IsWindows())
        {
            executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var escapedPath = capturedInputPath.Replace("'", "''", StringComparison.Ordinal);
            arguments = [
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"$inputStream = [Console]::OpenStandardInput(); $fileStream = [IO.File]::Create('{escapedPath}'); $inputStream.CopyTo($fileStream); $fileStream.Dispose(); exit 0"
            ];
        }
        else
        {
            executablePath = "/bin/sh";
            var escapedPath = capturedInputPath.Replace("'", "'\\''", StringComparison.Ordinal);
            arguments = ["-c", $"cat > '{escapedPath}'"];
        }

        return new CodexCliProcessRequestV1
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = prompt,
            Timeout = TimeSpan.FromSeconds(10),
            Environment = GetTestEnvironment()
        };
    }

    private static CodexCliProcessRequestV1 CreateSleepingProcessTreeRequest(
        string workingDirectory,
        string childPidPath)
    {
        string executablePath;
        IReadOnlyList<string> arguments;
        if (OperatingSystem.IsWindows())
        {
            executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var escapedExecutable = executablePath.Replace("'", "''", StringComparison.Ordinal);
            var escapedPidPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
            var command = $"$childArgs = '-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"'; $child = Start-Process -FilePath '{escapedExecutable}' -ArgumentList $childArgs -PassThru -WindowStyle Hidden; [IO.File]::WriteAllText('{escapedPidPath}', [string]$child.Id); Start-Sleep -Seconds 30";
            arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command];
        }
        else
        {
            executablePath = "/bin/sh";
            var escapedPidPath = childPidPath.Replace("'", "'\\''", StringComparison.Ordinal);
            arguments = ["-c", $"sleep 30 & child_pid=$!; printf '%s' \"$child_pid\" > '{escapedPidPath}'; wait"];
        }

        return new CodexCliProcessRequestV1
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = string.Empty,
            Timeout = TimeSpan.FromSeconds(2),
            Environment = GetTestEnvironment()
        };
    }

    private static async Task WaitUntilProcessExitsAsync(int processId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout && IsProcessRunning(processId))
        {
            await Task.Delay(50);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> GetTestEnvironment()
    {
        var names = new[] { "PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA" };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-stderr-test-{Guid.NewGuid():N}");
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
}
