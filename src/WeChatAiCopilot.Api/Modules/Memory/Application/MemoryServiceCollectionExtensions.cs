using Microsoft.Extensions.DependencyInjection.Extensions;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Memory.Integration.M2;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresMemoryServices(this IServiceCollection services)
    {
        services.TryAddScoped<IMemoryContextReaderV1, PostgresMemoryContextReaderV1>();
        services.TryAddScoped<IMemoryServiceV1, PostgresMemoryServiceV1>();
        services.TryAddScoped<IConsolidationStateRepositoryV1, PostgresConsolidationStateRepositoryV1>();
        services.TryAddScoped<IContextServiceV1, ContextBuilderV1>();
        services.TryAddScoped<IContextToolSessionFactoryV1, ContextToolSessionFactoryV1>();
        services.TryAddScoped<IConsolidationMessageSourceV1, M2ConsolidationMessageSourceAdapterV1>();
        services.TryAddScoped<ConsolidationMessageCollectorV1>();
        services.TryAddScoped<MemoryConsolidationEventHandlerV1>(serviceProvider =>
        {
            // Custom module registrations take precedence. The real CLI adapter is
            // explicitly opt-in and unavailable when Platform has no executor.
            var generator = serviceProvider.GetService<IMemoryConsolidationGeneratorV1>()
                ?? CodexCliMemoryConsolidationGeneratorV1.TryCreateConfigured(serviceProvider);
            return new MemoryConsolidationEventHandlerV1(
                serviceProvider.GetRequiredService<ConsolidationMessageCollectorV1>(),
                serviceProvider.GetRequiredService<IConsolidationStateRepositoryV1>(),
                generator);
        });
        services.TryAddScoped<IMemoryConsolidationEventHandlerV1>(serviceProvider =>
            serviceProvider.GetRequiredService<MemoryConsolidationEventHandlerV1>());
        return services;
    }
}
