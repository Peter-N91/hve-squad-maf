using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Sources;
using HveSquad.AgentFramework.Workflows;

// With no arguments, discover an installed project by walking up from the working directory.
SquadArtifactSource source = args.Length == 0
    ? new ProjectArtifactSource()
    : new DirectoryArtifactSource(args);

SquadArtifactRoots resolved;
try
{
    resolved = await source.ResolveAsync();
}
catch (SquadArtifactsNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"Origin:    {resolved.Origin}");

var artifacts = SquadArtifactLoader.Load(resolved.Roots, resolved.RosterPath);

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
    Console.WriteLine($"{unresolved.Count} delegation edge(s) do not resolve; the artifact tree is incomplete.");
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
