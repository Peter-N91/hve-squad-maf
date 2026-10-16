using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Sources;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeFederationExecutionTests
{
    private static readonly string[] PreFinalGates = ["federationplan", "council", "autonomousoutcome"];
    private static string PathFor(RuntimeTestHost host, string path) =>
        Path.Combine(host.Project, path.Replace('/', Path.DirectorySeparatorChar));

    private static string Root(string name) => ".copilot-tracking/squad/members/" + name;

    private static SquadFederationMemberConfiguration Member(string name, params string[] patterns) => new()
    {
        Name = name,
        Description = "Independent original outcome " + name,
        Patterns = patterns.Length > 0 ? patterns : [name],
    };

    private static async Task<SquadFederation> Create(RuntimeTestHost host, params SquadFederationMemberConfiguration[] members)
    {
        host.UseCurrentPlanContract();
        host.Options.Discovery = "skip";
        var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        var initialized = await federation.InitializeAsync(members.Length > 0 ? members : [Member("product"), Member("platform")]);
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        host.Approvals.Clear();
        return federation;
    }

    private static Dictionary<string, string> Manifest(RuntimeTestHost host, string root) =>
        Directory.EnumerateFiles(PathFor(host, root), "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(PathFor(host, root), path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void QueueResearch(RuntimeTestHost host)
    {
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("researcher");
        host.Client.Text("Actual focused evidence.");
    }

    private static void QueueWriter(RuntimeTestHost host, bool input = false, bool automatic = false)
    {
        host.QueuePipeline("technical-writer");
        if (input)
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (automatic)
        {
            RuntimeModeTests.Council(host);
        }
        host.QueueStage("technical-writer");
        if (automatic)
        {
            RuntimeModeTests.Council(host);
        }
        host.QueueStage("tester");
        host.Client.Text("Actual complete local writing.");
    }

    private static void Automatic(RuntimeTestHost host)
    {
        host.Options.Mode = "autopilot";
        host.Options.MaxConcurrentDispatches = 1;
        host.Options.Council = new() { Roles = ["architect"] };
    }

    private static async Task<SquadPendingHumanAction> Respond(SquadFederation federation,
        string gate, string command = "/approve")
    {
        var pending = Assert.Single(await federation.GetPendingHumanActionsAsync(), action => action.Status == "pending");
        Assert.Equal(gate, pending.Binding.Gate);
        var response = await federation.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, command,
            Guid.NewGuid().ToString("N")));
        Assert.True(response.Accepted, response.Diagnostic);
        return pending;
    }

    private static RuntimeState State(RuntimeTestHost host, string root = ".copilot-tracking/squad") =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, root + "/state.json")))!;

    private static async Task<SquadFederationRunResult> ToFinal(SquadFederation federation, SquadFederationRunResult result)
    {
        for (var attempt = 0; attempt < 10 && result.Status == SquadRunStatus.ApprovalRequired; attempt++)
        {
            var pending = Assert.Single(await federation.GetPendingHumanActionsAsync(), action => action.Status == "pending");
            if (pending.Binding.Gate == "finaloutcome")
            {
                return result;
            }
            Assert.Contains(pending.Binding.Gate, PreFinalGates);
            await Respond(federation, pending.Binding.Gate);
            result = await federation.ResumeAsync(result.RunId);
        }
        Assert.Fail("The real pipeline did not reach its exact final gate: " + result.Diagnostic);
        return result;
    }

    [Theory]
    [InlineData("test plan", null, "specific", false)]
    [InlineData("plan", null, "general", false)]
    [InlineData("test plan and cloud", null, null, true)]
    [InlineData("unmatched request", null, null, true)]
    [InlineData("test plan", "general", "general", false)]
    [InlineData("plan", "absent", null, true)]
    public async Task GivenConfirmedRoutes_WhenProposing_UsesSpecificContainmentAndExplicitTargetsWithoutStateChanges(
        string request, string? target, string? expected, bool ambiguous)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host, Member("general", "plan"), Member("specific", "test plan"), Member("infra", "cloud"));
        var before = Manifest(host, ".copilot-tracking/squad");

        var proposal = federation.GetRoutingProposal(request, target);

        Assert.Equal(ambiguous, proposal.NeedsHumanChoice);
        if (expected is not null)
        {
            Assert.Equal(expected, Assert.Single(proposal.Members));
        }
        Assert.Equal(before.OrderBy(pair => pair.Key), Manifest(host, ".copilot-tracking/squad").OrderBy(pair => pair.Key));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenExplicitTarget_WhenRunning_UsesActualInnerEngineAndRecordsBothLevels()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueResearch(host);

        var result = await federation.RunAsync("Inspect original bounded code.", new() { Target = "product" });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var inner = Assert.Single(result.Members).Result!;
        Assert.Equal(SquadRunStatus.ResearchCompleted, inner.Status);
        Assert.StartsWith(Root("product") + "/", Assert.Single(inner.Evidence).ArtifactPath, StringComparison.Ordinal);
        Assert.Contains(inner.RunId, File.ReadAllText(PathFor(host, ".copilot-tracking/squad/history/product.md")), StringComparison.Ordinal);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.FederationPlan);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.FinalOutcome);
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/consumption.md")));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/notifications.md")));
    }

    [Fact]
    public async Task GivenDisjointRoutes_WhenRunning_AsksOneOwnerAndNeverGuessesByKeywordLength()
    {
        using var host = new RuntimeTestHost();
        var questions = new List<SquadQuestion>();
        host.Options.AskAsync = (question, _) =>
        {
            questions.Add(question);
            return ValueTask.FromResult<string?>("platform");
        };
        using var federation = await Create(host, Member("product", "requirements"), Member("platform", "cloud"));
        QueueResearch(host);

        var result = await federation.RunAsync("Inspect requirements and cloud.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal("platform", Assert.Single(result.Members).Member);
        Assert.Equal("federation-routing", Assert.Single(questions).Role);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("unknown")]
    [InlineData("missing-owner")]
    public async Task GivenInvalidDependencyOwnership_WhenRunning_DoesNotInvokeAnyConsumer(string defect)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var dependencies = defect switch
        {
            "cycle" => new[] { new SquadFederationDependency("product", "platform", "technical-writer"),
                new SquadFederationDependency("platform", "product", "technical-writer") },
            "unknown" => [new SquadFederationDependency("absent", "platform", "technical-writer")],
            _ => [new SquadFederationDependency(null, "platform", "technical-writer")],
        };

        var result = await federation.RunAsync("Write bounded local notes.",
            new() { Members = ["product", "platform"], Dependencies = dependencies });

        Assert.True(result.Status is SquadRunStatus.Blocked or SquadRunStatus.InputRequired, result.Diagnostic);
        Assert.Empty(host.Client.Calls);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.FederationPlan);
    }

    [Fact]
    public async Task GivenUnrunProducer_WhenConsumerNeedsArtifact_RunsProducerAndResumesConsumerInSameTurnReadOnly()
    {
        using var host = new RuntimeTestHost();
        Dictionary<string, string>? producerBeforeConsumer = null;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.FederationHandoff)
            {
                producerBeforeConsumer = Manifest(host, Root("product"));
            }
            return ValueTask.FromResult(true);
        };
        using var federation = await Create(host);
        QueueWriter(host);
        QueueWriter(host, input: true);

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal(["product", "platform"], result.Members.Select(member => member.Member));
        var handoff = Assert.Single(result.Handoffs);
        var input = Assert.Single(handoff.Artifacts);
        Assert.Equal("product", input.Producer);
        Assert.Equal("technical-writer", input.ProducerRole);
        Assert.Equal("produce", input.ProducerStage);
        Assert.StartsWith(Root("product") + "/technical-writer/", input.Path, StringComparison.Ordinal);
        Assert.Equal(input.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathFor(host, input.Path)))));
        Assert.NotNull(producerBeforeConsumer);
        Assert.Equal(producerBeforeConsumer.OrderBy(pair => pair.Key), Manifest(host, Root("product")).OrderBy(pair => pair.Key));
        Assert.Equal("run inner pipeline", result.Plan!.Members[0].Work);
        Assert.Equal(["product"], result.Plan.Members[1].DependsOn);
        Assert.All(result.Plan.Members, member => Assert.False(member.ParallelEligible));
        Assert.Contains(input.Path, File.ReadAllText(PathFor(host, ".copilot-tracking/squad/decisions.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenExistingVerifiedProducer_WhenHandingOff_ReusesActualEnumeratedArtifactWithoutProducerCalls()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueWriter(host);
        using (var product = await federation.CreateMemberRuntimeAsync("product"))
        {
            var produced = await product.RunAsync("Write bounded local notes.");
            Assert.True(produced.Status == SquadRunStatus.Completed, produced.ResponseText);
        }
        var before = Manifest(host, Root("product"));
        QueueWriter(host, input: true);

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.True(result.Members.Single(member => member.Member == "product").Reused);
        Assert.Equal(before.OrderBy(pair => pair.Key), Manifest(host, Root("product")).OrderBy(pair => pair.Key));
        Assert.Single(result.Handoffs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenStaleOrMissingProducerArtifact_WhenHandingOff_RepairsOnlyProducingStage(bool missing)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueWriter(host);
        string artifact;
        using (var product = await federation.CreateMemberRuntimeAsync("product"))
        {
            var produced = await product.RunAsync("Write bounded local notes.");
            artifact = produced.Evidence.Single(evidence => evidence.Stage == "produce").ArtifactPath;
        }
        if (missing)
        {
            File.Delete(PathFor(host, artifact));
        }
        else
        {
            File.WriteAllText(PathFor(host, artifact), "Unverified changed bytes.");
        }
        var beforeCalls = host.Client.Calls.Count;
        host.QueueStage("technical-writer");
        QueueWriter(host, input: true);

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var repaired = result.Members.Single(member => member.Member == "product").Result!;
        Assert.Equal(SquadRunStatus.ArtifactCompleted, repaired.Status);
        Assert.Equal("produce", Assert.Single(repaired.Evidence).Stage);
        Assert.Equal(2, host.Client.Calls.Skip(beforeCalls).Count(call => call.Options!.Tools!.Any(tool => tool.Name == "run_pipeline")));
        Assert.NotEqual(artifact, Assert.Single(Assert.Single(result.Handoffs).Artifacts).Path);
    }

    [Fact]
    public async Task GivenAcceptedMissingOwnerGap_WhenDelivering_CarriesVisibleAttributedReadyWithGapsInActualOutput()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var dependency = new SquadFederationDependency(null, "platform", "technical-writer");
        QueueWriter(host);

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [dependency],
            InputOverrides = [new(dependency.Id, Assumption: "Use only the supplied local synthetic scope.")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var gap = Assert.Single(Assert.Single(result.Handoffs).Assumptions);
        Assert.Equal("confirming trusted host user", gap.ChosenBy);
        var output = result.Members.Single(member => member.Member == "platform").Result!.Evidence.Single(evidence => evidence.Stage == "produce");
        var bytes = File.ReadAllText(PathFor(host, output.ArtifactPath));
        Assert.Contains("## Ready-With-Gaps", bytes, StringComparison.Ordinal);
        Assert.Contains(gap.Description, bytes, StringComparison.Ordinal);
        Assert.Contains(gap.ChosenBy, bytes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("state.json")]
    [InlineData("decisions.md")]
    [InlineData("history/Test technical-writer.md")]
    public async Task GivenPrivateStateAsUserInput_WhenHandingOff_DeniesBeforeConsumer(string privatePath)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var dependency = new SquadFederationDependency(null, "platform", "technical-writer");

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [dependency],
            InputOverrides = [new(dependency.Id, Path: Root("product") + "/" + privatePath)],
        });

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.Empty(result.Handoffs);
    }

    [Fact]
    public async Task GivenUntargetedAutopilot_WhenDelivering_UsesRealInnerStagesAndOneConsolidatedHumanOutcome()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        QueueWriter(host, automatic: true);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.True(result.FinalValidated);
        Assert.True(result.Plan!.AggregateAutopilot);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.FinalOutcome);
        Assert.All(result.Members, member =>
        {
            Assert.Equal("autopilot", member.Result!.ModeOutcome!.Mode);
            Assert.False(member.Result.ModeOutcome.FinalValidated);
            Assert.Equal(["research", "plan", "council", "produce", "council", "review"], member.Result.Evidence.Select(evidence => evidence.Stage));
        });
        Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/history/autopilot-run-" + result.RunId + ".md")));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/consumption.md")));
    }

    [Fact]
    public async Task GivenSingleTargetAutopilot_WhenDelivering_KeepsInnerFinalAcceptanceAndNoAggregateGate()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Target = "product" });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.False(result.Plan!.AggregateAutopilot);
        Assert.False(result.FinalValidated);
        Assert.True(Assert.Single(result.Members).Result!.ModeOutcome!.FinalValidated);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.FinalOutcome);
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/history/autopilot-run-" + result.RunId + ".md")));
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("calibration")]
    [InlineData("stale")]
    [InlineData("identity")]
    public async Task GivenIncompleteAggregatePricing_WhenStarting_CannotConfirmBeforeAnyModelAndCannotBeApproved(string defect)
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, "100000", defect == "calibration" ? 0 : 1);
        if (defect == "rates")
        {
            host.Options.Consumption = null;
        }
        else if (defect == "stale")
        {
            host.Options.Consumption!.ObservedOn = ConsumptionTestData.Today.AddDays(-91);
        }
        else if (defect == "identity")
        {
            host.Options.ModelSelector = (_, _) => new(host.Client, "unpriced-model");
        }
        using var federation = await Create(host);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.InputRequired, result.Diagnostic);
        Assert.Equal("cannot-confirm", result.CostPreflight!.Decision);
        Assert.Empty(host.Client.Calls);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Contains(result.CostPreflight.Demand, row => row.Slot.StartsWith("product:", StringComparison.Ordinal));
        Assert.Contains(result.CostPreflight.Demand, row => row.Slot.StartsWith("platform:", StringComparison.Ordinal));
        Assert.Contains(result.CostPreflight.Demand, row => row.Slot == "meta:coordinator" && row.DeterministicNative);
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/consumption.md")));
    }

    [Fact]
    public async Task GivenVerifiedAggregateRates_WhenDelivering_ReservesOnceAndTotalsEveryActualInnerAttemptExactlyOnce()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, "100000");
        var metered = new MeteredClient(host.Client);
        host.Options.ModelSelector = (_, _) => new(metered, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0,
            response.Usage.OutputTokenCount!.Value);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        QueueWriter(host, automatic: true);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var state = State(host);
        var first = state.CostPreflights!.First(round => round.RunId == result.RunId);
        Assert.Equal("within-ceiling", first.Decision);
        Assert.Equal(ConsumptionMath.Round(first.Demand.Sum(row => row.ReservedAdmissionUsd ??
            row.ProjectedCostUsd!.Value * row.Count * 3m)), first.AdmissionCostUsd);
        var innerCharges = result.Members.SelectMany(member => State(host, Root(member.Member)).ModelCallCharges!
            .Where(charge => charge.RunId == member.InnerRunId)).ToArray();
        Assert.Equal(innerCharges.Length, result.Cost!.ProviderAttempts);
        Assert.Equal(innerCharges.Select(charge => charge.CallId).Distinct(StringComparer.Ordinal).Count(), innerCharges.Length);
        Assert.Equal(ConsumptionMath.Round(innerCharges.Sum(charge => charge.CostUsd!.Value)), result.Cost.EstimatedCostUsd);
        Assert.Equal(result.Cost, state.FederationCurrentRun);
        Assert.Equal(0m, result.Cost.OrchestrationCostUsd);
        Assert.DoesNotContain(result.Members, member => member.Result!.CostPreflight!.CeilingUsd is not null);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenAggregateOvershoot_WhenDeclined_StopsBeforeEveryProviderAndPreservesExactManifest()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, ".0001");
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.CostOvershoot);
        };
        using var federation = await Create(host);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal("over-ceiling", result.CostPreflight!.Decision);
        Assert.Empty(host.Client.Calls);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenApprovedAggregateOvershoot_WhenDelivering_PermitsOnlyExactSequentialSlots()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, "1");
        using var metered = new MeteredClient(host.Client);
        host.Options.ModelSelector = (_, _) => new(metered, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0,
            response.Usage.OutputTokenCount!.Value);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        QueueWriter(host, automatic: true);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var approvals = State(host).CostPreflights!.Where(round => round.Decision == "approved-over-ceiling").ToArray();
        Assert.NotEmpty(approvals);
        Assert.All(approvals, round => Assert.Single(round.PermittedSlots));
        Assert.Equal(1m, result.CostPreflight!.CeilingUsd);
        Assert.True(result.Cost!.EstimatedCostUsd < 1m);
    }

    [Fact]
    public async Task GivenActualAggregateSpendAtCeiling_WhenNextRoleStarts_CannotApproveOrInvokeAnotherProvider()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, ".1");
        using var metered = new MeteredClient(host.Client, input: 100_000, output: 0);
        host.Options.ModelSelector = (_, _) => new(metered, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0,
            response.Usage.OutputTokenCount!.Value);
        using var federation = await Create(host);
        host.QueuePipeline("technical-writer");

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.InputRequired, result.Diagnostic);
        Assert.Equal("over-ceiling", result.CostPreflight!.Decision);
        Assert.Contains("at or above", result.CostPreflight.Diagnostic!, StringComparison.Ordinal);
        Assert.Single(host.Client.Calls);
        Assert.Equal(1, result.Cost!.ProviderAttempts);
        Assert.Equal(.3m, result.Cost.EstimatedCostUsd);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenResealedChangedMemberIdentity_WhenResuming_RejectsItAgainstConfirmedMetaScript()
    {
        using var host = new RuntimeTestHost();
        using var output = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        var result = await federation.RunAsync("Inspect bounded original code.", new() { Target = "product" });
        await Respond(federation, "federationplan");
        host.Client.Steps.Enqueue((_, _) => throw new OperationCanceledException("Exact original interruption."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => federation.ResumeAsync(result.RunId));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PathFor(host, ".copilot-tracking/squad/state.json")))!;
        json["FederationRun"]!["Steps"]![0]!["InnerRunId"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(PathFor(host, ".copilot-tracking/squad/state.json"), json.ToJsonString());
        var calls = host.Client.Calls.Count;

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => federation.ResumeAsync(result.RunId));

        Assert.Equal(calls, host.Client.Calls.Count);
    }
    [Fact]
    public async Task GivenAuthenticatedFinalGate_WhenRestarting_AcceptsOneConsolidatedOutcomeWithoutReplayingCompletedModels()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var output = RuntimeModeTests.Durable(host);
        SquadFederationRunResult pending;
        int completedCalls;
        using (var federation = await Create(host))
        {
            QueueWriter(host, automatic: true);
            pending = await ToFinal(federation,
                await federation.RunAsync("Write bounded local notes.", new() { Members = ["product"] }));
            Assert.False(pending.FinalValidated);
            completedCalls = host.Client.Calls.Count;
            Assert.Equal("federation-awaiting-validation", State(host, Root("product")).Checkpoint!.Status);
            await Respond(federation, "finaloutcome");
        }
        using var restarted = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());

        var completed = await restarted.ResumeAsync(pending.RunId);

        Assert.True(completed.Status == SquadRunStatus.Completed, completed.Diagnostic);
        Assert.True(completed.FinalValidated);
        Assert.Equal(completedCalls, host.Client.Calls.Count);
        Assert.Null(State(host, Root("product")).Checkpoint);
        Assert.Null(State(host).Checkpoint);
        Assert.Single(State(host).HumanDecisions!, receipt => receipt.Binding.Gate == "finaloutcome");
        Assert.False(Assert.Single(completed.Members).Result!.ModeOutcome!.FinalValidated);
    }

    [Fact]
    public async Task GivenAuthenticatedConsolidatedChanges_WhenChoosingOwner_ReentersOnlyThatInnerRunAndNeverAddsAThirdCycle()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var output = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        var pending = await ToFinal(federation,
            await federation.RunAsync("Write bounded local notes.", new() { Members = ["product"] }));
        var initial = Assert.Single(pending.Members).Result!;
        var originalArtifacts = initial.Evidence.Select(evidence => new
        {
            evidence.ArtifactPath,
            Bytes = File.ReadAllBytes(PathFor(host, evidence.ArtifactPath)),
        }).ToArray();
        await Respond(federation, "finaloutcome", "/changes: Clarify only this bounded original output.");
        var changed = await federation.ResumeAsync(pending.RunId);
        Assert.Equal(SquadRunStatus.ChangesRequested, changed.Status);
        var proposal = await federation.ReviseMemberAsync(pending.RunId, new("product"));
        Assert.Equal(SquadRunStatus.ApprovalRequired, proposal.Status);
        await Respond(federation, "federationplan");
        host.QueueStage("technical-writer");
        RuntimeModeTests.Council(host);
        host.QueueStage("tester");

        var revised = await federation.ReviseMemberAsync(pending.RunId, new("product"));
        Assert.Equal(SquadRunStatus.ApprovalRequired, revised.Status);
        await Respond(federation, "autonomousoutcome");
        revised = await ToFinal(federation, await federation.ResumeAsync(pending.RunId));
        var actual = Assert.Single(revised.Members).Result!;
        Assert.Equal(initial.RunId, actual.RunId);
        Assert.Equal(2, actual.ModeOutcome!.Iterations.Count);
        Assert.Equal(initial.Evidence.Single(evidence => evidence.Stage == "research").DispatchId,
            actual.Evidence.Single(evidence => evidence.Stage == "research").DispatchId);
        Assert.Equal(initial.Evidence.Single(evidence => evidence.Stage == "plan").DispatchId,
            actual.Evidence.Single(evidence => evidence.Stage == "plan").DispatchId);
        Assert.All(originalArtifacts, artifact => Assert.Equal(artifact.Bytes, File.ReadAllBytes(PathFor(host, artifact.ArtifactPath))));
        await Respond(federation, "finaloutcome");
        var accepted = await federation.ResumeAsync(pending.RunId);
        Assert.True(accepted.Status == SquadRunStatus.Completed, accepted.Diagnostic);
    }

    [Fact]
    public async Task GivenAnInnerRiskGate_WhenSurfaced_PreservesTheOwningMemberAndMostRestrictiveStop()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var output = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        RuntimeModeTests.Council(host, verdict: "Block", risk: "High");
        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product"] });
        await Respond(federation, "federationplan");
        result = await federation.ResumeAsync(result.RunId);
        var risk = Assert.Single(await federation.GetPendingHumanActionsAsync(), action => action.Status == "pending");
        Assert.Equal("riskgate", risk.Binding.Gate);
        Assert.Equal("product", risk.Binding.FederationMember);
        Assert.Equal(result.RunId, risk.Binding.FederationRunId);
        var details = await federation.ReadHumanActionAsync(risk.Binding.Id);
        Assert.Equal("product", details.Approval!.FederationGate!.Member);
        Assert.NotNull(State(host, Root("product")).Checkpoint!.Mode!.RiskApproval);
        Assert.Empty(host.Client.Steps);
        await Respond(federation, "riskgate");

        var stopped = await federation.ResumeAsync(result.RunId);

        Assert.True(stopped.Status == SquadRunStatus.Blocked, stopped.Diagnostic);
        Assert.DoesNotContain(stopped.Members, member => member.Result?.Evidence.Any(evidence => evidence.Stage == "produce") == true);
        Assert.False(stopped.FinalValidated);
    }

    [Fact]
    public async Task GivenExplicitIndependentMembers_WhenRunning_InvokesActualProvidersConcurrentlyWithinBound()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.Options.MaxConcurrentDispatches = 2;
        using var product = new ScriptedRuntimeChatClient();
        using var platform = new ScriptedRuntimeChatClient();
        QueueWriter(host);
        while (host.Client.Steps.TryDequeue(out var step))
        {
            product.Steps.Enqueue(step);
        }
        QueueWriter(host);
        while (host.Client.Steps.TryDequeue(out var step))
        {
            platform.Steps.Enqueue(step);
        }
        using var routed = new MemberClient(new Dictionary<string, ScriptedRuntimeChatClient>
        {
            ["product"] = product,
            ["platform"] = platform,
        });
        host.Options.ModelSelector = (_, _) => new(routed);
        using var federation = await Create(host);

        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal(2, routed.MaximumActive);
        Assert.All(result.Plan!.Members, member => Assert.True(member.ParallelEligible));
        Assert.All(result.Members, member => Assert.All(member.Result!.Evidence, evidence =>
            Assert.StartsWith(Root(member.Member) + "/", evidence.ArtifactPath, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("read-state")]
    [InlineData("read-artifact")]
    [InlineData("read-hub")]
    public async Task GivenNoHandoff_WhenMemberTriesToDiscoverOtherRoots_BlocksBeforeDataDisclosure(string operation)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var source = Root("product") + "/technical-writer/original.md";
        Directory.CreateDirectory(Path.GetDirectoryName(PathFor(host, source))!);
        File.WriteAllText(PathFor(host, source), "Private original producer artifact.");
        host.Client.Steps.Enqueue((_, _) => RuntimeTestHost.Function(
            operation == "list" ? "project_list" : "project_read", new()
            {
                [operation == "list" ? "directory" : "path"] = operation == "list" ? Root("product") :
                    operation == "read-state" ? Root("product") + "/state.json" :
                    operation == "read-hub" ? ".copilot-tracking/squad/decisions.md" : source,
            }));
        using var member = await federation.CreateMemberRuntimeAsync("platform");

        var result = await member.RunAsync("Inspect bounded local code.");

        Assert.True(result.Status is SquadRunStatus.Failed or SquadRunStatus.Blocked, result.ResponseText);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain("Private original producer artifact.", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenChangedApprovedHandoff_WhenStartingConsumer_DeniesBeforeItsFirstModel()
    {
        using var host = new RuntimeTestHost();
        using var output = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        var dependency = new SquadFederationDependency(null, "platform", "technical-writer");
        File.WriteAllText(PathFor(host, "original-input.md"), "Original user-authorized input.");
        var initial = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [dependency],
            InputOverrides = [new(dependency.Id, Path: "original-input.md")],
        });
        await Respond(federation, "federationplan");
        initial = await federation.ResumeAsync(initial.RunId);
        await Respond(federation, "federationhandoff");
        File.WriteAllText(PathFor(host, "original-input.md"), "Different unapproved input.");

        var blocked = await federation.ResumeAsync(initial.RunId);

        Assert.True(blocked.Status is SquadRunStatus.Blocked or SquadRunStatus.ApprovalRequired, blocked.Diagnostic);
        Assert.Empty(host.Client.Calls);
        Assert.False(blocked.FinalValidated);
    }

    [Fact]
    public async Task GivenCancelledConsumer_WhenRestarting_PreservesProducerBytesAndResumesSameInnerIdentity()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueWriter(host);
        host.Client.Steps.Enqueue((_, _) => throw new OperationCanceledException("Original test interruption."));
        var configuration = new SquadFederationRunConfiguration
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            federation.RunAsync("Write bounded local notes.", configuration));
        var pending = State(host).FederationRun!;
        var producer = Manifest(host, Root("product"));
        var consumerId = pending.Steps.Single(step => step.Member == "platform").InnerRunId;
        QueueWriter(host, input: true);

        var resumed = await federation.ResumeAsync(pending.RunId);

        Assert.True(resumed.Status == SquadRunStatus.Completed, resumed.Diagnostic);
        Assert.Equal(consumerId, resumed.Members.Single(member => member.Member == "platform").InnerRunId);
        Assert.Equal(producer.OrderBy(pair => pair.Key), Manifest(host, Root("product")).OrderBy(pair => pair.Key));
        Assert.Single(resumed.Handoffs);
    }

    [Fact]
    public async Task GivenMissingOwner_WhenConfirmingExpansion_ContinuesOriginalUnstartedMetaTurn()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host, Member("platform"));
        var configuration = new SquadFederationRunConfiguration
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        };
        var first = await federation.RunAsync("Write bounded local notes.", configuration);
        Assert.Equal(SquadRunStatus.InputRequired, first.Status);
        Assert.Empty(host.Client.Calls);
        var expanded = await federation.InitializeAsync([Member("product")]);
        Assert.True(expanded.Status == SquadRunStatus.Completed, expanded.Diagnostic);
        QueueWriter(host);
        QueueWriter(host, input: true);

        var result = await federation.ResumeAsync(first.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal(first.RunId, result.RunId);
        Assert.Single(result.Handoffs);
    }

    [Fact]
    public async Task GivenTwoEligibleProfiles_WhenChoosingDiscovery_AsksOnceAtHubAndNeverRepeatsMemberOffer()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.Options.MaxConcurrentDispatches = 1;
        var questions = new List<SquadQuestion>();
        host.Options.AskAsync = (question, _) =>
        {
            questions.Add(question);
            return ValueTask.FromResult<string?>("skip");
        };
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        var product = Member("product");
        product.Roster = new() { Profile = "product" };
        var platform = Member("platform");
        platform.Roster = new() { Profile = "full" };
        Assert.Equal(SquadRunStatus.Completed, (await federation.InitializeAsync([product, platform])).Status);
        QueueResearch(host);
        QueueResearch(host);

        var result = await federation.RunAsync("Inspect bounded original code.", new() { Members = ["product", "platform"] });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal("federation-discovery", Assert.Single(questions).Role);
        Assert.Equal("skip", result.Plan!.Discovery);
        Assert.All(result.Members, member => Assert.Equal(SquadRunStatus.ResearchCompleted, member.Result!.Status));
    }

    [Fact]
    public async Task GivenFailedBoundedProducerRepair_WhenMissingInput_RemainsStoppedWithoutConsumerOrAutomaticSecondAttempt()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        QueueWriter(host);
        using (var producer = await federation.CreateMemberRuntimeAsync("product"))
        {
            var original = await producer.RunAsync("Write bounded local notes.");
            File.Delete(PathFor(host, original.Evidence.Single(evidence => evidence.Stage == "produce").ArtifactPath));
        }
        host.QueueStage("technical-writer", verdict: "Blocked", blockers: 1);
        var first = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        });
        var calls = host.Client.Calls.Count;
        Assert.False(first.Status == SquadRunStatus.Completed);
        Assert.Empty(State(host, Root("platform")).Runs);

        var second = await federation.ResumeAsync(first.RunId);

        Assert.False(second.Status == SquadRunStatus.Completed);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Empty(second.Handoffs);
        Assert.Empty(State(host, Root("platform")).Runs);
    }

    [Fact]
    public async Task GivenAcceptedAggregate_WhenReconcilingBilling_CalibratesOnceWithoutChangingAnyInnerBytes()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        ConsumptionTestData.Configure(host, "100000");
        using var metered = new MeteredClient(host.Client);
        host.Options.ModelSelector = (_, _) => new(metered, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0,
            response.Usage.OutputTokenCount!.Value);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        var result = await federation.RunAsync("Write bounded local notes.", new() { Members = ["product"] });
        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var before = Manifest(host, Root("product"));
        var denominator = State(host, Root("product")).ModelCallCharges!.Where(charge => charge.RunId == result.Members[0].InnerRunId)
            .Sum(charge => charge.UncalibratedCostUsd!.Value);
        var observation = new SquadConsumptionReconciliation(result.RunId, denominator * 2m, "original-aggregate-billing");

        var first = await federation.ReconcileConsumptionAsync(observation);
        var second = await federation.ReconcileConsumptionAsync(observation);

        Assert.Equal(SquadRunStatus.Completed, first.Status);
        Assert.Equal(1.5m, first.Factor);
        Assert.Equal(2, first.Observations);
        Assert.Equal(first.Factor, second.Factor);
        Assert.Equal(first.Observations, second.Observations);
        Assert.Single(State(host).ConsumptionReconciliations!);
        Assert.Equal(before.OrderBy(pair => pair.Key), Manifest(host, Root("product")).OrderBy(pair => pair.Key));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/consumption.md")));
    }

    [Fact]
    public async Task GivenAuthenticatedStop_WhenPendingFinal_PreservesActualSpendAndClearsOnlyQuiescentMemberContinuation()
    {
        using var host = new RuntimeTestHost();
        Automatic(host);
        using var output = RuntimeModeTests.Durable(host);
        using var federation = await Create(host);
        QueueWriter(host, automatic: true);
        var pending = await ToFinal(federation,
            await federation.RunAsync("Write bounded local notes.", new() { Members = ["product"] }));
        var attempts = pending.Cost!.ProviderAttempts;
        var calls = host.Client.Calls.Count;
        await Respond(federation, "finaloutcome", "/stop");

        var stopped = await federation.ResumeAsync(pending.RunId);

        Assert.Equal(SquadRunStatus.Stopped, stopped.Status);
        Assert.Equal(attempts, stopped.Cost!.ProviderAttempts);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.False(stopped.FinalValidated);
        Assert.Null(State(host).Checkpoint);
        Assert.Null(State(host, Root("product")).Checkpoint);
    }

    [Fact]
    public async Task GivenPromotedProducer_WhenHandingOff_UsesRebasedActualArtifactAndUnchangedPriorHistory()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        QueueWriter(host);
        using (var single = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.Completed, (await single.RunAsync("Write bounded local notes.")).Status);
        }
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        var moved = await federation.PromoteAsync("product", federation.GetPromotionCandidates().Select(candidate => candidate.Path).ToArray());
        Assert.True(moved.Status == SquadRunStatus.Completed, moved.Diagnostic);
        Assert.Equal(SquadRunStatus.Completed, (await federation.InitializeAsync([Member("platform")])).Status);
        var producer = Manifest(host, Root("product"));
        QueueWriter(host, input: true);

        var result = await federation.RunAsync("Write bounded local notes.", new()
        {
            Members = ["platform"],
            Dependencies = [new("product", "platform", "technical-writer")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.StartsWith(Root("product") + "/", Assert.Single(Assert.Single(result.Handoffs).Artifacts).Path, StringComparison.Ordinal);
        Assert.Equal(producer.OrderBy(pair => pair.Key), Manifest(host, Root("product")).OrderBy(pair => pair.Key));
    }

    [Trait("Category", "ReleaseCompatibility")]
    [Fact]
    public async Task GivenActualPinnedRelease_WhenRoutingDependency_ExecutesProducerFirstAndActualConsumerWithExplicitInput()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        using var releaseClient = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        var source = ReleaseTestHttpClient.Source(version, releaseClient);
        using var host = new RuntimeTestHost();
        host.Options.Discovery = "skip";
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, source);
        Assert.Equal(SquadRunStatus.Completed, (await federation.InitializeAsync([Member("product"), Member("platform")])).Status);
        QueueResearch(host);
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("researcher");
        host.Client.Text("Actual consumer research with original producer evidence.");

        var result = await federation.RunAsync("Inspect bounded original code.", new()
        {
            Members = ["product", "platform"],
            Dependencies = [new("product", "platform", "researcher", Stage: "research")],
        });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal(version, federation.Provenance.Release!.Tag);
        Assert.Equal(["product", "platform"], result.Members.Select(member => member.Member));
        Assert.Single(Assert.Single(result.Handoffs).Artifacts);
        Assert.All(result.Members, member => Assert.Equal(SquadRunStatus.ResearchCompleted, member.Result!.Status));
    }

    private sealed class MemberClient(IReadOnlyDictionary<string, ScriptedRuntimeChatClient> clients) : IChatClient
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<string> _arrived = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();
        private int _active;
        internal int MaximumActive { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            var text = string.Join("\n", snapshot.Select(message => message.Text));
            var match = Regex.Match(text, @"\.copilot-tracking/squad/members/(?<name>[a-z0-9-]+)/",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.True(match.Success, "Every actual member invocation must carry its own concrete root.");
            var name = match.Groups["name"].Value;
            var active = Interlocked.Increment(ref _active);
            using (_sync.EnterScope())
            {
                MaximumActive = Math.Max(MaximumActive, active);
                _arrived.Add(name);
                if (_arrived.Count == clients.Count)
                {
                    _both.TrySetResult();
                }
            }
            try
            {
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return await clients[name].GetResponseAsync(snapshot, options, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MeteredClient(IChatClient inner, long input = 10, long output = 1) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.ModelId = "model-fixed";
            response.Usage = new() { InputTokenCount = input, OutputTokenCount = output };
            return response;
        }
    }
}
