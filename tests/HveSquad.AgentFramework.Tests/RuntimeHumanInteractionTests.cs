using System.Security.Claims;
using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeHumanInteractionTests
{
    private static readonly string[] OriginalChoices = ["First", "Second"];
    private static ClaimsPrincipal Principal(bool authenticated = true, string subject = "original-human") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, "original-host")],
            authenticated ? "original-host-session" : null));

    private static StringWriter Configure(RuntimeTestHost host, bool authorized = true, bool authenticated = true)
    {
        var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        host.Options.HumanApprovalChannel = new InChatApprovalChannel("original-host",
            _ => ValueTask.FromResult(Principal(authenticated)), (_, _, _) => ValueTask.FromResult(authorized), writer);
        return writer;
    }

    private static RuntimeState State(RuntimeTestHost host) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "state.json")))!;

    private static async Task<SquadPendingHumanAction> PendingAsync(SquadRuntime runtime, string? gate = null) =>
        Assert.Single(await runtime.GetPendingHumanActionsAsync(), action => action.Status == "pending" &&
            (gate is null || action.Binding.Gate == gate));

    private static async Task<SquadHumanResponse> ApproveAsync(SquadRuntime runtime, string? gate = null)
    {
        var pending = await PendingAsync(runtime, gate);
        var response = SquadHumanResponse.FromCommand(pending, "/approve", Guid.NewGuid().ToString("N"));
        var submission = await runtime.SubmitHumanResponseAsync(response);
        Assert.True(submission.Accepted, submission.Diagnostic);
        return response;
    }

    private static async Task<SquadRunResult> SuspendAtPlanAsync(RuntimeTestHost host, string producer = "technical-writer",
        string request = "Produce an authorized local guide.", string[]? inputs = null)
    {
        host.QueuePipeline(producer, inputs: inputs);
        using var initial = await host.CreateAsync();
        var routing = await initial.RunAsync(request);
        Assert.Equal(SquadRunStatus.ApprovalRequired, routing.Status);
        _ = await ApproveAsync(initial, "routing");
        if (inputs is { Length: > 0 })
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueStage("researcher");
        var result = await initial.ResumeAsync(routing.RunId);
        Assert.True(result.Status == SquadRunStatus.ApprovalRequired, result.ResponseText);
        _ = await PendingAsync(initial, "plan");
        return result;
    }

    private static async Task<SquadRunResult> SuspendAtImplementationAsync(RuntimeTestHost host, string producer = "technical-writer")
    {
        var prior = await SuspendAtPlanAsync(host, producer);
        using var next = await host.CreateAsync();
        _ = await ApproveAsync(next, "plan");
        host.QueueStage("lead");
        var result = await next.ResumeAsync(prior.RunId);
        Assert.True(result.Status == SquadRunStatus.ApprovalRequired, result.ResponseText);
        _ = await PendingAsync(next, "implementation");
        return result;
    }

    private static async Task<SquadRunResult> SuspendAtToolAsync(RuntimeTestHost host)
    {
        var prior = await SuspendAtImplementationAsync(host, "developer");
        using var next = await host.CreateAsync();
        _ = await ApproveAsync(next, "implementation");
        host.Client.Call("apply_change", []);
        var result = await next.ResumeAsync(prior.RunId);
        Assert.True(result.Status == SquadRunStatus.ApprovalRequired, result.ResponseText);
        _ = await PendingAsync(next, "consequentialtool");
        Assert.Equal(0, host.Writes);
        return result;
    }

    private static void QueueProducedArtifacts(RuntimeTestHost host)
    {
        host.Client.Call("write_artifact", new() { ["content"] = "Actual authorized developer output." });
        host.Client.Call("complete_stage", new()
        {
            ["summary"] = "Actual authorized developer result",
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
        });
        host.Client.Text("Developer stage finished.");
        host.QueueStage("tester");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenDurableGate_WhenRestarting_ResumesSameRunWithoutRepeatingResearch(bool singlePlan)
    {
        using var host = new RuntimeTestHost();
        if (singlePlan)
        {
            host.UseCurrentPlanContract();
        }
        using var writer = Configure(host);
        var pending = await SuspendAtPlanAsync(host);
        var research = Assert.Single(pending.Evidence);
        var history = File.ReadAllBytes(Path.Combine(host.Project, research.HistoryPath));
        var calls = host.Client.Calls.Count;
        using (var waiting = await host.CreateAsync())
        {
            var stillPending = await waiting.ResumeAsync(pending.RunId);
            Assert.Equal(SquadRunStatus.ApprovalRequired, stillPending.Status);
            Assert.Equal(calls, host.Client.Calls.Count);
            Assert.Single(stillPending.PendingHumanActions!);
        }
        using (var approvePlan = await host.CreateAsync())
        {
            _ = await ApproveAsync(approvePlan, "plan");
            host.QueueStage("lead");
            var implementation = await approvePlan.ResumeAsync(pending.RunId);
            Assert.Equal(SquadRunStatus.ApprovalRequired, implementation.Status);
        }
        using var finish = await host.CreateAsync();
        _ = await ApproveAsync(finish, "implementation");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        var result = await finish.ResumeAsync(pending.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(pending.RunId, result.RunId);
        Assert.Equal(["research", "plan", "produce", "review"], result.Evidence.Select(evidence => evidence.Stage));
        Assert.Equal(research.DispatchId, result.Evidence[0].DispatchId);
        Assert.Equal(history, File.ReadAllBytes(Path.Combine(host.Project, research.HistoryPath)));
        var state = State(host);
        Assert.Null(state.Checkpoint);
        Assert.Equal(3, state.HumanDecisions!.Count);
        Assert.All(state.HumanDecisions, receipt =>
        {
            Assert.Equal("in-chat", receipt.Provider);
            Assert.True(HumanActions.IsHash(receipt.SubjectSha256));
            Assert.Single(state.NativeOperations, operation => operation.HumanDecision?.Binding.Id == receipt.Binding.Id);
        });
        Assert.True((await finish.ReconcileEvidenceAsync()).HistoryVerified);
        Assert.DoesNotContain("authorized local guide", writer.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("root")]
    [InlineData("gate")]
    [InlineData("round")]
    [InlineData("action")]
    [InlineData("evidence")]
    [InlineData("payload")]
    [InlineData("host")]
    [InlineData("revision")]
    [InlineData("dispatch")]
    public async Task GivenWrongBinding_WhenSubmitting_RejectsWithoutChangingStateOrExecutingAnything(string changed)
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        _ = await runtime.RunAsync("Produce a bounded local document.");
        var pending = await PendingAsync(runtime);
        var wrong = pending.Binding;
        wrong = changed switch
        {
            "run" => wrong with { RunId = Guid.NewGuid().ToString("N") },
            "root" => wrong with { RootSha256 = new('B', 64) },
            "gate" => wrong with { Gate = "implementation" },
            "round" => wrong with { RoundId = Guid.NewGuid().ToString("N") },
            "action" => wrong with { ActionSha256 = new('B', 64) },
            "evidence" => wrong with { EvidenceSha256 = new('B', 64) },
            "payload" => wrong with { PayloadSha256 = new('B', 64) },
            "host" => wrong with { HostBinding = new('B', 64) },
            "revision" => wrong with { Revision = wrong.Revision + 1 },
            "dispatch" => wrong with { DispatchId = Guid.NewGuid().ToString("N") },
            _ => throw new InvalidOperationException("Unknown test binding."),
        };
        var before = File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "state.json"));
        var calls = host.Client.Calls.Count;
        var result = await runtime.SubmitHumanResponseAsync(new(wrong, SquadHumanDecision.Approve, "original-response"));

        Assert.False(result.Accepted);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "state.json")));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(0, host.Writes);
        Assert.Contains(host.Log.Messages, message => message.Contains("Human response was rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenAcceptedResponse_WhenReplayed_RejectsItAndDoesNotExecuteAProjectEffect()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var result = await SuspendAtToolAsync(host);
        using var runtime = await host.CreateAsync();
        var response = await ApproveAsync(runtime);
        var replay = await runtime.SubmitHumanResponseAsync(response);
        Assert.False(replay.Accepted);
        Assert.Equal(0, host.Writes);

        host.Client.Call("apply_change", []);
        QueueProducedArtifacts(host);
        var completed = await runtime.ResumeAsync(result.RunId);
        Assert.True(completed.Status == SquadRunStatus.Completed, completed.ResponseText);
        Assert.Equal(1, host.Writes);
        Assert.Single(completed.Effects!);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GivenUnauthorizedOrUnauthenticatedActor_WhenSubmitting_DeniesDespiteValidAction(bool authorized, bool authenticated)
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host, authorized, authenticated);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        _ = await runtime.RunAsync("Produce a bounded guide.");
        var pending = await PendingAsync(runtime);

        var result = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/approve", "claimed-human"));

        Assert.False(result.Accepted);
        Assert.Null(State(host).HumanDecisions);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenExpiredChallenge_WhenSubmittingAndResuming_RequiresExplicitRenewalAndNeverTreatsTimeoutAsApproval()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var clock = new HumanTestClock();
        host.Options.TimeProvider = clock;
        host.Options.HumanResponseTimeout = TimeSpan.FromMinutes(5);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Produce a bounded guide.");
        var old = await PendingAsync(runtime);
        clock.UtcNow = old.ExpiresAt;
        var calls = host.Client.Calls.Count;

        var denied = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(old, "/approve", "expired-response"));
        var waiting = await runtime.ResumeAsync(run.RunId);
        var renewed = await runtime.RenewHumanActionAsync(old.Binding.Id);

        Assert.False(denied.Accepted);
        Assert.Equal(SquadRunStatus.ApprovalRequired, waiting.Status);
        Assert.NotEqual(old.Binding.Id, renewed.Binding.Id);
        Assert.Equal(clock.UtcNow, renewed.CreatedAt);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.False((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(old, "/approve", "old-replay"))).Accepted);
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(renewed, "/approve", "new-response"))).Accepted);
    }

    [Fact]
    public async Task GivenChangedActualInput_WhenSubmitting_RejectsTheOldApprovalBeforeAnyModelCall()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Original authorized input.");
        host.QueuePipeline("technical-writer", inputs: ["requirements.txt"]);
        using var runtime = await host.CreateAsync();
        _ = await runtime.RunAsync("Produce the local document from the requirement.");
        var pending = await PendingAsync(runtime);
        var calls = host.Client.Calls.Count;
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Changed input not approved by that response.");

        var result = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/approve", "stale-input"));

        Assert.False(result.Accepted);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenPrivatePendingAction_WhenReadingSafeViews_ExcludesRequestArgumentsAndHumanIdentity()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(Microsoft.Extensions.AI.AIFunctionFactory.Create((string privateText) => privateText, "private_write"),
            "edit", ["developer"], SquadToolEffect.ProjectWrite, _ => new(["implementation.txt"]),
            ProjectWritePaths: _ => ["implementation.txt"]));
        var prior = await SuspendAtImplementationAsync(host, "developer");
        using var runtime = await host.CreateAsync();
        _ = await ApproveAsync(runtime, "implementation");
        host.Client.Call("private_write", new() { ["privateText"] = "PRIVATE-PENDING-ARGUMENT" });
        var pendingResult = await runtime.ResumeAsync(prior.RunId);
        var pending = await PendingAsync(runtime);
        var details = await runtime.ReadHumanActionAsync(pending.Binding.Id);

        Assert.Contains("PRIVATE-PENDING-ARGUMENT", details.Approval!.ArgumentsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PENDING-ARGUMENT", JsonSerializer.Serialize(pending), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PENDING-ARGUMENT", JsonSerializer.Serialize(pendingResult.PendingHumanActions), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PENDING-ARGUMENT", writer.ToString(), StringComparison.Ordinal);
        var checkpoint = State(host).Checkpoint!;
        Assert.DoesNotContain("PRIVATE-PENDING-ARGUMENT", JsonSerializer.Serialize(checkpoint.HumanActions), StringComparison.Ordinal);
        Assert.DoesNotContain("original-human", JsonSerializer.Serialize(State(host).HumanDecisions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenPendingQuestion_WhenAnsweredAcrossRestart_PreservesExactHumanContextWithoutGrantingApproval()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.Client.Call("ask_host", new() { ["question"] = "Which PRIVATE-QUESTION source?", ["choices"] = OriginalChoices });
        string runId;
        using (var first = await host.CreateAsync())
        {
            var pendingRun = await first.RunAsync("Produce a bounded document.");
            Assert.Equal(SquadRunStatus.InputRequired, pendingRun.Status);
            runId = pendingRun.RunId;
            var pending = await PendingAsync(first);
            Assert.False((await first.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/approve", "not-an-answer"))).Accepted);
            Assert.True((await first.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/answer: 2", "real-answer"))).Accepted);
        }
        host.QueuePipeline("technical-writer");
        using var next = await host.CreateAsync();
        var routing = await next.ResumeAsync(runId);

        Assert.Equal(SquadRunStatus.ApprovalRequired, routing.Status);
        Assert.Single(State(host).ConfirmedAnswers, answer => answer.Answer == "2" && answer.HumanActionId is not null);
        Assert.Contains(host.Client.Calls, call => call.Messages.Any(message =>
            message.Text?.Contains("Which PRIVATE-QUESTION source?", StringComparison.Ordinal) == true &&
            message.Text.Contains("\"Answer\":\"2\"", StringComparison.Ordinal)));
        Assert.DoesNotContain("PRIVATE-QUESTION", writer.ToString(), StringComparison.Ordinal);
        Assert.Null(State(host).Checkpoint!.Routing);
    }

    [Fact]
    public async Task GivenAuthenticatedChanges_WhenResuming_ReopensPlanAndPreservesOriginalResearchAndAppendOnlyBytes()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var implementation = await SuspendAtImplementationAsync(host);
        var originalPlan = Assert.Single(implementation.Evidence, evidence => evidence.Stage == "plan");
        var research = Assert.Single(implementation.Evidence, evidence => evidence.Stage == "research");
        var oldPlanBytes = File.ReadAllBytes(Path.Combine(host.Project, originalPlan.ArtifactPath));
        using var runtime = await host.CreateAsync();
        var pending = await PendingAsync(runtime);
        var change = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending,
            "/changes: PRIVATE-CHANGE-NOTE: include the confirmed acceptance criteria.", "real-changes"));
        var reopened = await runtime.ResumeAsync(implementation.RunId);

        Assert.True(change.Accepted, change.Diagnostic);
        Assert.Equal(SquadRunStatus.ChangesRequested, change.RunStatus);
        Assert.Equal(SquadRunStatus.ApprovalRequired, reopened.Status);
        Assert.Equal(research.DispatchId, Assert.Single(reopened.Evidence).DispatchId);
        var replanning = await PendingAsync(runtime, "plan");
        Assert.True(replanning.Binding.Revision > pending.Binding.Revision);
        Assert.Equal(oldPlanBytes, File.ReadAllBytes(Path.Combine(host.Project, originalPlan.ArtifactPath)));
        Assert.Contains(State(host).DispatchRecords, evidence => evidence.DispatchId == originalPlan.DispatchId);
        Assert.DoesNotContain("PRIVATE-CHANGE-NOTE", writer.ToString(), StringComparison.Ordinal);

        _ = await ApproveAsync(runtime, "plan");
        host.QueueStage("lead");
        _ = await runtime.ResumeAsync(implementation.RunId);
        Assert.Contains(host.Client.Calls, call => call.Messages.Any(message =>
            message.Text?.Contains("PRIVATE-CHANGE-NOTE", StringComparison.Ordinal) == true));
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Fact]
    public async Task GivenToolChanges_WhenResuming_ReusesExactDispatchButRequiresAFreshConsequentialApproval()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var run = await SuspendAtToolAsync(host);
        using var runtime = await host.CreateAsync();
        var old = await PendingAsync(runtime);
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(old,
            "/changes: Review the requested change before executing.", "tool-changes"))).Accepted);
        host.Client.Call("apply_change", []);
        var pending = await runtime.ResumeAsync(run.RunId);
        var fresh = await PendingAsync(runtime);

        Assert.Equal(SquadRunStatus.ApprovalRequired, pending.Status);
        Assert.Equal(old.Binding.DispatchId, fresh.Binding.DispatchId);
        Assert.NotEqual(old.Binding.Id, fresh.Binding.Id);
        Assert.Equal(0, host.Writes);
        Assert.False((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(old, "/approve", "old-action"))).Accepted);
    }

    [Fact]
    public async Task GivenAuthenticatedStop_WhenResuming_TerminatesWithoutModelOrEffectReplayAndPreservesSpend()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var pending = await SuspendAtPlanAsync(host);
        using var runtime = await host.CreateAsync();
        var action = await PendingAsync(runtime);
        var calls = host.Client.Calls.Count;
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(action, "/stop", "real-stop"))).Accepted);

        var stopped = await runtime.ResumeAsync(pending.RunId);

        Assert.Equal(SquadRunStatus.Stopped, stopped.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(pending.Evidence[0].DispatchId, Assert.Single(stopped.Evidence).DispatchId);
        Assert.NotNull(stopped.Consumption);
        Assert.Null(State(host).Checkpoint);
        Assert.Contains(State(host).Runs, run => run.RunId == pending.RunId && run.Status == SquadRunStatus.Stopped);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenTwoIdenticalToolCalls_WhenResuming_RequiresTwoDistinctActualActionApprovals()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var pendingRun = await SuspendAtToolAsync(host);
        using var runtime = await host.CreateAsync();
        var first = await PendingAsync(runtime);
        _ = await ApproveAsync(runtime);
        host.Client.Call("apply_change", []);
        host.Client.Call("apply_change", []);
        var secondRun = await runtime.ResumeAsync(pendingRun.RunId);
        var second = await PendingAsync(runtime);

        Assert.Equal(SquadRunStatus.ApprovalRequired, secondRun.Status);
        Assert.Equal(1, host.Writes);
        Assert.NotEqual(first.Binding.ActionSha256, second.Binding.ActionSha256);
        _ = await ApproveAsync(runtime);
        host.Client.Call("apply_change", []);
        host.Client.Call("apply_change", []);
        QueueProducedArtifacts(host);
        var completed = await runtime.ResumeAsync(pendingRun.RunId);
        Assert.True(completed.Status == SquadRunStatus.Completed, completed.ResponseText);
        Assert.Equal(2, host.Writes);
        Assert.Equal(2, completed.Effects!.Count);
    }

    [Fact]
    public async Task GivenMissingChannelResponse_WhenPolling_ReportsPendingAndDoesNotInvokeModels()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var pending = await SuspendAtPlanAsync(host);
        using var runtime = await host.CreateAsync();
        var calls = host.Client.Calls.Count;

        var poll = await runtime.PollHumanResponsesAsync(pending.RunId);

        Assert.Empty(poll.Submissions);
        Assert.Single(poll.Diagnostics);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal("pending", (await PendingAsync(runtime)).Status);
    }

    [Fact]
    public async Task GivenChangedProtectedPayload_WhenSubmitting_RejectsWithoutOverwritingTheAlteredBytes()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        _ = await runtime.RunAsync("Produce a local guide.");
        var pending = await PendingAsync(runtime);
        var checkpoint = Assert.Single(State(host).Checkpoint!.HumanActions!);
        var path = Path.Combine(host.Project, checkpoint.PayloadPath);
        var altered = File.ReadAllText(path) + "\n ";
        File.WriteAllText(path, altered);

        var result = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/approve", "altered-payload"));

        Assert.False(result.Accepted);
        Assert.Equal(SquadRunStatus.Blocked, result.RunStatus);
        Assert.Equal(altered, File.ReadAllText(path));
        Assert.Null(State(host).HumanDecisions);
    }

    [Fact]
    public async Task GivenDurableFinancialOvershoot_WhenRestarting_UsesTheSameVerifiedRoundAndSequentialAdmissionAuthority()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        ConsumptionTestData.Configure(host, ".01");
        host.Options.TokenUsageSelector = _ => new(1, 0, 0, 1);
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        string runId;
        string roundId;
        using (var initial = await host.CreateAsync())
        {
            var routed = await initial.RunAsync("Research a bounded local case.");
            runId = routed.RunId;
            _ = await ApproveAsync(initial, "routing");
            host.QueueStage("researcher");
            var financial = await initial.ResumeAsync(runId);
            Assert.True(financial.Status == SquadRunStatus.ApprovalRequired, financial.ResponseText);
            var action = await PendingAsync(initial, "costovershoot");
            roundId = action.Binding.RoundId!;
            var details = await initial.ReadHumanActionAsync(action.Binding.Id);
            Assert.Equal("over-ceiling", details.Approval!.CostPreflight!.Decision);
            Assert.Equal(roundId, details.Approval.CostPreflight.RoundId);
            _ = await ApproveAsync(initial, "costovershoot");
        }
        using var resumed = await host.CreateAsync();

        var completed = await resumed.ResumeAsync(runId);

        Assert.True(completed.Status == SquadRunStatus.ResearchCompleted, completed.ResponseText);
        var approved = State(host).CostPreflights!.Where(round => round.Decision == "approved-over-ceiling").ToArray();
        Assert.NotEmpty(approved);
        Assert.Contains(approved, round => round.ApprovedFrom == roundId);
        Assert.All(approved, round => Assert.Single(round.PermittedSlots));
        Assert.True(completed.Consumption!.EstimatedCostUsd < .01m);
        Assert.Single(State(host).HumanDecisions!, receipt => receipt.Binding.Gate == "costovershoot");
    }

    [Fact]
    public async Task GivenExactProducingSet_WhenAwaitingApproval_BindsAllOwnersAndRejectsAChangedPlan()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.UseCurrentPlanContract();
        host.PlanTransform = content => content.Replace("## User Decisions and Requirements", """
            ## Deliverables
            | Id | Role | Tasks | Depends On | Project Outputs |
            | --- | --- | --- | --- | --- |
            | guide | technical-writer | P01-T01 | None | None |
            | summary | product-owner | P01-T01 | guide | None |

            ## User Decisions and Requirements
            """, StringComparison.Ordinal);
        var implementation = await SuspendAtImplementationAsync(host);
        using var runtime = await host.CreateAsync();
        var action = await PendingAsync(runtime, "implementation");
        var details = await runtime.ReadHumanActionAsync(action.Binding.Id);
        Assert.Equal(["technical-writer", "product-owner"], details.Approval!.ProductionPlan!.Units.Select(unit => unit.Role));
        Assert.Equal("guide", Assert.Single(details.Approval.ProductionPlan.Units[1].Dependencies));
        var plan = Assert.Single(implementation.Evidence, evidence => evidence.Stage == "plan");
        File.AppendAllText(Path.Combine(host.Project, plan.ArtifactPath), "\nChanged plan content.");

        var denied = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(action, "/approve", "old-producing-set"));

        Assert.False(denied.Accepted);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenPreviouslyConsumedSource_WhenSubmittingForANewGate_RejectsReplayEvenWithTheNewBinding()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Produce a bounded local document.");
        var routing = await PendingAsync(runtime);
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(routing, "/approve", "one-source"))).Accepted);
        host.QueueStage("researcher");
        _ = await runtime.ResumeAsync(run.RunId);
        var plan = await PendingAsync(runtime);

        var replay = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(plan, "/approve", "one-source"));

        Assert.False(replay.Accepted);
        Assert.Equal("pending", (await PendingAsync(runtime)).Status);
        Assert.Single(State(host).HumanDecisions!);
    }

    [Fact]
    public async Task GivenUnknownFinancialFacts_WhenResuming_NeverCreatesAnApprovableCostChallenge()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        ConsumptionTestData.Configure(host, "10", observations: 0);
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        using var runtime = await host.CreateAsync();
        var routing = await runtime.RunAsync("Research a bounded local case.");
        var original = await ApproveAsync(runtime, "routing");

        var blocked = await runtime.ResumeAsync(routing.RunId);

        Assert.Equal(SquadRunStatus.InputRequired, blocked.Status);
        Assert.Equal("cannot-confirm", blocked.CostPreflight!.Decision);
        Assert.Empty(await runtime.GetPendingHumanActionsAsync());
        Assert.False((await runtime.SubmitHumanResponseAsync(original)).Accepted);
        Assert.DoesNotContain(State(host).HumanDecisions!, receipt => receipt.Binding.Gate == "costovershoot");
        Assert.Single(host.Client.Calls);
    }

    [Fact]
    public async Task GivenParallelOwnerQuestions_WhenOneRequestsChanges_ReopensItsWorkWithoutWaitingForUnrelatedApproval()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        using var owners = new QuestioningClient();
        host.Options.HumanApprovalChannel = new QuestionPublicationBarrier(host.Options.HumanApprovalChannel!);
        host.Options.MaxConcurrentDispatches = 2;
        host.UseCurrentPlanContract();
        host.PlanTransform = content => content.Replace("## User Decisions and Requirements", """
            ## Deliverables
            | Id | Role | Tasks | Depends On | Project Outputs |
            | --- | --- | --- | --- | --- |
            | guide | technical-writer | P01-T01 | None | None |
            | summary | product-owner | P01-T01 | None | None |

            ## User Decisions and Requirements
            """, StringComparison.Ordinal);
        host.Options.ModelSelector = (entry, _) => new(entry?.Role is "technical-writer" or "product-owner" ? owners : host.Client);
        var implementation = await SuspendAtImplementationAsync(host);
        using var runtime = await host.CreateAsync();
        _ = await ApproveAsync(runtime, "implementation");
        var pendingRun = await runtime.ResumeAsync(implementation.RunId);
        var questions = (await runtime.GetPendingHumanActionsAsync()).Where(action => action.Status == "pending").ToArray();
        Assert.Equal(SquadRunStatus.InputRequired, pendingRun.Status);
        Assert.Equal(2, questions.Length);
        Assert.Equal(2, questions.Select(question => question.Binding.DispatchId).Distinct(StringComparer.Ordinal).Count());
        var change = SquadHumanResponse.FromCommand(questions[0], "/changes: PRIVATE-OWNER-NOTE: clarify this owner's source.", "owner-changes");
        Assert.True((await runtime.SubmitHumanResponseAsync(change)).Accepted);

        var reopened = await runtime.ResumeAsync(implementation.RunId);

        Assert.Equal(SquadRunStatus.InputRequired, reopened.Status);
        Assert.True(owners.Calls > 2);
        var fresh = (await runtime.GetPendingHumanActionsAsync()).Where(action => action.Status == "pending").ToArray();
        Assert.Equal(2, fresh.Length);
        Assert.All(fresh, action => Assert.True(action.Binding.Revision > questions[0].Binding.Revision));
        Assert.DoesNotContain("PRIVATE-OWNER-NOTE", writer.ToString(), StringComparison.Ordinal);
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenInterruptedDecisionContext_WhenResuming_RestoresOnlyTheAuthenticatedReceiptWithoutAskingAgain(bool stop)
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.Client.Call("ask_host", new() { ["question"] = "Which actual approved source?" });
        string runId;
        using (var initial = await host.CreateAsync())
        {
            var run = await initial.RunAsync("Produce a bounded local document.");
            runId = run.RunId;
            var pending = await PendingAsync(initial);
            var response = SquadHumanResponse.FromCommand(pending, stop ? "/stop" : "/answer: PRIVATE-AUTHENTIC-ANSWER", "durable-receipt");
            Assert.True((await initial.SubmitHumanResponseAsync(response)).Accepted);
        }
        var files = new ProjectFiles(host.Project, host.Options.MaxArtifactCharacters, host.Options.SquadRoot);
        var scribe = new RuntimeScribe(files);
        var intermediate = scribe.Load()!;
        intermediate.Checkpoint!.HumanStopRequested = false;
        intermediate.ConfirmedAnswers.Clear();
        scribe.Save(intermediate);
        if (!stop)
        {
            host.QueuePipeline("technical-writer");
        }
        using var resumed = await host.CreateAsync();
        var calls = host.Client.Calls.Count;

        var result = await resumed.ResumeAsync(runId);

        Assert.Equal(stop ? SquadRunStatus.Stopped : SquadRunStatus.ApprovalRequired, result.Status);
        Assert.Single(State(host).HumanDecisions!);
        if (stop)
        {
            Assert.Equal(calls, host.Client.Calls.Count);
            Assert.Null(State(host).Checkpoint);
        }
        else
        {
            Assert.Equal("PRIVATE-AUTHENTIC-ANSWER", Assert.Single(State(host).ConfirmedAnswers).Answer);
            Assert.Contains(host.Client.Calls.Last().Messages, message =>
                message.Text?.Contains("PRIVATE-AUTHENTIC-ANSWER", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task GivenCouncilConditionsAndAuthenticatedChanges_WhenReplanning_PreservesOriginalVerdictAndRequiresFreshExactSeats()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        host.Options.Council = new() { Roles = ["architect"] };
        var planPending = await SuspendAtPlanAsync(host);
        using var runtime = await host.CreateAsync();
        _ = await ApproveAsync(runtime, "plan");
        host.QueueStage("lead");
        var council = await runtime.ResumeAsync(planPending.RunId);
        Assert.Equal(SquadRunStatus.ApprovalRequired, council.Status);
        var councilAction = await PendingAsync(runtime, "council");
        var councilDetails = await runtime.ReadHumanActionAsync(councilAction.Binding.Id);
        Assert.Equal("architect", Assert.Single(councilDetails.Approval!.Council!.Roles));
        _ = await ApproveAsync(runtime, "council");
        host.QueueStage("architect", "Conditional");
        var implementation = await runtime.ResumeAsync(planPending.RunId);
        Assert.True(implementation.Status == SquadRunStatus.ApprovalRequired, implementation.ResponseText);
        var implementationAction = await PendingAsync(runtime, "implementation");
        var implementationDetails = await runtime.ReadHumanActionAsync(implementationAction.Binding.Id);
        var conditions = implementationDetails.Approval!.CouncilVerdict!.Conditions;
        Assert.NotEmpty(conditions);
        Assert.All(conditions, condition => Assert.Equal("architect", condition.Role));
        var oldVerdict = State(host).GateRecords!.Single(gate => gate.Kind == "Council Verdict");
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(implementationAction,
            "/changes: Revisit the exact design conditions in the lead plan.", "council-changes"))).Accepted);
        _ = await runtime.ResumeAsync(planPending.RunId);
        _ = await ApproveAsync(runtime, "plan");
        host.QueueStage("lead");
        _ = await runtime.ResumeAsync(planPending.RunId);
        _ = await ApproveAsync(runtime, "council");
        host.QueueStage("architect", "Conditional");

        var fresh = await runtime.ResumeAsync(planPending.RunId);

        Assert.True(fresh.Status == SquadRunStatus.ApprovalRequired, fresh.ResponseText);
        var verdicts = State(host).GateRecords!.Where(gate => gate.Kind == "Council Verdict").ToArray();
        Assert.Equal(2, verdicts.Length);
        Assert.Contains(verdicts, verdict => HumanActions.Serialize(verdict) == HumanActions.Serialize(oldVerdict));
        Assert.Contains(verdicts, verdict => verdict.Id.Contains("-human-revision-", StringComparison.Ordinal));
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Fact]
    public async Task GivenReturnedHumanIntakeQuestion_WhenAnswered_RequiresIndependentBoundedRevalidationAndPreservesOriginalBytes()
    {
        using var host = new RuntimeTestHost();
        using var writer = Configure(host);
        var input = Path.Combine(host.Project, "requirements.txt");
        File.WriteAllText(input, "Original private requirement with a human-owned acceptance threshold.");
        var original = File.ReadAllBytes(input);
        host.QueuePipeline("technical-writer", inputs: ["requirements.txt"]);
        using var runtime = await host.CreateAsync();
        var routing = await runtime.RunAsync("Produce the bounded local guide from supplied criteria.");
        _ = await ApproveAsync(runtime, "routing");
        host.Client.Call("list_gate_inputs", []);
        host.Client.Steps.Enqueue((messages, _) =>
        {
            var sources = JsonSerializer.Deserialize<SquadArtifactEvidence[]>(messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Last().Result!.ToString()!)!;
            return RuntimeTestHost.Function("complete_return", new()
            {
                ["summary"] = "Independent intake needs one actual human fact.",
                ["verdict"] = "Not-Ready",
                ["risk"] = "Low",
                ["blockingIssues"] = 1,
                ["payload"] = new SquadIntakeFindings(ReadinessContracts.IntakeDimensions,
                    [new("acceptance-threshold", "Completeness", "What is the actual approved acceptance threshold?", true, true, sources, [])],
                    Array.Empty<string>(), Array.Empty<SquadIntakeAssumption>()),
            });
        });
        host.Client.Text("Independent intake returned its question.");
        var questionRun = await runtime.ResumeAsync(routing.RunId);
        Assert.True(questionRun.Status == SquadRunStatus.InputRequired, questionRun.ResponseText);
        var question = await PendingAsync(runtime, "question");
        Assert.Equal("intake-validator", question.Binding.Role);
        Assert.Equal("intake", question.Binding.Step);
        Assert.True((await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(question,
            "/answer: PRIVATE-HUMAN-THRESHOLD: one measured local example.", "real-intake-answer"))).Accepted);
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");

        var resumed = await runtime.ResumeAsync(routing.RunId);

        Assert.True(resumed.Status == SquadRunStatus.ApprovalRequired, resumed.ResponseText);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(1, State(host).Checkpoint!.Readiness!.IntakeCycles);
        Assert.Contains(resumed.Evidence, evidence => evidence.Stage == "validation");
        Assert.Equal(2, State(host).GateRecords!.Count(gate => gate.Kind == "Intake Readiness Verdict"));
        Assert.Contains(host.Client.Calls, call => call.Messages.Any(message =>
            message.Text?.Contains("PRIVATE-HUMAN-THRESHOLD", StringComparison.Ordinal) == true));
        Assert.DoesNotContain("PRIVATE-HUMAN-THRESHOLD", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class QuestioningClient : IChatClient
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) >= 2)
            {
                _both.TrySetResult();
            }
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"),
                "ask_host", new Dictionary<string, object?> { ["question"] = "Which authorized source should this owner use?" })]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Acceptance uses actual non-streaming MAF calls.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class QuestionPublicationBarrier(ISquadHumanApprovalChannel inner) : ISquadHumanApprovalChannel
    {
        private readonly Lock _sync = new();
        private TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _published;
        private int _revision = -1;

        public string Name => inner.Name;
        public string Binding => inner.Binding;

        public async ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken)
        {
            if (action.IsQuestion)
            {
                Task both;
                using (_sync.EnterScope())
                {
                    if (_revision != action.Binding.Revision)
                    {
                        _revision = action.Binding.Revision;
                        _published = 0;
                        _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    if (++_published == 2)
                    {
                        _both.TrySetResult();
                    }
                    both = _both.Task;
                }
                await both.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return await inner.PublishAsync(action, cancellationToken);
        }

        public ValueTask<IReadOnlyList<SquadHumanResponse>> ReadResponsesAsync(
            SquadPendingHumanAction action, CancellationToken cancellationToken) =>
            inner.ReadResponsesAsync(action, cancellationToken);

        public ValueTask<SquadHumanAuthorization?> AuthorizeAsync(SquadPendingHumanAction action,
            SquadHumanResponse response, CancellationToken cancellationToken) =>
            inner.AuthorizeAsync(action, response, cancellationToken);

        public ValueTask<SquadHumanDelivery> ResolveAsync(SquadPendingHumanAction action,
            SquadHumanDecision decision, CancellationToken cancellationToken) =>
            inner.ResolveAsync(action, decision, cancellationToken);
    }
}

internal sealed class HumanTestClock : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
