using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeRoutingTests
{
    private static void Rules(RuntimeTestHost host, string rows) =>
        host.Write("instructions/squad/squad-routing.instructions.md", """
            ## Default Routing Rules
            | Pattern / Keyword | Role(s) | Autonomy Tier | Parallel-Eligible |
            | --- | --- | --- | --- |
            """ + "\n" + rows);

    private static void AddQaCatalogRole(RuntimeTestHost host, bool selected)
    {
        host.WriteCharter("qa-engineer", tools: "edit/project");
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace("## Squad Profiles",
            "| qa-engineer | Test qa-engineer | — | Original QA fixture | .copilot-tracking/qa/ |\n\n## Squad Profiles", StringComparison.Ordinal));
        var coordinator = Path.Combine(host.Artifacts, "agents", "coordinator.agent.md");
        File.WriteAllText(coordinator, File.ReadAllText(coordinator).Replace("agents:\n",
            "agents:\n  - Test qa-engineer\n", StringComparison.Ordinal));
        if (selected)
        {
            host.Options.AdditionalRoles.Add("qa-engineer");
            host.Options.Tools[0] = host.Options.Tools[0] with { AllowedRoles = ["qa-engineer"] };
        }
    }

    private static void Alternate(RuntimeTestHost host, bool allowed = true)
    {
        host.Write("agents/alternate.agent.md",
            "---\nname: Alternate researcher\nuser-invocable: false\ndisable-model-invocation: false\n---\nOriginal standalone alternate fixture.\n");
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace(
            "| researcher | Test researcher | — | testing only |",
            "| researcher | Test researcher | Alternate researcher | Blue stamp collection → Alternate researcher; otherwise primary research |",
            StringComparison.Ordinal));
        if (allowed)
        {
            var coordinator = Path.Combine(host.Artifacts, "agents", "coordinator.agent.md");
            File.WriteAllText(coordinator, File.ReadAllText(coordinator).Replace("agents:\n",
                "agents:\n  - Alternate researcher\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task GivenContainedSpecificIntent_WhenRouting_SelectsItsOwnerNotTheGenericPlanKeyword()
    {
        using var host = new RuntimeTestHost();
        AddQaCatalogRole(host, selected: true);
        Rules(host, "| plan | lead | confirm | no |\n| test plan | qa-engineer | confirm | no |");
        host.QueuePipeline("developer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.Client.Call("apply_change", []);
        host.QueueStage("qa-engineer");
        host.QueueStage("tester");
        host.Client.Text("Actual QA outcome.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Deliver a test plan.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("qa-engineer", host.Approvals.Single(approval => approval.Kind == SquadApprovalKind.Routing).Routing!.ProducingRole);
        Assert.Equal("qa-engineer", result.Evidence[2].Role);
        Assert.Equal(1, host.Writes);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Role == "developer");
        var table = File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "routing.md"));
        Assert.Contains("| Pattern / Keyword | Role(s) | Autonomy Tier | Parallel-Eligible |", table, StringComparison.Ordinal);
        Assert.Contains("| test plan | qa-engineer | confirm | no |", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenSpecificOffRosterIntent_WhenRouting_EscalatesWithoutSubstitutingGenericOwner()
    {
        using var host = new RuntimeTestHost();
        AddQaCatalogRole(host, selected: false);
        Rules(host, "| plan | lead | confirm | no |\n| test plan | qa-engineer | confirm | no |");
        host.QueuePipeline("developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Deliver a test plan.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("qa-engineer", result.ResponseText, StringComparison.Ordinal);
        Assert.Contains("off-roster", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenDisjointCompetingIntents_WhenNoHumanChoiceExists_StopsRatherThanRankingByLength()
    {
        using var host = new RuntimeTestHost();
        Rules(host, "| code | developer | confirm | no |\n| review | tester | auto | yes |");
        host.QueuePipeline("developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Review code.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Contains("different outcomes", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenDisjointCompetingIntents_WhenHumanSelectsReadOwner_PerformsOnlyFocusedReview()
    {
        using var host = new RuntimeTestHost();
        Rules(host, "| code | developer | confirm | no |\n| review | tester | auto | yes |");
        File.WriteAllText(Path.Combine(host.Project, "change.diff"), "Actual bounded diff input.");
        host.Options.InputPaths.Add("change.diff");
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("tester");
        host.QueuePipeline("developer");
        host.QueueStage("tester");
        host.Client.Text("Reviewed actual input.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Review code.");

        Assert.True(result.Status == SquadRunStatus.ReviewCompleted, result.ResponseText);
        Assert.Equal("tester", Assert.Single(result.Evidence).Role);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenEscalateOnlyPattern_WhenRouting_DoesNotDispatchItsRole()
    {
        using var host = new RuntimeTestHost();
        Rules(host, "| GitLab pipeline | product-owner | escalate | no |");
        host.QueuePipeline("product-owner");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Inspect this GitLab pipeline.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("escalate-only", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.False(File.Exists(Path.Combine(host.Project, ".copilot-tracking", "squad", "history", "Test product-owner.md")));
    }

    [Fact]
    public async Task GivenLiteralPatternSubstring_WhenRouting_DoesNotMatchInsideAnUnrelatedWord()
    {
        using var host = new RuntimeTestHost();
        Rules(host, "| code | developer | confirm | no |");
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the decoder notes.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("technical-writer", result.Evidence[2].Role);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenMatchingCanonicalAlternateCue_WhenDispatching_RecordsItsLiteralAgentAndRationale()
    {
        using var host = new RuntimeTestHost();
        Alternate(host);
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Document blue stamp collection.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("Alternate researcher", result.Evidence[0].Agent);
        Assert.Contains("Blue stamp collection", result.Evidence[0].AgentSelectionRationale!, StringComparison.Ordinal);
        Assert.EndsWith("Alternate researcher.md", result.Evidence[0].HistoryPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenNoMatchingAlternateCue_WhenDispatching_PreservesPrimary()
    {
        using var host = new RuntimeTestHost();
        Alternate(host);
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the ordinary notes.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("Test researcher", result.Evidence[0].Agent);
        Assert.False(File.Exists(Path.Combine(host.Project, ".copilot-tracking", "squad", "history", "Alternate researcher.md")));
    }

    [Fact]
    public async Task GivenUnjustifiedHostAlternate_WhenDispatching_RefusesItBeforeInvocation()
    {
        using var host = new RuntimeTestHost();
        Alternate(host);
        host.Options.AgentSelector = context => context.Role.Role == "researcher"
            ? new("Alternate researcher", "Invented cue", "Not canonical.") : null;
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the ordinary notes.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("canonical Selection Cue", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenJustifiedAlternateOutsideCoordinatorAllowlist_WhenDispatching_BlocksWithoutWideningPermission()
    {
        using var host = new RuntimeTestHost();
        Alternate(host, allowed: false);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Document blue stamp collection.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("child allowlist", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.False(File.Exists(Path.Combine(host.Project, ".copilot-tracking", "squad", "history", "Alternate researcher.md")));
    }
}
