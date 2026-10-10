using System.Globalization;
using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Memory.Api;

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var currentMemory = endpoints.MapGroup("/api/conversations/current/memory")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));

        currentMemory.MapGet("", ListAsync);
        currentMemory.MapGet("/{memoryId:guid}", GetAsync);
        currentMemory.MapPost("", CreateAsync);
        currentMemory.MapPut("/{memoryId:guid}", UpdateAsync);
        currentMemory.MapDelete("/{memoryId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryReadLimit(context, out var limit))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_LIMIT", "The limit query parameter must be a positive integer.", retryable: false);
        }

        var resolved = await TryResolveScopeAsync(context, configuration, services, cancellationToken);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        try
        {
            var memories = await resolved.Service!.ListAsync(resolved.Scope!, limit, cancellationToken);
            return Results.Ok(memories);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> GetAsync(
        Guid memoryId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var resolved = await TryResolveScopeAsync(context, configuration, services, cancellationToken);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        try
        {
            var memory = await resolved.Service!.GetAsync(resolved.Scope!, memoryId, cancellationToken);
            return memory is null
                ? MemoryNotFound(context)
                : Results.Ok(memory);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var bodyResult = await ReadBodyAsync<UserEnteredMemoryCreateRequestV1>(context, cancellationToken);
        if (bodyResult.Error is not null)
        {
            return bodyResult.Error;
        }

        var violations = MemoryItemRequestValidatorV1.ValidateCreate(bodyResult.Value);
        if (violations.Count > 0)
        {
            return InvalidRequest(context, violations);
        }

        var resolved = await TryResolveScopeAsync(context, configuration, services, cancellationToken);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        try
        {
            var memory = await resolved.Service!.CreateUserEnteredAsync(
                resolved.Scope!,
                bodyResult.Value!,
                cancellationToken);
            var location = $"/api/conversations/current/memory/{memory.MemoryId:D}";
            return Results.Created(location, memory);
        }
        catch (MemoryServiceException exception)
        {
            return MapMemoryError(context, exception);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid memoryId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var bodyResult = await ReadBodyAsync<UserEnteredMemoryUpdateRequestV1>(context, cancellationToken);
        if (bodyResult.Error is not null)
        {
            return bodyResult.Error;
        }

        var violations = MemoryItemRequestValidatorV1.ValidateUpdate(bodyResult.Value);
        if (violations.Count > 0)
        {
            return InvalidRequest(context, violations);
        }

        var resolved = await TryResolveScopeAsync(context, configuration, services, cancellationToken);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        try
        {
            var memory = await resolved.Service!.UpdateUserEnteredAsync(
                resolved.Scope!,
                memoryId,
                bodyResult.Value!,
                cancellationToken);
            return Results.Ok(memory);
        }
        catch (MemoryServiceException exception)
        {
            return MapMemoryError(context, exception);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid memoryId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryReadExpectedVersion(context, out var expectedVersion))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_EXPECTED_VERSION", "A positive expectedVersion query parameter is required.", retryable: false);
        }

        var resolved = await TryResolveScopeAsync(context, configuration, services, cancellationToken);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        try
        {
            await resolved.Service!.DeleteUserEnteredAsync(
                resolved.Scope!,
                memoryId,
                expectedVersion,
                cancellationToken);
            return Results.NoContent();
        }
        catch (MemoryServiceException exception)
        {
            return MapMemoryError(context, exception);
        }
        catch (NpgsqlException)
        {
            return DatabaseUnavailable(context);
        }
    }

    private static async Task<ScopeResolution> TryResolveScopeAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return new ScopeResolution(null, null, DatabaseNotConfigured(context));
        }

        var accountExternalId = configuration["Web:AllowedAccountExternalId"];
        var conversationExternalId = configuration["Web:AllowedConversationExternalId"];
        if (string.IsNullOrWhiteSpace(accountExternalId)
            || string.IsNullOrWhiteSpace(conversationExternalId))
        {
            return new ScopeResolution(null, null, Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "WEB_SCOPE_NOT_CONFIGURED",
                "The authorized Web account and conversation are not configured.",
                retryable: false));
        }

        var service = new PostgresMemoryServiceV1(dataSource);
        try
        {
            var scope = await service.ResolveConfiguredConversationAsync(
                configuration["Web:Provider"] ?? "local-wechat",
                accountExternalId,
                conversationExternalId,
                cancellationToken);
            return scope is null
                ? new ScopeResolution(null, null, MemoryNotFound(context))
                : new ScopeResolution(service, scope, null);
        }
        catch (NpgsqlException)
        {
            return new ScopeResolution(null, null, DatabaseUnavailable(context));
        }
    }

    private static async Task<BodyReadResult<T>> ReadBodyAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var value = await context.Request.ReadFromJsonAsync<T>(cancellationToken);
            return new BodyReadResult<T>(value, null);
        }
        catch (JsonException)
        {
            return new BodyReadResult<T>(null, Error(
                context,
                StatusCodes.Status400BadRequest,
                "INVALID_JSON",
                "The request body is not valid JSON.",
                retryable: false));
        }
        catch (InvalidOperationException)
        {
            return new BodyReadResult<T>(null, Error(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "The request body must use application/json.",
                retryable: false));
        }
    }

    private static bool TryReadLimit(HttpContext context, out int limit)
    {
        limit = 100;
        if (!context.Request.Query.TryGetValue("limit", out var values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit <= 0)
        {
            return false;
        }

        limit = Math.Min(limit, PostgresMemoryServiceV1.MaximumListLimit);
        return true;
    }

    private static bool TryReadExpectedVersion(HttpContext context, out long version)
    {
        version = 0;
        return context.Request.Query.TryGetValue("expectedVersion", out var values)
            && values.Count == 1
            && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }

    private static IResult InvalidRequest(
        HttpContext context,
        IReadOnlyList<MemoryRequestViolationV1> violations) => Error(
        context,
        StatusCodes.Status400BadRequest,
        "INVALID_MEMORY_REQUEST",
        string.Join("; ", violations.Select(violation => $"{violation.Field}: {violation.Code}")),
        retryable: false);

    private static IResult MapMemoryError(HttpContext context, MemoryServiceException exception) =>
        exception.FailureCode switch
        {
            MemoryServiceFailureCodeV1.InvalidRequest => Error(
                context, StatusCodes.Status400BadRequest, "INVALID_MEMORY_REQUEST",
                "The memory request does not satisfy the required contract.", retryable: false),
            MemoryServiceFailureCodeV1.EvidenceUnavailable => Error(
                context, StatusCodes.Status422UnprocessableEntity, "MEMORY_EVIDENCE_UNAVAILABLE",
                "One or more evidence messages are not available in the authorized conversation.", retryable: false),
            MemoryServiceFailureCodeV1.MemoryNotFound => MemoryNotFound(context),
            MemoryServiceFailureCodeV1.VersionConflict => Error(
                context, StatusCodes.Status409Conflict, "MEMORY_VERSION_CONFLICT",
                "The memory changed since it was read. Reload it before editing.", retryable: false),
            _ => throw new ArgumentOutOfRangeException(nameof(exception))
        };

    private static IResult DatabaseNotConfigured(HttpContext context) => Error(
        context,
        StatusCodes.Status503ServiceUnavailable,
        "DATABASE_NOT_CONFIGURED",
        "The formal memory store is not configured.",
        retryable: true);

    private static IResult DatabaseUnavailable(HttpContext context) => Error(
        context,
        StatusCodes.Status503ServiceUnavailable,
        "DATABASE_UNAVAILABLE",
        "The formal memory store is unavailable.",
        retryable: true);

    private static IResult MemoryNotFound(HttpContext context) => Error(
        context,
        StatusCodes.Status404NotFound,
        "MEMORY_NOT_FOUND",
        "The memory is not available in the authorized conversation.",
        retryable: false);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
        new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
        statusCode: statusCode);

    private sealed record BodyReadResult<T>(T? Value, IResult? Error) where T : class;

    private sealed record ScopeResolution(
        PostgresMemoryServiceV1? Service,
        MemoryConversationScopeV1? Scope,
        IResult? Error);
}
