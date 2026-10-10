using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var readerGroup = endpoints.MapGroup("/api/reader")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:ReaderBearerToken"));

        readerGroup.MapPost("/messages", IngestMessageAsync);
        readerGroup.MapPost("/history-batches", IngestHistoryBatchAsync);
        return endpoints;
    }

    private static async Task<IResult> IngestMessageAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_NOT_CONFIGURED", "The formal message store is not configured.", retryable: true);
        }

        var allowedProvider = configuration["Reader:Provider"] ?? "local-wechat";
        var allowedAccount = configuration["Reader:AllowedAccountExternalId"];
        var allowedConversation = configuration["Reader:AllowedConversationExternalId"];
        if (string.IsNullOrWhiteSpace(allowedAccount) || string.IsNullOrWhiteSpace(allowedConversation))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The authorized Reader target is not configured.", retryable: false);
        }

        RawMessageEventV1? message;
        try
        {
            message = await context.Request.ReadFromJsonAsync<RawMessageEventV1>(cancellationToken);
        }
        catch (JsonException)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_JSON", "The request body is not valid JSON.", retryable: false);
        }
        catch (InvalidOperationException)
        {
            return Error(context, StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE", "The request body must use application/json.", retryable: false);
        }

        var violations = ContractValidatorV1.Validate(message);
        if (violations.Count != 0)
        {
            return Results.Json(
                new ApiErrorV1(new ApiErrorDetailV1(
                    "INVALID_EVENT",
                    string.Join("; ", violations.Select(violation => $"{violation.Path}: {violation.Code}")),
                    false,
                    context.TraceIdentifier)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(message!.Provider, allowedProvider, StringComparison.Ordinal)
            || !string.Equals(message.AccountExternalId, allowedAccount, StringComparison.Ordinal)
            || !string.Equals(message.ConversationExternalId, allowedConversation, StringComparison.Ordinal))
        {
            return Error(context, StatusCodes.Status403Forbidden,
                "READER_SCOPE_NOT_ALLOWED", "The event is outside the configured Reader target.", retryable: false);
        }

        if (message.SourceKind != SourceKindV1.Realtime)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_SOURCE_KIND", "Realtime events must use sourceKind Realtime.", retryable: false);
        }

        try
        {
            var service = new PostgresMessageIngestionService(dataSource);
            var receipt = await service.IngestAsync(message, cancellationToken);
            return receipt.MatchStatus == MatchStatusV1.NeedsReview
                ? Results.Accepted(value: receipt)
                : Results.Ok(receipt);
        }
        catch (NpgsqlException)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);
        }
        catch (InvalidOperationException)
        {
            return Error(context, StatusCodes.Status409Conflict,
                "MESSAGE_SOURCE_CONFLICT", "The source event conflicts with stored message evidence.", retryable: false);
        }
    }

    private static async Task<IResult> IngestHistoryBatchAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_NOT_CONFIGURED", "The formal message store is not configured.", retryable: true);
        }

        var allowedProvider = configuration["Reader:Provider"] ?? "local-wechat";
        var allowedAccount = configuration["Reader:AllowedAccountExternalId"];
        var allowedConversation = configuration["Reader:AllowedConversationExternalId"];
        if (string.IsNullOrWhiteSpace(allowedAccount) || string.IsNullOrWhiteSpace(allowedConversation))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "READER_SCOPE_NOT_CONFIGURED", "The authorized Reader target is not configured.", retryable: false);
        }

        HistoryBatchV1? batch;
        try
        {
            batch = await context.Request.ReadFromJsonAsync<HistoryBatchV1>(cancellationToken);
        }
        catch (JsonException)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_JSON", "The request body is not valid JSON.", retryable: false);
        }
        catch (InvalidOperationException)
        {
            return Error(context, StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE", "The request body must use application/json.", retryable: false);
        }

        if (batch?.Items is { Count: > PostgresMessageIngestionService.MaxHistoryBatchItems })
        {
            return Error(context, StatusCodes.Status413PayloadTooLarge,
                "HISTORY_BATCH_TOO_LARGE",
                $"A history batch cannot contain more than {PostgresMessageIngestionService.MaxHistoryBatchItems} items.",
                retryable: false);
        }

        var violations = ContractValidatorV1.Validate(batch);
        var envelopeViolations = violations
            .Where(violation => !violation.Path.StartsWith("items[", StringComparison.Ordinal))
            .ToArray();
        if (envelopeViolations.Length > 0)
        {
            return Results.Json(
                new ApiErrorV1(new ApiErrorDetailV1(
                    "INVALID_HISTORY_BATCH",
                    string.Join("; ", envelopeViolations.Select(violation => $"{violation.Path}: {violation.Code}")),
                    false,
                    context.TraceIdentifier)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(batch!.AccountExternalId, allowedAccount, StringComparison.Ordinal)
            || !string.Equals(batch.ConversationExternalId, allowedConversation, StringComparison.Ordinal)
            || batch.Items!.Any(item => item is null
                || !string.Equals(item.Provider, allowedProvider, StringComparison.Ordinal)))
        {
            return Error(context, StatusCodes.Status403Forbidden,
                "READER_SCOPE_NOT_ALLOWED", "The batch is outside the configured Reader target.", retryable: false);
        }

        try
        {
            var service = new PostgresMessageIngestionService(dataSource);
            var receipt = await service.IngestHistoryBatchAsync(batch, cancellationToken);
            return Results.Ok(receipt);
        }
        catch (NpgsqlException)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);
        }
        catch (ArgumentException)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_HISTORY_BATCH", "The history batch does not satisfy the required contract.", retryable: false);
        }
    }

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);
}
