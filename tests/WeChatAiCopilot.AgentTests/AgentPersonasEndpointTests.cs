using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using Xunit;

namespace WeChatAiCopilot.AgentTests;

public sealed class AgentPersonasEndpointTests
{
    [Fact]
    public async Task GetPersonas_RequiresWebBearerAndReturnsOnlyConfiguredSafeLabels()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:WebBearerToken"] = "test-token",
                ["Agent:AllowedPersonaPresetIds:0"] = "natural",
                ["Agent:AllowedPersonaPresetIds:1"] = "warm",
                ["Agent:AllowedPersonaPresetIds:2"] = "custom-path-preset"
            })
            .Build();

        var unauthorized = await InvokeEndpointAsync(configuration, token: null);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);

        var authorized = await InvokeEndpointAsync(configuration, "test-token");
        Assert.Equal(StatusCodes.Status200OK, authorized.StatusCode);
        using var document = JsonDocument.Parse(authorized.Body);
        var personas = document.RootElement.GetProperty("personas");
        Assert.Equal(2, personas.GetArrayLength());
        Assert.Equal("natural", personas[0].GetProperty("personaPresetId").GetString());
        Assert.Equal("自然", personas[0].GetProperty("label").GetString());
        Assert.Equal("warm", personas[1].GetProperty("personaPresetId").GetString());
        Assert.Equal("温暖", personas[1].GetProperty("label").GetString());
        Assert.DoesNotContain("instruction", authorized.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", authorized.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPersonas_ReturnsEmptyListWhenNoSupportedPersonaIsConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:WebBearerToken"] = "test-token",
                ["Agent:AllowedPersonaPresetIds:0"] = "unknown-preset"
            })
            .Build();

        var response = await InvokeEndpointAsync(configuration, "test-token");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        Assert.Empty(document.RootElement.GetProperty("personas").EnumerateArray());
    }

    [Fact]
    public async Task GetExecutionProfiles_RequiresWebBearerAndReturnsServerOwnedProfiles()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:WebBearerToken"] = "test-token"
            })
            .Build();

        var unauthorized = await InvokeEndpointAsync(configuration, token: null, "/api/ai/execution-profiles");
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);

        var authorized = await InvokeEndpointAsync(configuration, "test-token", "/api/ai/execution-profiles");
        Assert.Equal(StatusCodes.Status200OK, authorized.StatusCode);
        using var document = JsonDocument.Parse(authorized.Body);
        var profiles = document.RootElement.GetProperty("profiles");
        Assert.Equal(4, profiles.GetArrayLength());
        Assert.Equal(new[] { "Quick", "Balanced", "Deep", "Max" }, profiles.EnumerateArray()
            .Select(profile => profile.GetProperty("executionProfileId").GetString()).ToArray());
        Assert.True(profiles[0].GetProperty("recentMessageLimit").GetInt32() < profiles[3].GetProperty("recentMessageLimit").GetInt32());
        Assert.True(profiles[3].GetProperty("reviewSuggestions").GetBoolean());
    }

    private static async Task<(int StatusCode, string Body)> InvokeEndpointAsync(
        IConfiguration configuration,
        string? token,
        string route = "/api/ai/personas")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(configuration.AsEnumerable());
        builder.Services.AddAgentModule(builder.Configuration);
        await using var app = builder.Build();
        app.MapAgentEndpoints(builder.Configuration);

        var routeBuilder = (IEndpointRouteBuilder)app;
        var endpoint = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = route;
        if (token is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {token}";
        }

        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var requestDelegate = endpoint.RequestDelegate ?? throw new InvalidOperationException("The endpoint has no request delegate.");
        await requestDelegate(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
