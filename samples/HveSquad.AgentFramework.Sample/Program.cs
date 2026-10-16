using System.Text.Json;
using System.Net.Http.Headers;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

SampleCommand command;
try
{
    command = SampleCommand.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    SampleCommand.PrintUsage(Console.Error);
    return 2;
}

if (command.Kind == SampleCommandKind.Help)
{
    SampleCommand.PrintUsage(Console.Out);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return command.Kind switch
    {
        SampleCommandKind.Inspect => await Inspector.RunAsync(command.Source!, cancellation.Token),
        SampleCommandKind.ApprovalDemo => await DurableApprovalDemo.RunAsync(command, cancellation.Token),
        SampleCommandKind.SkillExecutorDemo => await SkillExecutorDemo.RunAsync(command, cancellation.Token),
        SampleCommandKind.WatchEvent => await WatchEventRunner.RunAsync(command, cancellation.Token),
        SampleCommandKind.NativeHelper => await NativeHelpers.RunAsync(command.Helper!, cancellation.Token),
        SampleCommandKind.FederationDemo => await FederationDemo.RunAsync(command, cancellation.Token),
        _ => await RunLiveAsync(command, cancellation.Token),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static async Task<int> RunLiveAsync(SampleCommand command, CancellationToken cancellationToken)
{
    var (client, model) = SampleModelClient.Create();
    using IChatClient chatClient = client;

    var project = Path.GetFullPath(command.Project!);
    if (!Directory.Exists(project))
    {
        throw new DirectoryNotFoundException($"Project directory '{project}' does not exist.");
    }

    var writer = new ProjectFileWriter(project);
    using var githubHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    using var webhookHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var notificationChoices = command.RosterSettings?.Notifications;
    if (command.DurableApprovals && notificationChoices?.ApprovalChannel == "github-issue")
    {
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new InvalidOperationException("The registered GitHub adapter requires host-owned GITHUB_TOKEN with the documented repository permissions.");
        }
        githubHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
    }
    var services = new ServiceCollection();
    services.AddSingleton(chatClient);
    services.AddHveSquad(
        configureHost: options =>
        {
            options.ProjectPath = project;
            options.SquadRoot = command.SquadRoot;
            options.AllowNativeV1Migration = command.AllowNativeV1Migration;
            options.MaxConcurrentDispatches = command.MaxConcurrentDispatches;
            options.Profile = command.Profile!;
            if (command.RosterSettings is { } roster)
            {
                options.Profile = roster.Profile;
                options.Roles = roster.Roles.ToArray();
                options.AdditionalRoles = roster.AdditionalRoles.ToArray();
                options.Packs = roster.Packs.ToArray();
                options.NamingPolicy = roster.NamingPolicy;
                options.MemberNames = new Dictionary<string, string>(roster.MemberNames, StringComparer.OrdinalIgnoreCase);
                options.DeliverableRoots = new Dictionary<string, string>(roster.DeliverableRoots, StringComparer.OrdinalIgnoreCase);
                options.InitializationScope = roster.Scope;
                options.Notifications = roster.Notifications ?? new();
            }
            options.Version = command.Version!;
            options.ApprovalChannel = "in-chat";
            options.ApproveAsync = ConsoleCallbacks.ApproveAsync;
            options.AskAsync = ConsoleCallbacks.AskAsync;
            options.HumanResponseTimeout = command.HumanResponseTimeout ?? TimeSpan.FromDays(1);
            if (command.DurableApprovals)
            {
                options.HumanApprovalChannel = options.Notifications.ApprovalChannel == "github-issue"
                    ? new GitHubIssueApprovalChannel(githubHttp,
                        options.Notifications.GitHubRepository ?? throw new InvalidOperationException("Confirm an approval repository."),
                        options.Notifications.GitHubHandle ?? throw new InvalidOperationException("Confirm a human GitHub account."))
                    : ConsoleCallbacks.CreateDurableChannel(project, options.SquadRoot, Console.Out);
                if (options.Notifications.ApprovalChannel == "webhook")
                {
                    options.HumanNotifier = new WebhookSquadNotifier(
                        options.Notifications.WebhookBinding ?? throw new InvalidOperationException("Confirm an opaque webhook binding."),
                        webhookHttp, _ =>
                        {
                            var secretEndpoint = Environment.GetEnvironmentVariable("SQUAD_WEBHOOK_URL");
                            if (!Uri.TryCreate(secretEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                            {
                                throw new SquadHumanTransportException("Configure host-owned SQUAD_WEBHOOK_URL as HTTPS; never paste it into state or logs.");
                            }
                            return ValueTask.FromResult(uri);
                        });
                }
            }
            options.ModelSelector = (_, _) => new SquadModelSelection(chatClient, model);
            options.ModelRouting = command.ModelRouting;
            options.CostCeiling = command.CostCeiling;
            options.Consumption = command.Consumption;
            options.Discovery = command.Discovery;
            options.TopicId = command.TopicId;
            options.HumanPresent = command.HumanPresent;
            options.Council = command.Council;
            options.Mode = command.Mode;
            options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
                [new(model, chatClient)]);
            options.Tools.Add(new(
                AIFunctionFactory.Create(
                    writer.WriteAsync,
                    "write_project_file",
                    "Write UTF-8 text to one safe project-relative source path after exact host approval."),
                "edit",
                ["developer"],
                SquadToolEffect.ProjectWrite,
                ProjectFileWriter.GetOutputEvidence,
                ReconcileAsync: writer.ReconcileAsync,
                ProjectWritePaths: writer.ProjectWritePaths,
                Impact: SquadToolImpact.Safe));
        },
        source: command.Source);

    await using var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<SquadRuntimeFactory>();
    using var runtime = await factory.CreateAsync(cancellationToken);
    if (command.DurableApprovals && command.ResumeRunId is { } pendingRun)
    {
        if (notificationChoices?.ApprovalChannel == "github-issue")
        {
            if (command.HumanReply is not null)
            {
                throw new InvalidOperationException("GitHub decisions must originate in an authenticated issue comment or label, not a command-line reply.");
            }
            var poll = await runtime.PollHumanResponsesAsync(pendingRun, cancellationToken);
            foreach (var submission in poll.Submissions)
            {
                Console.WriteLine(submission.Diagnostic);
            }
            foreach (var diagnostic in poll.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }
        }
        else
        {
            await ConsoleCallbacks.SubmitPendingAsync(runtime, command.HumanReply, cancellationToken);
        }
    }
    var result = command.ResumeRunId is { } runId
        ? await runtime.ResumeAsync(runId, cancellationToken)
        : await runtime.RunAsync(command.Request!, cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Status: {result.Status}");
    Console.WriteLine($"Run ID: {result.RunId}");
    Console.WriteLine($"Evidence integrity: {result.FailureClass}");
    if (result.ModeOutcome is not null)
    {
        Console.WriteLine("Mode outcome: " + JsonSerializer.Serialize(result.ModeOutcome));
    }
    Console.WriteLine(result.ResponseText);
    foreach (var pending in result.PendingHumanActions ?? [])
    {
        Console.WriteLine($"Human action: {pending.Binding.Id}; channel={pending.Channel}; status={pending.Status}; " +
            $"delivery={pending.DeliveryStatus}; outbound={pending.OutboundStatus}; expires={pending.ExpiresAt:O}");
        Console.WriteLine($"Resume exactly run {result.RunId}; do not start a new request or edit approval state.");
    }
    if (result.Consumption is not null)
    {
        Console.WriteLine("Consumption: " + JsonSerializer.Serialize(result.Consumption));
    }
    if (result.CostPreflight is not null)
    {
        Console.WriteLine("Cost preflight: " + JsonSerializer.Serialize(result.CostPreflight));
    }
    foreach (var diagnostic in result.Diagnostics ?? [])
    {
        Console.Error.WriteLine($"WARNING: {diagnostic}");
    }
    Console.WriteLine($"Verified consequential calls: {result.Effects?.Count ?? 0}");
    if (result.Evidence.Count > 0)
    {
        Console.WriteLine($"Evidence: {result.Evidence.Count} dispatch(es)");
        foreach (var evidence in result.Evidence)
        {
            Console.WriteLine(
                $"- {evidence.Role}: {evidence.Verdict}; model={evidence.Model} ({evidence.ModelSource})");
        }
    }

    return result.Status is SquadRunStatus.Completed or SquadRunStatus.ResearchCompleted or
        SquadRunStatus.PlanCompleted or SquadRunStatus.ReviewCompleted ? 0 : 1;
}
