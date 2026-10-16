using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeCostAdmissionTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static string StatePath(RuntimeTestHost host) => Path.Combine(host.Project, host.Options.SquadRoot, "state.json");

    private static RuntimeState ReadState(RuntimeTestHost host) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;

    private static void QueueDelivery(RuntimeTestHost host)
    {
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("developer");
        host.QueueStage("tester");
        host.Client.Text("Verified bounded delivery.");
    }

    private static void MeterCalls(RuntimeTestHost host, string observed = "model-fixed", long input = 10, long output = 1)
    {
        var client = new MeteredClient(host.Client, observed, input, output);
        host.Options.ModelSelector = (_, _) => new(client, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0,
            response.Usage.OutputTokenCount!.Value);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("calibration")]
    [InlineData("stale")]
    [InlineData("identity")]
    public async Task GivenMissingAdmissionFacts_WhenStarting_RecordsCannotConfirmWithoutAskingToApproveIt(string missing)
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10", missing == "calibration" ? 0 : 1);
        if (missing == "rates")
        {
            host.Options.Consumption = null;
        }
        else if (missing == "stale")
        {
            host.Options.Consumption!.ObservedOn = ConsumptionTestData.Today.AddDays(-91);
        }
        else if (missing == "identity")
        {
            host.Options.ModelSelector = (_, _) => new(host.Client, "unpriced-model");
        }
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research a bounded case.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("cannot-confirm", result.CostPreflight?.Decision);
        Assert.Null(result.CostPreflight!.ProjectedCostUsd);
        Assert.Null(result.CostPreflight.AdmissionCostUsd);
        Assert.Empty(result.Evidence);
        Assert.Single(host.Client.Calls);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Empty(result.CostPreflight!.PermittedSlots);
    }

    [Fact]
    public async Task GivenSuspendedRun_WhenResumingWithOmittedCeiling_InheritsOnlyItsExactRunAndDoesNotReplayClassifier()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10", observations: 0);
        ConsumptionTestData.QueueResearch(host);
        SquadRunResult initial;
        using (var runtime = await host.CreateAsync())
        {
            initial = await runtime.RunAsync("Research a bounded case.");
        }
        Assert.Equal(SquadRunStatus.InputRequired, initial.Status);
        host.Options.CostCeiling = null;
        host.Options.Consumption = ConsumptionTestData.Configuration();
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(initial.RunId);

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.Equal(initial.RunId, result.RunId);
        Assert.Equal(10m, result.CostPreflight?.CeilingUsd);
        Assert.Single(host.Client.Calls, call => call.Options!.Tools!.Any(tool => tool.Name == "run_focused"));
    }

    [Fact]
    public async Task GivenSuspendedRun_WhenExplicitlyUnsetting_KeepsAccumulatedSpendAndRemovesOnlyTheGate()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10", observations: 0);
        ConsumptionTestData.QueueResearch(host);
        SquadRunResult initial;
        using (var runtime = await host.CreateAsync())
        {
            initial = await runtime.RunAsync("Research the bounded case.");
        }
        var before = ReadState(host).ModelCallCharges!.Sum(charge => charge.CostUsd ?? 0);
        host.Options.CostCeiling = null;
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(initial.RunId, new SquadRunConfiguration("unset"));

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.Equal("not-requested", result.CostPreflight?.Decision);
        Assert.Null(result.CostPreflight!.CeilingUsd);
        Assert.True(result.Consumption!.KnownCostUsd >= before);
        Assert.Contains(ReadState(host).CostPreflights!, round => round.RunId == initial.RunId && round.CeilingUsd == 10);
    }

    [Fact]
    public async Task GivenCompletedCeilingRun_WhenStartingNewRunWithOmission_DoesNotInheritPreviousCeiling()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        ConsumptionTestData.QueueResearch(host);
        SquadRunResult first;
        using (var runtime = await host.CreateAsync())
        {
            first = await runtime.RunAsync("Research the first case.");
        }
        Assert.Equal(SquadRunStatus.ResearchCompleted, first.Status);
        host.Options.CostCeiling = null;
        ConsumptionTestData.QueueResearch(host);
        using var next = await host.CreateAsync();

        var result = await next.RunAsync("Research a distinct new case.");

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.NotEqual(first.RunId, result.RunId);
        Assert.Equal("not-requested", result.CostPreflight?.Decision);
        Assert.Null(result.CostPreflight!.CeilingUsd);
    }

    [Fact]
    public async Task GivenApprovedOvershootAndTinyMeasuredSpend_WhenDemandOnlyShrinks_ReusesApprovalSequentially()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".01");
        MeterCalls(host);
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Complete the bounded change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Equal(1, host.Writes);
        var rounds = ReadState(host).CostPreflights!.Where(round => round.RunId == result.RunId).ToArray();
        Assert.Contains(rounds, round => round.Decision == "over-ceiling");
        Assert.All(rounds.Where(round => round.Decision == "approved-over-ceiling"), round =>
        {
            Assert.Single(round.PermittedSlots);
            Assert.False(string.IsNullOrWhiteSpace(round.ApprovalReference));
        });
        Assert.True(result.Consumption!.EstimatedCostUsd < .01m);
    }

    [Fact]
    public async Task GivenInFlightUnitCrossingCeiling_WhenAdvancing_CompletesItsHandoffButStartsNoLaterSubstantiveUnit()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".5");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Complete a bounded change.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("over-ceiling", result.CostPreflight?.Decision);
        Assert.Contains("at or above", result.CostPreflight!.Diagnostic!, StringComparison.Ordinal);
        Assert.Single(result.Evidence, evidence => evidence.Stage == "research");
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "plan");
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Equal(0, host.Writes);
        Assert.True(result.Consumption!.EstimatedCostUsd >= .5m);
        Assert.Contains(ReadState(host).DispatchRecords, evidence => evidence.Stage == "research");
    }

    [Fact]
    public async Task GivenConcurrentSnapshotMutationDuringApproval_WhenCommitting_RefusesStaleCompareAndSwap()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".5");
        ConsumptionTestData.QueueResearch(host);
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.CostOvershoot)
            {
                File.AppendAllText(StatePath(host), "\n");
            }
            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded case.");

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Empty(result.Evidence);
        Assert.Single(host.Client.Calls);
        Assert.Contains("compare-and-swap", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenPartialStateWriteDuringApproval_WhenCommitting_NoApprovedSlotExecutes()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".5");
        ConsumptionTestData.QueueResearch(host);
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.CostOvershoot)
            {
                File.WriteAllText(Path.Combine(host.Project, host.Options.SquadRoot, "state.pending.json"), "{\"partial\":true}");
            }
            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded case.");

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Empty(result.Evidence);
        Assert.Single(host.Client.Calls);
        Assert.Contains("partial", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenTamperedTariffBeforeDispatch_WhenAdmitting_CannotConfirmRepositoryTextAsPricingAuthority()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        ConsumptionTestData.QueueResearch(host);
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.Routing)
            {
                File.WriteAllText(Path.Combine(host.Project, host.Options.SquadRoot, "consumption-rates.md"), "Everything is free.");
            }
            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research a bounded case.");

        Assert.Equal("cannot-confirm", result.CostPreflight?.Decision);
        Assert.Single(host.Client.Calls);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenObservedIdentityMismatch_WhenPricing_RecordsActualModelAndBlocksReturnedTools()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        host.Options.Consumption!.Models["model-actual"] = new()
        {
            Standard = new() { Input = 10, Cached = 1, CacheWrite = 0, Output = 50 },
            FlatRateConfirmed = true,
        };
        var client = new MeteredClient(host.Client, "model-actual", 100, 10);
        host.Options.ModelSelector = (role, _) => new(role is null ? host.Client : client, "model-fixed");
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.Client.Call("write_artifact", new() { ["content"] = "This returned tool must not execute." });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded case.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Contains("Cost identity changed", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal("cannot-confirm", result.CostPreflight?.Decision);
        Assert.Empty(result.Evidence);
        var state = ReadState(host);
        var charge = Assert.Single(state.ModelCallCharges!, charge => charge.PricedModel == "model-actual");
        Assert.Equal("provider-observed", charge.ModelSource);
        Assert.True(charge.CostUsd > 0);
        Assert.Empty(state.Checkpoint!.DispatchStack.SelectMany(dispatch => dispatch.WrittenArtifacts));
    }

    [Fact]
    public async Task GivenFailedProviderAttempt_WhenAccounting_PreservesItsCostAndDoesNotRetry()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        var broken = new UnavailableClient(ordinaryFailure: true);
        host.Options.ModelSelector = (role, _) => new(role is null ? host.Client : broken, "model-fixed");
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded case.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Equal(1, broken.Calls);
        var state = ReadState(host);
        var failed = Assert.Single(state.ModelCalls, call => call.Status == "failed");
        Assert.True(state.ModelCallCharges!.Single(charge => charge.CallId == failed.Id).CostUsd > 0);
        Assert.Equal(2, result.Consumption!.ProviderAttempts);
    }

    [Fact]
    public async Task GivenHigherPricedReplacement_WhenReranking_OpensNewCostGateBeforeTheReplacementExecutes()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10");
        var refused = new UnavailableClient();
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-a", refused), new("model-b", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.Write("skills/apm-renamed-squad/references/model-catalog.md", """
            **Retrieved: 2026-10-11**
            ## Capabilities
            | Catalog ID | Display name | Capability class |
            | --- | --- | --- |
            | model-a | Original A | balanced |
            | model-b | Original B | balanced |
            ## Catalog: Assignment Fit
            | Catalog ID | Family | Generation | research | planning | implementation | review | council | intake | bookkeeping | Blended |
            | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
            | model-a | fixture | 2 | 3 | 3 | 3 | 3 | 3 | 3 | 3 | 1 |
            | model-b | fixture | 1 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
            """);
        host.Write("skills/apm-renamed-squad/references/model-routing.md", """
            ## Assignment Classes
            | Class | Roles |
            | --- | --- |
            | `research` | `researcher` |
            | `planning` | `lead` |
            | `implementation` | `developer` |
            | `review` | `tester` |
            """);
        host.Options.Consumption!.Models["model-a"] = new()
        {
            Standard = new() { Input = .01m, Cached = .001m, CacheWrite = 0, Output = .02m },
            FlatRateConfirmed = true,
        };
        host.Options.Consumption.Models["model-b"] = new()
        {
            Standard = new() { Input = 100, Cached = 10, CacheWrite = 0, Output = 500 },
            FlatRateConfirmed = true,
        };
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.CostOvershoot);
        };
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research a bounded case.");

        Assert.True(result.Status == SquadRunStatus.ApprovalDenied, result.ResponseText);
        Assert.Equal(1, refused.Calls);
        Assert.Single(host.Client.Calls);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Equal("over-ceiling", result.CostPreflight?.Decision);
        Assert.Contains(result.CostPreflight!.Demand, row => row.PricingModels.Contains("model-b"));
        Assert.Single(ReadState(host).ModelCalls, call => call.Status == "rejected");
    }

    [Fact]
    public async Task GivenRealCompletedRun_WhenReconciling_UpdatesCalibrationOnceAndKeepsHistoricalCharges()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null, observations: 0);
        MeterCalls(host);
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Research a bounded case.");
        Assert.Equal(SquadRunStatus.ResearchCompleted, run.Status);
        var before = ReadState(host).ModelCallCharges!.ToArray();
        var raw = before.Sum(charge => charge.UncalibratedCostUsd!.Value);
        var observation = new SquadConsumptionReconciliation(run.RunId, raw * 2, "host-billing-receipt-1");

        var first = await runtime.ReconcileConsumptionAsync(observation);
        var again = await runtime.ReconcileConsumptionAsync(observation);

        Assert.Equal(SquadRunStatus.Completed, first.Status);
        Assert.Equal(2m, first.Factor);
        Assert.Equal(1, first.Observations);
        Assert.Equal(first.Factor, again.Factor);
        Assert.Equal(first.Observations, again.Observations);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.ConsumptionReconciliation);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(ReadState(host).ModelCallCharges));
        Assert.Single(ReadState(host).ConsumptionReconciliations!);
    }

    [Fact]
    public async Task GivenReconciledCalibration_WhenStartingAgainWithSameHostTemplate_RetainsItAcrossRuntimeInstances()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null, observations: 0);
        MeterCalls(host);
        ConsumptionTestData.QueueResearch(host);
        using (var runtime = await host.CreateAsync())
        {
            var baseline = await runtime.RunAsync("Research a calibration baseline.");
            var raw = ReadState(host).ModelCallCharges!.Where(charge => charge.RunId == baseline.RunId)
                .Sum(charge => charge.UncalibratedCostUsd!.Value);
            var reconciled = await runtime.ReconcileConsumptionAsync(new(baseline.RunId, raw * 2, "host-billing-baseline"));
            Assert.Equal(2m, reconciled.Factor);
            ConsumptionTestData.QueueResearch(host);
            var next = await runtime.RunAsync("Research with the same host tariff.", new SquadRunConfiguration("10000"));
            Assert.True(next.Status == SquadRunStatus.ResearchCompleted, next.ResponseText);
        }
        ConsumptionTestData.QueueResearch(host);
        using var fresh = await host.CreateAsync();
        var result = await fresh.RunAsync("Research after process restart.", new SquadRunConfiguration("10000"));

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.Equal(2m, ReadState(host).Consumption!.Calibration.Factor);
        Assert.Equal(1, ReadState(host).Consumption!.Calibration.Observations);
        Assert.All(ReadState(host).ModelCallCharges!.Where(charge => charge.RunId == result.RunId),
            charge => Assert.Equal(2m, charge.CalibrationFactor));
    }

    [Theory]
    [InlineData(0, ".25")]
    [InlineData(100, "10")]
    public async Task GivenRealBillingRatio_WhenReconciling_ClampsOnceWithPersistedProvenance(int multiplier, string expected)
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null, observations: 0);
        MeterCalls(host);
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var baseline = await runtime.RunAsync("Research a calibration baseline.");
        var raw = ReadState(host).ModelCallCharges!.Sum(charge => charge.UncalibratedCostUsd!.Value);

        var result = await runtime.ReconcileConsumptionAsync(new(baseline.RunId, raw * multiplier, "host-billing-clamp"));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Factor);
        Assert.Equal(1, result.Observations);
        Assert.Equal("2026-10-11|2", ReadState(host).Consumption!.Calibration.Basis);
        Assert.Equal(ConsumptionTestData.Today, ReadState(host).Consumption!.Calibration.LastReconciled);
    }

    [Fact]
    public async Task GivenPriorCalibrationAndExtremeNewRatio_WhenReconciling_ClampsTheRunningMeanNotEachObservation()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        MeterCalls(host);
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var baseline = await runtime.RunAsync("Research a calibration baseline.");
        var raw = ReadState(host).ModelCallCharges!.Sum(charge => charge.UncalibratedCostUsd!.Value);

        var result = await runtime.ReconcileConsumptionAsync(new(baseline.RunId, raw * 20, "host-billing-second-observation"));

        Assert.Equal(10m, result.Factor);
        Assert.Equal(2, result.Observations);
    }

    [Fact]
    public async Task GivenSameDateRepricedTariffWithCopiedCalibration_WhenResuming_InvalidatesOldObservations()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        using var cancellation = new CancellationTokenSource();
        QueueDelivery(host);
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        using (var runtime = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync("Research a bounded case.", cancellation.Token));
        }
        var runId = ReadState(host).Checkpoint!.RunId;
        var callsBefore = host.Client.Calls.Count;
        host.Options.Consumption!.Models["model-fixed"].Standard.Input = 4;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(true);
        };
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(runId);

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("cannot-confirm", result.CostPreflight?.Decision);
        Assert.Equal(0, ReadState(host).Consumption!.Calibration.Observations);
        Assert.Equal(1m, ReadState(host).Consumption!.Calibration.Factor);
        Assert.Equal(callsBefore, host.Client.Calls.Count);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
    }

    [Fact]
    public async Task GivenUnknownHistoricalTariff_WhenReconciling_DoesNotAskForApprovalOfAnInvalidDenominator()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        host.Options.ModelSelector = (_, _) => new(host.Client, "unknown-tariff-model");
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var baseline = await runtime.RunAsync("Research an unpriced baseline.");
        Assert.Equal(SquadRunStatus.ResearchCompleted, baseline.Status);

        var result = await runtime.ReconcileConsumptionAsync(new(baseline.RunId, 1, "host-billing-unknown"));

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.ConsumptionReconciliation);
        Assert.Null(ReadState(host).ConsumptionReconciliations);
    }

    [Fact]
    public async Task GivenMissingExecutionTimeSnapshotsOnResume_WhenSettingCeiling_DoesNotTreatPastModelWorkAsFree()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, "10", observations: 0);
        ConsumptionTestData.QueueResearch(host);
        SquadRunResult suspended;
        using (var runtime = await host.CreateAsync())
        {
            suspended = await runtime.RunAsync("Research the legacy-accounting case.");
        }
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!.AsObject();
        var operations = node["NativeOperations"]!.AsArray();
        var removedReceipts = operations.Where(value => value!["Operation"]!.GetValue<string>() == "RecordProviderAttempt")
            .ToArray();
        var removedIds = removedReceipts.Select(value => value!["Id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var historyRelative = host.Options.SquadRoot + "/history/Squad Scribe.md";
        var historyPath = Path.Combine(host.Project, historyRelative);
        var originalHistory = File.ReadAllText(historyPath);
        foreach (var receipt in removedReceipts)
        {
            var operation = receipt!.Deserialize<NativeOperationEvidence>()!;
            var frame = "\n## Operation " + operation.Id + "\n\n```json\n" +
                JsonSerializer.Serialize(operation, IndentedJson) + "\n```\n";
            Assert.Contains(frame, originalHistory, StringComparison.Ordinal);
            originalHistory = originalHistory.Replace(frame, "", StringComparison.Ordinal);
            operations.Remove(receipt);
        }
        var ledger = node["LedgerOrder"]!.AsArray();
        foreach (var entry in ledger.Where(value => removedIds.Contains(value!.GetValue<string>())).ToArray())
        {
            ledger.Remove(entry);
        }
        File.WriteAllText(historyPath, originalHistory);
        node["AppendOnlyFiles"]![historyRelative] = JsonSerializer.SerializeToNode(new AppendOnlyFileEvidence(
            System.Text.Encoding.UTF8.GetByteCount(originalHistory), ProjectFiles.Hash(originalHistory)));
        node.Remove("ModelCallCharges");
        node.Remove("Consumption");
        node.Remove("ConsumptionInputSha256");
        node["Checkpoint"]!.AsObject().Remove("Cost");
        var checkpoint = node["Checkpoint"]!.Deserialize<RunCheckpoint>()!;
        node["CheckpointSha256"] = ProjectFiles.Hash(JsonSerializer.Serialize(checkpoint, IndentedJson));
        File.WriteAllText(StatePath(host), node.ToJsonString());
        host.Options.Consumption = ConsumptionTestData.Configuration();
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(suspended.RunId);

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Equal("cannot-confirm", result.CostPreflight?.Decision);
        Assert.Single(host.Client.Calls);
        Assert.Contains(ReadState(host).ModelCallCharges!, charge => charge.TokenBasis == "historical-missing-rate-provenance" && charge.CostUsd is null);
    }

    [Fact]
    public async Task GivenFullDeliveryBeforePlan_WhenReserving_CoversEveryPotentialOwnerThenNarrowsToVerifiedCurrentFanout()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        MeterCalls(host);
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Complete the bounded change.");

        Assert.Equal(SquadRunStatus.Completed, result.Status);
        var rounds = ReadState(host).CostPreflights!;
        Assert.Contains(rounds[0].Demand, row => row.Slot == "produce:technical-writer");
        Assert.Contains(rounds[0].Demand, row => row.Slot == "produce:iac-author");
        var production = rounds.Last(round => round.PermittedSlots.Contains("produce:developer"));
        Assert.DoesNotContain(production.Demand, row => row.Slot == "produce:technical-writer");
        Assert.DoesNotContain(production.Demand, row => row.Slot == "produce:iac-author");
        Assert.Contains(production.Demand, row => row.Slot == "produce:developer");
    }

    [Fact]
    public async Task GivenIntakeBeforeDelivery_WhenReserving_CoversBothMaximumRemediationAndValidationCycles()
    {
        using var host = new RuntimeTestHost();
        host.Options.AdditionalRoles = ["analyst"];
        ConsumptionTestData.Configure(host);
        MeterCalls(host);
        File.WriteAllText(Path.Combine(host.Project, "requirements.md"), "Original authorized requirements.");
        host.QueuePipeline(inputs: ["requirements.md"]);
        host.QueueStage("intake-validator", "Ready");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("developer");
        host.QueueStage("tester");
        host.Client.Text("Verified intake-backed delivery.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Complete the bounded change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var first = ReadState(host).CostPreflights![0];
        Assert.Equal(2, first.Demand.Count(row => row.Slot.StartsWith("intake-remediation:", StringComparison.Ordinal)));
        Assert.Equal(2, first.Demand.Count(row => row.Slot.StartsWith("intake-validation:", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.CostPreflight!.Demand, row => row.Slot.StartsWith("intake-remediation:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "remediation");
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenUnregisteredWatchWithCeiling_WhenStarting_DoesNotPretendTheDemandBoundaryIsImplemented(string mode)
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        host.Options.Mode = mode;
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("mode=watch Produce a bounded outcome.");

        Assert.Equal(SquadRunStatus.Unsupported, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Null(result.CostPreflight);
    }

    [Fact]
    public async Task GivenCeilingChangedAfterOvershootApproval_WhenResuming_DoesNotReuseItsEarlierPermission()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".01");
        MeterCalls(host);
        QueueDelivery(host);
        using var cancellation = new CancellationTokenSource();
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        using (var runtime = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.RunAsync("Complete the bounded change.", cancellation.Token));
        }
        var checkpoint = ReadState(host).Checkpoint!;
        Assert.NotNull(checkpoint.Cost!.Approval);
        var callsBefore = host.Client.Calls.Count;
        host.Options.CostCeiling = null;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.CostOvershoot);
        };
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(checkpoint.RunId, new SquadRunConfiguration(".02"));

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(2, host.Approvals.Count(approval => approval.Kind == SquadApprovalKind.CostOvershoot));
        Assert.Equal(.02m, result.CostPreflight!.CeilingUsd);
        Assert.Equal("over-ceiling", result.CostPreflight.Decision);
        Assert.Equal(callsBefore, host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenExpandedContextDuringApprovedUnit_WhenPreparingNextCall_OpensNewGateBeforeExecutingIt()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".01");
        MeterCalls(host);
        host.Options.MaxArtifactCharacters = 1_000_000;
        File.WriteAllText(Path.Combine(host.Project, "large-input.txt"), new string('X', 600_000));
        var gates = 0;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.CostOvershoot || ++gates == 1);
        };
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.Client.Call("project_read", new() { ["path"] = "large-input.txt" });
        host.Client.Call("write_artifact", new() { ["content"] = "This larger-demand call must not execute." });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded case.");

        Assert.True(result.Status == SquadRunStatus.ApprovalDenied, result.ResponseText);
        Assert.Equal(2, gates);
        Assert.Equal(2, host.Client.Calls.Count);
        Assert.Empty(result.Evidence);
        Assert.True(result.CostPreflight!.Demand.Single(row => row.Slot == "research:researcher").BaseContext > 40_000);
    }

    [Fact]
    public async Task GivenRemovedMember_WhenRewritingLedger_RetainsItsHistoricalPricedUsage()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Research a bounded case.");
        Assert.Equal(SquadRunStatus.ResearchCompleted, run.Status);
        var before = ReadState(host).ModelCallCharges!.ToArray();
        var roster = runtime.RosterConfiguration;
        roster.Roles = roster.Roles.Count > 0 ? roster.Roles.Where(role => role != "researcher").ToArray() :
            runtime.Roster.Members.Where(member => member.Role != "researcher").Select(member => member.Role).ToArray();

        var update = await runtime.UpdateRosterAsync(roster);

        Assert.Equal(SquadRunStatus.Completed, update.Status);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(ReadState(host).ModelCallCharges));
        Assert.Contains("researcher (removed)", File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenInvalidHostCacheNormalization_WhenRecording_KeepsValidProviderTotalsAndExecutesNoReturnedTool()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        MeterCalls(host, input: 100, output: 10);
        host.Options.TokenUsageSelector = _ => new(100, 101, 0, 10);
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research a bounded case.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("normalization", result.ResponseText, StringComparison.Ordinal);
        Assert.Single(host.Client.Calls);
        Assert.Empty(result.Evidence);
        var call = Assert.Single(ReadState(host).ModelCalls);
        Assert.Equal("returned", call.Status);
        Assert.Equal(100, call.InputTokens);
        Assert.Equal(10, call.OutputTokens);
        Assert.Null(call.CachedInputTokens);
        Assert.True(ReadState(host).ModelCallCharges![0].CostUsd > 0);
    }

    [Fact]
    public async Task GivenRecordedPricingTamper_WhenReconciling_FailsHistoryIntegrityInsteadOfAcceptingLowerSpend()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        MeterCalls(host);
        ConsumptionTestData.QueueResearch(host);
        using (var runtime = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.ResearchCompleted, (await runtime.RunAsync("Research a bounded case.")).Status);
        }
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!;
        var pricing = node["ModelCallCharges"]![0]!;
        Assert.Equal(3m, pricing["Pricing"]!["AppliedRates"]!["Standard"]!["Input"]!.GetValue<decimal>());
        pricing["CostUsd"] = 0m;
        File.WriteAllText(StatePath(host), node.ToJsonString());
        using var next = await host.CreateAsync();

        var result = await next.ReconcileEvidenceAsync();

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.False(result.HistoryVerified);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("pricing/calibration", StringComparison.Ordinal));
    }

    private sealed class MeteredClient(IChatClient inner, string observed, long input, long output) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.ModelId = observed;
            response.Usage = new() { InputTokenCount = input, OutputTokenCount = output };
            return response;
        }
    }

    private sealed class UnavailableClient(bool ordinaryFailure = false) : IChatClient
    {
        internal int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw ordinaryFailure ? new InvalidOperationException("Ordinary failure must not retry.") :
                new SquadModelUnavailableException("model-a", ["model-b"]);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
