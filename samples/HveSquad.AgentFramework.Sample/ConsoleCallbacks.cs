using System.Text.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sample;

internal static class ConsoleCallbacks
{
    private static readonly JsonSerializerOptions DisplayJson = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Interaction = new(1, 1);

    internal static async ValueTask<bool> ApproveAsync(
        SquadApprovalRequest request,
        CancellationToken cancellationToken)
    {
        await Interaction.WaitAsync(cancellationToken);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"APPROVAL: {request.Kind}");
            DisplayApproval(request);
            Console.Write("Approve exactly this proposal? Type 'yes' to approve [no]: ");
            var answer = await Console.In.ReadLineAsync(cancellationToken);
            return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Interaction.Release();
        }
    }

    internal static InChatApprovalChannel CreateDurableChannel(string project, string squadRoot, TextWriter writer,
        SquadLearningInvocation? learningInvocation = null)
    {
        var root = Path.GetFullPath(Path.Combine(project, squadRoot.Replace('/', Path.DirectorySeparatorChar)));
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)));
        var processAccount = Environment.MachineName + "\\" + Environment.UserName;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, processAccount, ClaimValueTypes.String, "operating-system-process")],
            "operating-system-process"));
        // The helper's root binding is accepted only after this exact human initiation.
        string? learningRootBinding = null;
        return new("console-process",
            _ => ValueTask.FromResult(principal),
            (actor, action, _) => ValueTask.FromResult(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value == processAccount &&
                (learningInvocation is null ? action.Binding.RootSha256 == rootHash :
                    action.Binding is { Step: "Squad Learn", Mode: "human-only-learning" } &&
                    action.Binding.RootSha256 == learningRootBinding)),
            writer, authorizeLearningInvocation: (actor, access, _) =>
            {
                var authorized = learningInvocation is not null && access.Invocation == learningInvocation &&
                    actor.FindFirst(ClaimTypes.NameIdentifier)?.Value == processAccount &&
                    Path.GetFullPath(Path.Combine(project, access.SquadRoot.Replace('/', Path.DirectorySeparatorChar)))
                        .Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (authorized)
                {
                    learningRootBinding = access.RootSha256;
                }
                return ValueTask.FromResult(authorized);
            });
    }

    internal static async Task SubmitPendingAsync(SquadRuntime runtime, string? reply, CancellationToken cancellationToken)
    {
        var pending = (await runtime.GetPendingHumanActionsAsync(cancellationToken))
            .Where(action => action.Status == "pending").ToArray();
        if (reply is not null && pending.Length != 1)
        {
            throw new InvalidOperationException("An explicit command-line reply requires exactly one pending action; it cannot approve a batch.");
        }
        foreach (var action in pending)
        {
            var details = await runtime.ReadHumanActionAsync(action.Binding.Id, cancellationToken);
            await Interaction.WaitAsync(cancellationToken);
            string? command;
            try
            {
                Console.WriteLine($"EXACT HUMAN ACTION: {action.Binding.Id}");
                if (details.Approval is { } approval)
                {
                    DisplayApproval(approval);
                }
                else if (details.Question is { } question)
                {
                    Console.WriteLine(JsonSerializer.Serialize(question, DisplayJson));
                }
                if (reply is not null)
                {
                    command = reply;
                }
                else
                {
                    Console.Write("Command /approve, /answer: text, /changes: note, or /stop (blank stays pending): ");
                    command = await Console.In.ReadLineAsync(cancellationToken);
                }
            }
            finally
            {
                Interaction.Release();
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                Console.WriteLine("No response supplied; the exact human action remains pending.");
                continue;
            }
            var response = SquadHumanResponse.FromCommand(action, command, Guid.NewGuid().ToString("N"));
            var result = await runtime.SubmitHumanResponseAsync(response, cancellationToken);
            Console.WriteLine(result.Diagnostic);
            if (!result.Accepted)
            {
                throw new InvalidOperationException("The human response was rejected; no action was authorized.");
            }
        }
    }

    private static void DisplayApproval(SquadApprovalRequest request)
    {
        Console.WriteLine(request.Description);
        Write("Role", request.Role);
        Write("Tool", request.ToolName);
        Write("Tool arguments", request.ArgumentsJson);
        WriteJson("Initialization proposal", request.Initialization);
        WriteJson("Routing proposal", request.Routing);
        WriteJson("Model policy", request.ModelRouting);
        WriteJson("Cost preflight", request.CostPreflight);
        WriteJson("Council proposal", request.Council);
        WriteJson("Council verdict and conditions", request.CouncilVerdict);
        WriteJson("Plan-derived producing owners and outputs", request.ProductionPlan);
        WriteJson("Projected project paths", request.ProjectWritePaths);
        WriteJson("Evidence", request.Evidence);
    }

    internal static async ValueTask<string?> AskAsync(
        SquadQuestion question,
        CancellationToken cancellationToken)
    {
        await Interaction.WaitAsync(cancellationToken);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"QUESTION from {question.Role}:");
            Console.WriteLine(question.Question);
            if (question.Choices is { } choices)
            {
                for (var index = 0; index < choices.Count; index++)
                {
                    Console.WriteLine($"{index + 1}. {choices[index]}");
                }
            }
            Console.Write("Answer (blank leaves unanswered): ");
            var answer = await Console.In.ReadLineAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }
        finally
        {
            Interaction.Release();
        }
    }

    private static void Write(string label, string? value)
    {
        if (value is not null)
        {
            Console.WriteLine($"{label}: {value}");
        }
    }

    private static void WriteJson<T>(string label, T? value)
    {
        if (value is not null)
        {
            Console.WriteLine($"{label}:");
            Console.WriteLine(JsonSerializer.Serialize(value, DisplayJson));
        }
    }
}
