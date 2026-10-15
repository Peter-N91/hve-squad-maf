using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeWatchTests
{
    internal static SquadWatchEvent ReportEvent() => Event();
    internal static SquadWatchConfiguration ReportConfiguration(bool terminal = false) => Configuration(terminal: terminal ? new() : null);
    internal static void QueueReportResearch(RuntimeTestHost host) => QueueFocused(host);

    private static SquadWatchEvent Event(SquadWatchEventKind kind = SquadWatchEventKind.Comment, long number = 15, long workflow = 101) => new()
    {
        Kind = kind,
        Repository = "fixture/watches",
        EventId = kind == SquadWatchEventKind.Comment ? "comment:100" : "workflow:" + workflow.ToString(CultureInfo.InvariantCulture),
        Action = kind == SquadWatchEventKind.Comment ? "created" : "labeled",
        Number = kind is SquadWatchEventKind.Issue or SquadWatchEventKind.PullRequest or SquadWatchEventKind.Comment ? number : null,
        IsPullRequest = kind == SquadWatchEventKind.PullRequest,
        WorkflowRunId = workflow,
        ReceivedAt = new(2026, 10, 12, 1, 45, 0, TimeSpan.Zero),
        Actor = "authorized",
        Labels = kind == SquadWatchEventKind.Issue ? ["squad/auto"] : kind == SquadWatchEventKind.PullRequest ? ["squad/review"] : [],
        Title = "Original local evidence",
        Body = "/squad Gather evidence for the authorized local task.",
        Ref = kind == SquadWatchEventKind.Push ? "refs/heads/release/native" : null,
        CommitSha = kind == SquadWatchEventKind.Push ? new('a', 40) : null,
    };

    private static SquadWatchConfiguration Configuration(TestAuthority? authority = null, TestTerminal? terminal = null,
        string? target = null, string schedule = "interactive") => new()
        {
            Repository = "fixture/watches",
            Authority = authority ?? new(),
            TerminalConnector = terminal,
            Target = target,
            ScheduleMode = schedule,
            ScheduleTerminal = SquadWatchTerminalKind.Issue,
            ScheduleRequest = "Gather maintenance research evidence.",
        };

    private static async Task<SquadFederation> Create(RuntimeTestHost host)
    {
        host.UseCurrentPlanContract();
        return await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
    }

    private static void QueueFocused(RuntimeTestHost host, string stage = "research", bool council = false)
    {
        host.Client.Call("run_focused", new()
        {
            ["stage"] = stage,
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = council,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("intake-validator", "Ready");
        if (stage != "review")
        {
            host.QueueStage("researcher");
        }
        if (stage == "plan")
        {
            host.QueueStage("lead");
            if (council)
            {
                RuntimeModeTests.Council(host);
            }
        }
        if (stage == "review")
        {
            host.QueueStage("tester");
        }
        host.Client.Text("Actual focused Watch result.");
    }

    private static void QueueAutopilot(RuntimeTestHost host, string producer = "technical-writer")
    {
        host.QueuePipeline(producer);
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        RuntimeModeTests.Council(host);
        host.QueueStage(producer);
        RuntimeModeTests.Council(host);
        host.QueueStage("tester");
        host.Client.Text("Actual full Watch delivery.");
    }

    private static string PathFor(RuntimeTestHost host, string relative) =>
        Path.Combine(host.Project, relative.Replace('/', Path.DirectorySeparatorChar));

    private static RuntimeState State(RuntimeTestHost host, string name) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host,
            ".copilot-tracking/squad/members/" + name + "/state.json")))!;

    private static Dictionary<string, string> Manifest(RuntimeTestHost host) =>
        Directory.EnumerateFiles(host.Project, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("federation.run.lock", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(host.Project, path), path =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    [Theory]
    [InlineData(SquadWatchEventKind.Issue, false, true, "/squad", false)]
    [InlineData(SquadWatchEventKind.Issue, true, false, "/squad", false)]
    [InlineData(SquadWatchEventKind.PullRequest, false, true, "/squad", false)]
    [InlineData(SquadWatchEventKind.Comment, false, true, "/squad", false)]
    [InlineData(SquadWatchEventKind.Comment, true, true, "/squadevil run", false)]
    [InlineData(SquadWatchEventKind.Schedule, true, true, "/squad", false)]
    public async Task GivenUnmatchedOrUnauthorizedSignals_WhenHandling_IsExplicitlyIgnoredWithoutStateModelsOrActions(
        SquadWatchEventKind kind, bool writer, bool labels, string body, bool workflow)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var terminal = new TestTerminal();
        var source = Event(kind) with { Body = body, Labels = labels ? Event(kind).Labels : [] };

        var result = await federation.HandleWatchEventAsync(source,
            Configuration(new() { Writer = writer, Workflow = workflow }, terminal));

        Assert.Equal(SquadWatchStatus.Ignored, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.Empty(host.Approvals);
        Assert.Empty(Directory.EnumerateFiles(host.Project, "*", SearchOption.AllDirectories));
        Assert.Equal(0, terminal.Executions);
    }

    [Theory]
    [InlineData(true, "fixture/watches")]
    [InlineData(false, "different/repository")]
    public async Task GivenForkOrWrongRepository_WhenHandling_NeverCallsTheAuthorityOrCreatesAPath(bool fork, string repository)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var authority = new TestAuthority();
        var result = await federation.HandleWatchEventAsync(Event() with { ForkOrigin = fork, Repository = repository }, Configuration(authority));

        Assert.Equal(SquadWatchStatus.Ignored, result.Status);
        Assert.Equal(0, authority.Calls);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenDifferentAuthenticatedStructuralIdentity_WhenHandling_IgnoresRatherThanRebindingTheSource()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var result = await federation.HandleWatchEventAsync(Event(), Configuration(new() { ChangeNumber = true }));

        Assert.Equal(SquadWatchStatus.Ignored, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.False(Directory.Exists(PathFor(host, ".copilot-tracking/squad")));
    }

    [Fact]
    public async Task GivenAuthorizedComment_WhenHandling_BootstrapsOneMemberAndActuallyDispatchesIntakeAndResearch()
    {
        using var host = new RuntimeTestHost();
        host.Options.ApproveAsync = null;
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal();

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Run!.Status);
        Assert.Equal("issue-15", result.Trigger!.Member);
        Assert.Equal("watch-mode", Assert.Single(federation.GetSnapshot().Members).Owner);
        Assert.Equal(["intake", "research"], result.Run.Evidence.Select(item => item.Stage));
        Assert.All(result.Run.Evidence, evidence => Assert.StartsWith(
            ".copilot-tracking/squad/members/issue-15/", evidence.ArtifactPath, StringComparison.Ordinal));
        Assert.Equal(SquadWatchTerminalKind.SourceComment, result.Terminal!.Kind);
        Assert.Equal(1, terminal.Executions);
        Assert.Empty(host.Approvals);
        Assert.Null(State(host, "issue-15").Checkpoint);
    }

    [Fact]
    public async Task GivenOriginalDelivery_WhenRestartingAndRetriggering_ReusesTheExactRunAndTerminalWithoutModelOrEffectReplay()
    {
        using var host = new RuntimeTestHost();
        using var first = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal();
        var original = await first.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));
        Assert.True(original.Status == SquadWatchStatus.Delivered, original.Diagnostic);
        var calls = host.Client.Calls.Count;
        using var restarted = await Create(host);

        var repeated = await restarted.HandleWatchEventAsync(Event() with { Body = "/squad mode=autopilot discovery=deep" },
            Configuration(terminal: terminal));

        Assert.Equivalent(original, repeated);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, terminal.Executions);
        Assert.Single(restarted.GetSnapshot().Members);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMissingOrDeniedTerminalCapability_WhenHandling_ReturnsPreparedWithoutInventingALink(bool deny)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = deny ? new TestTerminal { Authorized = false } : null;

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));

        Assert.True(result.Status == SquadWatchStatus.Prepared, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Null(result.Terminal);
        Assert.Equal(0, terminal?.Executions ?? 0);
        Assert.NotNull(State(host, "issue-15").Checkpoint);
        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Run!.Status);
    }

    [Fact]
    public async Task GivenPreparedOutcome_WhenRegisteringTheTerminalLater_PerformsOnlyTheOriginalProviderAction()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var prepared = await federation.HandleWatchEventAsync(Event(), Configuration());
        Assert.True(prepared.Status == SquadWatchStatus.Prepared, prepared.Diagnostic);
        var calls = host.Client.Calls.Count;
        var terminal = new TestTerminal();

        var delivered = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));

        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        Assert.Equal(prepared.Trigger, delivered.Trigger);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, terminal.Executions);
    }

    [Fact]
    public async Task GivenPayloadControls_WhenRunningFullAutopilot_IgnoresThemAndNeverOffersDiscoveryOrFinalInChatAcceptance()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"] };
        host.Options.HumanPresent = true;
        host.Options.Discovery = "deep";
        host.Options.MaxConcurrentDispatches = 4;
        host.Options.AskAsync = (_, _) => throw new InvalidOperationException("Unattended discovery/model prompts are forbidden.");
        using var federation = await Create(host);
        QueueAutopilot(host);
        var source = Event(SquadWatchEventKind.Issue) with
        {
            Body = "Write the local documentation. mode=interactive routing=manual models=imagined tier=fast " +
                "cost-ceiling=unset discovery=deep root=../outside permissions=all",
        };

        var result = await federation.HandleWatchEventAsync(source, Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Equal("autopilot", result.Trigger!.Mode);
        Assert.Equal(SquadWatchTerminalKind.DraftPullRequest, result.Terminal!.Kind);
        Assert.False(result.Run!.ModeOutcome!.FinalValidated);
        Assert.Equal(0, result.Run.ModeOutcome.PipelinedTransitions);
        Assert.Contains(result.Run.Evidence, item => item.Stage == "intake");
        Assert.DoesNotContain(result.Run.Evidence, item => item.Stage == "discovery");
        var decisions = File.ReadAllText(PathFor(host, ".copilot-tracking/squad/decisions.md"));
        Assert.Contains("SeenAndIgnoredControls", decisions, StringComparison.Ordinal);
        Assert.Contains("cost-ceiling", decisions, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.FinalOutcome);
    }

    [Fact]
    public async Task GivenPullRequestIntent_WhenHandling_DispatchesActualFocusedReviewWithIndependentIntake()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host, "review");

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.PullRequest), Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Equal(SquadRunStatus.ReviewCompleted, result.Run!.Status);
        Assert.Equal("pr-15", result.Trigger!.Member);
        Assert.Equal(SquadWatchTerminalKind.PullRequestReview, result.Terminal!.Kind);
        Assert.Equal(["intake", "review"], result.Run.Evidence.Select(item => item.Stage));
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenStructuralReviewIntent_WhenTheModelRequestsDelivery_BlocksBeforeProducingWork()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        host.QueuePipeline();
        host.Client.Text("No valid review was dispatched.");

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.PullRequest), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.Empty(result.Run!.Evidence);
    }

    [Fact]
    public async Task GivenPushIntent_WhenHandling_ActuallyRunsThePlanAndIndependentValidationCouncilWithoutImplementation()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"] };
        using var federation = await Create(host);
        QueueFocused(host, "plan", council: true);

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Push), Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Equal(SquadRunStatus.PlanCompleted, result.Run!.Status);
        Assert.Equal(SquadWatchTerminalKind.CommitComment, result.Terminal!.Kind);
        Assert.StartsWith("push-release-native-aaaaaaa", result.Trigger!.Member, StringComparison.Ordinal);
        Assert.Contains(result.Run.Evidence, item => item.Stage == "council");
        Assert.DoesNotContain(result.Run.Evidence, item => item.Stage == "produce");
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData(SquadToolReceiptKind.Process, SquadToolOutputStatus.Succeeded)]
    [InlineData(SquadToolReceiptKind.HostObservation, SquadToolOutputStatus.DryRun)]
    [InlineData(SquadToolReceiptKind.HostObservation, SquadToolOutputStatus.Failed)]
    public async Task GivenUnprovenTerminalOutput_WhenHandling_RequiresOriginalReconciliationAndNeverReturnsADraftedUrl(
        SquadToolReceiptKind kind, SquadToolOutputStatus status)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal { ReceiptKind = kind, OutputStatus = status };

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));
        var repeated = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: terminal));

        Assert.Equal(SquadWatchStatus.ReconciliationRequired, result.Status);
        Assert.Equal(SquadWatchStatus.ReconciliationRequired, repeated.Status);
        Assert.Null(result.Terminal);
        Assert.Equal(1, terminal.Executions);
        Assert.Equal("in-flight", Assert.Single(State(host, "issue-15").Checkpoint!.Effects).Status);
    }

    [Fact]
    public async Task GivenInterruptedProviderMutation_WhenReconciling_RecordsTheOriginalReceiptOnceAndRequiresExplicitContinuation()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal { ThrowAfterEffect = true };
        var configuration = Configuration(terminal: terminal);
        var uncertain = await federation.HandleWatchEventAsync(Event(), configuration);
        Assert.Equal(SquadWatchStatus.ReconciliationRequired, uncertain.Status);
        var calls = host.Client.Calls.Count;

        var reconciled = await federation.ReconcileWatchAsync(uncertain.Trigger!.WatchRunId, configuration);
        Assert.Equal(SquadWatchStatus.Prepared, reconciled.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        var delivered = await federation.HandleWatchEventAsync(Event(), configuration);

        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        Assert.Equal(uncertain.Trigger, delivered.Trigger);
        Assert.Equal(1, terminal.Executions);
        Assert.Equal(1, terminal.Observations);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Single(State(host, "issue-15").NativeOperations, item => item.ExecutedEffect?.Role == "watch-terminal");
    }

    [Fact]
    public async Task GivenCannotConfirmProviderOutcome_WhenReconciling_NeverReplaysOrClaimsNonExecution()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal { ThrowAfterEffect = true, CannotConfirm = true };
        var configuration = Configuration(terminal: terminal);
        var uncertain = await federation.HandleWatchEventAsync(Event(), configuration);

        var reconciled = await federation.ReconcileWatchAsync(uncertain.Trigger!.WatchRunId, configuration);

        Assert.Equal(SquadWatchStatus.ReconciliationRequired, reconciled.Status);
        Assert.Equal(1, terminal.Executions);
        Assert.Equal("in-flight", Assert.Single(State(host, "issue-15").Checkpoint!.Effects).Status);
    }

    [Fact]
    public async Task GivenHumanOwnedEventName_WhenHandling_BlocksWithoutChangingThatMemberOrDispatchingAModel()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        Assert.Equal(SquadRunStatus.Completed, (await federation.InitializeAsync([new()
        {
            Name = "issue-15", Description = "Human-owned task", Owner = "human", Patterns = ["human"],
        }])).Status);
        var before = Manifest(host);

        var result = await federation.HandleWatchEventAsync(Event(), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Contains("human-created", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(before, Manifest(host));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenUnprovenOrphanDirectory_WhenHandling_NeverOverwritesOrAdoptsIt()
    {
        using var host = new RuntimeTestHost();
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/squad/members/issue-15"));
        File.WriteAllText(PathFor(host, ".copilot-tracking/squad/members/issue-15/original.txt"), "Human bytes.");
        using var federation = await Create(host);
        var result = await federation.HandleWatchEventAsync(Event(), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Equal("Human bytes.", File.ReadAllText(PathFor(host, ".copilot-tracking/squad/members/issue-15/original.txt")));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenDifferentSweepsOnOneUtcDay_WhenHandling_DisambiguatesOnceUsingOnlyTheWorkflowIdentity()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var first = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Schedule), Configuration(terminal: new()));
        Assert.True(first.Status == SquadWatchStatus.Delivered, first.Diagnostic);
        QueueFocused(host);

        var second = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Schedule, workflow: 102),
            Configuration(terminal: new()));

        Assert.True(second.Status == SquadWatchStatus.Delivered, second.Diagnostic);
        Assert.Equal("sweep-2026-10-12", first.Trigger!.Member);
        Assert.Equal("sweep-2026-10-12-102", second.Trigger!.Member);
        Assert.NotEqual(first.Trigger.RunId, second.Trigger.RunId);
    }

    [Fact]
    public async Task GivenChangedOriginalInput_WhenRetriggering_BlocksEvenACachedSuccessWithoutAnyNewWork()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal();
        var configuration = Configuration(terminal: terminal);
        var delivered = await federation.HandleWatchEventAsync(Event(), configuration);
        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        File.AppendAllText(PathFor(host, delivered.Trigger!.InputPath), "\nchanged");
        var calls = host.Client.Calls.Count;

        var result = await federation.HandleWatchEventAsync(Event(), configuration);

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, terminal.Executions);
    }

    [Fact]
    public async Task GivenQuiescentNativeSingleSquad_WhenWatchBootstraps_PromotesOriginalBytesAndExpandsWithoutHumanStateReplacement()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        using (var runtime = await host.CreateAsync())
        {
            host.Client.Call("run_focused", new()
            {
                ["stage"] = "research",
                ["inputPaths"] = Array.Empty<string>(),
                ["requiresCouncil"] = false,
                ["includesAiRisk"] = false
            });
            host.QueueStage("researcher");
            host.Client.Text("Original human-owned research.");
            Assert.Equal(SquadRunStatus.ResearchCompleted, (await runtime.RunAsync("Inspect the original local evidence.")).Status);
        }
        var original = File.ReadAllBytes(PathFor(host, ".copilot-tracking/squad/state.json"));
        using var federation = await Create(host);
        QueueFocused(host);

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic);
        Assert.Equal(["default", "issue-15"], federation.GetSnapshot().Members.Select(member => member.Name));
        Assert.Null(federation.GetSnapshot().Members[0].Owner);
        var archive = Assert.Single(Directory.EnumerateFiles(PathFor(host, ".copilot-tracking/squad/members/default/migrations"),
            "original-state.json", SearchOption.AllDirectories));
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.Single(State(host, "default").Runs);
        Assert.Single(State(host, "issue-15").Runs);
    }

    [Fact]
    public async Task GivenAnActiveHumanSingleSquad_WhenWatchBootstraps_BlocksWithoutMovingOriginalAuthorityOrCallingAModel()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        using var notifications = RuntimeModeTests.Durable(host);
        using (var runtime = await host.CreateAsync())
        {
            host.Client.Call("run_focused", new()
            {
                ["stage"] = "research",
                ["inputPaths"] = Array.Empty<string>(),
                ["requiresCouncil"] = false,
                ["includesAiRisk"] = false
            });
            Assert.Equal(SquadRunStatus.ApprovalRequired, (await runtime.RunAsync("Inspect original work.")).Status);
        }
        var statePath = PathFor(host, ".copilot-tracking/squad/state.json");
        var original = File.ReadAllBytes(statePath);
        using var federation = await Create(host);
        var calls = host.Client.Calls.Count;

        var result = await federation.HandleWatchEventAsync(Event(), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Equal(original, File.ReadAllBytes(statePath));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/federation.md")));
    }

    [Fact]
    public async Task GivenARealRemoteRiskGate_WhenRestarting_UsesOriginalMemberAuthenticationAndNeverClearsStopOrRedispatchesCouncil()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"] };
        using var notifications = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        host.QueuePipeline("technical-writer");
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        RuntimeModeTests.Council(host, verdict: "Block", risk: "High");
        var configuration = Configuration(terminal: new());
        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Issue), configuration);
        Assert.True(result.Status == SquadWatchStatus.Pending, result.Diagnostic + "\n" + result.Run?.ResponseText);
        var risk = Assert.Single(await federation.GetPendingHumanActionsAsync(), action => action.Status == "pending");
        Assert.Equal("riskgate", risk.Binding.Gate);
        var calls = host.Client.Calls.Count;
        using var restarted = await Create(host);
        restarted.RegisterWatch(configuration);
        var details = await restarted.ReadHumanActionAsync(risk.Binding.Id);
        Assert.Equal(SquadApprovalKind.RiskGate, details.Approval!.Kind);
        var submitted = await restarted.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(risk, "/approve", "watch-risk:1"));
        Assert.True(submitted.Accepted, submitted.Diagnostic);

        var stopped = await restarted.HandleWatchEventAsync(Event(SquadWatchEventKind.Issue), configuration);

        Assert.Equal(SquadWatchStatus.Blocked, stopped.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.DoesNotContain(stopped.Run!.Evidence, evidence => evidence.Stage == "produce");
        Assert.NotNull(State(host, "issue-15").Checkpoint!.Mode!.RiskApproval);
    }

    [Fact]
    public async Task GivenARequiredCouncilRoleAbsent_WhenUnattended_NeverAddsOrWaivesItEvenWithARemoteChannel()
    {
        using var host = new RuntimeTestHost();
        host.Options.Roles = ["researcher", "lead", "technical-writer", "tester", "intake-validator", "scribe"];
        host.Options.Council = new() { Roles = ["architect"] };
        using var notifications = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        host.QueuePipeline("technical-writer");
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");
        host.QueueStage("lead");

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Issue), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Contains("architect", result.Run!.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(State(host, "issue-15").Initialization!.Roster!, member => member.Role == "architect");
        Assert.DoesNotContain(result.Run.Evidence, evidence => evidence.Stage is "council" or "produce");
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.RosterUpdate);
    }

    [Fact]
    public async Task GivenMissingIndependentIntake_WhenUnattended_BlocksWithoutSelfReviewOrAutomaticExtension()
    {
        using var host = new RuntimeTestHost();
        host.Options.Roles = ["researcher", "lead", "technical-writer", "tester", "scribe"];
        using var federation = await Create(host);
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false
        });
        host.Client.Text("Missing role blocks.");

        var result = await federation.HandleWatchEventAsync(Event(), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Contains("intake-validator", result.Run!.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Run.Evidence);
    }

    [Fact]
    public async Task GivenImpactfulHostTool_WhenWatchExecutes_NeverInvokesItOrTreatsTriggerRightsAsEffectAuthority()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"] };
        host.Options.Tools[0] = host.Options.Tools[0] with { Impact = SquadToolImpact.Impactful, Action = SquadToolAction.Deletion };
        using var federation = await Create(host);
        QueueAutopilot(host, "developer");

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Issue), Configuration());

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.Contains("separate authenticated human-approved", result.Run!.ResponseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refs/heads/RELEASE/Very.Long/branch", "push-release-very-long-branch-aaaaaaa")]
    [InlineData("refs/heads/名字", "push-aaaaaaa")]
    public void GivenStructuralPushRefs_WhenNaming_UsesOnlyNormalizedMetadataAndPreservesTheCommitDisambiguator(string branch, string expected)
    {
        var source = Event(SquadWatchEventKind.Push) with { Ref = branch, Title = "../../human", Body = "root=../outside" };
        Assert.Equal(expected, WatchContracts.MemberName(source));
        var longName = WatchContracts.MemberName(source with { Ref = "refs/heads/" + new string('x', 240) });
        Assert.Equal(64, longName.Length);
        Assert.EndsWith("-aaaaaaa", longName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenTrustedExplicitTarget_WhenHandling_HonorsOnlyTheRegisteredNameWithoutCreatingAnEventMember()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        Assert.Equal(SquadRunStatus.Completed, (await federation.InitializeAsync([new()
        {
            Name = "human-project", Owner = "human", Description = "Original approved scope", Patterns = ["original"],
        }])).Status);
        QueueFocused(host);

        var result = await federation.HandleWatchEventAsync(Event() with { Body = "/squad squad=unregistered mode=autopilot Gather evidence." },
            Configuration(terminal: new(), target: "human-project"));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic);
        Assert.Equal("human-project", result.Trigger!.Member);
        Assert.True(result.Trigger.ExplicitTarget);
        Assert.Single(federation.GetSnapshot().Members);
        Assert.False(Directory.Exists(PathFor(host, ".copilot-tracking/squad/members/issue-15")));
    }

    [Trait("Category", "ReleaseCompatibility")]
    [Fact]
    public async Task GivenActualPinnedRelease_WhenWatchHandlesAnAuthorizedEvent_ExecutesRealInstalledIntakeAndResearch()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        using var releaseClient = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        using var host = new RuntimeTestHost();
        host.Options.AdditionalRoles = ["intake-validator"];
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options,
            ReleaseTestHttpClient.Source(version, releaseClient));
        QueueFocused(host);

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Equal(version, federation.Provenance.Release!.Tag);
        Assert.Equal(["intake", "research"], result.Run!.Evidence.Select(evidence => evidence.Stage));
        Assert.Equal("watch-mode", Assert.Single(federation.GetSnapshot().Members).Owner);
    }

    [Theory]
    [InlineData("write", "User", true)]
    [InlineData("maintain", "User", true)]
    [InlineData("admin", "User", true)]
    [InlineData("read", "User", false)]
    [InlineData("triage", "User", false)]
    [InlineData("admin", "Bot", false)]
    public async Task GivenActualGitHubLabelActor_WhenAuthorizing_ReReadsHumanIdentityAndCurrentWriteRights(
        string permission, string actorType, bool authorized)
    {
        using var handler = new WatchHttpHandler();
        handler.Permission = permission;
        handler.ActorType = actorType;
        using var client = new HttpClient(handler);
        var authority = new GitHubWatchEventAuthority(client, "fixture/watches");
        var source = Event(SquadWatchEventKind.Issue) with { EventId = "label:50", WorkflowRunId = null };

        var result = await authority.AuthorizeAsync(source, default);

        Assert.Equal(authorized, result is not null);
        Assert.Equal(0, handler.Mutations);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("pull_request_target", StringComparison.Ordinal));
        if (result is not null)
        {
            Assert.True(result.WriteCollaborator);
            Assert.Equal("Provider task data.", result.Event.Body);
        }
    }

    [Theory]
    [InlineData(SquadWatchEventKind.Schedule)]
    [InlineData(SquadWatchEventKind.Dispatch)]
    [InlineData(SquadWatchEventKind.Push)]
    public async Task GivenWorkflowOptIn_WhenAuthorizing_RequiresTheExactCommittedBlobAndActualRunFilters(SquadWatchEventKind kind)
    {
        using var handler = new WatchHttpHandler { WorkflowKind = kind };
        using var client = new HttpClient(handler);
        var policy = new GitHubWatchWorkflowPolicy
        {
            Path = ".github/workflows/native-watch.yml",
            BlobSha = new('c', 40),
            Events = [kind],
            PushBranchPrefixes = ["release/"],
        };
        var authority = new GitHubWatchEventAuthority(client, "fixture/watches", workflow: policy);
        var source = Event(kind);

        var authorized = await authority.AuthorizeAsync(source, default);
        handler.WorkflowBlob = new('d', 40);
        var moved = await authority.AuthorizeAsync(source, default);

        Assert.NotNull(authorized);
        Assert.True(HumanActions.IsHash(authorized.CommittedWorkflowSha256));
        Assert.Null(moved);
        Assert.Equal(0, handler.Mutations);
    }

    [Theory]
    [InlineData(SquadWatchTerminalKind.SourceComment)]
    [InlineData(SquadWatchTerminalKind.PullRequestReview)]
    [InlineData(SquadWatchTerminalKind.Issue)]
    [InlineData(SquadWatchTerminalKind.CommitComment)]
    [InlineData(SquadWatchTerminalKind.DraftPullRequest)]
    public async Task GivenAuthorizedGitHubTerminal_WhenExecuting_ReReadsActualProviderPostconditionsAndReconcilesWithoutMutation(
        SquadWatchTerminalKind kind)
    {
        using var handler = new WatchHttpHandler();
        using var client = new HttpClient(handler);
        var options = new GitHubWatchTerminalOptions
        {
            Kinds = [kind],
            DraftHead = "squad-watch/prepared",
            DraftBase = "main",
            DraftHeadSha = new('a', 40),
        };
        var connector = new GitHubWatchTerminalConnector(client, "fixture/watches", options,
            (_, _) => ValueTask.FromResult(true), "fixture-terminal-rights:1");
        var request = TerminalRequest(kind);

        var receipt = await connector.ExecuteAsync(request, default);
        var reconciled = await connector.ReconcileAsync(request, default);

        Assert.Equal(receipt, reconciled.Receipt);
        Assert.Equal(SquadEffectDisposition.Executed, reconciled.Disposition);
        Assert.Equal(SquadToolReceiptKind.HostObservation, receipt.ReceiptKind);
        Assert.Equal(1, handler.Mutations);
        Assert.Equal(kind == SquadWatchTerminalKind.DraftPullRequest ? "77" : kind == SquadWatchTerminalKind.Issue ? "77" : "900",
            receipt.ResourceId);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/merge", StringComparison.Ordinal) ||
            path.Contains("/git/refs", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE_SENTINEL", handler.LastBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GivenProtectedOrWrongBytePreparedBranch_WhenOpeningADraft_RefusesBeforeAnyMutation(bool protect, bool changeBytes)
    {
        using var handler = new WatchHttpHandler { Protected = protect, ChangedOutput = changeBytes };
        using var client = new HttpClient(handler);
        var connector = new GitHubWatchTerminalConnector(client, "fixture/watches", new()
        {
            Kinds = [SquadWatchTerminalKind.DraftPullRequest],
            DraftHead = "squad-watch/prepared",
            DraftBase = "main",
            DraftHeadSha = new('a', 40),
        }, (_, _) => ValueTask.FromResult(true), "fixture-terminal-rights:1");

        await Assert.ThrowsAsync<SquadHumanTransportException>(() =>
            connector.ExecuteAsync(TerminalRequest(SquadWatchTerminalKind.DraftPullRequest), default).AsTask());

        Assert.Equal(0, handler.Mutations);
    }

    [Fact]
    public async Task GivenNoActualProviderMarker_WhenReconciling_NeverInfersNonExecutionOrRetriesTheMutation()
    {
        using var handler = new WatchHttpHandler();
        using var client = new HttpClient(handler);
        var connector = new GitHubWatchTerminalConnector(client, "fixture/watches",
            new() { Kinds = [SquadWatchTerminalKind.Issue] }, (_, _) => ValueTask.FromResult(true), "fixture-terminal-rights:1");

        var result = await connector.ReconcileAsync(TerminalRequest(SquadWatchTerminalKind.Issue), default);

        Assert.Equal(SquadEffectDisposition.CannotConfirm, result.Disposition);
        Assert.Equal(0, handler.Mutations);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task GivenWrongProviderResourceUrl_WhenObserving_RejectsEvenAnOtherwiseSuccessfulSameRepositoryResponse()
    {
        using var handler = new WatchHttpHandler { WrongReference = true };
        using var client = new HttpClient(handler);
        var connector = new GitHubWatchTerminalConnector(client, "fixture/watches",
            new() { Kinds = [SquadWatchTerminalKind.Issue] }, (_, _) => ValueTask.FromResult(true), "fixture-terminal-rights:1");

        var exception = await Assert.ThrowsAsync<SquadHumanTransportException>(() =>
            connector.ExecuteAsync(TerminalRequest(SquadWatchTerminalKind.Issue), default).AsTask());

        Assert.True(exception.OutcomeUncertain);
        Assert.Equal(1, handler.Mutations);
    }

    [Fact]
    public async Task GivenOnlyProvenMemberTreesRetained_WhenRegistryIsAbsent_AdoptsOriginalEvidenceWithoutOverwritingOrReplaying()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal();
        var configuration = Configuration(terminal: terminal);
        var delivered = await federation.HandleWatchEventAsync(Event(), configuration);
        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        var memberRoot = PathFor(host, ".copilot-tracking/squad/members/issue-15");
        var before = Directory.EnumerateFiles(memberRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
        foreach (var name in new[] { "federation.md", "meta-routing.md", "state.json", "decisions.md", "consumption-rates.md" })
        {
            File.Delete(PathFor(host, ".copilot-tracking/squad/" + name));
        }
        foreach (var path in Directory.EnumerateFiles(PathFor(host, ".copilot-tracking/squad/history"), "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        Directory.Delete(PathFor(host, ".copilot-tracking/squad/history"));
        var calls = host.Client.Calls.Count;
        using var recreated = await Create(host);

        var adopted = await recreated.HandleWatchEventAsync(Event(), configuration);

        Assert.True(adopted.Status == SquadWatchStatus.Delivered, adopted.Diagnostic);
        Assert.Equal(delivered.Trigger, adopted.Trigger);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, terminal.Executions);
        Assert.Equal(before, Directory.EnumerateFiles(memberRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal));
    }

    [Fact]
    public async Task GivenStartedMemberTreeMissing_WhenRetriggering_NeverReconstructsLostAttemptsOrReplaysEffects()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueFocused(host);
        var terminal = new TestTerminal();
        var configuration = Configuration(terminal: terminal);
        var delivered = await federation.HandleWatchEventAsync(Event(), configuration);
        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        var memberRoot = PathFor(host, ".copilot-tracking/squad/members/issue-15");
        foreach (var path in Directory.EnumerateFiles(memberRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        foreach (var directory in Directory.EnumerateDirectories(memberRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory);
        }
        Directory.Delete(memberRoot);
        var calls = host.Client.Calls.Count;

        var result = await federation.HandleWatchEventAsync(Event(), configuration);

        Assert.Equal(SquadWatchStatus.Blocked, result.Status);
        Assert.Contains("original evidence", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, terminal.Executions);
        Assert.False(Directory.Exists(memberRoot));
    }

    [Fact]
    public async Task GivenExactAuthorizedUnstartedSeed_WhenItsTreeIsMissing_RestoresTheOriginalImageAndStartsOnlyItsOriginalRun()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var terminal = new TestTerminal();
        var configuration = Configuration(terminal: terminal);
        var source = Event();
        var authorization = (await configuration.Authority.AuthorizeAsync(source, default))!;
        var prepared = await federation.PrepareWatchAsync(source, authorization, configuration, default);
        Assert.False(prepared.Started);
        Assert.Empty(host.Client.Calls);
        var memberRoot = PathFor(host, ".copilot-tracking/squad/members/issue-15");
        foreach (var path in Directory.EnumerateFiles(memberRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        foreach (var directory in Directory.EnumerateDirectories(memberRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory);
        }
        Directory.Delete(memberRoot);
        QueueFocused(host);

        var repaired = await federation.HandleWatchEventAsync(source, configuration);

        Assert.True(repaired.Status == SquadWatchStatus.Delivered, repaired.Diagnostic + "\n" + repaired.Run?.ResponseText);
        Assert.Equal(prepared.Trigger, repaired.Trigger);
        Assert.Single(State(host, "issue-15").Runs);
        Assert.Equal(1, terminal.Executions);
        var hub = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, ".copilot-tracking/squad/state.json")))!;
        Assert.Single(hub.NativeOperations, operation => operation.Operation == "WatchRepairMember");
    }

    [Fact]
    public void GivenTrustedNativeRunnerArguments_WhenParsing_SeparatesEventDataFromHostSettingsAndRequiresAnIsolatedCase()
    {
        var command = HveSquad.AgentFramework.Sample.SampleCommand.Parse(
            ["--watch-event", "event.json", "--watch-settings", "trusted.json", "--project", "."]);
        Assert.Equal(HveSquad.AgentFramework.Sample.SampleCommandKind.WatchEvent, command.Kind);
        Assert.Equal("event.json", command.WatchEventPath);
        Assert.Equal("trusted.json", command.WatchSettingsPath);
        Assert.Throws<ArgumentException>(() => HveSquad.AgentFramework.Sample.SampleCommand.Parse(
            ["--watch-event", "event.json", "--watch-settings", "trusted.json", "--project", ".", "--mode", "autopilot"]));
        Assert.Throws<ArgumentException>(() => HveSquad.AgentFramework.Sample.SampleCommand.Parse(
            ["--watch-event", "event.json", "--project", "."]));
    }

    [Fact]
    public async Task GivenEmptyManualCells_WhenWatchDispatches_NeverPromptsAndUsesOnlyTheActualFloorAdmittedStaticClient()
    {
        using var host = new RuntimeTestHost();
        host.Options.ModelRouting = new() { Mode = "manual" };
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("actual-static", host.Client, CapabilityClass: "balanced")]);
        host.Options.ModelSelector = (_, _) => new(host.Client, "actual-static", "balanced");
        host.Options.AskAsync = (_, _) => throw new InvalidOperationException("A Watch model prompt is forbidden.");
        using var federation = await Create(host);
        QueueFocused(host);

        var result = await federation.HandleWatchEventAsync(Event(), Configuration(terminal: new()));

        Assert.True(result.Status == SquadWatchStatus.Delivered, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.All(result.Run!.Evidence, evidence => Assert.Equal("actual-static", evidence.ModelRouting!.EffectiveModel));
        Assert.All(result.Run.Evidence, evidence => Assert.Contains("fallback", evidence.ModelRouting!.Rationale, StringComparison.Ordinal));
        Assert.Empty(State(host, "issue-15").ModelRouting!.Models);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.ModelRouting);
    }

    [Fact]
    public async Task GivenUnknownTariffWithFiniteWatchCeiling_WhenHandling_RecordsClassifierSpendButAdmitsNoSubstantiveCallOrCostOverride()
    {
        using var host = new RuntimeTestHost();
        host.Options.CostCeiling = "10";
        host.Options.AdditionalRoles = ["analyst"];
        using var federation = await Create(host);
        host.QueuePipeline("technical-writer");

        var result = await federation.HandleWatchEventAsync(Event(SquadWatchEventKind.Issue), Configuration());

        Assert.True(result.Status == SquadWatchStatus.Pending, result.Diagnostic + "\n" + result.Run?.ResponseText);
        Assert.Single(host.Client.Calls);
        Assert.Empty(result.Run!.Evidence);
        Assert.Equal("cannot-confirm", result.Run!.CostPreflight!.Decision);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Equal(10m, State(host, "issue-15").Checkpoint!.Cost!.CeilingUsd);
    }

    private static SquadWatchTerminalRequest TerminalRequest(SquadWatchTerminalKind kind) => new(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), ".copilot-tracking/squad/members/issue-15",
        "fixture/watches", "fixture/watches#15", "comment:100", 15, new('a', 40), kind,
        SquadRunStatus.Completed, [new("primary", "PRIVATE_SENTINEL", new('A', 64))],
        [new("source", "implementation.txt", Convert.ToHexString(SHA256.HashData("actual source"u8.ToArray())))], []);

    private sealed class WatchHttpHandler : HttpMessageHandler
    {
        private JsonObject? _resource;
        internal string ActorType { get; set; } = "User";
        internal bool ChangedOutput { get; init; }
        internal bool Protected { get; init; }
        internal bool WrongReference { get; init; }
        internal string Permission { get; set; } = "write";
        internal string WorkflowBlob { get; set; } = new('c', 40);
        internal SquadWatchEventKind WorkflowKind { get; init; } = SquadWatchEventKind.Schedule;
        internal int Mutations { get; private set; }
        internal string LastBody { get; private set; } = "";
        internal List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            object response;
            if (request.Method != HttpMethod.Get)
            {
                Mutations++;
                var content = await request.Content!.ReadAsStringAsync(cancellationToken);
                var data = JsonNode.Parse(content)!.AsObject();
                LastBody = data["body"]!.GetValue<string>();
                _resource = new()
                {
                    ["id"] = 900,
                    ["number"] = 77,
                    ["body"] = LastBody,
                    ["html_url"] = "https://github.com/fixture/watches/" + (path.EndsWith("/reviews", StringComparison.Ordinal)
                        ? "pull/15#pullrequestreview-900" : path.EndsWith("/pulls", StringComparison.Ordinal) ? "pull/77" :
                        path.Contains("/commits/", StringComparison.Ordinal) ? "commit/" + new string('a', 40) + "#commitcomment-900" :
                        path.EndsWith("/comments", StringComparison.Ordinal) ? "issues/15#issuecomment-900" : "issues/77"),
                    ["draft"] = true,
                    ["state"] = "COMMENTED",
                    ["commit_id"] = new string('a', 40),
                    ["issue_url"] = "https://api.github.com/repos/fixture/watches/issues/15",
                    ["head"] = new JsonObject { ["ref"] = "squad-watch/prepared", ["sha"] = new string('a', 40) },
                    ["base"] = new JsonObject { ["ref"] = "main" },
                };
                if (WrongReference)
                {
                    _resource["html_url"] = "https://github.com/fixture/watches/issues/78";
                }
                response = _resource;
            }
            else if (path.EndsWith("/permission", StringComparison.Ordinal))
            {
                response = new { permission = Permission, user = new { id = 42 } };
            }
            else if (path.Contains("/actions/runs/", StringComparison.Ordinal))
            {
                response = new
                {
                    id = 101,
                    @event = GitHubWatch.EventName(WorkflowKind),
                    repository = new { full_name = "fixture/watches" },
                    head_repository = new { full_name = "fixture/watches" },
                    path = ".github/workflows/native-watch.yml",
                    head_sha = new string('a', 40),
                    head_branch = "release/native",
                    created_at = "2026-10-12T01:45:00Z",
                    actor = new { id = 42, login = "authorized", type = ActorType }
                };
            }
            else if (path.Contains("/contents/.github/", StringComparison.Ordinal))
            {
                response = new { sha = WorkflowBlob, encoding = "base64", content = Convert.ToBase64String("reviewed workflow"u8.ToArray()) };
            }
            else if (path.Contains("/branches/", StringComparison.Ordinal))
            {
                response = new { @protected = Protected, commit = new { sha = new string('a', 40) } };
            }
            else if (path.Contains("/compare/", StringComparison.Ordinal))
            {
                response = new { status = "ahead", files = new[] { new { filename = "implementation.txt" } } };
            }
            else if (path.Contains("/contents/implementation.txt", StringComparison.Ordinal))
            {
                response = new
                {
                    encoding = "base64",
                    content = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(ChangedOutput ? "different source" : "actual source"))
                };
            }
            else if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                response = new[] { new { id = 50, @event = "labeled", label = new { name = "squad/auto" },
                    actor = new { id = 42, login = "authorized", type = ActorType } } };
            }
            else if (path == "/repos/fixture/watches/issues/15")
            {
                response = new
                {
                    number = 15,
                    title = "Actual source thread",
                    body = "Provider task data.",
                    labels = new[] { new { name = "squad/auto" } }
                };
            }
            else if (path == "/repos/fixture/watches/commits/" + new string('a', 40))
            {
                response = new { sha = new string('a', 40), files = Array.Empty<object>() };
            }
            else if (request.RequestUri.Query.Contains("per_page=", StringComparison.Ordinal))
            {
                response = _resource is null ? Array.Empty<object>() : [_resource];
            }
            else if (_resource is not null)
            {
                response = _resource;
            }
            else
            {
                return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class TestAuthority : ISquadWatchEventAuthority
    {
        internal bool Writer { get; init; } = true;
        internal bool Workflow { get; init; } = true;
        internal bool ChangeNumber { get; init; }
        internal int Calls { get; private set; }
        public string Binding => "fixture-watch-authority:1";

        public ValueTask<SquadWatchAuthorization?> AuthorizeAsync(SquadWatchEvent source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<SquadWatchAuthorization?>(new(
                ChangeNumber ? source with { Number = source.Number + 1 } : source,
                "fixture-human", "fixture-event:verified", Writer, CommittedWorkflowSha256: Workflow ? new('A', 64) : null));
        }
    }

    private sealed class TestTerminal : ISquadWatchTerminalConnector
    {
        private SquadWatchTerminalReceipt? _receipt;
        internal bool Authorized { get; init; } = true;
        internal bool CannotConfirm { get; init; }
        internal bool ThrowAfterEffect { get; init; }
        internal SquadToolReceiptKind ReceiptKind { get; init; } = SquadToolReceiptKind.HostObservation;
        internal SquadToolOutputStatus OutputStatus { get; init; } = SquadToolOutputStatus.Succeeded;
        internal int Executions { get; private set; }
        internal int Observations { get; private set; }
        public string Binding => "fixture-watch-terminal:1";

        public ValueTask<bool> AuthorizeAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Authorized);
        }

        public ValueTask<SquadWatchTerminalReceipt> ExecuteAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions++;
            _receipt = new(request.OperationId, request.Sha256, Binding, request.Repository, request.SourceRef,
                request.Kind, "999", "https://github.com/fixture/watches/issues/999", new('B', 64),
                request.Kind == SquadWatchTerminalKind.DraftPullRequest, OutputStatus, ReceiptKind);
            if (ThrowAfterEffect)
            {
                throw new SquadHumanTransportException("Synthetic applied provider action lost its response.", outcomeUncertain: true);
            }
            return ValueTask.FromResult(_receipt);
        }

        public ValueTask<SquadWatchTerminalReconciliation> ReconcileAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observations++;
            return ValueTask.FromResult(CannotConfirm ? new SquadWatchTerminalReconciliation(SquadEffectDisposition.CannotConfirm) :
                new(SquadEffectDisposition.Executed, _receipt));
        }
    }
}
