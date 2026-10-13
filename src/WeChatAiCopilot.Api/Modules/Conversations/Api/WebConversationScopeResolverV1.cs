using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Operations;

namespace WeChatAiCopilot.Api.Modules.Conversations.Api;

internal sealed record WebConversationTargetResolutionV1(WebConversationTarget? Target, IResult? Error);

/// <summary>
/// Resolves a browser-selected internal scope through the local database. During
/// migration, requests without IDs continue to use the configured /current target.
/// The Web bearer token remains the boundary for all locally imported accounts.
/// </summary>
internal static class WebConversationScopeResolverV1
{
    public static async Task<WebConversationTargetResolutionV1> ResolveAsync(
        HttpContext context,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var hasAccountId = context.Request.Query.ContainsKey("accountId");
        var hasConversationId = context.Request.Query.ContainsKey("conversationId");
        if (!hasAccountId && !hasConversationId)
        {
            if(context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 {AllImported:false})
                return Failed(context,400,"INVALID_CONVERSATION_SCOPE","An explicit authorized conversation is required.");
            var accountExternalId = configuration["Web:AllowedAccountExternalId"];
            var conversationExternalId = configuration["Web:AllowedConversationExternalId"];
            if (string.IsNullOrWhiteSpace(accountExternalId) || string.IsNullOrWhiteSpace(conversationExternalId))
            {
                return Failed(context, StatusCodes.Status503ServiceUnavailable,
                    "WEB_SCOPE_NOT_CONFIGURED", "No default Web conversation is configured.");
            }

            return new WebConversationTargetResolutionV1(
                new WebConversationTarget(
                    configuration["Web:Provider"] ?? "local-wechat",
                    accountExternalId,
                    conversationExternalId),
                null);
        }

        if (!TryReadSingleGuid(context, "accountId", hasAccountId, out var accountId)
            || !TryReadSingleGuid(context, "conversationId", hasConversationId, out var conversationId))
        {
            return Failed(context, StatusCodes.Status400BadRequest,
                "INVALID_CONVERSATION_SCOPE", "Both accountId and conversationId must be valid UUIDs.");
        }

        if(context.Items[WebSessionServiceV1.IdentityItemKey] is WebSessionIdentityV1 identity&&!identity.Allows(accountId,conversationId))
            return Failed(context,404,"CONVERSATION_NOT_FOUND","The selected conversation is not available.");

        const string sql = """
            SELECT account.provider, account.external_account_id, conversation.external_conversation_id
            FROM copilot.chat_accounts AS account
            JOIN copilot.conversations AS conversation
              ON conversation.account_id = account.id
            WHERE account.id = @account_id
              AND conversation.id = @conversation_id
              AND conversation.conversation_type = 'Direct';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Failed(context, StatusCodes.Status404NotFound,
                "CONVERSATION_NOT_FOUND", "The selected conversation is not available.");
        }

        return new WebConversationTargetResolutionV1(
            new WebConversationTarget(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            null);
    }

    private static bool TryReadSingleGuid(HttpContext context, string key, bool required, out Guid value)
    {
        value = Guid.Empty;
        if (!required
            || context.Request.Query[key].Count != 1
            || !Guid.TryParse(context.Request.Query[key][0], out value))
        {
            return false;
        }

        return true;
    }

    private static WebConversationTargetResolutionV1 Failed(
        HttpContext context,
        int statusCode,
        string code,
        string message) => new(null, Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, false, context.TraceIdentifier)),
            statusCode: statusCode));
}
