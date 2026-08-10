using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework;

/// <summary>A squad loaded from markdown artifacts and materialized onto Agent Framework.</summary>
public sealed class Squad
{
    public required SquadArtifacts Artifacts { get; init; }

    public required IReadOnlyDictionary<string, AIAgent> Agents { get; init; }

    /// <summary>The staged workflow, or null when fewer than two stages were configured.</summary>
    public Workflow? Workflow { get; init; }

    public string ToMermaid() => Workflow is null
        ? throw new InvalidOperationException("No workflow was built; add at least two stages.")
        : SquadWorkflowBuilder.ToMermaid(Workflow);
}

/// <summary>
/// Fluent entry point. Charters and the roster stay authoritative; this builder only selects which
/// of them participate and how they are wired.
/// </summary>
public sealed class HveSquadBuilder
{
    private readonly List<StageSelector> _stages = [];
    private readonly SquadAgentFactoryOptions _factoryOptions = new();
    private string? _artifactRoot;
    private string? _rosterPath;
    private IChatClient? _chatClient;

    private readonly record struct StageSelector(string Value, bool IsRole);

    /// <param name="artifactRoot">A <c>.github</c> or <c>squad-src/.github</c> directory.</param>
    /// <param name="rosterPath">Optional path to <c>team.md</c>.</param>
    public HveSquadBuilder FromArtifacts(string artifactRoot, string? rosterPath = null)
    {
        _artifactRoot = artifactRoot;
        _rosterPath = rosterPath;
        return this;
    }

    public HveSquadBuilder WithChatClient(IChatClient chatClient)
    {
        _chatClient = chatClient;
        return this;
    }

    /// <summary>Selects the chat client per charter, so <c>model:</c> preferences can route providers.</summary>
    public HveSquadBuilder WithChatClientSelector(Func<AgentCharter, IChatClient?> selector)
    {
        _factoryOptions.ChatClientSelector = selector;
        return this;
    }

    public HveSquadBuilder WithOpenTelemetry(string? sourceName = null)
    {
        _factoryOptions.EnableOpenTelemetry = true;

        if (sourceName is not null)
        {
            _factoryOptions.OpenTelemetrySourceName = sourceName;
        }

        return this;
    }

    public HveSquadBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _factoryOptions.LoggerFactory = loggerFactory;
        return this;
    }

    /// <summary>Adds a workflow stage by charter name, for example <c>Squad Researcher</c>.</summary>
    public HveSquadBuilder WithAgent(string charterName)
    {
        _stages.Add(new StageSelector(charterName, IsRole: false));
        return this;
    }

    /// <summary>Adds a workflow stage by roster role, for example <c>lead</c>.</summary>
    public HveSquadBuilder WithRole(string role)
    {
        _stages.Add(new StageSelector(role, IsRole: true));
        return this;
    }

    public Squad Build()
    {
        if (_artifactRoot is null)
        {
            throw new InvalidOperationException($"Call {nameof(FromArtifacts)} before {nameof(Build)}.");
        }

        if (_chatClient is null)
        {
            throw new InvalidOperationException($"Call {nameof(WithChatClient)} before {nameof(Build)}.");
        }

        var artifacts = SquadArtifactLoader.Load(_artifactRoot, _rosterPath);
        var factory = new SquadAgentFactory(_chatClient, _factoryOptions);

        var stageNames = _stages.Select(s => Resolve(artifacts, s)).ToList();

        var workflow = stageNames.Count >= 2
            ? SquadWorkflowBuilder.BuildStagedWorkflow(artifacts, factory, stageNames)
            : null;

        return new Squad
        {
            Artifacts = artifacts,
            Agents = factory.CreateAll(artifacts),
            Workflow = workflow,
        };
    }

    private static string Resolve(SquadArtifacts artifacts, StageSelector stage)
    {
        if (!stage.IsRole)
        {
            return stage.Value;
        }

        var entry = artifacts.Roster.FindByRole(stage.Value)
            ?? throw new InvalidOperationException(
                $"Role '{stage.Value}' is not in the roster. The roster is written at runtime by the " +
                "coordinator; pass its path to FromArtifacts, or select the charter by name instead.");

        return entry.PrimaryAgent;
    }
}
