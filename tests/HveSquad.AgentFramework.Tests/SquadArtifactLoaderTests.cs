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
}
