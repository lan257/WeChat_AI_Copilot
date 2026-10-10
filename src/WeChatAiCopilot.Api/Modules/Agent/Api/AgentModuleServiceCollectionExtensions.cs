using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeChatAiCopilot.Api.Modules.Agent.Application;
using WeChatAiCopilot.Api.Modules.Agent.Contracts;
using WeChatAiCopilot.Api.Modules.Agent.Persistence;
using WeChatAiCopilot.Api.Modules.Agent.Runtime;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Platform.CodexCli;

namespace WeChatAiCopilot.Api.Modules.Agent.Api;

public static class AgentModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the M4 application and PostgreSQL adapter. A runtime is registered
    /// only for an explicit Agent:Runtime selection; absent/unknown selections fail closed.
    /// </summary>
    public static IServiceCollection AddAgentModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterConfiguredRuntime(services, configuration);
        services.TryAddScoped<IAgentRepositoryV1, PostgresAgentRepositoryV1>();
        services.TryAddScoped<IAgentPresetCatalogV1>(_ => new ConfiguredAgentPresetCatalogV1(configuration));
        services.TryAddScoped<IAgentTaskCoordinatorV1>(serviceProvider =>
            new ConfiguredAgentTaskCoordinatorV1(serviceProvider, configuration));
        services.TryAddScoped<IAgentTaskApplicationServiceV1, AgentTaskApplicationServiceV1>();
        return services;
    }

    private static void RegisterConfiguredRuntime(IServiceCollection services, IConfiguration configuration)
    {
        services.AddStructuredCodexCliExecutorV1(configuration);
        var runtime = configuration["Agent:Runtime"];
        if (string.Equals(runtime, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IAgentRuntime, FakeAgentRuntime>();
        }
        else if (string.Equals(runtime, "CodexCli", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IAgentRuntime>(provider => new CodexCliAgentRuntime(
                provider.GetRequiredService<IStructuredCodexCliExecutorV1>()));
        }
    }
}

internal sealed class ConfiguredAgentPresetCatalogV1 : IAgentPresetCatalogV1
{
    private sealed record PresetDefinition(string PersonaPresetId, string Label, string Instruction);

    private static readonly PresetDefinition[] PresetDefinitions =
    [
        new("natural", "自然", "用自然、真诚、贴合语境的表达；不夸大关系或编造共同经历。"),
        new("warm", "温暖", "用温和、体贴的表达，适度回应对方情绪，不替对方推断感受。"),
        new("playful", "轻松", "在语境允许时用轻松、友善的表达；避免挖苦、施压或冒犯。"),
        new("concise", "简短", "用简短、清晰的口语表达，保留必要回应，不添加没有依据的细节。")
    ];

    private readonly HashSet<string> _allowedPresetIds;

    public ConfiguredAgentPresetCatalogV1(IConfiguration configuration)
    {
        _allowedPresetIds = configuration.GetSection("Agent:AllowedPersonaPresetIds")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool IsConfigured => PresetDefinitions.Any(preset => _allowedPresetIds.Contains(preset.PersonaPresetId));

    public bool Contains(string presetId) =>
        _allowedPresetIds.Contains(presetId)
        && PresetDefinitions.Any(preset => string.Equals(preset.PersonaPresetId, presetId, StringComparison.Ordinal));

    public IReadOnlyList<AgentPersonaPresetSummaryV1> GetAvailablePersonas() => PresetDefinitions
        .Where(preset => _allowedPresetIds.Contains(preset.PersonaPresetId))
        .Select(preset => new AgentPersonaPresetSummaryV1(preset.PersonaPresetId, preset.Label))
        .ToArray();

    public bool TryGetInstruction(string presetId, out string instruction)
    {
        var preset = PresetDefinitions.FirstOrDefault(candidate =>
            _allowedPresetIds.Contains(candidate.PersonaPresetId)
            && string.Equals(candidate.PersonaPresetId, presetId, StringComparison.Ordinal));
        instruction = preset?.Instruction ?? string.Empty;
        return preset is not null;
    }
}

internal sealed class ConfiguredAgentTaskCoordinatorV1(
    IServiceProvider services,
    IConfiguration configuration) : IAgentTaskCoordinatorV1
{
    public Task<SuggestionBatchV1> CreateSuggestionsAsync(
        AgentTaskV1 task,
        WeChatAiCopilot.Api.Modules.Memory.Contracts.AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default) =>
        CreateCoordinator().CreateSuggestionsAsync(task, authorizedScope, cancellationToken);

    public Task<ConsultationTurnV1> CreateConsultationTurnAsync(
        AgentTaskV1 task,
        AgentConsultationThreadV1 boundThread,
        WeChatAiCopilot.Api.Modules.Memory.Contracts.AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default) =>
        CreateCoordinator().CreateConsultationTurnAsync(task, boundThread, authorizedScope, cancellationToken);

    private AgentTaskCoordinatorV1 CreateCoordinator()
    {
        var contextService = services.GetService<IContextServiceV1>()
            ?? throw new AgentModuleNotReadyException(
                "M3_CONTEXT_NOT_CONFIGURED", "M3 context is not registered.");
        var runtime = services.GetService<IAgentRuntime>()
            ?? throw new AgentModuleNotReadyException(
                "AGENT_RUNTIME_NOT_CONFIGURED", "An Agent runtime is not registered.");
        var repository = services.GetService<IAgentRepositoryV1>()
            ?? throw new AgentModuleNotReadyException(
                "AGENT_RUN_AUDIT_NOT_CONFIGURED", "Agent run audit storage is not registered.");
        var options = ReadOptions(configuration)
            ?? throw new AgentModuleNotReadyException(
                "AGENT_CONFIGURATION_NOT_CONFIGURED", "The Agent execution configuration is incomplete.");
        return new AgentTaskCoordinatorV1(
            contextService,
            runtime,
            repository,
            services.GetRequiredService<IAgentPresetCatalogV1>(),
            options,
            services.GetService<TimeProvider>(),
            services.GetService<IContextToolSessionFactoryV1>());
    }

    private static AgentTaskCoordinatorOptionsV1? ReadOptions(IConfiguration configuration)
    {
        var skillVersion = configuration["Agent:SkillVersion"];
        if (string.IsNullOrWhiteSpace(skillVersion)
            || !int.TryParse(configuration["Agent:Budget:MaxDurationSeconds"], NumberStyles.None, CultureInfo.InvariantCulture, out var maxDurationSeconds)
            || maxDurationSeconds <= 0
            || !int.TryParse(configuration["Agent:Budget:MaxContextCharacters"], NumberStyles.None, CultureInfo.InvariantCulture, out var maxContextCharacters)
            || maxContextCharacters < 0
            || !int.TryParse(configuration["Agent:Budget:MaxToolCalls"], NumberStyles.None, CultureInfo.InvariantCulture, out var maxToolCalls)
            || maxToolCalls < 0)
        {
            return null;
        }

        var capabilities = new List<AgentReadCapabilityV1>();
        foreach (var child in configuration.GetSection("Agent:AllowedReadCapabilities").GetChildren())
        {
            if (!Enum.TryParse<AgentReadCapabilityV1>(child.Value, ignoreCase: false, out var capability)
                || !Enum.IsDefined(capability))
            {
                return null;
            }

            capabilities.Add(capability);
        }

        if (capabilities.Distinct().Count() != capabilities.Count)
        {
            return null;
        }

        var recentMessageLimit = 30;
        if (int.TryParse(configuration["Agent:RecentMessageLimit"], NumberStyles.None, CultureInfo.InvariantCulture, out var configuredLimit))
        {
            recentMessageLimit = configuredLimit;
        }

        var maxConsultationHistoryTurns = 8;
        if (configuration["Agent:ConsultationHistory:MaxTurns"] is { } configuredMaxTurns
            && (!int.TryParse(configuredMaxTurns, NumberStyles.None, CultureInfo.InvariantCulture, out maxConsultationHistoryTurns)
                || maxConsultationHistoryTurns is < 1 or > 100))
        {
            return null;
        }

        var maxConsultationHistoryCharacters = 4000;
        if (configuration["Agent:ConsultationHistory:MaxCharacters"] is { } configuredMaxCharacters
            && (!int.TryParse(configuredMaxCharacters, NumberStyles.None, CultureInfo.InvariantCulture, out maxConsultationHistoryCharacters)
                || maxConsultationHistoryCharacters is < 1 or > 100_000))
        {
            return null;
        }

        if (recentMessageLimit <= 0)
        {
            return null;
        }

        return new AgentTaskCoordinatorOptionsV1
        {
            SkillVersion = skillVersion,
            AllowedReadCapabilities = capabilities,
            Budget = new AgentExecutionBudgetV1
            {
                MaxDuration = TimeSpan.FromSeconds(maxDurationSeconds),
                MaxContextCharacters = maxContextCharacters,
                MaxToolCalls = maxToolCalls
            },
            RecentMessageLimit = recentMessageLimit,
            IncludeMemory = !bool.TryParse(configuration["Agent:IncludeMemory"], out var includeMemory) || includeMemory,
            MaxConsultationHistoryTurns = maxConsultationHistoryTurns,
            MaxConsultationHistoryCharacters = maxConsultationHistoryCharacters
        };
    }
}
