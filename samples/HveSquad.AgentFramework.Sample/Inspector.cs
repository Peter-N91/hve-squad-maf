using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using HveSquad.AgentFramework.Workflows;

namespace HveSquad.AgentFramework.Sample;

internal static class Inspector
{
    internal static async Task<int> RunAsync(
        SquadArtifactSource source,
        CancellationToken cancellationToken)
    {
        var resolved = await source.ResolveAsync(cancellationToken);
        Console.WriteLine($"Origin:    {resolved.Origin}");
        if (resolved.Release is { } release)
        {
            Console.WriteLine($"Release:   {release.Tag}");
            Console.WriteLine($"Commit:    {release.CommitSha}");
        }

        var artifacts = SquadArtifactLoader.Load(resolved.Roots, resolved.RosterPath);
        Console.WriteLine($"Charters:  {artifacts.Charters.Count}");
        Console.WriteLine($"Skills:    {artifacts.SkillDirectories.Count}");
        Console.WriteLine($"Rules:     {artifacts.Instructions.Count}");
        Console.WriteLine($"Roster:    {artifacts.Roster.Members.Count} member(s)");
        Console.WriteLine();

        if (resolved.Release is not null)
        {
            var catalog = SquadCatalog.Load(artifacts);
            var roster = catalog.ResolveRoster("default");
            catalog.ValidateRoster(roster);
            Console.WriteLine($"Profiles:  {string.Join(", ", catalog.Profiles.Select(profile => profile.Name))}");
            Console.WriteLine($"Packs:     {string.Join(", ", catalog.Packs.Select(pack => pack.Name))}");
            Console.WriteLine($"Default:   {string.Join(", ", roster.Select(member => member.Role))}");
        }

        foreach (var warning in artifacts.Warnings)
        {
            Console.WriteLine($"WARN  {warning}");
        }

        var unresolved = artifacts.FindUnresolvedDelegations();
        foreach (var edge in unresolved)
        {
            Console.WriteLine($"EDGE  {edge}");
        }

        if (unresolved.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{unresolved.Count} declared delegates are unavailable (including opt-in external packs). " +
                "Selected roster roles are validated separately; unavailable agents are never substituted.");
        }

        var stages = new[] { "Squad Researcher", "Squad Lead", "Squad Reviewer" };
        if (stages.Any(stage => artifacts.FindCharter(stage) is null))
        {
            Console.WriteLine();
            Console.WriteLine("Skipping graph render: the staged charters are not all present.");
            return 0;
        }

        var factory = new SquadAgentFactory(new OfflineChatClient());
        var workflow = SquadWorkflowBuilder.BuildStagedWorkflow(artifacts, factory, stages);
        Console.WriteLine();
        Console.WriteLine("Staged workflow:");
        Console.WriteLine(SquadWorkflowBuilder.ToMermaid(workflow));
        return 0;
    }
}
