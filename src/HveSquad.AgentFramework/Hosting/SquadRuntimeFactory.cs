using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>Creates an independently owned runtime for each consumer request.</summary>
public sealed class SquadRuntimeFactory
{
    private readonly IServiceProvider _services;
    private readonly Func<SquadRuntimeOptions> _optionsFactory;
    private readonly SquadArtifactSource? _source;

    internal SquadRuntimeFactory(
        IServiceProvider services,
        Func<SquadRuntimeOptions> optionsFactory,
        SquadArtifactSource? source)
    {
        _services = services;
        _optionsFactory = optionsFactory;
        _source = source;
    }

    /// <summary>
    /// Acquires artifacts asynchronously and creates a new runtime. The resolved chat client remains
    /// caller-owned and is not disposed with the runtime.
    /// </summary>
    public Task<SquadRuntime> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatClient = _services.GetService(typeof(IChatClient)) as IChatClient
            ?? throw new InvalidOperationException(
                "AddHveSquad requires the consumer to register an existing Microsoft.Extensions.AI.IChatClient.");
        return SquadRuntime.CreateAsync(chatClient, _optionsFactory(), _source, cancellationToken);
    }
}
