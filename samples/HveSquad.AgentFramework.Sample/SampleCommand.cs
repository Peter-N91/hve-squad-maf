using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Sources;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sample;

internal enum SampleCommandKind
{
    Inspect,
    Run,
    ApprovalDemo,
    SkillExecutorDemo,
    WatchEvent,
    NativeHelper,
    FederationDemo,
    Help,
}

internal sealed record SampleCommand(
    SampleCommandKind Kind,
    SquadArtifactSource? Source = null,
    string? Request = null,
    string? Project = null,
    string? Version = null,
    string? Profile = null,
    string SquadRoot = ".copilot-tracking/squad",
    string? ResumeRunId = null,
    bool AllowNativeV1Migration = false,
    int MaxConcurrentDispatches = 4,
    SquadRosterConfiguration? RosterSettings = null,
    SquadModelRoutingConfiguration? ModelRouting = null,
    string? CostCeiling = null,
    SquadConsumptionConfiguration? Consumption = null,
    string? Discovery = null,
    string? TopicId = null,
    bool HumanPresent = true,
    SquadCouncilConfiguration? Council = null,
    bool DurableApprovals = false,
    TimeSpan? HumanResponseTimeout = null,
    string? HumanReply = null,
    string? DemoOperation = null,
    string Mode = "interactive",
    string? ExecutorPath = null,
    string? WatchEventPath = null,
    string? WatchSettingsPath = null,
    NativeHelperCommand? Helper = null)
{
    private static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    internal static SampleCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return new(SampleCommandKind.Inspect, new ProjectArtifactSource());
        }

        if (args is ["--help"] or ["-h"])
        {
            return new(SampleCommandKind.Help);
        }

        if (args is ["--latest"])
        {
            return new(SampleCommandKind.Inspect, new ReleaseArtifactSource(version: null));
        }

        if (args is ["--release", var releaseTag])
        {
            return new(SampleCommandKind.Inspect, new ReleaseArtifactSource(releaseTag));
        }
        if (args.Any(argument => argument is "--report" or "--memory" or "--learn"))
        {
            return new(SampleCommandKind.NativeHelper, Helper: NativeHelperCommand.Parse(args));
        }
        if (args.Contains("--federation-demo", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            if (Required(values, "--federation-demo") != "research" ||
                values.Keys.Any(key => key is not ("--federation-demo" or "--project" or "--version")))
            {
                throw new ArgumentException("Federation demo accepts research, a fresh isolated project and an optional pinned release.");
            }
            var version = values.GetValueOrDefault("--version") ?? SupportedSquadRelease.Current.Tag;
            return new(SampleCommandKind.FederationDemo, new ReleaseArtifactSource(version),
                Project: Required(values, "--project"), Version: version);
        }
        if (args.Contains("--watch-event", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            if (values.Keys.Any(key => key is not ("--watch-event" or "--watch-settings" or "--project" or "--version")))
            {
                throw new ArgumentException("Watch accepts a separate event file, trusted settings, isolated project and optional pinned version; event prose is not configuration.");
            }
            var version = values.GetValueOrDefault("--version") ?? SupportedSquadRelease.Current.Tag;
            return new(SampleCommandKind.WatchEvent, new ReleaseArtifactSource(version),
                Project: Required(values, "--project"), Version: version,
                WatchEventPath: Required(values, "--watch-event"), WatchSettingsPath: Required(values, "--watch-settings"));
        }

        if (args.Contains("--skill-executor-demo", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            if (values.Keys.Any(key => key is not ("--skill-executor-demo" or "--project" or "--version")))
            {
                throw new ArgumentException("Skill executor demo accepts only an explicit executable path, isolated project and optional release version.");
            }
            var executable = Required(values, "--skill-executor-demo");
            if (!Path.IsPathFullyQualified(executable))
            {
                throw new ArgumentException("Skill executor demo requires the absolute path of a trusted PowerShell 7.4+ executable.");
            }
            var version = values.GetValueOrDefault("--version") ?? SupportedSquadRelease.Current.Tag;
            return new(SampleCommandKind.SkillExecutorDemo, new ReleaseArtifactSource(version),
                Project: Required(values, "--project"), Version: version, ExecutorPath: executable);
        }
        if (args.Contains("--approval-demo", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            var operation = Required(values, "--approval-demo");
            if (operation is not ("start" or "resume") || values.Keys.Any(key =>
                key is not ("--approval-demo" or "--project" or "--version" or "--squad-root" or "--reply" or "--human-response-timeout")) ||
                operation == "start" && values.ContainsKey("--reply") ||
                operation == "resume" && !values.ContainsKey("--reply"))
            {
                throw new ArgumentException("Approval demo accepts start or resume, project, optional version/root/timeout, and an explicit --reply only for resume.");
            }
            var version = values.GetValueOrDefault("--version") ?? SupportedSquadRelease.Current.Tag;
            return new(SampleCommandKind.ApprovalDemo, new ReleaseArtifactSource(version),
                Project: Required(values, "--project"), Version: version,
                SquadRoot: values.GetValueOrDefault("--squad-root") ?? ".copilot-tracking/squad",
                DurableApprovals: true,
                HumanResponseTimeout: ParseHumanTimeout(values.GetValueOrDefault("--human-response-timeout")),
                HumanReply: values.GetValueOrDefault("--reply"), DemoOperation: operation);
        }
        if (args is ["--config", var configPath])
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<RunConfiguration>(json, ConfigurationJson)
                ?? throw new ArgumentException("The JSON configuration is empty.", nameof(args));
            return CreateRun(config.Request, config.Project, config.Version, config.Profile,
                config.SquadRoot, config.ResumeRunId, config.AllowNativeV1Migration, config.MaxConcurrentDispatches,
                new()
                {
                    Profile = config.Profile ?? "default",
                    Packs = config.Packs,
                    Roles = config.Roles,
                    AdditionalRoles = config.AdditionalRoles,
                    NamingPolicy = config.NamingPolicy,
                    MemberNames = config.MemberNames,
                    DeliverableRoots = config.DeliverableRoots,
                    Scope = config.InitializationScope,
                    Notifications = config.Notifications,
                }, config.ModelRouting, config.CostCeiling, config.Consumption, config.Discovery, config.TopicId,
                config.HumanPresent, config.Council, config.DurableApprovals,
                ParseHumanTimeout(config.HumanResponseTimeout), config.HumanReply, config.Mode);
        }

        if (args.Contains("--run", StringComparer.Ordinal) || args.Contains("--resume", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            if (values.Keys.Any(key => key is "--approval-demo" or "--skill-executor-demo" or
                "--watch-event" or "--watch-settings" or "--federation-demo"))
            {
                throw new ArgumentException("Live runs cannot silently accept another host command's capability settings.");
            }
            return CreateRun(
                values.GetValueOrDefault("--run"),
                Required(values, "--project"),
                values.GetValueOrDefault("--version") ?? SupportedSquadRelease.Current.Tag,
                values.GetValueOrDefault("--profile") ?? "default",
                values.GetValueOrDefault("--squad-root") ?? ".copilot-tracking/squad",
                values.GetValueOrDefault("--resume"),
                ParseBoolean(values.GetValueOrDefault("--migrate-v1")),
                ParseConcurrency(values.GetValueOrDefault("--max-concurrent-dispatches")),
                new()
                {
                    Profile = values.GetValueOrDefault("--profile") ?? "default",
                    Packs = ParseList(values.GetValueOrDefault("--packs")),
                    Roles = ParseList(values.GetValueOrDefault("--roles")),
                    AdditionalRoles = ParseList(values.GetValueOrDefault("--additional-roles")),
                    NamingPolicy = values.GetValueOrDefault("--naming-policy"),
                    Scope = values.GetValueOrDefault("--topology") ?? "single-squad",
                }, values.TryGetValue("--routing", out var routing) ? new() { Mode = routing } : null,
                values.GetValueOrDefault("--cost-ceiling"), null, values.GetValueOrDefault("--discovery"),
                values.GetValueOrDefault("--topic-id"), true,
                values.ContainsKey("--council-roles") || values.ContainsKey("--council-waiver") ? new()
                {
                    Roles = ParseList(values.GetValueOrDefault("--council-roles")),
                    WaiverReason = values.GetValueOrDefault("--council-waiver"),
                } : null, ParseBoolean(values.GetValueOrDefault("--durable-approvals")),
                ParseHumanTimeout(values.GetValueOrDefault("--human-response-timeout")),
                values.GetValueOrDefault("--reply"), values.GetValueOrDefault("--mode") ?? "interactive");
        }

        if (args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Unknown or incomplete command-line option.", nameof(args));
        }

        return new(SampleCommandKind.Inspect, new DirectoryArtifactSource(args));
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("HVE Squad Agent Framework sample");
        writer.WriteLine();
        writer.WriteLine("Credential-free inspection:");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --latest");
        writer.WriteLine($@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --release {SupportedSquadRelease.Current.Tag}");
        writer.WriteLine();
        writer.WriteLine("Optional live run:");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --run ""request"" --project <directory> --version latest|vX.Y.Z --profile <name>");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --config <run.json>");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --resume <run-id> --project <directory>");
        writer.WriteLine();
        writer.WriteLine("Credential-free, real MAF process-restart approval example (scripted research, not business-quality evidence):");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --approval-demo start --project <isolated-demo-directory>");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --approval-demo resume --project <same-directory> --reply /approve");
        writer.WriteLine("Credential-free registered released-script example (explicit trusted PowerShell 7.4+, isolated local fixture):");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --skill-executor-demo ""C:\Program Files\PowerShell\7\pwsh.exe"" --project <isolated-demo-directory>");
        writer.WriteLine("Only pr-reference/scripts/read-diff.ps1 is registered; it reads one original demo input. No general shell, credentials or state-script executor is enabled.");
        writer.WriteLine("The demo may use host-owned HVE_SQUAD_RELEASE_GITHUB_TOKEN for authenticated release metadata; it never passes that token to the process.");
        writer.WriteLine("Native event host (not an always-on service or Copilot CLI):");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --watch-event <platform-event.json> --watch-settings <trusted-watch-settings.json> --project <durable-isolated-case>");
        writer.WriteLine("Watch requires authenticated GITHUB_EVENT_NAME/GITHUB_RUN_ID, host-owned GITHUB_TOKEN and the actual model settings.");
        writer.WriteLine("Configure the exact reviewed workflow Git blob SHA; event text cannot select controls. Missing terminal authority yields Prepared, never a fake URL.");
        writer.WriteLine("Credential-free native federation example (fresh isolated directory, real member dispatch, synthetic local approvals only):");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --federation-demo research --project <empty-isolated-directory>");
        NativeHelperCommand.PrintUsage(writer);
        writer.WriteLine();
        writer.WriteLine($"JSON fields: request or resumeRunId, project, version (default {SupportedSquadRelease.Current.Tag}), profile, squadRoot, allowNativeV1Migration, maxConcurrentDispatches (default 4).");
        writer.WriteLine("Optional data flags: --squad-root <relative-root>, --migrate-v1 true|false, --max-concurrent-dispatches <positive integer>. Migration has its own approval.");
        writer.WriteLine("Roster flags: --roles <comma-separated>, --additional-roles <comma-separated>, --packs <comma-separated>, --naming-policy skip|provided|assigned|mixed, --topology single-squad|federation.");
        writer.WriteLine("JSON also accepts roles, additionalRoles, packs, namingPolicy, memberNames, deliverableRoots, initializationScope and non-secret notifications.");
        writer.WriteLine("Model policy: --routing off|ranked|manual; JSON modelRouting has mode and per-role models. Null inherits persisted policy.");
        writer.WriteLine("This sample advertises only OPENAI_MODEL and its actual client. Other catalog models are not assumed available.");
        writer.WriteLine("Cost policy: --cost-ceiling <positive USD decimal>|unset. Omission inherits only on same-run resume.");
        writer.WriteLine("JSON consumption supplies provider-specific observedOn, source, estimatorRevision, exact models tariffs and calibration.");
        writer.WriteLine("Discovery: --discovery quick|standard|deep|skip; --topic-id <stable-topic>. Eligible product/full goals are offered once.");
        writer.WriteLine("Council: --council-roles <comma-separated canonical lenses>; --council-waiver <reason>. Both still require human confirmation.");
        writer.WriteLine("JSON also accepts discovery, topicId, humanPresent (default true), and council { roles, waiverReason, overrideReason }.");
        writer.WriteLine("Mode: --mode interactive|autonomous|autopilot; JSON mode defaults to interactive. Input prose cannot select a mode.");
        writer.WriteLine("Autonomous uses a task-fit council and at most two independent revalidations; cycle two always reaches a human.");
        writer.WriteLine("Autopilot sequences full delivery and requires final human validation. Safe local work flows; specific risk/impact/cost/identity gates remain.");
        writer.WriteLine("Unattended runs never interview. Missing independent intake/council capabilities stop or require an explicit extension.");
        writer.WriteLine("No default Copilot tariff is applied to an OpenAI client. Missing rates or eligible calibration cannot admit a ceiling.");
        writer.WriteLine("Assigned/mixed aliases come from the released wordlist. A custom roster can omit methodology roles, but a later missing leg blocks.");
        writer.WriteLine("'latest' explicitly evaluates a candidate; it does not update the supported release or existing state.");
        writer.WriteLine("Environment: OPENAI_API_KEY, OPENAI_MODEL, optional OPENAI_ENDPOINT.");
        writer.WriteLine("Durable gates: --durable-approvals true; --human-response-timeout <TimeSpan> defaults to one day, maximum 30 days.");
        writer.WriteLine("In-chat resume accepts --reply /approve|/stop|'/answer: text'|'/changes: note', only for one exact pending action.");
        writer.WriteLine("JSON also accepts durableApprovals, humanResponseTimeout and humanReply. Adapters and authentication stay code-only.");
        writer.WriteLine("GitHub uses confirmed notifications settings and host-owned GITHUB_TOKEN; provision squad-approval and squad/changes labels deliberately.");
        writer.WriteLine("Webhook uses the confirmed opaque binding and host-owned SQUAD_WEBHOOK_URL. It is outbound-only; secret URL HTTP telemetry must be suppressed.");
        writer.WriteLine("The live host exposes only write_project_file. It does not provide shell, build, test, or network tools.");
        writer.WriteLine("Interactive writes display exact path/content arguments and default to denial. Other modes permit this host's bounded safe source writer only.");
    }

    private static SampleCommand CreateRun(string? request, string? project, string? version, string? profile,
        string squadRoot, string? resumeRunId, bool allowNativeV1Migration, int maxConcurrentDispatches,
        SquadRosterConfiguration rosterSettings, SquadModelRoutingConfiguration? modelRouting,
        string? costCeiling, SquadConsumptionConfiguration? consumption, string? discovery = null, string? topicId = null,
        bool humanPresent = true, SquadCouncilConfiguration? council = null, bool durableApprovals = false,
        TimeSpan? humanResponseTimeout = null, string? humanReply = null, string mode = "interactive")
    {
        if (string.IsNullOrWhiteSpace(request) == string.IsNullOrWhiteSpace(resumeRunId))
        {
            throw new ArgumentException("Supply exactly one nonempty request or resumeRunId.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(squadRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        mode = mode.ToLowerInvariant();
        if (mode is not ("interactive" or "autonomous" or "autopilot"))
        {
            throw new ArgumentException("Mode must be interactive, autonomous or autopilot.", nameof(mode));
        }
        if (maxConcurrentDispatches < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentDispatches), "Concurrency must be positive.");
        }
        if (modelRouting is { Mode: not ("off" or "ranked" or "manual") })
        {
            throw new ArgumentException("Model routing must be off, ranked or manual.", nameof(modelRouting));
        }
        new SquadRunConfiguration(costCeiling).Validate();
        if (consumption is not null)
        {
            consumption.Validate();
        }
        if (discovery is not (null or "quick" or "standard" or "deep" or "skip") ||
            topicId is not null && !Regex.IsMatch(topicId, "^(?=.{1,64}$)[a-z0-9]+(?:-[a-z0-9]+)*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException("Discovery must be quick, standard, deep or skip; topicId must be bounded lower-kebab-case.");
        }
        if (council is not null)
        {
            council.Validate();
        }
        if (humanReply is not null && (!durableApprovals || resumeRunId is null))
        {
            throw new ArgumentException("A humanReply needs durable approvals and an exact resumed run.");
        }
        version = string.IsNullOrWhiteSpace(version) ? SupportedSquadRelease.Current.Tag : version;
        profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        var source = version == "latest"
            ? new ReleaseArtifactSource(version: null)
            : new ReleaseArtifactSource(version);
        return new(SampleCommandKind.Run, source, request, project, version, profile, squadRoot, resumeRunId,
            allowNativeV1Migration, maxConcurrentDispatches, rosterSettings, modelRouting, costCeiling, consumption,
            discovery, topicId, humanPresent, council, durableApprovals, humanResponseTimeout, humanReply, Mode: mode);
    }

    private static string[] ParseList(string? value) => value is null ? [] :
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int ParseConcurrency(string? value) => value is null ? 4 :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : throw new ArgumentException("--max-concurrent-dispatches requires a positive integer.");

    private static bool ParseBoolean(string? value) => value is null ? false :
        bool.TryParse(value, out var parsed) ? parsed :
        throw new ArgumentException("Boolean flags require true or false.");

    private static TimeSpan? ParseHumanTimeout(string? value) => value is null ? null :
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeout) && timeout > TimeSpan.Zero &&
            timeout <= TimeSpan.FromDays(30) ? timeout :
            throw new ArgumentException("Human response timeout must be a positive TimeSpan of at most 30 days.");

    private static Dictionary<string, string> ParseNamedValues(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--run",
            "--project",
            "--version",
            "--profile",
            "--resume",
            "--squad-root",
            "--migrate-v1",
            "--max-concurrent-dispatches",
            "--roles",
            "--additional-roles",
            "--packs",
            "--naming-policy",
            "--topology",
            "--routing",
            "--cost-ceiling",
            "--discovery",
            "--topic-id",
            "--council-roles",
            "--council-waiver",
            "--durable-approvals",
            "--human-response-timeout",
            "--reply",
            "--approval-demo",
            "--skill-executor-demo",
            "--watch-event",
            "--watch-settings",
            "--federation-demo",
            "--mode",
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!allowed.Contains(key) || index + 1 >= args.Length || !result.TryAdd(key, args[index + 1]))
            {
                throw new ArgumentException($"Invalid or duplicate option '{key}'.", nameof(args));
            }
        }

        return result;
    }

    private static string Required(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '{key}' is missing.");

    private sealed class RunConfiguration
    {
        public string? Request { get; init; }
        public string? Project { get; init; }
        public string? Version { get; init; } = SupportedSquadRelease.Current.Tag;
        public string? Profile { get; init; } = "default";
        public string SquadRoot { get; init; } = ".copilot-tracking/squad";
        public string? ResumeRunId { get; init; }
        public bool AllowNativeV1Migration { get; init; }
        public int MaxConcurrentDispatches { get; init; } = 4;
        public string[] Roles { get; init; } = [];
        public string[] AdditionalRoles { get; init; } = [];
        public string[] Packs { get; init; } = [];
        public string? NamingPolicy { get; init; }
        public Dictionary<string, string> MemberNames { get; init; } = [];
        public Dictionary<string, string> DeliverableRoots { get; init; } = [];
        public string InitializationScope { get; init; } = "single-squad";
        public SquadNotificationConfiguration? Notifications { get; init; }
        public SquadModelRoutingConfiguration? ModelRouting { get; init; }
        public string? CostCeiling { get; init; }
        public SquadConsumptionConfiguration? Consumption { get; init; }
        public string? Discovery { get; init; }
        public string? TopicId { get; init; }
        public bool HumanPresent { get; init; } = true;
        public SquadCouncilConfiguration? Council { get; init; }
        public bool DurableApprovals { get; init; }
        public string? HumanResponseTimeout { get; init; }
        public string? HumanReply { get; init; }
        public string Mode { get; init; } = "interactive";
    }
}
