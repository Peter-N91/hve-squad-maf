using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Sample;

internal static class DurableApprovalDemo
{
    internal static async Task<int> RunAsync(SampleCommand command, CancellationToken cancellationToken,
        SquadArtifactSource? source = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var project = Path.GetFullPath(command.Project!);
        if (!Directory.Exists(project))
        {
            throw new DirectoryNotFoundException("Create the explicit isolated demo directory first.");
        }
        using var client = new ScriptedResearchClient();
        var options = new SquadRuntimeOptions
        {
            ProjectPath = project,
            SquadRoot = command.SquadRoot,
            Version = command.Version!,
            NamingPolicy = "skip",
            HumanResponseTimeout = command.HumanResponseTimeout ?? TimeSpan.FromDays(1),
            HumanApprovalChannel = ConsoleCallbacks.CreateDurableChannel(project, command.SquadRoot, output),
            ApproveAsync = (request, _) => ValueTask.FromResult(request.Kind == SquadApprovalKind.Initialization),
        };
        using var runtime = await SquadRuntime.CreateAsync(client, options, source ?? command.Source, cancellationToken);
        SquadRunResult result;
        if (command.DemoOperation == "start")
        {
            await output.WriteLineAsync("Explicit demo start confirms only this local default/skip roster. Every subsequent action still requires its authenticated human response.");
            result = await runtime.RunAsync("Research the local demonstration. Stop after research.", cancellationToken);
            if (result.Status != SquadRunStatus.ApprovalRequired || result.PendingHumanActions is not { Count: 1 })
            {
                throw new InvalidOperationException("The demonstration did not produce its exact durable suspended gate: " + result.ResponseText);
            }
        }
        else if (command.DemoOperation == "resume")
        {
            var pending = await runtime.GetPendingHumanActionsAsync(cancellationToken);
            if (pending.Count != 1 || pending[0].Status != "pending" || command.HumanReply is null)
            {
                throw new InvalidOperationException("Demo resume requires one exact pending action and an explicit human reply.");
            }
            var response = SquadHumanResponse.FromCommand(pending[0], command.HumanReply, Guid.NewGuid().ToString("N"));
            var submission = await runtime.SubmitHumanResponseAsync(response, cancellationToken);
            if (!submission.Accepted)
            {
                throw new InvalidOperationException(submission.Diagnostic);
            }
            result = await runtime.ResumeAsync(pending[0].Binding.RunId, cancellationToken);
        }
        else
        {
            throw new ArgumentException("Demo operation must be start or resume.", nameof(command));
        }
        await output.WriteLineAsync("Run ID: " + result.RunId);
        await output.WriteLineAsync("Status: " + result.Status);
        await output.WriteLineAsync("Scripted MAF evidence; not paid-model quality or IndustryIQ readiness.");
        await output.WriteLineAsync(JsonSerializer.Serialize(result.PendingHumanActions));
        return result.Status is SquadRunStatus.ApprovalRequired or SquadRunStatus.ResearchCompleted or SquadRunStatus.Stopped ? 0 : 1;
    }

}
