using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Platform.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the M3 consolidation outbox dispatcher when the formal database provider
    /// and M3 event handler have both been explicitly registered by the host.
    /// </summary>
    public static IServiceCollection AddConversationChangeOutboxDispatcher(
        this IServiceCollection services,
        OutboxDispatcherOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource))
            || !services.Any(descriptor => descriptor.ServiceType == typeof(IMemoryConsolidationEventHandlerV1)))
        {
            return services;
        }

        services.TryAddSingleton(options ?? new OutboxDispatcherOptionsV1());
        services.TryAddScoped<IConversationChangeOutboxDeliveryStoreV1, PostgresConversationChangeOutboxDeliveryStoreV1>();
        services.TryAddScoped<ConversationChangeOutboxDispatcherV1>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ConversationChangeOutboxHostedServiceV1>());
        return services;
    }
}
