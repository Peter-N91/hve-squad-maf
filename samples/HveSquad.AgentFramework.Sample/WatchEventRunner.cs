using System.ClientModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HveSquad.AgentFramework.Sample;

internal static class WatchEventRunner
{
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal static async Task<int> RunAsync(SampleCommand command, CancellationToken cancellationToken)
    {
        var settings = ReadSettings(command.WatchSettingsPath!);
        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME")
            ?? throw new InvalidOperationException("The native runner needs trusted GITHUB_EVENT_NAME.");
        if (!long.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var runId) || runId <= 0)
        {
            throw new InvalidOperationException("The native runner needs a positive trusted GITHUB_RUN_ID.");
        }
        using var document = JsonDocument.Parse(ReadBounded(command.WatchEventPath!, 200_000));
        SquadWatchEvent source;
        try
        {
            source = GitHubWatchEventParser.Parse(eventName, document.RootElement, runId, DateTimeOffset.UtcNow);
        }
        catch (NotSupportedException exception)
        {
            Console.WriteLine(exception.Message);
            return 0;
        }
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new InvalidOperationException("Register host-owned GITHUB_TOKEN for provider verification and separately permitted terminal actions.");
        }
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        var (client, model) = SampleModelClient.Create();
        using var ownedClient = client;
        var project = Path.GetFullPath(command.Project!);
        var writer = new ProjectFileWriter(project);
        var options = new SquadRuntimeOptions
        {
            ProjectPath = project,
            Version = command.Version!,
            HumanPresent = false,
            Discovery = "skip",
            Profile = settings.Profile,
            AdditionalRoles = settings.AdditionalRoles,
            CostCeiling = settings.CostCeiling,
            Consumption = settings.Consumption,
            HumanApprovalChannel = new GitHubIssueApprovalChannel(http, settings.Repository, settings.ApproverHandle),
            Notifications = new()
            {
                ApprovalChannel = "github-issue",
                GitHubRepository = settings.Repository,
                GitHubHandle = settings.ApproverHandle,
            },
            ModelSelector = (_, _) => new(client, model),
            AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new(model, client)]),
        };
        if (settings.WritePaths.Count > 0)
        {
            var paths = settings.WritePaths.Select(path => path.Replace('\\', '/')).ToArray();
            options.Tools.Add(new(Microsoft.Extensions.AI.AIFunctionFactory.Create(
                async (string path, string content, CancellationToken token) =>
                {
                    if (!paths.Contains(path.Replace('\\', '/'), StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException("This source path is outside the exact trusted Watch writer projection.");
                    }
                    return await writer.WriteAsync(path, content, token).ConfigureAwait(false);
                }, "write_project_file", "Write only one of the host's explicitly registered reversible local source paths."),
                "edit", ["developer"], SquadToolEffect.ProjectWrite, ProjectFileWriter.GetOutputEvidence,
                ReconcileAsync: writer.ReconcileAsync, ProjectWritePaths: writer.ProjectWritePaths, Impact: SquadToolImpact.Safe));
        }
        var authority = new GitHubWatchEventAuthority(http, settings.Repository, settings.ApproverHandle, settings.Workflow);
        var connector = new GitHubWatchTerminalConnector(http, settings.Repository, settings.Terminal,
            (request, _) => ValueTask.FromResult(settings.AuthorizeTerminal &&
                request.Repository.Equals(settings.Repository, StringComparison.OrdinalIgnoreCase) &&
                settings.Terminal.Kinds.Contains(request.Kind)), settings.TerminalAuthorizationBinding);
        var watch = new SquadWatchConfiguration
        {
            Repository = settings.Repository,
            Authority = authority,
            TerminalConnector = connector,
            Target = settings.Target,
            RoutedMode = settings.RoutedMode,
            ScheduleMode = settings.ScheduleMode,
            ScheduleRequest = settings.ScheduleRequest,
            ScheduleTerminal = settings.ScheduleTerminal,
        };
        using var federation = await SquadFederation.CreateAsync(client, options, command.Source, cancellationToken).ConfigureAwait(false);
        federation.RegisterWatch(watch);
        if (settings.ReconcileWatchRunId is { } interrupted)
        {
            var observed = await federation.ReconcileWatchAsync(interrupted, watch, cancellationToken).ConfigureAwait(false);
            Print(observed);
            return observed.Status == SquadWatchStatus.ReconciliationRequired ? 4 : 0;
        }
        var result = await federation.HandleWatchEventAsync(source, watch, cancellationToken).ConfigureAwait(false);
        if (result.Status == SquadWatchStatus.Pending && result.Trigger is { } trigger)
        {
            var responses = await federation.PollHumanResponsesAsync(trigger.RunId, cancellationToken).ConfigureAwait(false);
            if (responses.Submissions.Any(submission => submission.Accepted))
            {
                result = await federation.HandleWatchEventAsync(source, watch, cancellationToken).ConfigureAwait(false);
            }
        }
        Print(result);
        return result.Status is SquadWatchStatus.Delivered or SquadWatchStatus.Ignored ? 0 : 4;
    }

    internal static WatchSettings ReadSettings(string path) =>
        JsonSerializer.Deserialize<WatchSettings>(ReadBounded(path, 200_000), SettingsJson)
        ?? throw new ArgumentException("The trusted Watch settings are empty.", nameof(path));

    private static string ReadBounded(string path, int maximum)
    {
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).Length > maximum * 4L)
        {
            throw new ArgumentException("The event/settings file exceeds its bounded input size.", nameof(path));
        }
        var text = File.ReadAllText(full);
        return text.Length <= maximum ? text : throw new ArgumentException("The event/settings input is too large.", nameof(path));
    }

    private static void Print(SquadWatchRunResult result)
    {
        Console.WriteLine("Watch status: " + result.Status);
        Console.WriteLine(result.Diagnostic);
        if (result.Trigger is { } trigger)
        {
            Console.WriteLine("Watch identity: " + trigger.WatchRunId + "; inner run: " + trigger.RunId);
        }
        if (result.Terminal is { } terminal)
        {
            Console.WriteLine("Verified provider resource: " + terminal.Reference);
        }
    }
}

internal sealed class WatchSettings
{
    public required string Repository { get; init; }
    public required string ApproverHandle { get; init; }
    public required GitHubWatchWorkflowPolicy Workflow { get; init; }
    public GitHubWatchTerminalOptions Terminal { get; init; } = new();
    public string TerminalAuthorizationBinding { get; init; } = "reviewed-watch-terminal:1";
    public bool AuthorizeTerminal { get; init; }
    public string Profile { get; init; } = "default";
    public IList<string> AdditionalRoles { get; init; } = ["intake-validator", "architect"];
    public IList<string> WritePaths { get; init; } = [];
    public string? CostCeiling { get; init; }
    public SquadConsumptionConfiguration? Consumption { get; init; }
    public string? Target { get; init; }
    public string RoutedMode { get; init; } = "interactive";
    public string ScheduleMode { get; init; } = "autopilot";
    public string ScheduleRequest { get; init; } = "Perform an authorized maintenance sweep.";
    public SquadWatchTerminalKind ScheduleTerminal { get; init; } = SquadWatchTerminalKind.Issue;
    public string? ReconcileWatchRunId { get; init; }
}

internal static class SampleModelClient
{
    internal static (IChatClient Client, string Model) Create()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Live mode requires OPENAI_API_KEY and OPENAI_MODEL. OPENAI_ENDPOINT is optional.");
        }
        var options = new OpenAIClientOptions();
        if (Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") is { Length: > 0 } endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException("OPENAI_ENDPOINT must be an absolute HTTP or HTTPS URI.");
            }
            options.Endpoint = uri;
        }
        return (new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(model).AsIChatClient(), model);
    }
}
