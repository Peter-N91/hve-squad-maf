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

    /// <summary>Creates the confirmed federation lifecycle entry point using the same caller-owned client and host capabilities.</summary>
    /// <param name="cancellationToken">Cancellation for artifact acquisition.</param>
    /// <returns>An uninitialized controller; named Init/Promotion/Expansion still require exact confirmation.</returns>
    public Task<SquadFederation> CreateFederationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatClient = _services.GetService(typeof(IChatClient)) as IChatClient
            ?? throw new InvalidOperationException(
                "AddHveSquad requires the consumer to register an existing Microsoft.Extensions.AI.IChatClient.");
        return SquadFederation.CreateAsync(chatClient, _optionsFactory(), _source, cancellationToken);
    }

    /// <summary>Creates state-read-only helpers without resolving a chat client or acquiring/installing artifacts.</summary>
    /// <param name="configuration">Explicit host case disclosure and optional local renderer capabilities.</param>
    /// <returns>An independently owned reader; it initializes or repairs no state.</returns>
    public SquadReports CreateReports(SquadReportConfiguration configuration)
    {
        var options = _optionsFactory();
        return new(options.ProjectPath, configuration, options.SquadRoot, options.TimeProvider);
    }

    /// <summary>Creates consumer-local Scribe memory without resolving a model or acquiring artifacts.</summary>
    /// <param name="configuration">Explicit case/role memory policy.</param>
    /// <returns>A store over the configured native root.</returns>
    public SquadRoleMemory CreateMemory(SquadMemoryConfiguration configuration)
    {
        var options = _optionsFactory();
        return new(options.ProjectPath, configuration, options.SquadRoot, options.TimeProvider);
    }

    /// <summary>Creates human-only learning without resolving a model, acquiring artifacts or granting a capability from configuration text.</summary>
    /// <param name="configuration">Host-authenticated human learning and optional remote capabilities.</param>
    /// <returns>A helper over the configured native root.</returns>
    public SquadLearning CreateLearning(SquadLearningConfiguration configuration)
    {
        var options = _optionsFactory();
        return new(options.ProjectPath, configuration, options.SquadRoot, options.TimeProvider);
    }
}
