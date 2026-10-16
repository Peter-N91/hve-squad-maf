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
        nameof(SquadRuntimeOptions.SquadRoot),
        nameof(SquadRuntimeOptions.AllowNativeV1Migration),
        nameof(SquadRuntimeOptions.Profile),
        nameof(SquadRuntimeOptions.InitializationScope),
        nameof(SquadRuntimeOptions.Version),
        nameof(SquadRuntimeOptions.NamingPolicy),
        nameof(SquadRuntimeOptions.ApprovalChannel),
        nameof(SquadRuntimeOptions.Mode),
        nameof(SquadRuntimeOptions.RequireCouncil),
        nameof(SquadRuntimeOptions.IncludeRaiInCouncil),
        nameof(SquadRuntimeOptions.Discovery),
        nameof(SquadRuntimeOptions.TopicId),
        nameof(SquadRuntimeOptions.HumanPresent),
        nameof(SquadRuntimeOptions.CostCeiling),
        nameof(SquadRuntimeOptions.MaxDispatches),
        nameof(SquadRuntimeOptions.MaxConcurrentDispatches),
        nameof(SquadRuntimeOptions.MaxDepth),
        nameof(SquadRuntimeOptions.MaxModelCalls),
        nameof(SquadRuntimeOptions.MaxToolCalls),
        nameof(SquadRuntimeOptions.MaxArtifactCharacters),
        nameof(SquadRuntimeOptions.MaxArtifactsPerDispatch),
        nameof(SquadRuntimeOptions.RunTimeout),
        nameof(SquadRuntimeOptions.HumanResponseTimeout),
        nameof(SquadRuntimeOptions.EnableOpenTelemetry),
        nameof(SquadRuntimeOptions.OpenTelemetrySourceName),
    };

    private static readonly HashSet<string> ListSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SquadRuntimeOptions.Packs),
        nameof(SquadRuntimeOptions.Roles),
        nameof(SquadRuntimeOptions.AdditionalRoles),
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

            if (setting.Key.Equals(nameof(SquadRuntimeOptions.MemberNames), StringComparison.OrdinalIgnoreCase) ||
                setting.Key.Equals(nameof(SquadRuntimeOptions.DeliverableRoots), StringComparison.OrdinalIgnoreCase))
            {
                ValidateDictionary(configuration, setting);
                continue;
            }
            if (setting.Key.Equals(nameof(SquadRuntimeOptions.Notifications), StringComparison.OrdinalIgnoreCase))
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    nameof(SquadNotificationConfiguration.ApprovalChannel), nameof(SquadNotificationConfiguration.Enabled),
                    nameof(SquadNotificationConfiguration.Email), nameof(SquadNotificationConfiguration.GitHubHandle),
                    nameof(SquadNotificationConfiguration.GitHubRepository), nameof(SquadNotificationConfiguration.WebhookBinding),
                };
                if (setting.Value is not null || setting.GetChildren().Any(child =>
                    !allowed.Contains(child.Key) || child.Value is null || child.GetChildren().Any()))
                {
                    throw InvalidSetting(configuration, setting.Key,
                        "must contain only non-secret channel, contact and opaque host binding scalars");
                }
                continue;
            }
            if (setting.Key.Equals(nameof(SquadRuntimeOptions.ModelRouting), StringComparison.OrdinalIgnoreCase))
            {
                if (setting.Value is not null)
                {
                    throw InvalidSetting(configuration, setting.Key, "must contain Mode and an optional Models dictionary");
                }
                foreach (var child in setting.GetChildren())
                {
                    if (child.Key.Equals(nameof(SquadModelRoutingConfiguration.Mode), StringComparison.OrdinalIgnoreCase) &&
                        child.Value is not null && !child.GetChildren().Any())
                    {
                        continue;
                    }
                    if (child.Key.Equals(nameof(SquadModelRoutingConfiguration.Models), StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateDictionary(setting, child);
                        continue;
                    }
                    throw InvalidSetting(setting, child.Key, "is unknown or must be a scalar Mode or Models dictionary");
                }
                continue;
            }
            if (setting.Key.Equals(nameof(SquadRuntimeOptions.Consumption), StringComparison.OrdinalIgnoreCase))
            {
                ValidateConsumption(setting);
                continue;
            }
            if (setting.Key.Equals(nameof(SquadRuntimeOptions.Council), StringComparison.OrdinalIgnoreCase))
            {
                if (setting.Value is not null)
                {
                    throw InvalidSetting(configuration, setting.Key, "must contain Roles and optional WaiverReason/OverrideReason");
                }
                foreach (var child in setting.GetChildren())
                {
                    if (child.Key.Equals(nameof(SquadCouncilConfiguration.Roles), StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateList(setting, child);
                    }
                    else if (new[] { nameof(SquadCouncilConfiguration.WaiverReason), nameof(SquadCouncilConfiguration.OverrideReason) }
                        .Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        RequireScalar(child);
                    }
                    else
                    {
                        throw InvalidSetting(setting, child.Key, "is unknown or host-only");
                    }
                }
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

    private static void ValidateConsumption(IConfigurationSection setting)
    {
        if (setting.Value is not null)
        {
            throw InvalidSetting(setting, setting.Key, "must be a dated tariff and calibration object");
        }
        foreach (var child in setting.GetChildren())
        {
            if (new[] { "ObservedOn", "Source", "EstimatorRevision" }.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                RequireScalar(child);
            }
            else if (child.Key.Equals("Calibration", StringComparison.OrdinalIgnoreCase))
            {
                ValidateScalarObject(child, ["Factor", "Observations", "LastReconciled", "Basis"]);
            }
            else if (child.Key.Equals("Models", StringComparison.OrdinalIgnoreCase))
            {
                if (child.Value is not null)
                {
                    throw InvalidSetting(setting, child.Key, "must map exact model IDs to tariff objects");
                }
                foreach (var model in child.GetChildren())
                {
                    if (model.Value is not null)
                    {
                        throw InvalidSetting(child, model.Key, "must be a tariff object");
                    }
                    foreach (var rate in model.GetChildren())
                    {
                        if (rate.Key.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
                            rate.Key.Equals("LongContext", StringComparison.OrdinalIgnoreCase))
                        {
                            ValidateScalarObject(rate, ["Input", "Cached", "CacheWrite", "Output"], requireAll: true);
                        }
                        else if (rate.Key.Equals("FlatRateConfirmed", StringComparison.OrdinalIgnoreCase) ||
                            rate.Key.Equals("LongContextThresholdTokens", StringComparison.OrdinalIgnoreCase))
                        {
                            RequireScalar(rate);
                        }
                        else
                        {
                            throw InvalidSetting(model, rate.Key, "is not a supported tariff field");
                        }
                    }
                    if (!model.GetChildren().Any(rate => rate.Key.Equals("Standard", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw InvalidSetting(child, model.Key, "requires all four Standard rates");
                    }
                }
            }
            else
            {
                throw InvalidSetting(setting, child.Key, "is unknown or host-only");
            }
        }
    }

    private static void RequireScalar(IConfigurationSection setting)
    {
        if (setting.Value is null || setting.GetChildren().Any())
        {
            throw InvalidSetting(setting, setting.Key, "must be a scalar value");
        }
    }

    private static void ValidateScalarObject(IConfigurationSection setting, string[] allowed, bool requireAll = false)
    {
        var children = setting.GetChildren().ToArray();
        if (setting.Value is not null || children.Any(child => !allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)) ||
            requireAll && children.Length != allowed.Length)
        {
            throw InvalidSetting(setting, setting.Key, "must contain only the supported scalar fields");
        }
        foreach (var child in children)
        {
            RequireScalar(child);
        }
    }

    private static void ValidateDictionary(IConfiguration configuration, IConfigurationSection setting)
    {
        if (setting.Value is not null)
        {
            throw InvalidSetting(configuration, setting.Key, "must map role names to scalar values");
        }

        foreach (var item in setting.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null || item.GetChildren().Any())
            {
                throw InvalidSetting(configuration, setting.Key, "must map role names to scalar values");
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
        SquadRoot = options.SquadRoot,
        AllowNativeV1Migration = options.AllowNativeV1Migration,
        DeliverableRoots = new Dictionary<string, string>(options.DeliverableRoots, StringComparer.OrdinalIgnoreCase),
        Profile = options.Profile,
        Roles = options.Roles.ToArray(),
        AdditionalRoles = options.AdditionalRoles.ToArray(),
        InitializationScope = options.InitializationScope,
        Notifications = new()
        {
            ApprovalChannel = options.Notifications.ApprovalChannel,
            Enabled = options.Notifications.Enabled,
            Email = options.Notifications.Email,
            GitHubHandle = options.Notifications.GitHubHandle,
            GitHubRepository = options.Notifications.GitHubRepository,
            WebhookBinding = options.Notifications.WebhookBinding,
        },
        Packs = options.Packs.ToArray(),
        Version = options.Version,
        NamingPolicy = options.NamingPolicy,
        MemberNames = new Dictionary<string, string>(options.MemberNames, StringComparer.OrdinalIgnoreCase),
        ApprovalChannel = options.ApprovalChannel,
        Mode = options.Mode,
        InputPaths = options.InputPaths.ToArray(),
        RequireCouncil = options.RequireCouncil,
        IncludeRaiInCouncil = options.IncludeRaiInCouncil,
        Discovery = options.Discovery,
        TopicId = options.TopicId,
        HumanPresent = options.HumanPresent,
        Council = options.Council is null ? null : ReadinessContracts.Copy(options.Council),
        SelectCouncilAsync = options.SelectCouncilAsync,
        CostCeiling = options.CostCeiling,
        Consumption = options.Consumption is null ? null : ConsumptionMath.Copy(options.Consumption),
        TokenUsageSelector = options.TokenUsageSelector,
        MaxDispatches = options.MaxDispatches,
        MaxConcurrentDispatches = options.MaxConcurrentDispatches,
        MaxDepth = options.MaxDepth,
        MaxModelCalls = options.MaxModelCalls,
        MaxToolCalls = options.MaxToolCalls,
        MaxArtifactCharacters = options.MaxArtifactCharacters,
        MaxArtifactsPerDispatch = options.MaxArtifactsPerDispatch,
        RunTimeout = options.RunTimeout,
        ApproveAsync = options.ApproveAsync,
        AskAsync = options.AskAsync,
        HumanApprovalChannel = options.HumanApprovalChannel,
        HumanNotifier = options.HumanNotifier,
        HumanResponseTimeout = options.HumanResponseTimeout,
        SelectInitializationAsync = options.SelectInitializationAsync,
        Tools = options.Tools.Select(tool => tool with { AllowedRoles = tool.AllowedRoles.ToArray() }).ToArray(),
        ModelSelector = options.ModelSelector,
        ModelRouting = options.ModelRouting is null ? null : new()
        {
            Mode = options.ModelRouting.Mode,
            Models = new Dictionary<string, string>(options.ModelRouting.Models, StringComparer.OrdinalIgnoreCase),
        },
        AvailableModelsAsync = options.AvailableModelsAsync,
        AgentSelector = options.AgentSelector,
        TimeProvider = options.TimeProvider,
        Memory = options.Memory is null ? null : MemoryContracts.Copy(options.Memory),
        SkillFilter = options.SkillFilter,
        InstructionFilter = options.InstructionFilter,
        LoggerFactory = options.LoggerFactory,
        EnableOpenTelemetry = options.EnableOpenTelemetry,
        OpenTelemetrySourceName = options.OpenTelemetrySourceName,
    };
}
