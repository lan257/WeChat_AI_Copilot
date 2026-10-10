using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WeChatAiCopilot.Api.Platform.Outbox;

public sealed class ConversationChangeOutboxHostedServiceV1(
    IServiceScopeFactory scopeFactory,
    OutboxDispatcherOptionsV1 options,
    ILogger<ConversationChangeOutboxHostedServiceV1> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetService<ConversationChangeOutboxDispatcherV1>();
                if (dispatcher is null)
                {
                    logger.LogWarning(
                        "Outbox dispatcher is unavailable; error code {ErrorCode}.",
                        "dispatcher_unavailable");
                    await Task.Delay(options.PollInterval, stoppingToken);
                    continue;
                }

                var dispatched = await dispatcher.DispatchNextAsync(stoppingToken);
                if (!dispatched)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Outbox dispatcher iteration failed; error code {ErrorCode}.",
                    "dispatch_cycle_failed");
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
