using System.Security.Cryptography;
using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Operations;

namespace WeChatAiCopilot.Api.Platform.Authentication;

public sealed class BearerTokenEndpointFilter(
    IConfiguration configuration,
    string configurationKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (configurationKey == "Authentication:WebBearerToken"
            && httpContext.Request.Cookies.ContainsKey(WebSessionServiceV1.CookieName))
        {
            var validator=httpContext.RequestServices.GetService<IWebSessionValidatorV1>();
            var mutation=httpContext.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
            var session=validator is null?null:await validator.ValidateAsync(httpContext,mutation,httpContext.RequestAborted);
            if(session is null) return Unauthorized(httpContext);
            var path=httpContext.Request.Path.Value??"";
            if(!session.AllImported&&(path=="/api/system/status"||path=="/api/reader/status"||path.Contains("/reader-targets",StringComparison.Ordinal)||path.Contains("/discovery-jobs",StringComparison.Ordinal)))
                return Error(httpContext,403,"DEVICE_SCOPE_FORBIDDEN","This management operation requires local authorization.",false);
            return await next(context);
        }
        var expectedToken = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return Error(httpContext, StatusCodes.Status503ServiceUnavailable,
                "AUTH_NOT_CONFIGURED", "The required service identity is not configured.", retryable: false);
        }

        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if(configurationKey=="Authentication:WebBearerToken"&&httpContext.Connection.RemoteIpAddress is {} remote&&!System.Net.IPAddress.IsLoopback(remote))return Unauthorized(httpContext);
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(httpContext);
        }

        var suppliedToken = authorization[prefix.Length..].Trim();
        if (!TokensMatch(expectedToken, suppliedToken))
        {
            return Unauthorized(httpContext);
        }

        return await next(context);
    }

    private static bool TokensMatch(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static IResult Unauthorized(HttpContext context) => Error(
        context,
        StatusCodes.Status401Unauthorized,
        "UNAUTHENTICATED",
        "Authentication is required.",
        retryable: false);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);
}
