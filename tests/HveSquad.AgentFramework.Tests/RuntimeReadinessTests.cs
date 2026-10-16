using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeReadinessTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string StatePath(RuntimeTestHost host) => Path.Combine(host.Project, host.Options.SquadRoot, "state.json");
    private static RuntimeState State(RuntimeTestHost host) => JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;

    private static void QueuePlan(RuntimeTestHost host, string[]? inputs = null)
    {
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "plan",
            ["inputPaths"] = inputs ?? [],
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
    }

    private static void QueueIntake(RuntimeTestHost host, string verdict, string[]? gapIds = null,
        bool human = false, string[]? questions = null, string[]? assumptions = null, bool invalidSources = false)
    {
        host.Client.Call("list_gate_inputs", []);
        host.Client.Steps.Enqueue((messages, _) =>
        {
            var inputs = JsonSerializer.Deserialize<SquadArtifactEvidence[]>(messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Last().Result!.ToString()!)!;
            var sources = invalidSources ? inputs.Select(input => input with { Sha256 = "invented" }).ToArray() : inputs;
            var issues = (gapIds ?? []).Select(id => new SquadGateIssue(id, "Completeness",
                "Missing checkable local detail " + id, true, human, sources, [])).ToList();
            issues.AddRange((assumptions ?? []).Select(id => new SquadGateIssue(id, "Clarity",
                "Non-blocking local detail.", false, false, sources, [])));
            return RuntimeTestHost.Function("complete_return", new()
            {
                ["summary"] = "Independent actual readiness.",
                ["verdict"] = verdict,
                ["risk"] = "Low",
                ["blockingIssues"] = (gapIds ?? []).Length,
                ["payload"] = new SquadIntakeFindings(ReadinessContracts.IntakeDimensions, issues,
                    questions ?? [], (assumptions ?? []).Select(id => new SquadIntakeAssumption(id, "Local-only assumption.")).ToArray()),
            });
        });
        host.Client.Text("Intake finished.");
    }

    private static void QueueDraft(RuntimeTestHost host, string[] ids)
    {
        host.Client.Call("write_artifact", new() { ["content"] = "Actual supplemental local criteria: " + string.Join(", ", ids) });
        host.Client.Steps.Enqueue((_, _) => RuntimeTestHost.Function("complete_stage", new()
        {
            ["summary"] = "Actual supplemental input draft.",
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
            ["payload"] = new SquadIntakeRemediation(ids, State(host).Checkpoint!.Readiness!.Inputs.ToArray()),
        }));
        host.Client.Text("Draft finished.");
    }

    private static void QueueDiscovery(RuntimeTestHost host, string role, bool ask = true, bool omitDiscardedReason = false,
        bool blockQuestion = false)
    {
        if (ask)
        {
            host.Client.Call("ask_host", new()
            {
                ["question"] = "Choose the local direction for " + role + ".",
                ["choices"] = (string[])["Local option", "Remote option"],
            });
        }
        SquadDiscoveryFindings Findings()
        {
            var current = State(host);
            var decision = current.ConfirmedAnswers.Last(answer => answer.Role is "analyst" or "designer");
            return new("Local reporting is unclear.", "Measure completion locally.",
                [new("local", "Local option", "chosen", "The human chose local scope."),
                    new("remote", "Remote option", "discarded", omitDiscardedReason ? "" : "Remote disclosure is out of scope.")],
                role is "analyst" or "designer" ? decision : null,
                "A partner needs a reliable local summary.", "Local brief only.", "No publishing or deployment.",
                [], blockQuestion ? [new("Which external stakeholder has authority?", true)] : [],
                role == "challenger" ? [new("scope-objection", "Medium", "Keep the scope measurable.", false, false,
                    current.DispatchRecords.Where(item => item.Stage == "discovery" && item.Role == "designer")
                        .Select(item => new SquadArtifactEvidence("framing", item.ArtifactPath,
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(
                                Path.Combine(host.Project, item.ArtifactPath)))))).ToArray(), [])] : [],
                role == "experimenter" ? "A local summary improves clarity." : null,
                role == "experimenter" ? "Measure a small synthetic example." : null);
        }
        if (role == "analyst")
        {
            host.Client.Steps.Enqueue((_, _) =>
            {
                var findings = Findings();
                return RuntimeTestHost.Function("write_artifact", new()
                {
                    ["content"] = "# Discovery brief\n\n## Problem\n" + findings.Problem +
                        "\n## Why now\n" + findings.WhyNow + "\n## In scope\n" + findings.InScope +
                        "\n## Out of scope\n" + findings.OutOfScope + "\n## Success measure\n" + findings.SuccessMeasure +
                        "\n## Options considered\n" + string.Join("\n", findings.Options.Select(option => option.Description + ": " + option.Reason)) +
                        "\n## Chosen direction\n" + findings.DirectionDecision!.Answer +
                        "\n## Assumptions\nNone.\n## Open questions\nNone.",
                });
            });
        }
        host.Client.Steps.Enqueue((_, _) => RuntimeTestHost.Function(role == "analyst" ? "complete_stage" : "complete_return", new()
        {
            ["summary"] = "Actual discovery " + role,
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
            ["payload"] = Findings(),
        }));
        host.Client.Text(role + " discovery finished.");
    }

    private static void QueueCouncil(RuntimeTestHost host, string role, string verdict = "Approve",
        string risk = "Low", bool condition = false, bool blocker = false, bool requiresHuman = false)
    {
        if (role != "cost-manager")
        {
            host.Client.Call("write_artifact", new() { ["content"] = "Actual " + role + " lens evidence." });
        }
        host.Client.Call("list_gate_inputs", []);
        host.Client.Steps.Enqueue((messages, _) =>
        {
            var sources = JsonSerializer.Deserialize<SquadArtifactEvidence[]>(messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Last().Result!.ToString()!)!;
            return RuntimeTestHost.Function(role == "cost-manager" ? "complete_return" : "complete_stage", new()
            {
                ["summary"] = "Actual " + role + " council findings.",
                ["verdict"] = verdict,
                ["risk"] = risk,
                ["blockingIssues"] = blocker ? 1 : 0,
                ["payload"] = new SquadCouncilFindings(blocker
                    ? [new("blocking-condition", role, "The recorded local constraint is unresolved.", true, requiresHuman, sources, [])] : [],
                    condition ? [new("local-boundary", "Do not publish the private source.", sources, [])] : [],
                    verdict == "Concern" ? ["Check the local behavior."] : []),
            });
        });
        host.Client.Text(role + " council finished.");
    }

    [Theory]
    [InlineData("quick", "analyst")]
    [InlineData("standard", "designer,analyst")]
    [InlineData("deep", "designer,challenger,experimenter,analyst")]
    public async Task GivenExplicitDiscovery_WhenPlanning_RunsExactDepthAndIndependentIntake(string depth, string expected)
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = depth;
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueuePlan(host);
        foreach (var role in expected.Split(','))
        {
            QueueDiscovery(host, role, ask: role is "analyst" or "designer");
        }
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Bounded plan completed.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a bounded local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        var discovery = result.Evidence.Where(item => item.Stage == "discovery").ToArray();
        Assert.Equal(expected.Split(','), discovery.Select(item => item.Role));
        Assert.All(discovery.Where(item => item.Role != "analyst"), item =>
        {
            Assert.Equal(SquadEvidenceKind.StructuredReturn, item.EvidenceKind);
            Assert.Null(item.Artifacts);
        });
        var brief = Assert.Single(discovery, item => item.Role == "analyst");
        Assert.EndsWith("-brief.md", brief.ArtifactPath, StringComparison.Ordinal);
        Assert.Single(brief.Artifacts!);
        Assert.NotEqual(brief.Agent, Assert.Single(result.Evidence, item => item.Stage == "intake").Agent);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.Discovery);
        var decisions = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md"));
        Assert.Contains("Remote disclosure is out of scope.", decisions, StringComparison.Ordinal);
        Assert.Contains("### Discovery Gate", decisions, StringComparison.Ordinal);
        Assert.Contains("### Findings", decisions, StringComparison.Ordinal);
        Assert.Empty(host.Client.Steps);
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Fact]
    public async Task GivenEligibleTopic_WhenDeclinedAndRepeated_OffersOnlyOnceAndRecordsSkip()
    {
        using var host = new RuntimeTestHost();
        host.Options.Profile = "product";
        var offers = 0;
        host.Options.AskAsync = (question, _) =>
        {
            Assert.Equal("discovery", question.Role);
            Assert.Equal(["quick", "standard", "deep", "skip"], question.Choices);
            offers++;
            return ValueTask.FromResult<string?>("skip");
        };
        for (var turn = 0; turn < 2; turn++)
        {
            QueuePlan(host);
            host.QueueStage("researcher");
            host.QueueStage("lead");
            host.Client.Text("Plan finished.");
        }
        using var runtime = await host.CreateAsync();

        var first = await runtime.RunAsync("We need better local reporting.");
        var second = await runtime.RunAsync("We need better local reporting.");

        Assert.Equal(SquadRunStatus.PlanCompleted, first.Status);
        Assert.Equal(SquadRunStatus.PlanCompleted, second.Status);
        Assert.Equal(1, offers);
        Assert.DoesNotContain(first.Evidence.Concat(second.Evidence), item => item.Stage == "discovery");
        var record = Assert.Single(State(host).GateRecords!, item => item.Kind == "Discovery Verdict");
        Assert.Equal("skip", record.Payload.GetProperty("Depth").GetString());
    }

    [Fact]
    public async Task GivenNoHuman_WhenDiscoveryExplicit_IsRecordedIgnoredWithoutInterview()
    {
        using var host = new RuntimeTestHost();
        host.Options.HumanPresent = false;
        host.Options.Discovery = "deep";
        host.Options.Profile = "full";
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.Client.Text("Plan finished.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("We need better local reporting.");

        Assert.Equal(SquadRunStatus.PlanCompleted, result.Status);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "discovery");
        Assert.Contains("explicit discovery input was ignored",
            File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenExplicitSkipWithExistingInputs_WhenRunning_RecordsDeclinationWithoutSkippingIntake()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "skip";
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Existing input.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Input-grounded plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(State(host).GateRecords!, record => record.Kind == "Discovery Verdict" &&
            record.Payload.GetProperty("Depth").GetString() == "skip");
        Assert.Single(result.Evidence, evidence => evidence.Stage == "intake");
    }

    [Fact]
    public async Task GivenDeniedBriefConfirmation_WhenDiscoveryCompletes_DoesNotAdmitAnInventedDirectionToIntake()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "quick";
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        host.Options.ApproveAsync = (approval, _) => ValueTask.FromResult(approval.Kind != SquadApprovalKind.Discovery);
        QueuePlan(host);
        QueueDiscovery(host, "analyst");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Single(result.Evidence, evidence => evidence.Stage == "discovery");
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "intake");
        Assert.DoesNotContain(State(host).GateRecords ?? [], record => record.Kind == "Intake Readiness Verdict");
    }

    [Fact]
    public async Task GivenUnansweredDiscovery_WhenResuming_PreservesActualInterviewAndDoesNotRepeatCompletedRoles()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "standard";
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (question, _) => ValueTask.FromResult<string?>(question.Role == "designer" ? "Local option" : null);
        QueuePlan(host);
        QueueDiscovery(host, "designer");
        host.Client.Call("ask_host", new() { ["question"] = "Choose the local direction for analyst." });
        string runId;
        using (var first = await host.CreateAsync())
        {
            var pending = await first.RunAsync("Shape a local summary.");
            Assert.True(pending.Status == SquadRunStatus.InputRequired, pending.ResponseText);
            Assert.Single(pending.Evidence, item => item.Role == "designer");
            runId = pending.RunId;
        }
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueueDiscovery(host, "analyst");
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(runId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(result.Evidence, item => item.Role == "designer");
        Assert.Single(result.Evidence, item => item.Role == "analyst");
        Assert.Equal(2, State(host).GateRecords!.Count(item => item.Kind == "Discovery Verdict"));
    }

    [Fact]
    public async Task GivenCompletedBrief_WhenNextTurnMatches_ReusesDiscoveryAndIntakeWithoutAnotherInterview()
    {
        using var host = new RuntimeTestHost();
        host.Options.Profile = "product";
        var interviews = 0;
        host.Options.AskAsync = (question, _) =>
        {
            interviews++;
            return ValueTask.FromResult<string?>(question.Role == "discovery" ? "quick" : "Local option");
        };
        QueuePlan(host);
        QueueDiscovery(host, "analyst");
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("First brief-grounded plan.");
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("We need better local reporting.");
        Assert.True(first.Status == SquadRunStatus.PlanCompleted, first.ResponseText);
        QueuePlan(host);
        host.QueueStage("lead");
        host.Client.Text("Reused brief-grounded plan.");

        var second = await runtime.RunAsync("We need better local reporting.");

        Assert.True(second.Status == SquadRunStatus.PlanCompleted, second.ResponseText);
        Assert.Equal(2, interviews);
        Assert.DoesNotContain(second.Evidence, item => item.Stage is "discovery" or "intake" or "research");
        Assert.Single(State(host).GateRecords!, item => item.Kind == "Discovery Verdict");
        Assert.Contains("#discovery-verdict-", second.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenChangedBrief_WhenTopicRepeats_RevalidatesConfirmedDepthWithoutReofferingOrReusingStaleReadiness()
    {
        using var host = new RuntimeTestHost();
        host.Options.Profile = "product";
        var offers = 0;
        host.Options.AskAsync = (question, _) =>
        {
            if (question.Role == "discovery")
            {
                offers++;
            }
            return ValueTask.FromResult<string?>(question.Role == "discovery" ? "quick" : "Local option");
        };
        QueuePlan(host);
        QueueDiscovery(host, "analyst");
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("First plan.");
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("We need better local reporting.");
        Assert.True(first.Status == SquadRunStatus.PlanCompleted, first.ResponseText);
        var brief = Assert.Single(first.Evidence, evidence => evidence.Role == "analyst");
        File.AppendAllText(Path.Combine(host.Project, brief.ArtifactPath), "\nChanged by the human after the completed run.");
        QueuePlan(host);
        QueueDiscovery(host, "analyst");
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Freshly revalidated plan.");

        var next = await runtime.RunAsync("We need better local reporting.");

        Assert.True(next.Status == SquadRunStatus.PlanCompleted, next.ResponseText);
        Assert.Equal(1, offers);
        Assert.Single(next.Evidence, evidence => evidence.Stage == "discovery");
        Assert.Single(next.Evidence, evidence => evidence.Stage == "intake");
        Assert.Equal(2, State(host).GateRecords!.Count(record => record.Kind == "Discovery Verdict"));
    }

    [Fact]
    public async Task GivenBatchedDiscoveryQuestions_WhenProviderReturns_StopsBeforeAnyAnswerAndRecordsActualAttempt()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "quick";
        host.EnableDiscoveryRoles();
        var questions = 0;
        host.Options.AskAsync = (_, _) =>
        {
            questions++;
            return ValueTask.FromResult<string?>("Must not be asked.");
        };
        QueuePlan(host);
        host.Client.Steps.Enqueue((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("question-a", "ask_host", new Dictionary<string, object?> { ["question"] = "First question?" }),
            new FunctionCallContent("question-b", "ask_host", new Dictionary<string, object?> { ["question"] = "Second question?" }),
        ])));
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(0, questions);
        Assert.Contains("exactly one", result.ResponseText, StringComparison.Ordinal);
        var attempt = Assert.Single(State(host).ModelCalls, call => call.Stage == "discovery");
        Assert.Equal("returned", attempt.Status);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMalformedDiscovery_WhenCompleting_CannotBankAnUnagreedBrief(bool blockingQuestion)
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "quick";
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueuePlan(host);
        QueueDiscovery(host, "analyst", omitDiscardedReason: !blockingQuestion, blockQuestion: blockingQuestion);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.Equal(blockingQuestion ? SquadRunStatus.InputRequired : SquadRunStatus.Failed, result.Status);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain(State(host).GateRecords ?? [], record => record.Kind == "Intake Readiness Verdict");
    }

    [Fact]
    public async Task GivenReturnedHumanQuestion_WhenResuming_ActuallyAsksAndRetainsPriorIncompleteFindings()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "quick";
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueuePlan(host);
        QueueDiscovery(host, "analyst", blockQuestion: true);
        string runId;
        using (var first = await host.CreateAsync())
        {
            var pending = await first.RunAsync("Shape a local summary.");
            Assert.Equal(SquadRunStatus.InputRequired, pending.Status);
            runId = pending.RunId;
        }
        var returned = Assert.Single(State(host).OpenQuestions, question =>
            question.Question == "Which external stakeholder has authority?");
        Assert.Equal("analyst", returned.Role);
        var answered = 0;
        host.Options.AskAsync = (question, _) =>
        {
            Assert.Equal("Which external stakeholder has authority?", question.Question);
            answered++;
            return ValueTask.FromResult<string?>("The local case owner.");
        };
        QueueDiscovery(host, "analyst", ask: false);
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(runId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal(1, answered);
        Assert.Empty(State(host).OpenQuestions);
        Assert.Contains(State(host).GateRecords!, record => record.Payload.TryGetProperty("PendingFindings", out var findings) &&
            findings.ValueKind == JsonValueKind.Array && findings.GetArrayLength() == 1);
    }

    [Fact]
    public async Task GivenSameAgentAsBriefAuthor_WhenIntakeRequired_RejectsSelfReviewBeforeCallingValidator()
    {
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "quick";
        host.EnableDiscoveryRoles();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace(
            "| intake-validator | Test intake-validator |", "| intake-validator | Test analyst |", StringComparison.Ordinal));
        QueuePlan(host);
        QueueDiscovery(host, "analyst");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("distinct validator", result.ResponseText, StringComparison.Ordinal);
        Assert.Single(result.Evidence, item => item.Stage == "discovery");
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "intake");
    }

    [Fact]
    public async Task GivenExplicitDeepInProduct_WhenMissingChallenger_ConfirmsExtensionWithoutSilentlyReducingDepth()
    {
        using var host = new RuntimeTestHost();
        host.Options.Profile = "product";
        host.Options.Discovery = "deep";
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueuePlan(host);
        foreach (var role in new[] { "designer", "challenger", "experimenter", "analyst" })
        {
            QueueDiscovery(host, role, ask: role is "designer" or "analyst");
        }
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Deep plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Contains(host.Approvals, approval => approval.Kind == SquadApprovalKind.RosterUpdate &&
            approval.Initialization!.Roles.Contains("challenger"));
        Assert.Equal(4, result.Evidence.Count(item => item.Stage == "discovery"));
        Assert.Contains("challenger", runtime.RosterConfiguration.AdditionalRoles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenReadyInput_WhenNextTurnRuns_ReusesOnlyUnchangedHashes(bool unchanged)
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Project, "requirements.txt");
        File.WriteAllText(path, "Local summary requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Ready-With-Gaps", assumptions: ["local-scope"]);
        host.QueueStage("lead");
        host.Client.Text("First plan.");
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.PlanCompleted, (await runtime.RunAsync("Plan a local summary.")).Status);
        if (!unchanged)
        {
            File.AppendAllText(path, "Changed success criterion.");
        }
        QueuePlan(host, ["requirements.txt"]);
        if (!unchanged)
        {
            QueueIntake(host, "Ready");
        }
        host.QueueStage("lead");
        host.Client.Text("Next plan.");

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal(unchanged ? 0 : 1, result.Evidence.Count(item => item.Stage == "intake"));
        var verdict = State(host).GateRecords!.Last(item => item.Kind == "Intake Readiness Verdict");
        Assert.Equal(unchanged, verdict.Payload.GetProperty("ReusedFrom").ValueKind == JsonValueKind.String);
        var planCall = host.Client.Calls.Last(call => call.Messages.Any(message => message.Text.Contains("stage=plan", StringComparison.Ordinal)));
        if (unchanged)
        {
            Assert.Contains(planCall.Messages, message => message.Text.Contains("Local-only assumption.", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GivenShrinkingNonHumanGaps_WhenRemediating_PreservesOriginalBytesAndBoundedIndependentRevalidation(int cycles)
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        var input = Path.Combine(host.Project, "requirements.txt");
        File.WriteAllText(input, "Original immutable requirement.");
        var before = File.ReadAllBytes(input);
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", cycles == 1 ? ["gap-a"] : ["gap-a", "gap-b"]);
        QueueDraft(host, ["gap-a"]);
        QueueIntake(host, cycles == 1 ? "Ready" : "Not-Ready", cycles == 1 ? [] : ["gap-b"]);
        if (cycles == 2)
        {
            QueueDraft(host, ["gap-b"]);
            QueueIntake(host, "Ready");
        }
        host.QueueStage("lead");
        host.Client.Text("Remediated plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(cycles, result.Evidence.Count(item => item.Stage == "remediation"));
        Assert.Equal(cycles, result.Evidence.Count(item => item.Stage == "validation"));
        Assert.Equal(cycles, host.Approvals.Count(item => item.Kind == SquadApprovalKind.IntakeRemediation));
        Assert.Equal(cycles + 1, State(host).GateRecords!.Count(item => item.Kind == "Intake Readiness Verdict"));
        Assert.Equal(result.Evidence.Count, result.Evidence.Select(item => item.UnitSlot ?? item.DispatchId).Distinct(StringComparer.Ordinal).Count());
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenHumanOrNonshrinkingGap_WhenIntakeRuns_StopsWithoutGuessingOrAnotherCycle(bool human)
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Local requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", ["gap-a"], human);
        if (!human)
        {
            QueueDraft(host, ["gap-a"]);
            QueueIntake(host, "Not-Ready", ["gap-a"]);
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.DoesNotContain(result.Evidence, item => item.Stage is "plan" or "research" or "produce");
        Assert.Equal(human ? 0 : 1, result.Evidence.Count(item => item.Stage == "remediation"));
        Assert.Contains(human ? "blocking/human" : "strictly shrink", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenTwoShrinkingButUnresolvedCycles_WhenIntakeRuns_StopsAtTheExactCap()
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Local requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", ["gap-a", "gap-b", "gap-c"]);
        QueueDraft(host, ["gap-a"]);
        QueueIntake(host, "Not-Ready", ["gap-b", "gap-c"]);
        QueueDraft(host, ["gap-b"]);
        QueueIntake(host, "Not-Ready", ["gap-c"]);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(2, result.Evidence.Count(item => item.Stage == "remediation"));
        Assert.Equal(2, result.Evidence.Count(item => item.Stage == "validation"));
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "plan");
    }

    [Fact]
    public async Task GivenPendingDraftConfirmation_WhenResuming_DoesNotRepeatDraftAndRevalidatesExactSupplement()
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Original immutable requirement.");
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.IntakeRemediation)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", ["gap-a"]);
        QueueDraft(host, ["gap-a"]);
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync("Plan a local summary.", cancellation.Token));
        }
        var pending = State(host).Checkpoint!;
        var draft = Assert.Single(pending.Evidence, item => item.Stage == "remediation");
        var bytes = File.ReadAllBytes(Path.Combine(host.Project, draft.ArtifactPath));
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(true);
        };
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(pending.RunId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(result.Evidence, item => item.Stage == "remediation");
        Assert.Single(result.Evidence, item => item.Stage == "validation");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, draft.ArtifactPath)));
    }

    [Fact]
    public async Task GivenConfirmedSupplementBeforeInterruptedValidation_WhenResuming_DoesNotRestartIntakeOrLoseCycleCount()
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Original immutable requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", ["gap-a"]);
        QueueDraft(host, ["gap-a"]);
        host.Client.Steps.Enqueue((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync("Plan a local summary.", cancellation.Token));
        }
        var pending = State(host).Checkpoint!;
        Assert.Equal(1, pending.Readiness!.IntakeCycles);
        Assert.Equal(2, pending.Readiness.Inputs.Count);
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(pending.RunId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(result.Evidence, item => item.Stage == "intake");
        Assert.Single(result.Evidence, item => item.Stage == "remediation");
        Assert.Single(result.Evidence, item => item.Stage == "validation");
    }

    [Fact]
    public async Task GivenReadyLabelWithBlocker_WhenValidated_RecordsNotReadyAndDoesNotProceed()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Local requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Ready", ["human-choice"], human: true);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        var verdict = Assert.Single(State(host).GateRecords!, item => item.Kind == "Intake Readiness Verdict");
        Assert.Equal("Not-Ready", verdict.Payload.GetProperty("Verdict").GetString());
        Assert.Equal("Ready", verdict.Payload.GetProperty("ReportedVerdict").GetString());
    }

    [Fact]
    public async Task GivenInventedFindingHash_WhenValidated_RejectsSuccessShapedReadiness()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Local requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Not-Ready", ["gap-a"], invalidSources: true);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("exact verified source", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task GivenTypedBusinessInput_WhenValidating_SelectsCanonicalIndependentAlternate()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "# BRD\nBusiness requirements for local reporting.");
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace(
            "| intake-validator | Test intake-validator | — | testing only |",
            "| intake-validator | Test intake-validator | Business Reviewer | BRD input → Business Reviewer; otherwise PRD input |",
            StringComparison.Ordinal));
        host.Write("agents/business-reviewer.agent.md", "---\nname: Business Reviewer\nuser-invocable: false\n---\nIndependent business reviewer.");
        var coordinator = Path.Combine(host.Artifacts, "agents", "coordinator.agent.md");
        File.WriteAllText(coordinator, File.ReadAllText(coordinator).Replace("agents:\n", "agents:\n  - Business Reviewer\n", StringComparison.Ordinal));
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        var intake = Assert.Single(result.Evidence, item => item.Stage == "intake");
        Assert.Equal("Business Reviewer", intake.Agent);
        Assert.Contains("BRD input", intake.AgentSelectionRationale!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenTaskFitCouncil_WhenPlanning_RecordsOnlyRelevantSeatAndAllOmissions()
    {
        using var host = new RuntimeTestHost();
        host.Options.RequireCouncil = true;
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security", "Concern");
        host.Client.Text("Council plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan an authentication constraint.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal("security", Assert.Single(result.Evidence, item => item.Stage == "council").Role);
        var proposal = Assert.Single(host.Approvals, item => item.Kind == SquadApprovalKind.Council).Council!;
        Assert.Equal(5, proposal.Lenses.Count);
        Assert.Equal(4, proposal.Lenses.Count(lens => !lens.Needed));
        var verdict = Assert.Single(State(host).GateRecords!, item => item.Kind == "Council Verdict");
        Assert.Equal("Go-With-Conditions", verdict.Payload.GetProperty("Verdict").GetString());
    }

    [Fact]
    public async Task GivenCouncilCondition_WhenImplementationApproved_CarriesExactRoleIdentityAndSourceIntoRealProducer()
    {
        using var host = new RuntimeTestHost();
        host.Options.RequireCouncil = true;
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security", "Conditional", condition: true);
        host.Client.Steps.Enqueue((messages, _) =>
        {
            var approval = host.Approvals.Last(item => item.Kind == SquadApprovalKind.Implementation);
            var condition = Assert.Single(approval.CouncilVerdict!.Conditions);
            Assert.Equal("security", condition.Role);
            Assert.Equal("local-boundary", condition.Condition.Id);
            Assert.Contains(messages, message => message.Text.Contains("Do not publish the private source.", StringComparison.Ordinal));
            return RuntimeTestHost.Function("write_artifact", new() { ["content"] = "Actual local documentation." });
        });
        host.Client.Call("complete_stage", new()
        {
            ["summary"] = "Local document.",
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
        });
        host.Client.Text("Producer finished.");
        host.QueueStage("tester");
        host.Client.Text("Delivery finished.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Document an authentication constraint.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Single(result.Evidence, item => item.Stage == "produce");
    }

    [Fact]
    public async Task GivenCouncilDecline_WhenContinuing_RecordsScopedWaiverButStillRequiresConsequentialApproval()
    {
        using var host = new RuntimeTestHost();
        host.Options.RequireCouncil = true;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind is not (SquadApprovalKind.Council or SquadApprovalKind.ConsequentialTool));
        };
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement an authentication constraint.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "council");
        Assert.Contains(host.Approvals, item => item.Kind == SquadApprovalKind.Implementation);
        Assert.Single(State(host).GateRecords!, item => item.Kind == "Council Waiver");
    }

    [Fact]
    public async Task GivenTopicWaiver_WhenScopeUnchanged_DoesNotReofferAndChangedScopeNeedsNewCouncil()
    {
        using var host = new RuntimeTestHost();
        host.Options.TopicId = "local-auth";
        host.Options.RequireCouncil = true;
        var offers = 0;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.Council)
            {
                offers++;
                return ValueTask.FromResult(false);
            }
            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();
        foreach (var request in new[] { "Plan an authentication constraint.", "Plan an authentication constraint.", "Plan a changed authentication constraint." })
        {
            QueuePlan(host);
            host.QueueStage("researcher");
            host.QueueStage("lead");
            host.Client.Text("Waived local plan.");
            var result = await runtime.RunAsync(request);
            Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        }
        Assert.Equal(2, offers);
        Assert.Equal(3, State(host).GateRecords!.Count(item => item.Kind == "Council Waiver"));
    }

    [Fact]
    public async Task GivenNeededLensOmissionWithoutReason_WhenSelected_DoesNotDispatchPartialCouncil()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"] };
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan infrastructure authentication.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Contains("waiver reason", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "council");
    }

    [Fact]
    public async Task GivenPartialCouncilWaiver_WhenConfirmed_DispatchesOnlySelectedLensAndRecordsMissingCoverage()
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["architect"], WaiverReason = "Security lens explicitly deferred for this local plan." };
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "architect");
        host.Client.Text("Scoped plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan infrastructure authentication.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal("architect", Assert.Single(result.Evidence, item => item.Stage == "council").Role);
        var proposal = Assert.Single(host.Approvals, item => item.Kind == SquadApprovalKind.Council).Council!;
        Assert.Equal(["security"], proposal.WaivedRoles);
        Assert.Contains("explicitly deferred", proposal.Lenses.Single(lens => lens.Role == "security").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenCeilingAndDiscovery_WhenAdmitting_ReservesEverySelectedDepthAndMaximumIntakeCycle()
    {
        using var host = new RuntimeTestHost();
        host.EnableDiscoveryRoles();
        ConsumptionTestData.Configure(host);
        host.Options.Discovery = "deep";
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Local option");
        QueuePlan(host);
        foreach (var role in new[] { "designer", "challenger", "experimenter", "analyst" })
        {
            QueueDiscovery(host, role, ask: role is "analyst" or "designer");
        }
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Cost-bound plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Shape a local summary.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        var first = State(host).CostPreflights![0];
        Assert.Equal(4, first.Demand.Count(row => row.Stage == "discovery"));
        Assert.Equal(2, first.Demand.Count(row => row.Slot.StartsWith("intake-validation:", StringComparison.Ordinal)));
        Assert.Contains(first.Demand, row => row.Slot == "intake-remediation:2:analyst");
        Assert.Contains(first.Demand, row => row.Slot == "intake-remediation:2:product-owner");
        Assert.Equal(4, result.Evidence.Count(item => item.Stage == "discovery"));
        Assert.DoesNotContain(result.CostPreflight!.Demand, row => row.Stage == "discovery");
    }

    [Fact]
    public async Task GivenCouncilAndCeilingBeforePlan_WhenReserving_CoversEveryPossibleInstalledLensThenShrinksToExactSeats()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        host.Options.RequireCouncil = true;
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security");
        host.Client.Text("Priced council plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan an authentication constraint.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        var rounds = State(host).CostPreflights!;
        Assert.Equal(ReadinessContracts.CouncilRoles, rounds[0].Demand.Where(row => row.Stage == "council").Select(row => row.Role));
        var precise = rounds.Last(round => round.Demand.Any(row => row.Stage == "council"));
        Assert.Equal("security", Assert.Single(precise.Demand, row => row.Stage == "council").Role);
        Assert.Equal("security", Assert.Single(result.Evidence, evidence => evidence.Stage == "council").Role);
    }

    [Fact]
    public async Task GivenCouncilWaiverWithUnknownCostFacts_WhenStarting_CannotBypassFinancialAdmission()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10", observations: 0);
        host.Options.Council = new() { WaiverReason = "Local council waived." };
        host.Options.RequireCouncil = true;
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement a local authentication constraint.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("cannot-confirm", result.CostPreflight!.Decision);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain(host.Approvals, item => item.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenInvalidConditionDependency_WhenTypedFindingValidated_RejectsCyclicOrInventedIdentity()
    {
        var source = new SquadArtifactEvidence("plan", "approved.md", new string('A', 64));
        var invalid = new SquadCouncilFindings([],
            [new("first", "First condition.", [source], ["second"]),
                new("second", "Second condition.", [source], ["first"])], []);
        Assert.Throws<InvalidDataException>(() => ReadinessContracts.ValidateCouncilFindings(invalid, [source], "Conditional", 0));
        var valid = invalid with
        {
            Conditions = [new("first", "First condition.", [source], []),
            new("second", "Second condition.", [source], ["first"])]
        };
        ReadinessContracts.ValidateCouncilFindings(valid, [source], "Conditional", 0);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("Low", true)]
    [InlineData("High", false)]
    public async Task GivenCouncilStopOverride_WhenRequested_CannotWaiveHighRisk(string risk, bool completes)
    {
        using var host = new RuntimeTestHost();
        host.Options.Council = new() { Roles = ["security"], OverrideReason = "Accept the measured local-only concern." };
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security", "Block", risk, blocker: true);
        if (completes)
        {
            host.Client.Text("Plan with override.");
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan an authentication constraint.");

        Assert.Equal(completes ? SquadRunStatus.PlanCompleted : SquadRunStatus.Blocked, result.Status);
        Assert.Equal(completes ? 1 : 0, host.Approvals.Count(item => item.Kind == SquadApprovalKind.CouncilOverride));
        Assert.Single(State(host).GateRecords!, item => item.Kind == "Council Verdict");
    }

    [Fact]
    public async Task GivenPendingCouncilOverride_WhenResuming_PreservesStopAndAsksExactOverrideWithoutRepeatingSeats()
    {
        using var host = new RuntimeTestHost();
        using var cancellation = new CancellationTokenSource();
        host.Options.Council = new() { Roles = ["security"], OverrideReason = "Accept the local measured constraint." };
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.CouncilOverride)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security", "Block", blocker: true);
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                first.RunAsync("Plan an authentication constraint.", cancellation.Token));
        }
        var saved = State(host).Checkpoint!;
        Assert.Single(saved.Evidence, item => item.Stage == "council");
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(true);
        };
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(saved.RunId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(result.Evidence, item => item.Stage == "council");
        Assert.Single(State(host).GateRecords!, record => record.Kind == "Council Verdict");
        Assert.Single(State(host).GateRecords!, record => record.Kind == "Council Override");
        Assert.Equal(2, host.Approvals.Count(approval => approval.Kind == SquadApprovalKind.CouncilOverride));
    }

    [Fact]
    public async Task GivenEmptyAutomaticCouncilRoles_WhenResuming_InheritsConfirmedSelectionNotNormalizedHostInput()
    {
        using var host = new RuntimeTestHost();
        using var cancellation = new CancellationTokenSource();
        host.Options.Council = new() { OverrideReason = "Accept the local measured constraint." };
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.CouncilOverride)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security", "Block", blocker: true);
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                first.RunAsync("Plan an authentication constraint.", cancellation.Token));
        }
        var saved = State(host).Checkpoint!;
        Assert.Empty(host.Options.Council.Roles);
        host.Options.ApproveAsync = (_, _) => ValueTask.FromResult(true);
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(saved.RunId);

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Single(result.Evidence, evidence => evidence.Stage == "council");
    }

    [Fact]
    public async Task GivenMissingCouncilRole_WhenConfirmed_AddsExactPersistentExtensionWithoutRewritingPriorEvidence()
    {
        using var host = new RuntimeTestHost();
        host.Options.Roles = ["lead", "researcher", "technical-writer", "tester"];
        host.Options.RequireCouncil = true;
        QueuePlan(host);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        QueueCouncil(host, "security");
        host.Client.Text("Extended plan.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Plan an authentication constraint.");

        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.NotNull(runtime.Roster.Resolve("security"));
        Assert.Contains(host.Approvals, item => item.Kind == SquadApprovalKind.RosterUpdate && item.Council!.MissingRoles.Contains("security"));
        Assert.Contains("security", runtime.RosterConfiguration.AdditionalRoles);
        var history = File.ReadAllBytes(Path.Combine(host.Project, result.Evidence[0].HistoryPath));
        using var recreated = await SquadRuntime.CreateAsync(host.Client, new SquadRuntimeOptions
        {
            ProjectPath = host.Project,
            Roles = runtime.RosterConfiguration.Roles,
            AdditionalRoles = runtime.RosterConfiguration.AdditionalRoles,
            Tools = host.Options.Tools,
        }, new LocalReadinessSource(host.Artifacts));
        Assert.NotNull(recreated.Roster.Resolve("security"));
        Assert.Equal(history, File.ReadAllBytes(Path.Combine(host.Project, result.Evidence[0].HistoryPath)));
    }

    [Fact]
    public async Task GivenTamperedReadinessIndex_WhenNextRunStarts_BlocksDespiteRecomputedCheckpoint()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Local requirement.");
        QueuePlan(host, ["requirements.txt"]);
        QueueIntake(host, "Ready");
        host.QueueStage("lead");
        host.Client.Text("Plan.");
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.PlanCompleted, (await runtime.RunAsync("Plan a local summary.")).Status);
        var state = State(host);
        var gate = state.GateRecords![0];
        state.GateRecords[0] = gate with { ScopeSha256 = new string('A', 64) };
        File.WriteAllText(StatePath(host), JsonSerializer.Serialize(state, Indented));
        host.Client.Text("Must never execute.");

        var result = await runtime.RunAsync("Plan the next summary.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Contains("readiness", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenInterruptedGateReceiptAfterExactDecision_WhenReconciling_RecoversOnceWithoutReplayOrPriorByteChanges()
    {
        using var host = new RuntimeTestHost();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Original local requirement.");
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        host.QueuePipeline("technical-writer", inputs: ["requirements.txt"]);
        QueueIntake(host, "Ready");
        host.QueueStage("researcher");
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                first.RunAsync("Document a local summary.", cancellation.Token));
        }
        var state = State(host);
        var gate = Assert.Single(state.GateRecords!);
        var operation = Assert.Single(state.NativeOperations, item => item.GateRecord?.Id == gate.Id);
        state.NativeOperations.Remove(operation);
        state.LedgerOrder.Remove(operation.Id);
        var nativePath = Path.Combine(host.Project, host.Options.SquadRoot, "history", "Squad Scribe.md");
        var history = File.ReadAllText(nativePath);
        var marker = "\n## Operation " + operation.Id;
        var start = history.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var next = history.IndexOf("\n## Operation ", start + marker.Length, StringComparison.Ordinal);
        File.WriteAllText(nativePath, history[..start] + (next >= 0 ? history[next..] : ""));
        var nativeBytes = File.ReadAllBytes(nativePath);
        state.AppendOnlyFiles[host.Options.SquadRoot + "/history/Squad Scribe.md"] = new(nativeBytes.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nativeBytes)));
        File.WriteAllText(StatePath(host), JsonSerializer.Serialize(state, Indented));
        var decisionsPath = Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md");
        var decisions = File.ReadAllBytes(decisionsPath);
        var calls = host.Client.Calls.Count;
        using var resumed = await host.CreateAsync();

        var result = await resumed.ReconcileEvidenceAsync();

        Assert.True(result.HistoryVerified);
        Assert.Equal(1, result.CorrectivePasses);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(decisions, File.ReadAllBytes(decisionsPath));
        Assert.True(File.ReadAllBytes(nativePath).AsSpan().StartsWith(nativeBytes));
        Assert.Single(State(host).NativeOperations, item => item.GateRecord?.Id == gate.Id);
        Assert.Equal(0, (await resumed.ReconcileEvidenceAsync()).CorrectivePasses);
    }

    private sealed class LocalReadinessSource(string root) : HveSquad.AgentFramework.Sources.SquadArtifactSource
    {
        public override ValueTask<HveSquad.AgentFramework.Sources.SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HveSquad.AgentFramework.Sources.SquadArtifactRoots([root], null, "Synthetic test release")
            {
                Release = new("v0.16.2", new string('a', 40)),
            });
    }
}
