using System.Globalization;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

public static class WebConversationQueryEndpoints
{
    public static IEndpointRouteBuilder MapWebConversationQueryEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var currentConversation = endpoints.MapGroup("/api/conversations/current")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));

        currentConversation.MapGet("", GetCurrentConversationAsync);
        currentConversation.MapGet("/messages", GetCurrentMessagesAsync);
        currentConversation.MapGet("/sender-perspective", GetCurrentSenderPerspectiveAsync);
        return endpoints;
    }

    private static async Task<IResult> GetCurrentConversationAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(configuration, out var target))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "WEB_SCOPE_NOT_CONFIGURED", "The authorized Web account and conversation are not configured.", retryable: false);
        }

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return DatabaseNotConfigured(context);
        }

        try
        {
            var snapshot = await new PostgresWebConversationReadServiceV1(dataSource)
                .GetCurrentConversationAsync(target, cancellationToken);
            return snapshot is null
                ? CurrentConversationNotFound(context)
                : Results.Ok(snapshot);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetCurrentMessagesAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(configuration, out var target))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "WEB_SCOPE_NOT_CONFIGURED", "The authorized Web account and conversation are not configured.", retryable: false);
        }

        if (!TryReadLimit(context, out var limit))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_LIMIT", "The limit query parameter must be a positive integer.", retryable: false);
        }

        if (!TryReadAnchorMessageId(context, out var anchorMessageId))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_ANCHOR", "The anchorMessageId query parameter must be a message UUID.", retryable: false);
        }

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return DatabaseNotConfigured(context);
        }

        var messageQuery = services.GetService<IMessageQueryV1>();
        if (messageQuery is null)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "MESSAGE_QUERY_NOT_CONFIGURED", "The formal message query is not configured.", retryable: true);
        }

        try
        {
            var snapshot = await new PostgresWebConversationReadServiceV1(dataSource)
                .GetCurrentMessagesAsync(messageQuery, target, limit, anchorMessageId, cancellationToken);
            return snapshot is null
                ? CurrentConversationNotFound(context)
                : Results.Ok(snapshot);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetCurrentSenderPerspectiveAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(configuration, out var target))
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable,
                "WEB_SCOPE_NOT_CONFIGURED", "The authorized Web account and conversation are not configured.", retryable: false);
        }

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return DatabaseNotConfigured(context);
        }

        try
        {
            var view = await new PostgresWebConversationReadServiceV1(dataSource)
                .GetCurrentSenderPerspectiveAsync(target, cancellationToken);
            return view is null
                ? CurrentConversationNotFound(context)
                : Results.Ok(view);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static bool TryGetTarget(IConfiguration configuration, out WebConversationTarget target)
    {
        var accountExternalId = configuration["Web:AllowedAccountExternalId"];
        var conversationExternalId = configuration["Web:AllowedConversationExternalId"];
        if (string.IsNullOrWhiteSpace(accountExternalId)
            || string.IsNullOrWhiteSpace(conversationExternalId))
        {
            target = default!;
            return false;
        }

        target = new WebConversationTarget(
            configuration["Web:Provider"] ?? "local-wechat",
            accountExternalId,
            conversationExternalId);
        return true;
    }

    private static bool TryReadLimit(HttpContext context, out int limit)
    {
        limit = 30;
        if (!context.Request.Query.TryGetValue("limit", out var values))
        {
            return true;
        }

        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit > 0;
    }

    private static bool TryReadAnchorMessageId(HttpContext context, out Guid? anchorMessageId)
    {
        anchorMessageId = null;
        if (!context.Request.Query.TryGetValue("anchorMessageId", out var values))
        {
            return true;
        }

        if (values.Count != 1 || !Guid.TryParse(values[0], out var parsed))
        {
            return false;
        }

        anchorMessageId = parsed;
        return true;
    }

    private static IResult CurrentConversationNotFound(HttpContext context) => Error(
        context,
        StatusCodes.Status404NotFound,
        "CURRENT_CONVERSATION_NOT_FOUND",
        "The authorized conversation is not available.",
        retryable: false);

    private static IResult DatabaseNotConfigured(HttpContext context) => Error(
        context,
        StatusCodes.Status503ServiceUnavailable,
        "DATABASE_NOT_CONFIGURED",
        "The formal message store is not configured.",
        retryable: true);

    private static IResult DatabaseUnavailable(HttpContext context) => Error(
        context,
        StatusCodes.Status503ServiceUnavailable,
        "DATABASE_UNAVAILABLE",
        "The formal message store is unavailable.",
        retryable: true);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);
}
