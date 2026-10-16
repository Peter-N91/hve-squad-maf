using System.Globalization;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Sample;

internal sealed class ScriptedResearchClient : IChatClient
{
    private int _coordinatorCalls;
    private int _researchCalls;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.Tools?.Any(tool => tool.Name == "run_focused") == true)
        {
            return Task.FromResult(_coordinatorCalls++ == 0 ? Call("run_focused", new()
            {
                ["stage"] = "research",
                ["inputPaths"] = Array.Empty<string>(),
                ["requiresCouncil"] = false,
                ["includesAiRisk"] = false,
            }) : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Verified scripted member research completed.")));
        }
        if (options?.Tools?.Any(tool => tool.Name == "write_artifact") != true)
        {
            throw new InvalidOperationException("This scripted demonstration supports only the real research artifact contract.");
        }
        return Task.FromResult(_researchCalls++ switch
        {
            0 => Call("write_artifact", new()
            {
                ["content"] = "# Scripted native demonstration\n\nOriginal synthetic execution evidence, not business research. " +
                    "The host used the exact native run and local gate. No external action or paid model was used.",
            }),
            1 => Call("complete_stage", new()
            {
                ["summary"] = "Scripted research artifact actually written.",
                ["verdict"] = "Completed",
                ["risk"] = "Low",
                ["blockingIssues"] = 0,
            }),
            2 => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Scripted research completed.")),
            _ => throw new InvalidOperationException("Unexpected extra model invocation in the bounded research demonstration."),
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demonstration uses native non-streaming MAF calls.");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, arguments)]));
}
