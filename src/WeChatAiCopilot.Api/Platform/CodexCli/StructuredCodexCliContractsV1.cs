using System.Text.Json;

namespace WeChatAiCopilot.Api.Platform.CodexCli;

public enum StructuredCodexCliExecutionStatusV1
{
    Completed,
    TimedOut,
    Cancelled,
    ProcessStartFailed,
    ProcessExitedNonZero,
    AuthenticationMissing,
    NetworkConnectionFailed,
    CliConfigurationInvalid,
    SchemaRejected,
    SchemaInvalid,
    SchemaOutputMissing,
    SchemaOutputInvalid,
    PromptTooLarge,
    ExecutionFailed
}

public sealed record StructuredCodexCliExecutionResultV1(
    StructuredCodexCliExecutionStatusV1 Status,
    JsonElement? Output);

/// <summary>
/// Fixed-policy structured Codex execution. Business modules provide data, a
/// JSON Schema, a timeout, and an optional server-configured model ID; command
/// lines and environments stay in Platform.
/// </summary>
public interface IStructuredCodexCliExecutorV1
{
    Task<StructuredCodexCliExecutionResultV1> ExecuteAsync(
        string prompt,
        string outputSchemaJson,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? modelId = null);
}
