using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WeChatAiCopilot.Api.Platform.CodexCli;

public static class CodexCliServiceCollectionExtensions
{
    /// <summary>
    /// Registers the fixed-policy local executor. The old Agent setting remains
    /// a fallback for deployments that have not moved to the Platform key yet.
    /// </summary>
    public static IServiceCollection AddStructuredCodexCliExecutorV1(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var executablePath = configuration["Platform:CodexCli:ExecutablePath"]
            ?? configuration["Agent:CodexCli:ExecutablePath"]
            ?? "codex";
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = "codex";
        }

        services.TryAddSingleton<ICodexCliProcessRunnerV1, SystemCodexCliProcessRunnerV1>();
        services.TryAddSingleton<IStructuredCodexCliExecutorV1>(provider =>
            new StructuredCodexCliExecutorV1(
                provider.GetRequiredService<ICodexCliProcessRunnerV1>(),
                executablePath));
        return services;
    }
}
