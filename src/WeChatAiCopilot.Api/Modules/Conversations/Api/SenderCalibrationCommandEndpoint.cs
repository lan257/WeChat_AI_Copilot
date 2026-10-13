using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class SenderCalibrationCommandEndpoint
{
    public static IEndpointRouteBuilder MapSenderCalibrationCommandEndpoint(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        endpoints.MapPut("/api/conversations/current/sender-perspective", SetPerspectiveAsync)
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        return endpoints;
    }

    private static async Task<IResult> SetPerspectiveAsync(
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

        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_REQUIRED", "A valid Idempotency-Key header is required.", retryable: false);
        }

        SetWeChatAccountPerspectiveRequestV1? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<SetWeChatAccountPerspectiveRequestV1>(cancellationToken);
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

        var violations = ContractValidatorV1.Validate(request);
        if (violations.Count != 0)
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_PERSPECTIVE_REQUEST",
                string.Join("; ", violations.Select(violation => $"{violation.Path}: {violation.Code}")),
                retryable: false);
        }

        WebConversationTargetResolutionV1 targetResolution;
        try
        {
            targetResolution = await WebConversationScopeResolverV1.ResolveAsync(
                context, configuration, dataSource, cancellationToken);
        }
        catch (NpgsqlException)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);
        }
        if (targetResolution.Error is not null) return targetResolution.Error;
        var target = await FindAuthorizedTargetAsync(dataSource, targetResolution.Target!, cancellationToken);
        if (target is null)
        {
            return Error(context, StatusCodes.Status404NotFound,
                "CONVERSATION_NOT_FOUND", "The authorized conversation is not available.", retryable: false);
        }

        if (request!.AccountId != target.AccountId
            || request.ConversationId != target.ConversationId)
        {
            return Error(context, StatusCodes.Status404NotFound,
                "CONVERSATION_NOT_FOUND", "The authorized conversation is not available.", retryable: false);
        }

        try
        {
            var service = new PostgresSenderCalibrationCommandService(dataSource);
            var result = await service.SetPerspectiveAsync(request, idempotencyKey, cancellationToken);
            return Results.Ok(result);
        }
        catch (SenderCalibrationCommandException exception)
        {
            var status = exception.IsConflict
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity;
            return Error(context, status, exception.Code, exception.Message, retryable: false);
        }
        catch (NpgsqlException)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);
        }
    }

    private static async Task<AuthorizedTarget?> FindAuthorizedTargetAsync(
        NpgsqlDataSource dataSource,
        WebConversationTarget target,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT account.id, conversation.id
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
            WHERE account.provider = @provider
              AND account.external_account_id = @account_external_id
              AND conversation.external_conversation_id = @conversation_external_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider", target.Provider);
        command.Parameters.AddWithValue("account_external_id", target.AccountExternalId);
        command.Parameters.AddWithValue("conversation_external_id", target.ConversationExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuthorizedTarget(reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);

    private sealed record AuthorizedTarget(Guid AccountId, Guid ConversationId);
}
