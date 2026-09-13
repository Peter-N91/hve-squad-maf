using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>Dependency-injection registration for consumer-hosted squad runtimes.</summary>
public static class HveSquadServiceCollectionExtensions
{
    private static readonly HashSet<string> ScalarSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SquadRuntimeOptions.ProjectPath),
        nameof(SquadRuntimeOptions.Profile),
        nameof(SquadRuntimeOptions.Version),
        nameof(SquadRuntimeOptions.NamingPolicy),
        nameof(SquadRuntimeOptions.ApprovalChannel),
        nameof(SquadRuntimeOptions.Mode),
        nameof(SquadRuntimeOptions.RequireCouncil),
        nameof(SquadRuntimeOptions.IncludeRaiInCouncil),
        nameof(SquadRuntimeOptions.MaxDispatches),
        nameof(SquadRuntimeOptions.MaxDepth),
        nameof(SquadRuntimeOptions.MaxModelCalls),
        nameof(SquadRuntimeOptions.MaxToolCalls),
        nameof(SquadRuntimeOptions.MaxArtifactCharacters),
        nameof(SquadRuntimeOptions.MaxArtifactsPerDispatch),
        nameof(SquadRuntimeOptions.RunTimeout),
        nameof(SquadRuntimeOptions.EnableOpenTelemetry),
        nameof(SquadRuntimeOptions.OpenTelemetrySourceName),
    };

    private static readonly HashSet<string> ListSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SquadRuntimeOptions.Packs),
        nameof(SquadRuntimeOptions.InputPaths),
    };

    /// <summary>
    /// Registers a transient async runtime factory. <paramref name="configuration"/> is the squad
    /// section itself; callbacks, tools, selectors, and other host objects must be supplied by
    /// <paramref name="configureHost"/> and are never deserialized.
    /// </summary>
    /// <param name="services">The consumer service collection.</param>
    /// <param name="configuration">Optional scalar/list runtime settings, such as an HveSquad section.</param>
    /// <param name="configureHost">Trusted host setup for callbacks, tools, and programmatic settings.</param>
    /// <param name="source">Optional artifact source override, useful for offline hosts and tests.</param>
    public static IServiceCollection AddHveSquad(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<SquadRuntimeOptions>? configureHost = null,
        SquadArtifactSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configured = new SquadRuntimeOptions();
        if (configuration is not null)
        {
            ValidateConfiguration(configuration);
            try
            {
                configuration.Bind(configured);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new InvalidOperationException(
                    $"Invalid HVE Squad configuration at '{GetPath(configuration)}': {ex.Message}", ex);
            }
        }

        configureHost?.Invoke(configured);
        var template = Copy(configured);
        services.AddTransient(provider =>
            new SquadRuntimeFactory(provider, () => Copy(template), source));
        return services;
    }

    private static void ValidateConfiguration(IConfiguration configuration)
    {
        foreach (var setting in configuration.GetChildren())
        {
            if (ScalarSettings.Contains(setting.Key))
            {
                if (setting.Value is null || setting.GetChildren().Any())
                {
                    throw InvalidSetting(configuration, setting.Key, "must be a scalar value");
                }

                continue;
            }

            if (ListSettings.Contains(setting.Key))
            {
                ValidateList(configuration, setting);
                continue;
            }

            if (setting.Key.Equals(nameof(SquadRuntimeOptions.MemberNames), StringComparison.OrdinalIgnoreCase))
            {
                ValidateDictionary(configuration, setting);
                continue;
            }

            throw InvalidSetting(
                configuration,
                setting.Key,
                "is unknown or host-only. Configure callbacks, tools, model selectors, filters, and logging in configureHost");
        }
    }

    private static void ValidateList(IConfiguration configuration, IConfigurationSection setting)
    {
        if (setting.Value is not null)
        {
            throw InvalidSetting(configuration, setting.Key, "must be an indexed list");
        }

        var expectedIndex = 0;
        foreach (var item in setting.GetChildren())
        {
            if (!int.TryParse(item.Key, out var actualIndex) ||
                actualIndex != expectedIndex ||
                item.Value is null ||
                item.GetChildren().Any())
            {
                throw InvalidSetting(configuration, setting.Key, "must be a contiguous list of scalar values");
            }

            expectedIndex++;
        }
    }

    private static void ValidateDictionary(IConfiguration configuration, IConfigurationSection setting)
    {
        if (setting.Value is not null)
        {
            throw InvalidSetting(configuration, setting.Key, "must map role names to scalar member names");
        }

        foreach (var item in setting.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null || item.GetChildren().Any())
            {
                throw InvalidSetting(configuration, setting.Key, "must map role names to scalar member names");
            }
        }
    }

    private static InvalidOperationException InvalidSetting(
        IConfiguration configuration,
        string key,
        string problem) =>
        new($"Invalid HVE Squad configuration '{ConfigurationPath.Combine(GetPath(configuration), key)}': {problem}.");

    private static string GetPath(IConfiguration configuration) =>
        configuration is IConfigurationSection section && !string.IsNullOrEmpty(section.Path)
            ? section.Path
            : "HveSquad";

    private static SquadRuntimeOptions Copy(SquadRuntimeOptions options) => new()
    {
        ProjectPath = options.ProjectPath,
        Profile = options.Profile,
        Packs = options.Packs.ToArray(),
        Version = options.Version,
        NamingPolicy = options.NamingPolicy,
        MemberNames = new Dictionary<string, string>(options.MemberNames, StringComparer.OrdinalIgnoreCase),
        ApprovalChannel = options.ApprovalChannel,
        Mode = options.Mode,
        InputPaths = options.InputPaths.ToArray(),
        RequireCouncil = options.RequireCouncil,
        IncludeRaiInCouncil = options.IncludeRaiInCouncil,
        MaxDispatches = options.MaxDispatches,
        MaxDepth = options.MaxDepth,
        MaxModelCalls = options.MaxModelCalls,
        MaxToolCalls = options.MaxToolCalls,
        MaxArtifactCharacters = options.MaxArtifactCharacters,
        MaxArtifactsPerDispatch = options.MaxArtifactsPerDispatch,
        RunTimeout = options.RunTimeout,
        ApproveAsync = options.ApproveAsync,
        AskAsync = options.AskAsync,
        Tools = options.Tools.Select(tool => tool with { AllowedRoles = tool.AllowedRoles.ToArray() }).ToArray(),
        ModelSelector = options.ModelSelector,
        SkillFilter = options.SkillFilter,
        InstructionFilter = options.InstructionFilter,
        LoggerFactory = options.LoggerFactory,
        EnableOpenTelemetry = options.EnableOpenTelemetry,
        OpenTelemetrySourceName = options.OpenTelemetrySourceName,
    };
}
