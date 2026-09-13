using System.Text.Json;
using System.Text.Json.Serialization;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Sample;

internal enum SampleCommandKind
{
    Inspect,
    Run,
    Help,
}

internal sealed record SampleCommand(
    SampleCommandKind Kind,
    SquadArtifactSource? Source = null,
    string? Request = null,
    string? Project = null,
    string? Version = null,
    string? Profile = null)
{
    private static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
            return new(SampleCommandKind.Inspect, new ReleaseArtifactSource());
        }

        if (args is ["--release", var releaseTag])
        {
            return new(SampleCommandKind.Inspect, new ReleaseArtifactSource(releaseTag));
        }

        if (args is ["--config", var configPath])
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<RunConfiguration>(json, ConfigurationJson)
                ?? throw new ArgumentException("The JSON configuration is empty.", nameof(args));
            return CreateRun(config.Request, config.Project, config.Version, config.Profile);
        }

        if (args.Contains("--run", StringComparer.Ordinal))
        {
            var values = ParseNamedValues(args);
            return CreateRun(
                Required(values, "--run"),
                Required(values, "--project"),
                values.GetValueOrDefault("--version") ?? "latest",
                values.GetValueOrDefault("--profile") ?? "default");
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
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --release v0.16.2");
        writer.WriteLine();
        writer.WriteLine("Optional live run:");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --run ""request"" --project <directory> --version latest|vX.Y.Z --profile <name>");
        writer.WriteLine(@"  dotnet run --project samples\HveSquad.AgentFramework.Sample -- --config <run.json>");
        writer.WriteLine();
        writer.WriteLine("JSON fields: request, project, version (latest or a published stable vX.Y.Z), profile.");
        writer.WriteLine("Environment: OPENAI_API_KEY, OPENAI_MODEL, optional OPENAI_ENDPOINT.");
        writer.WriteLine("The live host exposes only write_project_file. It does not provide shell, build, test, or network tools.");
        writer.WriteLine("Every write displays its exact path/content arguments and defaults to denial unless 'yes' is entered.");
    }

    private static SampleCommand CreateRun(string? request, string? project, string? version, string? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        version = string.IsNullOrWhiteSpace(version) ? "latest" : version;
        profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        var source = version == "latest"
            ? new ReleaseArtifactSource()
            : new ReleaseArtifactSource(version);
        return new(SampleCommandKind.Run, source, request, project, version, profile);
    }

    private static Dictionary<string, string> ParseNamedValues(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--run",
            "--project",
            "--version",
            "--profile",
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
        public string? Version { get; init; } = "latest";
        public string? Profile { get; init; } = "default";
    }
}
