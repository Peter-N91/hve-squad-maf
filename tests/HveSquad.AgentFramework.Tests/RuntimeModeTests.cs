using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeModeTests
{
    private static string StatePath(RuntimeTestHost host) => Path.Combine(host.Project, host.Options.SquadRoot, "state.json");
    private static RuntimeState State(RuntimeTestHost host) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;

    internal static void Mode(RuntimeTestHost host, string mode, bool safe = true)
    {
        host.Options.Mode = mode;
        host.Options.MaxConcurrentDispatches = 1;
        if (safe)
        {
            host.Options.Tools[0] = host.Options.Tools[0] with { Impact = SquadToolImpact.Safe };
        }
    }

    internal static StringWriter Durable(RuntimeTestHost host)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "original-mode-operator")], "original-mode-session"));
        host.Options.HumanApprovalChannel = new InChatApprovalChannel("mode-authority",
            _ => ValueTask.FromResult(principal), (_, _, _) => ValueTask.FromResult(true), writer);
        return writer;
    }

    internal static async Task<SquadPendingHumanAction> RespondAsync(SquadRuntime runtime, string gate, string command = "/approve")
    {
        var pending = Assert.Single(await runtime.GetPendingHumanActionsAsync(), item => item.Status == "pending");
        Assert.Equal(gate, pending.Binding.Gate);
        var submitted = await runtime.SubmitHumanResponseAsync(
            SquadHumanResponse.FromCommand(pending, command, Guid.NewGuid().ToString("N")));
        Assert.True(submitted.Accepted, submitted.Diagnostic);
        return pending;
    }

    internal static void Council(RuntimeTestHost host, string role = "architect", string verdict = "Approve",
        string risk = "Low", bool condition = false, bool resolve = false, bool cost = false,
        bool compliance = false, bool humanIssue = false, bool issue = false, ScriptedRuntimeChatClient? client = null)
    {
        client ??= host.Client;
        if (!RuntimeTestHost.ReturnsOnly(role))
        {
            client.Call("write_artifact", new() { ["content"] = "Original independent council assessment." });
        }
        client.Call("list_gate_inputs", []);
        client.Steps.Enqueue((messages, _) =>
        {
            var response = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last().Result!;
            var sources = JsonSerializer.Deserialize<SquadArtifactEvidence[]>(response.ToString()!)!;
            var outputs = sources.Where(source => source.Path == "implementation.txt" ||
                source.Path.Contains("/developer/", StringComparison.Ordinal)).ToArray();
            var findings = new SquadCouncilFindings(
                humanIssue || issue ? [new("stable-issue", role, "An actual attributed issue.", humanIssue, humanIssue, sources, [])] : [],
                condition ? [new("local-boundary", "Prove the bounded implementation boundary.", sources, [])] : [],
                [], ResolvedConditions: resolve ?
                    [new("local-boundary", "Independently checked the actual produced local output.", outputs)] : null,
                RequiresCostConfirmation: cost, ComplianceViolation: compliance);
            return RuntimeTestHost.Function(RuntimeTestHost.ReturnsOnly(role) ? "complete_return" : "complete_stage", new()
            {
                ["summary"] = "Actual independent findings.",
                ["verdict"] = verdict,
                ["risk"] = risk,
                ["blockingIssues"] = humanIssue ? 1 : 0,
                ["payload"] = findings,
            });
        });
        client.Text("Council seat finished.");
    }

    internal static void FirstDraft(RuntimeTestHost host, string initialVerdict = "Approve", bool condition = false)
    {
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        Council(host, verdict: initialVerdict, condition: condition);
        host.QueueStage("developer");
    }

    private static void Success(RuntimeTestHost host, bool tail = true)
    {
        FirstDraft(host);
        Council(host);
        host.QueueStage("tester");
        if (tail)
        {
            host.Client.Text("Actual bounded mode delivery.");
        }
    }

    [Theory]
    [InlineData("autonomous", false)]
    [InlineData("autopilot", true)]
    public async Task GivenTrustedMode_WhenDelivering_ExecutesRealMethodologyWithoutRoutineStageApprovals(string mode, bool final)
    {
        using var host = new RuntimeTestHost();
        Mode(host, mode);
        Success(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(1, host.Writes);
        Assert.Equal(["research", "plan", "council", "produce", "council", "review"],
            result.Evidence.Select(item => item.Stage));
        Assert.True(result.ModeOutcome!.Converged);
        Assert.Equal(final, result.ModeOutcome.FinalValidated);
        Assert.Single(result.ModeOutcome.Iterations);
        Assert.Equal("autonomy-validation:1:architect", result.Evidence.Last(item => item.Stage == "council").UnitSlot);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind is SquadApprovalKind.Routing or SquadApprovalKind.Plan or
            SquadApprovalKind.Implementation or SquadApprovalKind.Council or SquadApprovalKind.ConsequentialTool);
        Assert.Equal(final, host.Approvals.Any(approval => approval.Kind == SquadApprovalKind.FinalOutcome));
        var state = State(host);
        Assert.Null(state.Checkpoint);
        var summaries = state.GateRecords!.Where(record => record.Kind == "Mode Outcome").ToArray();
        Assert.Contains(summaries, record => ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload).Result.Outcome == "completed");
        var history = Directory.GetFiles(Path.Combine(host.Project, host.Options.SquadRoot, "history"),
            mode == "autopilot" ? "autopilot-run-*.md" : "autonomous-loop-*.md");
        Assert.Contains("Opt-In: mode=" + mode, File.ReadAllText(Assert.Single(history)), StringComparison.Ordinal);
        Assert.All(result.Evidence, evidence => Assert.Contains(evidence.DispatchId,
            File.ReadAllText(Path.Combine(host.Project, evidence.HistoryPath)), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenUnclassifiedHostWrite_WhenUsingAMode_RequiresItsOwnExactGate(string mode)
    {
        using var host = new RuntimeTestHost();
        Mode(host, mode, safe: false);
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.ConsequentialTool);
        };
        FirstDraft(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(0, host.Writes);
        var gate = Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.ConsequentialTool);
        Assert.NotNull(gate.DispatchId);
        Assert.NotNull(gate.ActionId);
        Assert.Equal(["implementation.txt"], gate.ProjectWritePaths);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "produce");
    }

    [Theory]
    [InlineData(SquadToolAction.Deployment)]
    [InlineData(SquadToolAction.Push)]
    [InlineData(SquadToolAction.Merge)]
    [InlineData(SquadToolAction.Migration)]
    [InlineData(SquadToolAction.Deletion)]
    [InlineData(SquadToolAction.SecretRotation)]
    [InlineData(SquadToolAction.Compliance)]
    [InlineData(SquadToolAction.CostConfirmation)]
    [InlineData(SquadToolAction.Irreversible)]
    public async Task GivenMandatoryActionMarkedSafe_WhenUsingAutopilot_ModeCannotAuthorizeIt(SquadToolAction action)
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.Tools[0] = host.Options.Tools[0] with { Action = action };
        host.Options.ApproveAsync = (approval, _) => ValueTask.FromResult(approval.Kind != SquadApprovalKind.ConsequentialTool);
        FirstDraft(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenAStopCouncil_WhenExecutingAMode_EscalatesBeforeAnyProducingCall(string mode)
    {
        using var host = new RuntimeTestHost();
        Mode(host, mode);
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        Council(host, verdict: "Block");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.RiskGate);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "produce");
        Assert.Empty(result.ModeOutcome!.Iterations);
    }

    [Theory]
    [InlineData("security", true, false, false, false)]
    [InlineData("cost-manager", false, true, false, false)]
    [InlineData("security", false, false, true, false)]
    [InlineData("rai", false, false, true, false)]
    [InlineData("architect", false, false, false, true)]
    public async Task GivenMandatoryFindingEvenWithApprove_WhenRunningAutopilot_StopsForSpecificHumanRisk(
        string role, bool high, bool cost, bool compliance, bool human)
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.Council = new() { Roles = [role] };
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        Council(host, role, risk: high ? "High" : "Low", cost: cost, compliance: compliance, humanIssue: human);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded local documentation.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains(host.Approvals, approval => approval.Kind == SquadApprovalKind.RiskGate);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "produce");
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenOutstandingConditions_WhenCycleTwoConverges_StillSurfacesItToHumanAndPreservesExactOutputVersions(string mode)
    {
        using var host = new RuntimeTestHost();
        Mode(host, mode);
        var writes = 0;
        host.Options.Tools[0] = host.Options.Tools[0] with
        {
            Function = AIFunctionFactory.Create(() =>
            {
                File.WriteAllText(Path.Combine(host.Project, "implementation.txt"), "actual revision " + (++writes).ToString(CultureInfo.InvariantCulture));
                return "Applied the actual bounded revision.";
            }, "apply_change"),
        };
        FirstDraft(host, "Conditional", condition: true);
        Council(host, verdict: "Conditional", condition: true);
        host.QueueStage("developer");
        Council(host, resolve: true);
        host.QueueStage("tester");
        host.Client.Text("Two real independently validated drafts.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, writes);
        Assert.Equal([1, 2], result.ModeOutcome!.Iterations.Select(item => item.Cycle));
        Assert.True(result.ModeOutcome.Converged);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.AutonomousOutcome);
        var state = State(host);
        var snapshot = Assert.Single(state.OutputRevisions!);
        Assert.Equal("actual revision 1", File.ReadAllText(Path.Combine(host.Project, snapshot.ArchivePath)));
        Assert.Equal("actual revision 2", File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
        Assert.Single(state.NativeOperations, operation => operation.OutputRevision?.Id == snapshot.Id);
        var outputs = result.Evidence.Where(item => item.Stage == "produce").ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.NotEqual(outputs[0].ExecutedHostTools![0].Outputs![0].Sha256, outputs[1].ExecutedHostTools![0].Outputs![0].Sha256);
        Assert.Equal(2, result.Effects!.Count);
        Assert.Equal("autonomy:2:produce:developer", outputs[1].UnitSlot);
    }

    [Fact]
    public async Task GivenCycleTwoStillConditional_WhenSurfacingResult_DoesNotRunThirdCycleOrClaimCompletedDelivery()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        FirstDraft(host, "Conditional", condition: true);
        Council(host, verdict: "Conditional", condition: true);
        host.QueueStage("developer");
        Council(host, verdict: "Conditional", condition: true);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(2, host.Writes);
        Assert.Equal(2, result.ModeOutcome!.Iterations.Count);
        Assert.False(result.ModeOutcome.Converged);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.AutonomousOutcome);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "review");
    }

    [Fact]
    public async Task GivenChangedJudgmentOnTheSameStableIssue_WhenRevalidating_EscalatesAsDivergenceWithoutAnotherDraft()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        FirstDraft(host);
        Council(host, verdict: "Concern", issue: true);
        host.QueueStage("developer");
        Council(host, verdict: "Approve", risk: "Medium", issue: true);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("changed their judgment", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(2, host.Writes);
        Assert.Equal(2, result.ModeOutcome!.Iterations.Count);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.RiskGate);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.AutonomousOutcome);
    }

    [Fact]
    public async Task GivenOmittedUnresolvedCondition_WhenValidatorSaysApprove_RejectsFalseConvergence()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        FirstDraft(host, "Conditional", condition: true);
        Council(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("retain every unresolved", result.ResponseText, StringComparison.Ordinal);
        Assert.False(result.ModeOutcome!.Converged);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "review");
    }

    [Fact]
    public async Task GivenDurableFinalGate_WhenRecreatingHost_ResumesOnlyExactHumanValidatedOutcomeWithoutAnyModelReplay()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        using var notifications = Durable(host);
        Success(host, tail: false);
        SquadRunResult initial;
        using (var runtime = await host.CreateAsync())
        {
            initial = await runtime.RunAsync("Implement the bounded local code.");
            Assert.True(initial.Status == SquadRunStatus.ApprovalRequired, initial.ResponseText);
            Assert.False(initial.ModeOutcome!.FinalValidated);
            Assert.Equal("finaloutcome", Assert.Single(initial.PendingHumanActions!).Binding.Gate);
        }
        var calls = host.Client.Calls.Count;
        using var resumed = await host.CreateAsync();
        _ = await RespondAsync(resumed, "finaloutcome");

        var result = await resumed.ResumeAsync(initial.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(initial.RunId, result.RunId);
        Assert.True(result.ModeOutcome!.FinalValidated);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, host.Writes);
        Assert.Single(State(host).HumanDecisions!);
        Assert.DoesNotContain("bounded local code", notifications.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenModeChangedAfterSuspension_WhenResuming_RejectsWithoutModelOrEffectCalls()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        using var notifications = Durable(host);
        Success(host, tail: false);
        SquadRunResult initial;
        using (var runtime = await host.CreateAsync())
        {
            initial = await runtime.RunAsync("Implement the bounded local code.");
        }
        var calls = host.Client.Calls.Count;
        host.Options.Mode = "interactive";
        using var resumed = await host.CreateAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => resumed.ResumeAsync(initial.RunId));

        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public async Task GivenFinalChangesAfterOneValidation_WhenResuming_ReopensOneBoundedDraftAndRequiresFreshCapAndFinalDecisions()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        using var notifications = Durable(host);
        Success(host, tail: false);
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("Implement the bounded local code.");
        Assert.Equal(SquadRunStatus.ApprovalRequired, first.Status);
        var old = await RespondAsync(runtime, "finaloutcome", "/changes: Amend the local bounded draft.");
        host.QueueStage("developer");
        Council(host);

        var cap = await runtime.ResumeAsync(first.RunId);

        Assert.True(cap.Status == SquadRunStatus.ApprovalRequired, cap.ResponseText);
        Assert.Equal(2, host.Writes);
        Assert.Equal(2, cap.ModeOutcome!.Iterations.Count);
        _ = await RespondAsync(runtime, "autonomousoutcome");
        host.QueueStage("tester");
        var final = await runtime.ResumeAsync(first.RunId);
        Assert.True(final.Status == SquadRunStatus.ApprovalRequired, final.ResponseText);
        var fresh = await RespondAsync(runtime, "finaloutcome");
        Assert.NotEqual(old.Binding.Id, fresh.Binding.Id);
        Assert.Equal(1, fresh.Binding.Revision);
        var completed = await runtime.ResumeAsync(first.RunId);
        Assert.True(completed.Status == SquadRunStatus.Completed, completed.ResponseText);
        Assert.Equal(2, host.Writes);
        Assert.Equal([SquadHumanDecision.Changes, SquadHumanDecision.Approve, SquadHumanDecision.Approve],
            State(host).HumanDecisions!.Select(item => item.Decision));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 2)]
    public async Task GivenSafePlanningTransition_WhenDispatching_UsesActualSingleWriterOverlapOnlyWhenHostParallelismExists(
        int concurrency, int pipelined)
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.MaxConcurrentDispatches = concurrency;
        Success(host);
        using var handoffEntered = new ManualResetEventSlim();
        using var roleEntered = new ManualResetEventSlim();
        if (pipelined != 0)
        {
            host.Log.Observer = id =>
            {
                if (id.Name == "PipelinedHandoff")
                {
                    handoffEntered.Set();
                    Assert.True(roleEntered.Wait(TimeSpan.FromSeconds(30)), "The actual lead provider did not overlap the Scribe handoff.");
                }
            };
            var steps = host.Client.Steps.ToArray();
            host.Client.Steps.Clear();
            var leadSeen = 0;
            foreach (var step in steps)
            {
                host.Client.Steps.Enqueue((messages, options) =>
                {
                    if (options?.Instructions?.Contains("Synthetic agent for lead.", StringComparison.Ordinal) == true &&
                        Interlocked.Exchange(ref leadSeen, 1) == 0)
                    {
                        Assert.True(handoffEntered.Wait(TimeSpan.FromSeconds(30)), "No Scribe handoff was actually issued beside lead.");
                        var state = State(host);
                        Assert.Single(state.Checkpoint!.PendingEvidence, item => item.Stage == "research");
                        Assert.DoesNotContain(state.DispatchRecords, item => item.Stage == "research");
                        roleEntered.Set();
                    }
                    return step(messages, options);
                });
            }
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(pipelined, result.ModeOutcome!.PipelinedTransitions);
        Assert.Empty(State(host).Checkpoint?.PendingEvidence ?? []);
        Assert.Equal(result.Evidence.Count, State(host).DispatchRecords.Count);
    }

    [Fact]
    public async Task GivenPipelinedOutputTampering_WhenVerifyingHandoff_LatchesHistoryFailureAndBlocksAllConsumingStages()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.MaxConcurrentDispatches = 4;
        host.Log.Observer = id =>
        {
            if (id.Name == "PipelinedHandoff")
            {
                var research = Assert.Single(State(host).Checkpoint!.PendingEvidence, item => item.Stage == "research");
                File.WriteAllText(Path.Combine(host.Project, research.ArtifactPath), "Deliberately changed protected evidence.");
            }
        };
        Success(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.True(State(host).Checkpoint!.PipeliningDisabled);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "produce" || item.Stage == "review");
    }

    [Fact]
    public async Task GivenAutopilotCostCeiling_WhenAdmittingFirstStage_ReservesFullModeBoundaryAndBothPermittedCycles()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        ConsumptionTestData.Configure(host);
        host.Options.MaxConcurrentDispatches = 4;
        Success(host);
        var observations = new List<(decimal? Actual, decimal Expected)>();
        using var researchSampled = new ManualResetEventSlim();
        using var planSampled = new ManualResetEventSlim();
        var steps = host.Client.Steps.ToArray();
        host.Client.Steps.Clear();
        foreach (var step in steps)
        {
            host.Client.Steps.Enqueue((messages, options) =>
            {
                if (options?.Instructions?.Contains("Synthetic agent for lead.", StringComparison.Ordinal) == true)
                {
                    Assert.True(researchSampled.Wait(TimeSpan.FromSeconds(30)), "Research admission was not sampled before the lead response.");
                }
                else if (options?.Instructions?.Contains("Synthetic agent for architect.", StringComparison.Ordinal) == true)
                {
                    Assert.True(planSampled.Wait(TimeSpan.FromSeconds(30)), "Plan admission was not sampled before the council response.");
                }
                return step(messages, options);
            });
        }
        host.Log.Observer = id =>
        {
            if (id.Name == "PipelinedHandoff")
            {
                var snapshot = State(host);
                var source = Assert.Single(snapshot.Checkpoint!.PendingEvidence);
                var slot = snapshot.Checkpoint.Cost!.DispatchSlots[source.DispatchId];
                var preflights = Assert.IsType<List<SquadCostPreflight>>(snapshot.CostPreflights);
                var raw = preflights.Last(round => round.PermittedSlots.Contains(slot))
                    .Demand.Single(row => row.Slot == slot).ProjectedCostUsd!.Value;
                var pending = snapshot.Checkpoint.PendingEvidence.Select(item => item.DispatchId).ToHashSet(StringComparer.Ordinal);
                var realized = snapshot.ModelCallCharges!.Where(charge =>
                    !pending.Contains(charge.DispatchId ?? "") &&
                    snapshot.ModelCalls.Single(call => call.Id == charge.CallId).Status != "started")
                    .Sum(charge => charge.CostUsd!.Value);
                observations.Add((preflights.Last().EvaluatedSpendUsd,
                    decimal.Round(realized + raw * 3.0m, 4)));
                (source.Stage == "research" ? researchSampled : planSampled).Set();
            }
        };
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var rounds = State(host).CostPreflights!;
        var demand = rounds.First(round => round.PermittedSlots.Contains("research:researcher")).Demand;
        Assert.Contains(demand, row => row.Slot == "plan:lead");
        Assert.Contains(demand, row => row.Slot == "autonomy-validation:1:architect");
        Assert.Contains(demand, row => row.Slot == "autonomy-validation:2:architect");
        Assert.Contains(demand, row => row.Slot == "autonomy:2:produce:developer");
        Assert.Contains(demand, row => row.Slot == "review:tester");
        Assert.Contains(demand, row => row.Slot == "final-validation" && row.DeterministicNative && row.ProjectedCostUsd == 0);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, observation => Assert.Equal(observation.Expected, observation.Actual));
        Assert.Equal(2, result.ModeOutcome!.PipelinedTransitions);
        Assert.All(rounds.Where(round => round.Decision == "within-ceiling"), round =>
            Assert.True(round.AdmissionCostUsd <= round.RemainingUsd));
    }

    [Fact]
    public async Task GivenUnknownModeCosts_WhenStartingAutopilot_DoesNotAutoApproveOrDispatchResearch()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        ConsumptionTestData.Configure(host, observations: 0);
        host.QueuePipeline();
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("cannot-confirm", result.CostPreflight!.Decision);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(host.Approvals, item => item.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenConditionalWithActualIndependentResolutions_WhenAllConditionsAreSatisfied_ConvergesAtCycleOne()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        FirstDraft(host, "Conditional", condition: true);
        Council(host, verdict: "Conditional", condition: true, resolve: true);
        host.QueueStage("tester");
        host.Client.Text("Converged on actual verified conditions.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.True(result.ModeOutcome!.Converged);
        Assert.Equal("Go-With-Conditions", Assert.Single(result.ModeOutcome.Iterations).Verdict);
        Assert.Equal(1, host.Writes);
        Assert.DoesNotContain(host.Approvals, item => item.Kind == SquadApprovalKind.AutonomousOutcome);
    }

    [Fact]
    public async Task GivenTwoIndependentLenses_WhenRunningAutopilot_BothActualCouncilRoundsInvokePrivateAgentsConcurrently()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.MaxConcurrentDispatches = 4;
        using var architectScript = new ScriptedRuntimeChatClient();
        using var securityScript = new ScriptedRuntimeChatClient();
        var barrier = new CouncilBarrier();
        using var architect = new ConcurrentCouncilClient(architectScript, barrier);
        using var security = new ConcurrentCouncilClient(securityScript, barrier);
        host.Options.ModelSelector = (role, _) => new(role?.Role switch
        {
            "architect" => architect,
            "security" => security,
            _ => host.Client,
        }, "model-fixed");
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            Council(host, client: architectScript);
            Council(host, "security", client: securityScript);
        }
        host.QueueStage("developer");
        host.QueueStage("tester");
        host.Client.Text("Actually concurrent independent lenses.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement bounded authentication code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, barrier.Peak);
        Assert.Equal([2, 2], barrier.Counts);
        var seats = result.Evidence.Where(item => item.Stage == "council").ToArray();
        Assert.Equal(4, seats.Length);
        Assert.Equal(4, seats.Select(item => item.ArtifactPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, seats.Count(item => item.Role == "architect"));
        Assert.Equal(2, seats.Count(item => item.Role == "security"));
        Assert.All(seats, item => Assert.Equal(RuntimeTestHost.Agent(item.Role), item.Agent));
        Assert.Equal(2, result.ModeOutcome!.PipelinedTransitions);
    }

    [Fact]
    public async Task GivenSettledRequirementInput_WhenAutopilotStarts_RunsIndependentIntakeBeforeResearchWithoutInterviewing()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.HumanPresent = false;
        host.Options.AskAsync = (_, _) => throw new InvalidOperationException("No unattended interview may occur.");
        File.WriteAllText(Path.Combine(host.Project, "requirements.md"), "Original settled bounded input.");
        host.QueuePipeline(inputs: ["requirements.md"]);
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        Council(host);
        host.QueueStage("developer");
        Council(host);
        host.QueueStage("tester");
        host.Client.Text("Actual grounded unattended delivery.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("intake", result.Evidence[0].Stage);
        Assert.Equal("research", result.Evidence[1].Stage);
        Assert.Single(State(host).GateRecords!, record => record.Kind == "Intake Readiness Verdict");
        Assert.DoesNotContain(host.Approvals, item => item.Kind == SquadApprovalKind.Discovery);
    }

    [Fact]
    public async Task GivenHumanChangesAtTheCycleTwoCap_WhenResuming_DoesNoMoreModelWorkAndOffersANewExactRiskStop()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        using var notifications = Durable(host);
        FirstDraft(host, "Conditional", condition: true);
        Council(host, verdict: "Conditional", condition: true);
        host.QueueStage("developer");
        Council(host, resolve: true);
        using var runtime = await host.CreateAsync();
        var cap = await runtime.RunAsync("Implement the bounded local code.");
        Assert.True(cap.Status == SquadRunStatus.ApprovalRequired, cap.ResponseText);
        var calls = host.Client.Calls.Count;
        _ = await RespondAsync(runtime, "autonomousoutcome", "/changes: More work must be separately scoped.");

        var risk = await runtime.ResumeAsync(cap.RunId);

        Assert.True(risk.Status == SquadRunStatus.ApprovalRequired, risk.ResponseText);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(2, risk.ModeOutcome!.Iterations.Count);
        Assert.Equal(2, host.Writes);
        _ = await RespondAsync(runtime, "riskgate", "/stop");
        var stopped = await runtime.ResumeAsync(cap.RunId);
        Assert.Equal(SquadRunStatus.Stopped, stopped.Status);
        Assert.Equal("stopped", stopped.ModeOutcome!.Outcome);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Null(State(host).Checkpoint);
        Assert.Contains(State(host).GateRecords!, record => record.Kind == "Mode Outcome" &&
            ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload).Result.Outcome == "stopped");
    }

    [Fact]
    public async Task GivenAlteredArchivedOutput_WhenApprovingCycleTwo_RejectsConsentAndPreservesAllPriorReceipts()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        using var notifications = Durable(host);
        FirstDraft(host, "Conditional", condition: true);
        Council(host, verdict: "Conditional", condition: true);
        host.QueueStage("developer");
        Council(host, resolve: true);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the bounded local code.");
        Assert.Equal(SquadRunStatus.ApprovalRequired, result.Status);
        var pending = Assert.Single(await runtime.GetPendingHumanActionsAsync());
        var state = State(host);
        var snapshot = Assert.Single(state.OutputRevisions!);
        File.WriteAllText(Path.Combine(host.Project, snapshot.ArchivePath), "Deliberately altered archived bytes.");
        var calls = host.Client.Calls.Count;

        var response = await runtime.SubmitHumanResponseAsync(
            SquadHumanResponse.FromCommand(pending, "/approve", Guid.NewGuid().ToString("N")));

        Assert.False(response.Accepted);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(2, host.Writes);
        Assert.Equal(state.NativeOperations.Count, State(host).NativeOperations.Count);
    }

    [Fact]
    public async Task GivenApprovedModeOvershoot_WhenExecuting_RetainsSequentialUnitsAndDisablesEveryScribeOverlap()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.MaxConcurrentDispatches = 4;
        ConsumptionTestData.Configure(host, ".5");
        Success(host);
        var steps = host.Client.Steps.ToArray();
        host.Client.Steps.Clear();
        foreach (var step in steps)
        {
            host.Client.Steps.Enqueue((messages, options) =>
            {
                var response = step(messages, options);
                response.ModelId = "model-fixed";
                response.Usage = new() { InputTokenCount = 10, OutputTokenCount = 1 };
                return response;
            });
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Contains(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Equal(0, result.ModeOutcome!.PipelinedTransitions);
        Assert.DoesNotContain(host.Log.Messages, message => message.Contains("overlaps a safe role", StringComparison.Ordinal));
        Assert.All(State(host).CostPreflights!.Where(round => round.Decision == "approved-over-ceiling"), round =>
            Assert.Single(round.PermittedSlots));
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public async Task GivenExplicitCouncilWaiver_WhenRunningAutonomous_EndsTheTurnWithoutAnImplementerOrStuckCheckpoint()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autonomous");
        host.Options.Council = new() { Roles = [], WaiverReason = "The human explicitly waives only this council." };
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal("council waived: loop ended", result.ModeOutcome!.Outcome);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, item => item.Stage is "council" or "produce");
        Assert.Single(State(host).GateRecords!, record => record.Kind == "Council Waiver");
        Assert.Null(State(host).Checkpoint);
    }

    [Fact]
    public async Task GivenLedgerOnlyWriteFailure_WhenPipelining_WarnsOnceAndStillVerifiesBothOverlaps()
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.MaxConcurrentDispatches = 4;
        FileStream? lockedLedger = null;
        var overlaps = 0;
        host.Log.Observer = id =>
        {
            if (id.Name == "PipelinedHandoff" && Interlocked.Increment(ref overlaps) == 1)
            {
                lockedLedger = new FileStream(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"),
                    FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else if (id.Name == "LedgerOnly")
            {
                lockedLedger?.Dispose();
                lockedLedger = null;
            }
        };
        Success(host);
        using var runtime = await host.CreateAsync();
        try
        {
            var result = await runtime.RunAsync("Implement the bounded local code.");

            Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
            Assert.Equal(2, result.ModeOutcome!.PipelinedTransitions);
            Assert.Contains(host.Log.Messages, message => message.Contains("Consumption ledger needs reconciliation", StringComparison.Ordinal));
            Assert.NotEqual(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        }
        finally
        {
            lockedLedger?.Dispose();
        }
    }

    [Theory]
    [InlineData("interactive", true)]
    [InlineData("autonomous", false)]
    [InlineData("autopilot", false)]
    public async Task GivenExplicitlySafeResearchRetrieval_WhenDelivering_ExecutesTheActualToolAndKeepsInteractiveConsent(
        string mode, bool gated)
    {
        using var host = new RuntimeTestHost();
        Mode(host, mode);
        host.WriteCharter("researcher", tools: "tools:\n  - web/read\n");
        var reads = 0;
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
        {
            reads++;
            return "Actual bounded retrieval result.";
        }, "retrieve_fact"), "web/read", ["researcher"], SquadToolEffect.ExternalRead,
            result => new([], result!.ToString()), Impact: SquadToolImpact.Safe));
        host.QueuePipeline();
        host.Client.Call("retrieve_fact", []);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (mode != "interactive")
        {
            Council(host);
        }
        host.QueueStage("developer");
        if (mode != "interactive")
        {
            Council(host);
        }
        host.QueueStage("tester");
        host.Client.Text("Actual retrieved and implemented outcome.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded local code.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(1, reads);
        Assert.Equal(gated, host.Approvals.Any(approval => approval.Kind == SquadApprovalKind.ConsequentialTool &&
            approval.ToolName == "retrieve_fact"));
        var receipt = Assert.Single(result.Effects!, effect => effect.Receipt.Name == "retrieve_fact").Receipt;
        Assert.Equal(SquadToolEffect.ExternalRead, receipt.Effect);
        Assert.Equal(SquadToolOutputStatus.Succeeded, receipt.Status);
        Assert.Equal("Actual bounded retrieval result.", receipt.Receipt);
    }

    [Theory]
    [InlineData(SquadToolEffect.ProjectWrite)]
    [InlineData(SquadToolEffect.ExternalRead)]
    [InlineData(SquadToolEffect.External)]
    public async Task GivenSafeClassificationWithoutRequiredEvidence_WhenCreating_RejectsUnsupportedAuthority(SquadToolEffect effect)
    {
        using var host = new RuntimeTestHost();
        Mode(host, "autopilot");
        host.Options.Tools[0] = host.Options.Tools[0] with
        {
            Effect = effect,
            OutputEvidence = null,
            ProjectWritePaths = effect == SquadToolEffect.ProjectWrite ? _ => ["implementation.txt"] : null,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => host.CreateAsync());

        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    private sealed class CouncilBarrier
    {
        private readonly TaskCompletionSource[] _gates =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private int _active;
        private int _peak;
        internal int[] Counts { get; } = new int[2];
        internal int Peak => Volatile.Read(ref _peak);

        internal async Task EnterAsync(int round, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            var peak = Volatile.Read(ref _peak);
            while (active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
            {
                peak = Volatile.Read(ref _peak);
            }
            if (Interlocked.Increment(ref Counts[round]) == 2)
            {
                _gates[round].TrySetResult();
            }
            try
            {
                await _gates[round].Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ConcurrentCouncilClient(IChatClient inner, CouncilBarrier barrier) : DelegatingChatClient(inner)
    {
        private int _round;
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            if (!snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                await barrier.EnterAsync(_round++, cancellationToken);
            }
            return await base.GetResponseAsync(snapshot, options, cancellationToken);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("watch")]
    [InlineData("")]
    public async Task GivenInvalidHostMode_WhenCreating_RejectsBeforeAnyStateOrProviderWork(string mode)
    {
        using var host = new RuntimeTestHost();
        host.Options.Mode = mode;

        await Assert.ThrowsAsync<ArgumentException>(() => host.CreateAsync());

        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }
}
