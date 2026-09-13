using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sample;

internal static class ConsoleCallbacks
{
    private static readonly JsonSerializerOptions DisplayJson = new() { WriteIndented = true };

    internal static async ValueTask<bool> ApproveAsync(
        SquadApprovalRequest request,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"APPROVAL: {request.Kind}");
        Console.WriteLine(request.Description);
        Write("Role", request.Role);
        Write("Tool", request.ToolName);
        Write("Tool arguments", request.ArgumentsJson);
        WriteJson("Initialization proposal", request.Initialization);
        WriteJson("Routing proposal", request.Routing);
        WriteJson("Evidence", request.Evidence);
        Console.Write("Approve exactly this proposal? Type 'yes' to approve [no]: ");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static async ValueTask<string?> AskAsync(
        SquadQuestion question,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"QUESTION from {question.Role}:");
        Console.WriteLine(question.Question);
        Console.Write("Answer (blank leaves unanswered): ");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(answer) ? null : answer;
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
