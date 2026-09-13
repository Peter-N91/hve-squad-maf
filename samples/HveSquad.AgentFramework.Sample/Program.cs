using System.ClientModel;
using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

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
    return command.Kind == SampleCommandKind.Inspect
        ? await Inspector.RunAsync(command.Source!, cancellation.Token)
        : await RunLiveAsync(command, cancellation.Token);
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
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
    {
        throw new InvalidOperationException(
            "Live mode requires OPENAI_API_KEY and OPENAI_MODEL. OPENAI_ENDPOINT is optional.");
    }

    var project = Path.GetFullPath(command.Project!);
    if (!Directory.Exists(project))
    {
        throw new DirectoryNotFoundException($"Project directory '{project}' does not exist.");
    }

    var clientOptions = new OpenAIClientOptions();
    var endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(endpoint))
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("OPENAI_ENDPOINT must be an absolute HTTP or HTTPS URI.");
        }

        clientOptions.Endpoint = endpointUri;
    }

    var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    using IChatClient chatClient = openAIClient.GetChatClient(model).AsIChatClient();
    var writer = new ProjectFileWriter(project);
    var services = new ServiceCollection();
    services.AddSingleton(chatClient);
    services.AddHveSquad(
        configureHost: options =>
        {
            options.ProjectPath = project;
            options.Profile = command.Profile!;
            options.Version = command.Version!;
            options.ApprovalChannel = "in-chat";
            options.ApproveAsync = ConsoleCallbacks.ApproveAsync;
            options.AskAsync = ConsoleCallbacks.AskAsync;
            options.ModelSelector = (_, _) => new SquadModelSelection(chatClient, model);
            options.Tools.Add(new(
                AIFunctionFactory.Create(
                    writer.WriteAsync,
                    "write_project_file",
                    "Write UTF-8 text to one safe project-relative source path after exact host approval."),
                "edit",
                ["developer"],
                SquadToolEffect.ProjectWrite,
                ProjectFileWriter.GetOutputEvidence));
        },
        source: command.Source);

    await using var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<SquadRuntimeFactory>();
    using var runtime = await factory.CreateAsync(cancellationToken);
    var result = await runtime.RunAsync(command.Request!, cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Status: {result.Status}");
    Console.WriteLine(result.ResponseText);
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
