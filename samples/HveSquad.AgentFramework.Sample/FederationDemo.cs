using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Sample;

internal static class FederationDemo
{
    internal static async Task<int> RunAsync(SampleCommand command, CancellationToken cancellationToken,
        SquadArtifactSource? source = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var project = Path.GetFullPath(command.Project!);
        if (!Directory.Exists(project) || Directory.EnumerateFileSystemEntries(project).Any())
        {
            throw new InvalidOperationException("The federation demonstration requires an existing empty isolated directory; no case is overwritten.");
        }
        using var client = new ScriptedResearchClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(client);
        services.AddHveSquad(configureHost: options =>
        {
            options.ProjectPath = project;
            options.Version = command.Version!;
            options.NamingPolicy = "skip";
            options.Discovery = "skip";
            options.ApproveAsync = (_, _) => ValueTask.FromResult(true);
        }, source: source ?? command.Source);
        using var provider = services.BuildServiceProvider();
        using var federation = await provider.GetRequiredService<SquadRuntimeFactory>().CreateFederationAsync(cancellationToken);
        await output.WriteLineAsync("This explicit synthetic demo confirms local research/review members and local gates only. No production authentication, model quality, remote effect or publication is implied.");
        var initialized = await federation.InitializeAsync([
            new() { Name = "research", Description = "Original local demonstration research", Patterns = ["research"] },
            new() { Name = "review", Description = "Independent local review outcome", Patterns = ["review"] },
        ], cancellationToken);
        if (initialized.Status != SquadRunStatus.Completed)
        {
            throw new InvalidOperationException(initialized.Diagnostic);
        }
        var result = await federation.RunAsync("Research the bounded synthetic case.", new() { Target = "research" }, cancellationToken);
        await output.WriteLineAsync("Federation status: " + result.Status);
        await output.WriteLineAsync("Run ID: " + result.RunId);
        foreach (var member in result.Members)
        {
            await output.WriteLineAsync("Member " + member.Member + ": " + member.Result?.Status);
        }
        await output.WriteLineAsync(result.Diagnostic);
        return result.Status == SquadRunStatus.Completed &&
            result.Members.Single().Result?.Status == SquadRunStatus.ResearchCompleted ? 0 : 1;
    }
}
