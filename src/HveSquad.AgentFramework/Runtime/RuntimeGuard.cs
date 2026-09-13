using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace HveSquad.AgentFramework.Runtime;

internal sealed class RuntimeStopException(SquadRunStatus status, string message) : Exception(message)
{
    internal SquadRunStatus Status { get; } = status;
}

internal sealed class RuntimeGuard(SquadRuntimeOptions options)
{
    private int _dispatches;
    private int _modelCalls;
    private int _toolCalls;
    private RuntimeStopException? _stop;

    internal void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stop is { } stop)
        {
            throw stop;
        }
    }

    internal RuntimeStopException Stop(SquadRunStatus status, string message)
    {
        var stop = new RuntimeStopException(status, message);
        return Interlocked.CompareExchange(ref _stop, stop, null) ?? stop;
    }

    internal void Dispatch(int depth)
    {
        if (depth > options.MaxDepth || Interlocked.Increment(ref _dispatches) > options.MaxDispatches)
        {
            throw Stop(SquadRunStatus.LimitReached, "Dispatch count or nesting depth limit reached.");
        }
    }

    internal void Model(CancellationToken token)
    {
        Check(token);
        if (Interlocked.Increment(ref _modelCalls) > options.MaxModelCalls)
        {
            throw Stop(SquadRunStatus.LimitReached, "Model call limit reached.");
        }
    }

    internal void Tool(CancellationToken token)
    {
        Check(token);
        if (Interlocked.Increment(ref _toolCalls) > options.MaxToolCalls)
        {
            throw Stop(SquadRunStatus.LimitReached, "Tool call limit reached.");
        }
    }
}

internal sealed class RuntimeFunction(
    AIFunction function,
    RuntimeGuard guard,
    Func<AIFunctionArguments, CancellationToken, ValueTask>? before = null,
    Action<AIFunctionArguments, object?>? after = null) : DelegatingAIFunction(function)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            guard.Tool(cancellationToken);
            if (before is not null)
            {
                await before(arguments, cancellationToken).ConfigureAwait(false);
            }

            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            after?.Invoke(arguments, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeStopException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw guard.Stop(SquadRunStatus.Failed, $"Tool '{Name}' failed: {ex.Message}");
        }
    }
}

internal sealed class RuntimeChatClient(IChatClient inner, RuntimeGuard guard) : DelegatingChatClient(inner)
{
    internal string? ObservedModel { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        guard.Model(cancellationToken);
        options = options?.Clone();
        if (options?.Tools is not null)
        {
            options.Tools = options.Tools.Where(t => t.Name != AgentSkillsProvider.RunSkillScriptToolName).ToList();
        }

        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        guard.Check(cancellationToken);
        foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            if (call.Name == AgentSkillsProvider.RunSkillScriptToolName)
            {
                throw guard.Stop(SquadRunStatus.Unsupported, "Skill script execution is not a native runtime capability.");
            }

            if (options?.Tools?.Any(t => t.Name == call.Name) != true)
            {
                throw guard.Stop(SquadRunStatus.Blocked, $"Function '{call.Name}' is not registered for this agent.");
            }

            if (call.Name is AgentSkillsProvider.LoadSkillToolName or AgentSkillsProvider.ReadSkillResourceToolName)
            {
                guard.Tool(cancellationToken);
            }
        }

        ObservedModel = response.ModelId ?? ObservedModel;
        return response;
    }

}
