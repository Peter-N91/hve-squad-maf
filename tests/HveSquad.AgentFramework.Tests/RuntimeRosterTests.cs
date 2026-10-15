using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeRosterTests
{
    private static string StatePath(RuntimeTestHost host) =>
        Path.Combine(host.Project, host.Options.SquadRoot, "state.json");

    private static void AddAliases(RuntimeTestHost host)
    {
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.AppendAllText(path, "\n\n#### Deterministic Alias Wordlist\n\n```text\nAlpha, Beta, Gamma, Delta\n```\n");
    }

    private static void AddProfile(RuntimeTestHost host, string name, string roles)
    {
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("| default | ",
            $"| {name} | {roles} | original test profile |\n| default | ", StringComparison.Ordinal));
    }

    private static void AddUnavailableRole(RuntimeTestHost host)
    {
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("## Squad Profiles",
            "| external | Missing External Expert | — | opt-in expert | outputs/external |\n\n## Squad Profiles", StringComparison.Ordinal) +
            "\n\n## Registered External Cast\n" +
            "| Role | Tier | Resource | Resource Kind | Origin | Install or Entry | License | Prerequisites | Dispatchability Verified | Verified On |\n" +
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n" +
            "| external | opt-in | external-fixture | agent | fixture/provider | approved-fixture-install | MIT | host-fixture-tools | yes | 2026-10-10 |\n");
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("provided")]
    [InlineData("assigned")]
    [InlineData("mixed")]
    public async Task GivenNamingChoice_WhenInitializing_ConfirmsTheExactReleasedAliasOrProvidedNames(string policy)
    {
        using var host = new RuntimeTestHost();
        AddAliases(host);
        host.Options.NamingPolicy = policy;
        if (policy is "provided" or "mixed")
        {
            host.Options.MemberNames["researcher"] = "Alpha";
        }
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the approved bounded document.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var proposal = Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.Initialization).Initialization!;
        Assert.Equal(policy, proposal.NamingPolicy);
        Assert.NotNull(proposal.NotificationQuestion);
        Assert.Contains("optional courtesy email", proposal.NotificationQuestion, StringComparison.Ordinal);
        Assert.Equal("in-chat", proposal.Notifications!.ApprovalChannel);
        Assert.False(proposal.Notifications.Enabled);
        Assert.Equal(runtime.Roster.Members.Select(member => member.PrimaryAgent), proposal.Roster!.Select(member => member.PrimaryAgent));
        if (policy == "skip")
        {
            Assert.All(runtime.Roster.Members, member => Assert.Null(member.MemberName));
        }
        else if (policy == "provided")
        {
            Assert.Equal("Alpha", runtime.Roster.Resolve("researcher")!.MemberName);
            Assert.Null(runtime.Roster.Resolve("lead")!.MemberName);
        }
        else
        {
            Assert.Equal("Alpha", runtime.Roster.Resolve("researcher")!.MemberName);
            Assert.Equal("Beta", runtime.Roster.Resolve("lead")!.MemberName);
            Assert.Equal("Alpha-2", runtime.Roster.Resolve("scribe")!.MemberName);
            Assert.Equal(runtime.Roster.Members.Count,
                runtime.Roster.Members.Select(member => member.MemberName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        var team = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "team.md"));
        Assert.Contains("Alternate Agents | Selection Cue | Invocation", team, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenInitializationSelection_WhenDeclined_DoesNotCreateAnyStateOrInvokeModels()
    {
        using var host = new RuntimeTestHost();
        host.Options.SelectInitializationAsync = (choices, _) =>
        {
            Assert.Equal(["single-squad", "federation"], choices.Topologies);
            Assert.Equal(["provided", "assigned", "mixed", "skip"], choices.NamingPolicies);
            Assert.Equal(["in-chat", "github-issue", "webhook"], choices.NotificationChannels);
            Assert.NotEmpty(choices.Roles);
            Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
            return ValueTask.FromResult<SquadRosterConfiguration?>(null);
        };
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Propose without silently creating a squad.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenFederationSelection_WhenInitializing_NeverSilentlySeedsASingleSquad()
    {
        using var host = new RuntimeTestHost();
        host.Options.SelectInitializationAsync = (_, _) =>
            ValueTask.FromResult<SquadRosterConfiguration?>(new() { Scope = "federation" });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Select the named federation topology.");

        Assert.Equal(SquadRunStatus.ApprovalRequired, result.Status);
        Assert.Contains("Federation was explicitly selected", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(host.Client.Calls);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenChangedInitProposal_WhenConfirmed_CreatesOnlyTheChosenRosterAndCopiesHostInputs()
    {
        using var host = new RuntimeTestHost();
        var selection = new SquadRosterConfiguration
        {
            Roles = ["researcher", "technical-writer"],
            MemberNames = new Dictionary<string, string> { ["researcher"] = "Chosen" },
            DeliverableRoots = new Dictionary<string, string> { ["researcher"] = "outputs/chosen-research" },
        };
        host.Options.SelectInitializationAsync = (_, _) => ValueTask.FromResult<SquadRosterConfiguration?>(selection);
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Initialization)
            {
                Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
                Assert.Equal(["researcher", "technical-writer", "scribe"], approval.Initialization!.Roles);
            }
            return ValueTask.FromResult(true);
        };
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("researcher");
        host.Client.Text("Research only.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Use the exact confirmed custom roster.");

        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        selection.MemberNames["researcher"] = "Changed by the caller afterward";
        selection.DeliverableRoots["researcher"] = "outputs/unapproved-afterward";
        Assert.Equal("Chosen", runtime.Roster.Resolve("researcher")!.MemberName);
        Assert.Equal("outputs/chosen-research", runtime.Roster.Resolve("researcher")!.DeliverableRoot);
        var copied = runtime.RosterConfiguration;
        copied.MemberNames["researcher"] = "Changed defensive copy";
        Assert.Equal("Chosen", runtime.Roster.Resolve("researcher")!.MemberName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCustomRosterWithoutSpine_WhenRunning_PreservesTheSelectionAndEscalatesOnlyWhenTheMissingLegIsNeeded(bool needPlan)
    {
        using var host = new RuntimeTestHost();
        host.Options.Roles = ["researcher", "technical-writer"];
        if (needPlan)
        {
            host.QueuePipeline("technical-writer");
            host.QueueStage("researcher");
        }
        else
        {
            host.Client.Call("run_focused", new()
            {
                ["stage"] = "research",
                ["inputPaths"] = Array.Empty<string>(),
                ["requiresCouncil"] = false,
                ["includesAiRisk"] = false,
            });
            host.QueueStage("researcher");
            host.Client.Text("Focused research only.");
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded request.");

        Assert.Equal(needPlan ? SquadRunStatus.Blocked : SquadRunStatus.ResearchCompleted, result.Status);
        Assert.Equal(["researcher", "technical-writer", "scribe"], runtime.Roster.Members.Select(member => member.Role));
        var proposal = host.Approvals[0].Initialization!;
        Assert.Equal("custom:default", proposal.Profile);
        Assert.Equal(["lead", "developer", "tester"], proposal.MissingMethodologyRoles);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenReadOnlyProjectDiscovery_WhenOfferingInit_PreservesFilesAndSuggestsTheSupportedProfileAndCouncilExtension()
    {
        using var host = new RuntimeTestHost();
        AddProfile(host, "minimal", "researcher, lead, developer, tester, scribe");
        host.Options.Profile = "minimal";
        Directory.CreateDirectory(Path.Combine(host.Project, "src"));
        var input = Path.Combine(host.Project, "src", "AuthService.cs");
        File.WriteAllText(input, "// Original synthetic file-name discovery fixture.");
        var before = File.ReadAllBytes(input);
        using var runtime = await host.CreateAsync();

        var choices = runtime.GetInitializationChoices("Request council validation of architecture and security.");

        Assert.Equal("minimal", choices.Proposed.Profile);
        Assert.Contains("src/AuthService.cs", choices.Discovery);
        Assert.Equal(["architect", "security"], choices.SuggestedCouncilRoles);
        Assert.All(choices.Roles.Where(role => role.Selectable), role => Assert.NotEmpty(role.Description));
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Empty(host.Client.Calls);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenAutoProfile_WhenReadOnlyDiscoveryFindsSecurityEvidence_ProposesTheCanonicalSpecializedProfile()
    {
        using var host = new RuntimeTestHost();
        AddProfile(host, "security", "researcher, lead, developer, tester, security, scribe");
        File.WriteAllText(Path.Combine(host.Project, "AuthService.cs"), "Original source fixture.");
        host.Options.Profile = "auto";
        using var runtime = await host.CreateAsync();

        var choices = runtime.GetInitializationChoices();

        Assert.Equal("security", choices.Proposed.Profile);
        Assert.NotNull(runtime.Roster.Resolve("security"));
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenExplicitNamingReset_WhenRefreshing_SkipsNamesOnlyAfterItsSpecificConfirmation()
    {
        using var host = new RuntimeTestHost();
        AddAliases(host);
        host.Options.NamingPolicy = "assigned";
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.Completed, (await runtime.RunAsync("Write the first document.")).Status);

        var update = await runtime.UpdateRosterAsync(new() { NamingPolicy = "skip" });

        Assert.Equal(SquadRunStatus.Completed, update.Status);
        Assert.All(runtime.Roster.Members, member => Assert.Null(member.MemberName));
        Assert.Equal("skip", update.Proposal.NamingPolicy);
    }

    [Fact]
    public async Task GivenUnavailableRegisteredRole_WhenOfferingCustomRoster_ReportsExactPrerequisitesAndNeverSeedsIt()
    {
        using var host = new RuntimeTestHost();
        AddUnavailableRole(host);
        using var runtime = await host.CreateAsync();
        var choices = runtime.GetInitializationChoices();
        var unavailable = Assert.Single(choices.Roles, role => role.Role.Name == "external");
        Assert.False(unavailable.Selectable);
        Assert.Contains("approved-fixture-install", unavailable.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("host-fixture-tools", unavailable.Diagnostic, StringComparison.Ordinal);
        host.Options.Roles = ["external"];

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreateAsync());

        Assert.Contains("Missing External Expert", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenPackRemoval_WhenRefreshing_PreservesSurvivorsOutputsHistoryOverridesAndRemovedMemberSpend()
    {
        using var host = new RuntimeTestHost();
        AddProfile(host, "minimal", "researcher, lead, developer, tester, scribe");
        host.Options.Profile = "minimal";
        host.Options.Packs = ["test-pack"];
        host.Options.MemberNames["researcher"] = "Retained";
        host.Options.DeliverableRoots["researcher"] = "outputs/approved-research";
        host.Options.Notifications = new() { Email = "reviewer@example.com" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("Write the initial document.");
        Assert.True(first.Status == SquadRunStatus.Completed, first.ResponseText);
        var before = first.Evidence.SelectMany(e => new[] { e.ArtifactPath, e.HistoryPath }
            .Concat((e.Artifacts ?? []).Select(a => a.Path))).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(host.Project, path)), StringComparer.Ordinal);
        var configuration = new SquadRosterConfiguration { Profile = "minimal" };

        var update = await runtime.UpdateRosterAsync(configuration);

        Assert.Equal(SquadRunStatus.Completed, update.Status);
        Assert.Equal(["technical-writer"], update.RemovedRoles);
        Assert.Equal("Retained", runtime.Roster.Resolve("researcher")!.MemberName);
        Assert.Equal("outputs/approved-research", runtime.Roster.Resolve("researcher")!.DeliverableRoot);
        Assert.Null(runtime.Roster.Resolve("technical-writer"));
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, path)));
        }
        var ledger = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"));
        Assert.Contains("role `technical-writer` (removed)", ledger, StringComparison.Ordinal);
        Assert.All(first.Evidence, evidence => Assert.Contains(evidence.DispatchId, ledger, StringComparison.Ordinal));
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        Assert.Equal("reviewer@example.com", state.RootElement.GetProperty("notify").GetProperty("Email").GetString());
        Assert.Contains(state.RootElement.GetProperty("RemovedMembers").EnumerateArray(), member =>
            member.GetProperty("Role").GetString() == "technical-writer");
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Fact]
    public async Task GivenPackAlsoContributedByProfile_WhenRemovingPack_KeepsTheRole()
    {
        using var host = new RuntimeTestHost();
        host.Options.Packs = ["test-pack"];
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.Completed, (await runtime.RunAsync("Write the first document.")).Status);

        var update = await runtime.UpdateRosterAsync(new() { Profile = "default" });

        Assert.Equal(SquadRunStatus.Completed, update.Status);
        Assert.Empty(update.RemovedRoles);
        Assert.NotNull(runtime.Roster.Resolve("technical-writer"));
    }

    [Fact]
    public async Task GivenDeclinedRosterUpdate_WhenRefreshing_LeavesExactStateAndWriterBytesUntouched()
    {
        using var host = new RuntimeTestHost();
        var denyRefresh = false;
        host.Options.ApproveAsync = (approval, _) =>
            ValueTask.FromResult(approval.Kind != SquadApprovalKind.RosterUpdate || !denyRefresh);
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.Completed, (await runtime.RunAsync("Write the first document.")).Status);
        var paths = new[] { "state.json", "team.md", "routing.md", "decisions.md", "consumption.md" };
        var before = paths.ToDictionary(path => path,
            path => File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, path)));
        denyRefresh = true;
        var updateConfiguration = new SquadRosterConfiguration { Roles = ["researcher"] };

        var update = await runtime.UpdateRosterAsync(updateConfiguration);

        Assert.Equal(SquadRunStatus.ApprovalDenied, update.Status);
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, path)));
        }
    }

    [Fact]
    public async Task GivenSuspendedRun_WhenRefreshingRoster_BlocksBeforeChangingTheBoundAssignment()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.Client.Call("ask_host", new() { ["question"] = "A pending human question." });
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.InputRequired, (await runtime.RunAsync("Prepare the local document.")).Status);
        var before = File.ReadAllBytes(StatePath(host));

        var update = await runtime.UpdateRosterAsync(new() { Roles = ["researcher"] });

        Assert.Equal(SquadRunStatus.Blocked, update.Status);
        Assert.Contains("active/suspended", update.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(StatePath(host)));
    }

    [Theory]
    [InlineData("https://secret.example.invalid/path?token=fixture")]
    [InlineData("C:\\private\\webhook")]
    public async Task GivenSecretShapedWebhookBinding_WhenCreatingOptions_RefusesToPersistIt(string value)
    {
        using var host = new RuntimeTestHost();
        host.Options.Notifications = new() { Enabled = true, ApprovalChannel = "webhook", WebhookBinding = value };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => host.CreateAsync());

        Assert.Contains("never persist a webhook URL", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Fact]
    public async Task GivenConfirmedContactChoice_WhenInitializing_PersistsOnlyProtectedNotifyAndNotHistoryOrDecisions()
    {
        using var host = new RuntimeTestHost();
        host.Options.Notifications = new()
        {
            Enabled = true,
            ApprovalChannel = "github-issue",
            GitHubHandle = "fixture-reviewer",
            GitHubRepository = "fixture/review",
            Email = "reviewer@example.com",
        };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the approved local document.");

        Assert.Equal(SquadRunStatus.Completed, result.Status);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var notify = state.RootElement.GetProperty("notify");
        Assert.Equal("github-issue", notify.GetProperty("ApprovalChannel").GetString());
        Assert.True(notify.GetProperty("Enabled").GetBoolean());
        Assert.Equal("reviewer@example.com", notify.GetProperty("Email").GetString());
        foreach (var path in Directory.GetFiles(Path.Combine(host.Project, host.Options.SquadRoot, "history"), "*.md")
            .Append(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md")))
        {
            Assert.DoesNotContain("reviewer@example.com", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Notifications:WebhookUrl", "https://example.invalid/private")]
    [InlineData("Notifications:Unexpected", "value")]
    [InlineData("SelectInitializationAsync", "true")]
    public void GivenInvalidOrHostOnlyInitConfiguration_WhenBinding_RejectsExplicitly(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHveSquad(configuration));
    }

    [Fact]
    public void GivenCustomRosterFlags_WhenParsingSample_ExposesTheSameApprovedDataSettings()
    {
        var command = SampleCommand.Parse(["--run", "request", "--project", ".",
            "--roles", "researcher,technical-writer", "--additional-roles", "intake-validator",
            "--packs", "test-pack", "--naming-policy", "mixed", "--topology", "single-squad"]);
        Assert.Equal(["researcher", "technical-writer"], command.RosterSettings!.Roles);
        Assert.Equal(["intake-validator"], command.RosterSettings.AdditionalRoles);
        Assert.Equal(["test-pack"], command.RosterSettings.Packs);
        Assert.Equal("mixed", command.RosterSettings.NamingPolicy);
    }
}
