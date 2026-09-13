using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Tests;

public class SquadArtifactLoaderTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void LoadCharter_ReadsFrontmatterAndBody()
    {
        var charter = SquadArtifactLoader.LoadCharter(Path.Combine(FixtureDir, "test-researcher.agent.md"));

        Assert.Equal("Test Researcher", charter.Name);
        Assert.Equal("Fixture charter used to verify frontmatter and body parsing", charter.Description);
        Assert.False(charter.UserInvocable);
        Assert.Equal(["Claude Sonnet 5 (copilot)", "GPT-5.6 Terra (copilot)"], charter.ModelPreferences);
        Assert.Equal(["Test Worker"], charter.DelegateAgents);
        Assert.StartsWith("# Test Researcher", charter.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCharter_DefaultsUserInvocableToTrue()
    {
        var charter = SquadArtifactLoader.LoadCharter(Path.Combine(FixtureDir, "test-worker.agent.md"));

        Assert.True(charter.UserInvocable);
    }

    [Fact]
    public void LoadCharter_AcceptsScalarModelValue()
    {
        var charter = SquadArtifactLoader.LoadCharter(Path.Combine(FixtureDir, "test-worker.agent.md"));

        Assert.Equal(["GPT-5.6 Terra (copilot)"], charter.ModelPreferences);
    }

    [Fact]
    public void LoadCharter_KeepsInvocationFlagsIndependentAndReadsTools()
    {
        using var tree = new TemporaryArtifactTree();
        var path = tree.Write(
            "entry.agent.md",
            """
            ---
            name: Entry
            user-invocable: true
            disable-model-invocation: true
            tools:
              - read
              - execute
            ---
            Body
            """);

        var charter = SquadArtifactLoader.LoadCharter(path);

        Assert.True(charter.UserInvocable);
        Assert.True(charter.DisableModelInvocation);
        Assert.Equal(["read", "execute"], charter.Tools);
    }

    [Fact]
    public void LoadRoster_ParsesMembersTableOnly()
    {
        var roster = SquadArtifactLoader.LoadRoster(Path.Combine(FixtureDir, "team.md"));

        Assert.Equal(2, roster.Members.Count);

        var lead = roster.FindByRole("lead");
        Assert.NotNull(lead);
        Assert.Equal("Alpha", lead.MemberName);
        Assert.Equal("Test Researcher", lead.PrimaryAgent);
        Assert.Equal(["RPI Planner", "Squad Lead"], lead.AlternateAgents);
        Assert.Equal(".copilot-tracking/plans/", lead.DeliverableRoot);
        Assert.Equal("lead/Alpha", lead.Key);
    }

    [Fact]
    public void LoadRoster_TreatsEmptyMemberNameAsNull()
    {
        var roster = SquadArtifactLoader.LoadRoster(Path.Combine(FixtureDir, "team.md"));

        var developer = roster.FindByRole("developer");
        Assert.NotNull(developer);
        Assert.Null(developer.MemberName);
        Assert.Empty(developer.AlternateAgents);
        Assert.Equal("developer", developer.Key);
    }

    [Fact]
    public void LoadRoster_PreservesOwnerAndSelectionCue()
    {
        using var tree = new TemporaryArtifactTree();
        var path = tree.Write(
            "team.md",
            """
            ## Members

            | Role | Member Name | Agent Name (Primary) | Alternate Agents | Selection Cue |
            |------|-------------|----------------------|------------------|---------------|
            | tester | Delta | Reviewer | Security Reviewer | security diff → Security Reviewer |
            | scribe |  | Scribe | — | — |
            """);

        var roster = SquadArtifactLoader.LoadRoster(path);

        var tester = roster.Resolve("tester", "Delta");
        Assert.NotNull(tester);
        Assert.Equal("security diff → Security Reviewer", tester.SelectionCue);
        var scribe = roster.FindByRole("scribe");
        Assert.NotNull(scribe);
        Assert.Empty(scribe.AlternateAgents);
        Assert.Null(scribe.SelectionCue);
    }

    [Fact]
    public void Load_DiscoversChartersAndResolvesDelegations()
    {
        var artifacts = SquadArtifactLoader.Load(FixtureDir, Path.Combine(FixtureDir, "team.md"));

        Assert.Equal(2, artifacts.Charters.Count);
        Assert.Empty(artifacts.Warnings);
        Assert.Empty(artifacts.FindUnresolvedDelegations());
        Assert.NotNull(artifacts.FindCharter("test researcher"));
    }

    [Fact]
    public void Load_ReturnsEmptyRosterWhenTeamFileIsAbsent()
    {
        var artifacts = SquadArtifactLoader.Load(FixtureDir, Path.Combine(FixtureDir, "does-not-exist.md"));

        Assert.Empty(artifacts.Roster.Members);
    }

    [Fact]
    public void Load_ThrowsWhenRootMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => SquadArtifactLoader.Load(Path.Combine(FixtureDir, "nope")));
    }

    [Fact]
    public void FindUnresolvedDelegations_ReportsDanglingEdge()
    {
        var artifacts = new SquadArtifacts
        {
            Charters =
            [
                new AgentCharter
                {
                    Name = "A",
                    Instructions = "body",
                    SourcePath = "a.agent.md",
                    DelegateAgents = ["Ghost"],
                },
            ],
            Roster = SquadRoster.Empty,
            SkillDirectories = [],
            Warnings = [],
        };

        var unresolved = artifacts.FindUnresolvedDelegations();

        Assert.Single(unresolved);
        Assert.Contains("Ghost", unresolved[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DiscoversInstructionsInFlatAndNestedLayoutsWithRootPrecedence()
    {
        using var source = new TemporaryArtifactTree();
        using var deployed = new TemporaryArtifactTree();
        source.Write(
            Path.Combine("instructions", "squad", "squad-roster.instructions.md"),
            Instruction("source", "source body"));
        source.Write(
            Path.Combine("instructions", "squad-state.instructions.md"),
            Instruction("state", "state body"));
        deployed.Write(
            Path.Combine("instructions", "squad-roster.instructions.md"),
            Instruction("deployed", "deployed body"));

        var artifacts = SquadArtifactLoader.Load([source.Root, deployed.Root]);

        Assert.Equal(2, artifacts.Instructions.Count);
        var roster = Assert.Single(
            artifacts.Instructions,
            i => i.FileName == "squad-roster.instructions.md");
        Assert.Equal("source", roster.Description);
        Assert.Equal("source body", roster.Body);
    }

    [Fact]
    public void Load_DiscoversSkillByMetadataWhenFolderIsRenamed()
    {
        using var tree = new TemporaryArtifactTree();
        tree.Write(
            Path.Combine("skills", "renamed-by-apm", "SKILL.md"),
            """
            ---
            name: squad
            description: released squad skill
            ---
            # Squad
            """);

        var artifacts = SquadArtifactLoader.Load(tree.Root);

        var skill = Assert.Single(artifacts.Skills);
        Assert.Equal("squad", skill.Name);
        Assert.EndsWith(
            Path.Combine("skills", "renamed-by-apm"),
            skill.DirectoryPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Instruction(string description, string body) =>
        $"---{Environment.NewLine}description: {description}{Environment.NewLine}" +
        $"applyTo: '**'{Environment.NewLine}---{Environment.NewLine}{body}";

    private sealed class TemporaryArtifactTree : IDisposable
    {
        public TemporaryArtifactTree()
        {
            Root = Path.Combine(
                AppContext.BaseDirectory,
                "generated-artifacts",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
