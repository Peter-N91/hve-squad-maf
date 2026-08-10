using HveSquad.AgentFramework;
using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Workflows;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: inspect <artifactRoot> [additionalRoots...]");
    Console.Error.WriteLine(@"  example: inspect C:\Solutions\hve-squad\.github");
    return 1;
}

var artifacts = SquadArtifactLoader.Load(args);

Console.WriteLine($"Charters:  {artifacts.Charters.Count}");
Console.WriteLine($"Skills:    {artifacts.SkillDirectories.Count}");
Console.WriteLine($"Roster:    {artifacts.Roster.Members.Count} member(s)");
Console.WriteLine();

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
    Console.WriteLine($"{unresolved.Count} delegation edge(s) do not resolve. Check that every dependency root was passed.");
}

var stages = new[] { "Squad Researcher", "Squad Lead", "Squad Reviewer" };
if (stages.Any(s => artifacts.FindCharter(s) is null))
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
