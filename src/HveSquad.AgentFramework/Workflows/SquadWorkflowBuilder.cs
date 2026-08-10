using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace HveSquad.AgentFramework.Workflows;

/// <summary>
/// Translates a squad roster and the charters' <c>agents:</c> allowlists into Microsoft Agent
/// Framework workflow graphs.
/// </summary>
public static class SquadWorkflowBuilder
{
    /// <summary>
    /// Builds a sequential workflow over the named charters, in the order given. This mirrors the
    /// squad's research to plan to implement to review stage order.
    /// </summary>
    public static Workflow BuildStagedWorkflow(
        SquadArtifacts artifacts,
        SquadAgentFactory factory,
        IEnumerable<string> charterNames)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(charterNames);

        var agents = ResolveCharters(artifacts, charterNames)
            .Select(factory.Create)
            .ToList();

        if (agents.Count < 2)
        {
            throw new ArgumentException("A staged workflow needs at least two charters.", nameof(charterNames));
        }

        return AgentWorkflowBuilder.BuildSequential(agents);
    }

    /// <summary>
    /// Builds a handoff workflow rooted at <paramref name="coordinatorName"/>, with one edge per
    /// entry in that charter's <c>agents:</c> allowlist. Delegation permissions declared in the
    /// artifacts become the graph's edges.
    /// </summary>
    public static Workflow BuildDelegationWorkflow(
        SquadArtifacts artifacts,
        SquadAgentFactory factory,
        string coordinatorName)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(factory);

        var coordinator = artifacts.FindCharter(coordinatorName)
            ?? throw new ArgumentException($"No charter named '{coordinatorName}'.", nameof(coordinatorName));

        if (coordinator.DelegateAgents.Count == 0)
        {
            throw new ArgumentException(
                $"Charter '{coordinator.Name}' declares no 'agents:' allowlist, so it has no delegation edges.",
                nameof(coordinatorName));
        }

        var coordinatorAgent = factory.Create(coordinator);
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(coordinatorAgent);

        foreach (var target in ResolveCharters(artifacts, coordinator.DelegateAgents))
        {
            var targetAgent = factory.Create(target);
            builder.WithHandoff(coordinatorAgent, targetAgent, target.Description);
            builder.WithHandoff(targetAgent, coordinatorAgent, $"Return control to {coordinator.Name}.");
        }

        return builder.Build();
    }

    /// <summary>Renders a workflow as a Mermaid diagram for documentation or review.</summary>
    public static string ToMermaid(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return WorkflowVisualizer.ToMermaidString(workflow);
    }

    private static IEnumerable<AgentCharter> ResolveCharters(SquadArtifacts artifacts, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            yield return artifacts.FindCharter(name)
                ?? throw new ArgumentException($"No charter named '{name}'.", nameof(names));
        }
    }
}
