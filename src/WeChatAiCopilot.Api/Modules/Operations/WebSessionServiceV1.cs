using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace WeChatAiCopilot.Api.Modules.Operations;

public sealed record WebSessionIdentityV1(Guid SessionId, bool AllImported,
    IReadOnlyList<Guid> AuthorizedAccountIds, IReadOnlyList<Guid> AuthorizedConversationIds,
    bool IsLocal, DateTimeOffset ExpiresAtUtc)
{
    public bool Allows(Guid accountId, Guid conversationId) => AllImported ||
        (AuthorizedAccountIds.Contains(accountId) && AuthorizedConversationIds.Contains(conversationId));
    public bool AllowsAccount(Guid accountId) => AllImported || AuthorizedAccountIds.Contains(accountId);
}

public interface IWebSessionValidatorV1
{
    Task<WebSessionIdentityV1?> ValidateAsync(HttpContext context, bool requireCsrf, CancellationToken cancellationToken = default);
}

public sealed class WebSessionServiceV1(NpgsqlDataSource dataSource) : IWebSessionValidatorV1
{
    public const string CookieName = "copilot_session";
    public const string IdentityItemKey = "copilot.web-session";
    public const string CsrfHeader = "X-Copilot-CSRF";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Csrf(string token) => Hash("copilot.csrf.v1:" + token);
    public static bool IsLoopback(HttpContext context) => context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
    public static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || origin == "null") return false;
        return string.Equals(uri.GetLeftPart(UriPartial.Authority),
            context.Request.Scheme + "://" + context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<WebSessionIdentityV1?> ValidateAsync(HttpContext context, bool requireCsrf, CancellationToken cancellationToken = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || token.Length != 64) return null;
        if (requireCsrf && (!SameOrigin(context) || !string.Equals(context.Request.Headers[CsrfHeader], Csrf(token), StringComparison.Ordinal))) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT session_id,all_imported,authorized_account_ids,authorized_conversation_ids,is_local,expires_at
            FROM copilot.web_sessions WHERE token_hash=@hash AND revoked_at IS NULL AND expires_at>now();
            """);
        command.Parameters.AddWithValue("hash", Hash(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var identity = new WebSessionIdentityV1(reader.GetGuid(0),reader.GetBoolean(1),reader.GetFieldValue<Guid[]>(2),
            reader.GetFieldValue<Guid[]>(3),reader.GetBoolean(4),reader.GetFieldValue<DateTimeOffset>(5));
        if (identity.IsLocal ? !IsLoopback(context) : !context.Request.IsHttps) return null;
        context.Items[IdentityItemKey] = identity;
        return identity;
    }

    public async Task<object> CreateAsync(HttpContext context, string deviceName, bool local, Guid[] accounts, Guid[] conversations, CancellationToken ct)
    {
        var token = NewToken();
        var expires = DateTimeOffset.UtcNow.AddHours(local ? 12 : 1);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO copilot.web_sessions(token_hash,device_name,all_imported,authorized_account_ids,authorized_conversation_ids,is_local,expires_at)
            VALUES(@hash,@name,@all,@accounts,@conversations,@local,@expires) RETURNING session_id;
            """);
        command.Parameters.AddWithValue("hash",Hash(token)); command.Parameters.AddWithValue("name",deviceName);
        command.Parameters.AddWithValue("all",local); command.Parameters.AddWithValue("accounts",accounts);
        command.Parameters.AddWithValue("conversations",conversations); command.Parameters.AddWithValue("local",local);
        command.Parameters.AddWithValue("expires",expires);
        var sessionId = (Guid)(await command.ExecuteScalarAsync(ct))!;
        await using var purge = dataSource.CreateCommand("""
            DELETE FROM copilot.web_sessions
            WHERE (expires_at < now() - interval '7 days')
               OR (revoked_at IS NOT NULL AND revoked_at < now() - interval '7 days');
            DELETE FROM copilot.device_pairing_grants
            WHERE (expires_at < now() - interval '1 day')
               OR (consumed_at IS NOT NULL AND consumed_at < now() - interval '1 day');
            """);
        await purge.ExecuteNonQueryAsync(ct);
        context.Response.Cookies.Append(CookieName,token,new CookieOptions {
            HttpOnly=true, SameSite=SameSiteMode.Strict, Secure=context.Request.IsHttps, Path="/", Expires=expires, IsEssential=true });
        context.Response.Headers.CacheControl="no-store";
        return new { authenticated=true,csrfToken=Csrf(token),expiresAtUtc=expires,allImported=local,sessionId,
            authorizedAccountIds=accounts,authorizedConversationIds=conversations };
    }
}
