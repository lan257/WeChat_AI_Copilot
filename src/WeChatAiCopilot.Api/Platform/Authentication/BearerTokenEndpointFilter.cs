using System.Security.Cryptography;
using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;

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
        var expectedToken = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return Error(httpContext, StatusCodes.Status503ServiceUnavailable,
                "AUTH_NOT_CONFIGURED", "The required service identity is not configured.", retryable: false);
        }

        var authorization = httpContext.Request.Headers.Authorization.ToString();
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
