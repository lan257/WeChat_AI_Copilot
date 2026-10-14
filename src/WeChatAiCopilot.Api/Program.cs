using Npgsql;
using Microsoft.AspNetCore.StaticFiles;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Modules.Memory.Api;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using WeChatAiCopilot.Api.Platform.Migrations;
using WeChatAiCopilot.Api.Platform.Outbox;
using WeChatAiCopilot.Api.Modules.Skills;
using WeChatAiCopilot.Api.Modules.Operations;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false));
});

NpgsqlDataSource? formalDataSource = null;
var formalConnectionString = builder.Configuration.GetConnectionString("FormalDatabase");
if (!string.IsNullOrWhiteSpace(formalConnectionString))
{
    formalDataSource = new NpgsqlDataSourceBuilder(formalConnectionString).Build();
    builder.Services.AddSingleton(formalDataSource);
    builder.Services.AddScoped<IMessageQueryV1, PostgresMessageQueryV1>();
    builder.Services.AddPostgresMemoryServices();
    builder.Services.AddConversationChangeOutboxDispatcher();
    builder.Services.AddAgentModule(builder.Configuration);
    builder.Services.AddV6SkillServices();
    builder.Services.AddOperationsModule();
    builder.Services.AddAsyncAgentTasksV6();
}

var app = builder.Build();

app.UseDefaultFiles();
// Entry pages must never come from cache: a browser that cached the previous root page would keep
// loading the old UI after an upgrade even though the server already serves the new one. The assets
// are served without fingerprints, so a cached ES module would pin the whole UI to the old version.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var name = context.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-store";
        }
    }
});

if (formalDataSource is not null)
{
    await MigrationRunner.ApplyPendingAsync(formalDataSource, app.Lifetime.ApplicationStopping);
}

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken cancellationToken) =>
{
    var dataSource = services.GetService<NpgsqlDataSource>();
    if (dataSource is null)
    {
        return Results.Json(
            new { status = "not-ready", database = "not-configured" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok(new { status = "ready", database = "ready" });
    }
    catch (Exception)
    {
        return Results.Json(
            new { status = "not-ready", database = "unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapConversationEndpoints(builder.Configuration);
app.MapConversationDirectoryEndpointsV1(builder.Configuration);
app.MapReaderManagementEndpointsV1(builder.Configuration);
app.MapSystemStatusEndpointsV1(builder.Configuration);
app.MapReaderDiscoveryEndpointsV1(builder.Configuration);
app.MapWebConversationQueryEndpoints(builder.Configuration);
app.MapSenderCalibrationCommandEndpoint(builder.Configuration);
app.MapMemoryEndpoints(builder.Configuration);
app.MapAgentEndpoints(builder.Configuration);
if(formalDataSource is not null)
{
    app.MapV6SkillEndpoints(builder.Configuration);
    app.MapSessionEndpoints(builder.Configuration);
    app.MapOperationsEndpoints(builder.Configuration);
    app.MapHistoryJobEndpointsV1(builder.Configuration);
    app.MapAsyncAgentTasksV6(builder.Configuration);
    app.MapPortraitEndpointsV6(builder.Configuration);
    app.MapConversationActivityV6(builder.Configuration);
}
else
{
    app.MapGet("/api/session",()=>Results.Json(new{authenticated=false,error=new{code="DATABASE_NOT_CONFIGURED"}},statusCode:503));
}

app.Run();
