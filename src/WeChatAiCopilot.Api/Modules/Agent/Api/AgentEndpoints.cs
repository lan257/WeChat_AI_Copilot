using System.Text.Json;
using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Platform.Authentication;

namespace WeChatAiCopilot.Api.Modules.Agent.Api;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        var agent = endpoints.MapGroup("/api/ai")
            .AddEndpointFilter(new BearerTokenEndpointFilter(configuration, "Authentication:WebBearerToken"));
        agent.MapGet("/personas", GetPersonas);
        agent.MapPost("/suggestions", CreateSuggestionsAsync);
        agent.MapGet("/suggestions/{batchId:guid}", GetSuggestionBatchAsync);
        agent.MapGet("/runs/{runId:guid}", GetAgentRunAsync);
        agent.MapPost("/consultations", CreateConsultationThreadAsync);
        agent.MapGet("/consultations/{threadId:guid}", GetConsultationHistoryAsync);
        agent.MapPost("/consultations/{threadId:guid}/turns", CreateConsultationTurnAsync);
        return endpoints;
    }

    private static IResult GetPersonas(IServiceProvider services)
    {
        var catalog = services.GetService<IAgentPresetCatalogV1>();
        return Results.Ok(new
        {
            personas = catalog?.GetAvailablePersonas() ?? Array.Empty<AgentPersonaPresetSummaryV1>()
        });
    }

    private static async Task<IResult> CreateSuggestionsAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var (body, bodyError) = await ReadJsonBodyAsync<CreateAgentSuggestionsRequestV1>(context, cancellationToken)
            .ConfigureAwait(false);
        if (bodyError is not null)
        {
            return bodyError;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.PersonaPresetId))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_SUGGESTION_REQUEST", "A persona preset ID is required.", retryable: false);
        }

        try
        {
            var batch = await access.Service!.CreateSuggestionsAsync(access.Scope!, body, cancellationToken)
                .ConfigureAwait(false);
            return Results.Created($"/api/ai/suggestions/{batch.BatchId}", batch);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<IResult> GetSuggestionBatchAsync(
        Guid batchId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        try
        {
            var batch = await access.Service!.GetSuggestionBatchAsync(access.Scope!, batchId, cancellationToken)
                .ConfigureAwait(false);
            return batch is null
                ? NotFound(context, "SUGGESTION_BATCH_NOT_FOUND", "The suggestion batch is not available.")
                : Results.Ok(batch);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<IResult> GetAgentRunAsync(
        Guid runId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        try
        {
            var run = await access.Service!.GetAgentRunAsync(access.Scope!, runId, cancellationToken)
                .ConfigureAwait(false);
            return run is null
                ? NotFound(context, "AGENT_RUN_NOT_FOUND", "The Agent run is not available.")
                : Results.Ok(run);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<IResult> CreateConsultationThreadAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        try
        {
            var thread = await access.Service!.CreateConsultationThreadAsync(access.Scope!, cancellationToken)
                .ConfigureAwait(false);
            return Results.Created($"/api/ai/consultations/{thread.ThreadId}", thread);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<IResult> GetConsultationHistoryAsync(
        Guid threadId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        try
        {
            var history = await access.Service!.GetConsultationHistoryAsync(access.Scope!, threadId, cancellationToken)
                .ConfigureAwait(false);
            return history is null
                ? NotFound(context, "CONSULTATION_THREAD_NOT_FOUND", "The consultation thread is not available.")
                : Results.Ok(history);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<IResult> CreateConsultationTurnAsync(
        Guid threadId,
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var access = await ResolveScopeAsync(context, configuration, services, cancellationToken).ConfigureAwait(false);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var (body, bodyError) = await ReadJsonBodyAsync<CreateAgentConsultationTurnRequestV1>(context, cancellationToken)
            .ConfigureAwait(false);
        if (bodyError is not null)
        {
            return bodyError;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.UserInput))
        {
            return Error(context, StatusCodes.Status400BadRequest,
                "INVALID_CONSULTATION_REQUEST", "A consultation question is required.", retryable: false);
        }

        try
        {
            var turn = await access.Service!.CreateConsultationTurnAsync(
                access.Scope!,
                threadId,
                body.UserInput,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(turn);
        }
        catch (Exception exception) when (IsAgentOperationException(exception))
        {
            return MapAgentException(context, exception);
        }
    }

    private static async Task<ScopeResolution> ResolveScopeAsync(
        HttpContext context,
        IConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!TryGetWebTarget(configuration, out var target))
        {
            return ScopeResolution.Failed(Error(context, StatusCodes.Status503ServiceUnavailable,
                "WEB_SCOPE_NOT_CONFIGURED", "The authorized Web account and conversation are not configured.", retryable: false));
        }

        if (services.GetService<NpgsqlDataSource>() is null)
        {
            return ScopeResolution.Failed(Error(context, StatusCodes.Status503ServiceUnavailable,
                "DATABASE_NOT_CONFIGURED", "The formal message store is not configured.", retryable: true));
        }

        IAgentTaskApplicationServiceV1? service;
        try
        {
            service = services.GetService<IAgentTaskApplicationServiceV1>();
        }
        catch (AgentModuleNotReadyException exception)
        {
            return ScopeResolution.Failed(Error(context, StatusCodes.Status503ServiceUnavailable,
                exception.Code, "The Agent module is not configured for execution.", retryable: true));
        }
        catch (InvalidOperationException)
        {
            return ScopeResolution.Failed(Error(context, StatusCodes.Status503ServiceUnavailable,
                "AGENT_MODULE_NOT_CONFIGURED", "The Agent application service is not configured.", retryable: true));
        }

        if (service is null)
        {
            return ScopeResolution.Failed(Error(context, StatusCodes.Status503ServiceUnavailable,
                "AGENT_MODULE_NOT_CONFIGURED", "The Agent application service is not configured.", retryable: true));
        }

        try
        {
            var scope = await service.ResolveAuthorizedScopeAsync(target, cancellationToken).ConfigureAwait(false);
            return scope is null
                ? ScopeResolution.Failed(NotFound(context, "CONVERSATION_NOT_FOUND", "The authorized conversation is not available."))
                : ScopeResolution.Succeeded(service, scope);
        }
        catch (NpgsqlException)
        {
            return ScopeResolution.Failed(DatabaseUnavailable(context));
        }
    }

    private static bool TryGetWebTarget(
        IConfiguration configuration,
        out AgentWebConversationTargetV1 target)
    {
        var accountExternalId = configuration["Web:AllowedAccountExternalId"];
        var conversationExternalId = configuration["Web:AllowedConversationExternalId"];
        if (string.IsNullOrWhiteSpace(accountExternalId) || string.IsNullOrWhiteSpace(conversationExternalId))
        {
            target = default!;
            return false;
        }

        target = new AgentWebConversationTargetV1(
            configuration["Web:Provider"] ?? "local-wechat",
            accountExternalId,
            conversationExternalId);
        return true;
    }

    private static async Task<(T? Body, IResult? Error)> ReadJsonBodyAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return (await context.Request.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false), null);
        }
        catch (JsonException)
        {
            return (null, Error(context, StatusCodes.Status400BadRequest,
                "INVALID_JSON", "The request body is not valid JSON.", retryable: false));
        }
        catch (InvalidOperationException)
        {
            return (null, Error(context, StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE", "The request body must use application/json.", retryable: false));
        }
    }

    private static bool IsAgentOperationException(Exception exception) =>
        exception is AgentResourceNotFoundException
            or AgentModuleNotReadyException
            or AgentTaskExecutionException
            or AgentTaskContractException
            or UnauthorizedAccessException
            or ArgumentException
            or NpgsqlException
            or InvalidOperationException;

    private static IResult MapAgentException(HttpContext context, Exception exception) => exception switch
    {
        AgentResourceNotFoundException => NotFound(context, "AGENT_RESOURCE_NOT_FOUND", "The requested Agent resource is not available."),
        AgentModuleNotReadyException module => Error(context, StatusCodes.Status503ServiceUnavailable,
            module.Code, "The Agent module is not configured for execution.", retryable: true),
        AgentTaskExecutionException => Error(context, StatusCodes.Status503ServiceUnavailable,
            "AGENT_EXECUTION_FAILED", "The Agent could not complete this request.", retryable: true),
        AgentTaskContractException => Error(context, StatusCodes.Status503ServiceUnavailable,
            "AGENT_RESULT_INVALID", "The Agent returned a result that failed contract validation.", retryable: true),
        UnauthorizedAccessException => NotFound(context, "AGENT_RESOURCE_NOT_FOUND", "The requested Agent resource is not available."),
        ArgumentException => Error(context, StatusCodes.Status400BadRequest,
            "INVALID_AGENT_REQUEST", "The Agent request is invalid.", retryable: false),
        NpgsqlException => DatabaseUnavailable(context),
        InvalidOperationException => Error(context, StatusCodes.Status503ServiceUnavailable,
            "AGENT_STORAGE_UNAVAILABLE", "The Agent result could not be read or stored.", retryable: true),
        _ => Error(context, StatusCodes.Status503ServiceUnavailable,
            "AGENT_UNAVAILABLE", "The Agent module is temporarily unavailable.", retryable: true)
    };

    private static IResult DatabaseUnavailable(HttpContext context) => Error(context,
        StatusCodes.Status503ServiceUnavailable,
        "DATABASE_UNAVAILABLE", "The formal message store is unavailable.", retryable: true);

    private static IResult NotFound(HttpContext context, string code, string message) => Error(
        context, StatusCodes.Status404NotFound, code, message, retryable: false);

    private static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable) => Results.Json(
            new ApiErrorV1(new ApiErrorDetailV1(code, message, retryable, context.TraceIdentifier)),
            statusCode: statusCode);

    private sealed record ScopeResolution(
        IAgentTaskApplicationServiceV1? Service,
        AuthorizedContextScopeV1? Scope,
        IResult? Error)
    {
        public static ScopeResolution Failed(IResult error) => new(null, null, error);
        public static ScopeResolution Succeeded(IAgentTaskApplicationServiceV1 service, AuthorizedContextScopeV1 scope) =>
            new(service, scope, null);
    }
}

public sealed record CreateAgentConsultationTurnRequestV1
{
    public string? UserInput { get; init; }
}
