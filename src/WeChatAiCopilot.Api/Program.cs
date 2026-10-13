using Npgsql;
using WeChatAiCopilot.Api.Modules.Agent.Api;
using WeChatAiCopilot.Api.Modules.Conversations.Api;
using WeChatAiCopilot.Api.Modules.Conversations.Application;
using WeChatAiCopilot.Api.Modules.Memory.Api;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;
using WeChatAiCopilot.Api.Platform.Migrations;
using WeChatAiCopilot.Api.Platform.Outbox;

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
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

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

app.Run();
