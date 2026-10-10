namespace WeChatAiCopilot.Api.Contracts.V1;

public sealed record ApiErrorDetailV1(
    string Code,
    string Message,
    bool Retryable,
    string CorrelationId);

public sealed record ApiErrorV1(ApiErrorDetailV1 Error);
