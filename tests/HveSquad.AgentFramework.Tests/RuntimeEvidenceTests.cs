using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeEvidenceTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string StatePath(RuntimeTestHost host) =>
        Path.Combine(host.Project, host.Options.SquadRoot, "state.json");

    private static void QueueCouncil(RuntimeTestHost host)
    {
        host.Options.Council = new() { Roles = ["architect", "security", "cost-manager", "product-owner"] };
        host.QueuePipeline("technical-writer", council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        host.Client.Text("Coordinator finished the actual council-backed delivery.");
    }

    private static async Task<SquadRunResult> CompleteAsync(RuntimeTestHost host)
    {
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Produce a bounded local document.");
        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        return result;
    }

    [Fact]
    public async Task GivenThreeNativeTurns_WhenRebuildingLedger_PreservesAllReceiptsAndCumulativeRoleTotals()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        host.Options.TokenUsageSelector = _ => new(1000, 100, 50, 200);
        byte[] decisions = [];
        var histories = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var completed = new List<SquadRunResult>();
        for (var turn = 0; turn < 3; turn++)
        {
            completed.Add(await CompleteAsync(host));
            var decisionBytes = File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md"));
            Assert.True(decisionBytes.AsSpan().StartsWith(decisions));
            decisions = decisionBytes;
            foreach (var evidence in completed.SelectMany(run => run.Evidence))
            {
                var path = Path.Combine(host.Project, evidence.HistoryPath);
                var bytes = File.ReadAllBytes(path);
                if (histories.TryGetValue(path, out var prior))
                {
                    Assert.True(bytes.AsSpan().StartsWith(prior));
                }
                histories[path] = bytes;
                Assert.Contains(JsonSerializer.Serialize(evidence, IndentedJson), Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            }
        }
        var state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;
        Assert.Equal(3, state.Runs.Count);
        Assert.Equal(completed.SelectMany(run => run.Evidence).Select(evidence => evidence.DispatchId),
            state.DispatchRecords.Select(evidence => evidence.DispatchId));
        Assert.Equal(host.Client.Calls.Count, state.ModelCalls.Count);
        Assert.Equal(state.ModelCalls.Count, state.ModelCallCharges!.Count);
        var ledgerPath = Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md");
        var ledger = File.ReadAllText(ledgerPath);
        Assert.Contains("## Attribution", ledger, StringComparison.Ordinal);
        Assert.Contains("## Usage & Cost", ledger, StringComparison.Ordinal);
        Assert.Contains("| **Total** |", ledger, StringComparison.Ordinal);
        Assert.Contains("| orchestration |", ledger, StringComparison.Ordinal);
        Assert.DoesNotContain("| architect |", ledger, StringComparison.Ordinal);
        var attribution = ledger[(ledger.IndexOf("## Attribution", StringComparison.Ordinal))..ledger.IndexOf("## Usage & Cost", StringComparison.Ordinal)];
        var previous = -1;
        foreach (var role in state.Initialization!.Roles.Where(role => state.ModelCalls.Any(call => call.Role == role)))
        {
            var marker = "| " + role + " |";
            Assert.Equal(1, attribution.Split(marker, StringSplitOptions.None).Length - 1);
            var position = attribution.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(position > previous);
            previous = position;
        }
        var total = state.ModelCallCharges.Sum(charge => charge.CostUsd!.Value);
        Assert.Contains(total.ToString("F4", System.Globalization.CultureInfo.InvariantCulture), ledger, StringComparison.Ordinal);
        foreach (var charge in state.ModelCallCharges)
        {
            Assert.Contains(charge.CallId, ledger, StringComparison.Ordinal);
        }
        var groups = state.ModelCalls.GroupBy(call => call.Role ?? "orchestration").ToArray();
        foreach (var group in groups)
        {
            var ids = group.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            var cost = state.ModelCallCharges.Where(charge => ids.Contains(charge.CallId)).Sum(charge => charge.CostUsd!.Value);
            var row = FormattableString.Invariant($"| {group.Key} | {1000 * group.Count()} | {100 * group.Count()} | {50 * group.Count()} | {200 * group.Count()} | {cost:F4} | {cost / .01m:F4} |");
            Assert.Contains(row, ledger, StringComparison.Ordinal);
        }
        var saved = File.GetLastWriteTimeUtc(StatePath(host));
        Assert.True(saved >= File.GetLastWriteTimeUtc(ledgerPath));
        Assert.All(histories.Keys, path => Assert.True(saved >= File.GetLastWriteTimeUtc(path)));
        File.Delete(ledgerPath);
        using var runtime = await host.CreateAsync();

        var rebuilt = await runtime.ReconcileEvidenceAsync();

        Assert.True(rebuilt.HistoryVerified);
        Assert.True(rebuilt.LedgerVerified);
        Assert.Equal(1, rebuilt.CorrectivePasses);
        var actual = File.ReadAllText(ledgerPath);
        static string Usage(string text) => text[text.IndexOf("## Usage & Cost", StringComparison.Ordinal)..text.IndexOf("\n- ", text.IndexOf("## Usage & Cost", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Equal(Usage(ledger), Usage(actual));
        var after = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;
        Assert.Equal(JsonSerializer.Serialize(state.ModelCallCharges), JsonSerializer.Serialize(after.ModelCallCharges));
        Assert.Equal(state.ModelCalls.Count, after.ModelCalls.Count);
        Assert.Equal(state.NativeOperations.Count + 1, after.NativeOperations.Count);
        foreach (var (path, bytes) in histories)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task GivenUnpricedNativeAttempts_WhenRenderingLedger_NeverLabelsUnknownTotalsAsZero()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Inspect the bounded existing case.");

        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        var text = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"));
        Assert.Contains("| **Total** | unknown | unknown | unknown | unknown | unknown | unknown |", text, StringComparison.Ordinal);
        Assert.Contains("Uncalibrated", text, StringComparison.Ordinal);
        Assert.DoesNotContain("| scribe |", text, StringComparison.Ordinal);
        Assert.True((await runtime.ReconcileEvidenceAsync()).LedgerVerified);
    }

    private static (string Path, byte[] Before, byte[] Suffix) PreparePartialAppend(RuntimeTestHost host, bool damagePrefix)
    {
        var path = Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md");
        var before = File.ReadAllBytes(path);
        var suffix = Encoding.UTF8.GetBytes("\nKnown exact append from the trusted native writer fixture.\n");
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!.AsObject();
        node["PendingHistoryAppend"] = JsonSerializer.SerializeToNode(new
        {
            Path = host.Options.SquadRoot + "/decisions.md",
            Before = new { Length = before.Length, Sha256 = Convert.ToHexString(SHA256.HashData(before)) },
            Content = Encoding.UTF8.GetString(suffix),
            AfterSha256 = Convert.ToHexString(SHA256.HashData([.. before, .. suffix])),
        });
        File.WriteAllText(StatePath(host), node.ToJsonString());
        byte[] partial = [.. before, .. suffix.AsSpan(0, suffix.Length / 2)];
        if (damagePrefix)
        {
            partial[0] ^= 1;
        }
        File.WriteAllBytes(path, partial);
        return (path, before, suffix);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task GivenIndependentCouncilWorkers_WhenDispatching_UsesBoundedRealConcurrencyAndDeterministicOwnership(int concurrency)
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = concurrency;
        using var council = new ConcurrentCouncil(host, Math.Min(concurrency, 2));
        QueueCouncil(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the document with council validation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText + "\n" + string.Join("\n", host.Log.Messages));
        Assert.InRange(council.Peak, Math.Min(concurrency, 2), concurrency);
        var seats = result.Evidence.Where(e => e.Stage == "council" && e.Depth == 1).ToArray();
        Assert.Equal(["architect", "security", "cost-manager", "product-owner"], seats.Select(e => e.Role));
        var workers = result.Evidence.Where(e => e.Agent == "Shared Worker").ToArray();
        Assert.Equal(2, workers.Length);
        Assert.Equal(2, workers.Select(e => e.ArtifactPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(workers, worker =>
        {
            Assert.Equal(2, worker.Depth);
            Assert.EndsWith("/Shared Worker.md", worker.HistoryPath, StringComparison.Ordinal);
            Assert.Contains(seats, seat => seat.DispatchId == worker.ParentDispatchId && seat.Role == worker.Role);
        });
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var sessions = state.RootElement.GetProperty("Sessions");
        foreach (var worker in workers)
        {
            var own = sessions.GetProperty("dispatch:" + worker.DispatchId).GetRawText();
            Assert.Contains("PRIVATE_WORKER_" + worker.Role.ToUpperInvariant(), own, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_WORKER_" +
                (worker.Role == "architect" ? "SECURITY" : "ARCHITECT"), own, StringComparison.Ordinal);
        }
        var observations = state.RootElement.GetProperty("ModelCalls").EnumerateArray().ToArray();
        Assert.Equal(host.Client.Calls.Count + council.Calls, observations.Length);
        Assert.All(observations, call => Assert.Equal("returned", call.GetProperty("Status").GetString()));
        Assert.DoesNotContain(observations, call => call.GetProperty("Agent").GetString() == "Squad Scribe");
        Assert.All(state.RootElement.GetProperty("NativeOperations").EnumerateArray(),
            operation => Assert.Equal(0, operation.GetProperty("ModelInvocations").GetInt32()));
        Assert.Equal(result.Evidence.Select(e => e.DispatchId),
            state.RootElement.GetProperty("DispatchRecords").EnumerateArray().Select(e => e.GetProperty("DispatchId").GetString()));
        var coordinator = Assert.Single(state.RootElement.GetProperty("CoordinatorInvocations").EnumerateArray());
        Assert.Equal(2, coordinator.GetProperty("Calls").GetArrayLength());
        var reconciled = await runtime.ReconcileEvidenceAsync();
        Assert.True(reconciled.HistoryVerified);
        Assert.True(reconciled.LedgerVerified);
        Assert.Equal(0, reconciled.CorrectivePasses);
    }

    [Fact]
    public async Task GivenAutomaticTaskFitCouncil_WhenTwoLensesAreNeeded_ActuallyDispatchesOnlyThoseSeatsConcurrently()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = 2;
        using var council = new ConcurrentCouncil(host, 2);
        host.QueuePipeline("technical-writer", council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        host.Client.Text("Task-fit council-backed delivery.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the document with architecture and security council.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, council.Peak);
        var seats = result.Evidence.Where(evidence => evidence.Stage == "council" && evidence.Depth == 1).ToArray();
        Assert.Equal(["architect", "security"], seats.Select(evidence => evidence.Role));
        var proposal = Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.Council).Council!;
        Assert.Equal(3, proposal.Lenses.Count(lens => !lens.Needed));
        Assert.DoesNotContain(seats, evidence => evidence.Role is "cost-manager" or "product-owner" or "rai");
    }

    [Fact]
    public async Task GivenConfidentCostRound_WhenCouncilAndNestedWorkersRun_PreservesRealConcurrencyAndFullPricedAccounting()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = 2;
        using var council = new ConcurrentCouncil(host, 2, waitForPeerHandoffs: true);
        var handoffObserver = host.Log.Observer;
        var pendingReservations = new List<decimal>();
        host.Log.Observer = id =>
        {
            handoffObserver?.Invoke(id);
            if (id.Name != "CostAdmission")
            {
                return;
            }
            var current = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;
            var checkpoint = current.Checkpoint!;
            var pending = checkpoint.PendingEvidence.Select(evidence => evidence.DispatchId).ToHashSet(StringComparer.Ordinal);
            if (pending.Count == 0)
            {
                return;
            }
            var cost = checkpoint.Cost!;
            var reserve = pending.Sum(dispatchId =>
            {
                var slot = cost.DispatchSlots[dispatchId];
                return cost.Rounds.Last(round => round.PermittedSlots.Contains(slot))
                    .Demand.Single(row => row.Slot == slot).ProjectedCostUsd!.Value * 3m;
            });
            var realized = current.ModelCallCharges!.Where(charge => charge.RunId == cost.RunId &&
                (charge.DispatchId is null || !pending.Contains(charge.DispatchId)) &&
                current.ModelCalls.Single(call => call.Id == charge.CallId).Status != "started")
                .Sum(charge => charge.CostUsd!.Value);
            Assert.Equal(ConsumptionMath.Round(realized + reserve), cost.Rounds[^1].EvaluatedSpendUsd);
            pendingReservations.Add(reserve);
        };
        var selector = host.Options.ModelSelector;
        ConsumptionTestData.Configure(host);
        foreach (var role in new[] { "architect", "security", "cost-manager", "product-owner" })
        {
            host.Options.Consumption!.Models["fixture-" + role] =
                host.Options.Consumption.Models["model-fixed"];
        }
        host.Options.ModelSelector = (role, charter) =>
            selector?.Invoke(role, charter) ?? new SquadModelSelection(host.Client, "model-fixed");
        QueueCouncil(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the council-reviewed bounded document.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.InRange(council.Peak, 2, host.Options.MaxConcurrentDispatches);
        Assert.Equal(host.Client.Calls.Count + council.Calls, result.Consumption!.ProviderAttempts);
        Assert.NotNull(result.Consumption.EstimatedCostUsd);
        Assert.Equal(0, result.Consumption.UnpricedAttempts);
        Assert.Equal(2, result.Evidence.Count(evidence => evidence.Agent == "Shared Worker"));
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.Contains(pendingReservations, amount => amount > 0);
        var state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;
        Assert.All(state.NativeOperations, operation => Assert.Equal(0, operation.ModelInvocations));
        Assert.DoesNotContain(state.ModelCalls, call => call.Agent == "Squad Scribe");
    }

    [Fact]
    public async Task GivenConcurrentSeat_WhenReadingAnotherOwnersAllocation_BlocksWithoutProduction()
    {
        using var host = new RuntimeTestHost();
        using var council = new ConcurrentCouncil(host, 2, attackSibling: true);
        QueueCouncil(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write a council-reviewed document.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("another active dispatch", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "produce");
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenSuspendedParallelCouncil_WhenRestarting_ReusesCompletedOwnersAndPreservesTheirHistoryPrefix()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = 2;
        using var council = new ConcurrentCouncil(host, 2, suspendArchitect: true);
        QueueCouncil(host);
        SquadRunResult suspended;
        using (var runtime = await host.CreateAsync())
        {
            suspended = await runtime.RunAsync("Write the council-reviewed local document.");
            Assert.True(suspended.Status == SquadRunStatus.InputRequired,
                suspended.ResponseText + "\n" + string.Join("\n", host.Log.Messages));
        }
        var completed = suspended.Evidence.Where(e => e.Stage == "council").ToArray();
        Assert.Equal(3, completed.Count(e => e.Depth == 1));
        Assert.DoesNotContain(completed, e => e.Role == "architect");
        var histories = completed.Select(e => e.HistoryPath).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(host.Project, path)), StringComparer.Ordinal);
        var originalCalls = council.Calls;
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("The human supplied the required council input.");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(suspended.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(suspended.RunId, result.RunId);
        Assert.Equal(completed.Select(e => e.DispatchId),
            result.Evidence.Where(e => completed.Any(previous => previous.DispatchId == e.DispatchId)).Select(e => e.DispatchId));
        Assert.Equal(4, result.Evidence.Count(e => e.Stage == "council" && e.Depth == 1));
        foreach (var (path, prefix) in histories)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(host.Project, path)).AsSpan().StartsWith(prefix));
        }
        Assert.True(council.Calls > originalCalls);
        Assert.Equal(1, result.Evidence.Count(e => e.Stage == "research"));
        Assert.Equal(1, result.Evidence.Count(e => e.Stage == "plan"));
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        Assert.Empty(state.RootElement.GetProperty("OpenQuestions").EnumerateArray());
        Assert.True((await resumed.ReconcileEvidenceAsync()).HistoryVerified);
    }

    [Theory]
    [InlineData("AUX")]
    [InlineData("CON.txt")]
    [InlineData("Invalid/name")]
    [InlineData("Invalid:name")]
    [InlineData("Invalid|name")]
    [InlineData("Trailing.")]
    public async Task GivenInvalidLiteralAgentIdentity_WhenDispatching_RejectsInsteadOfHashingTheName(string name)
    {
        using var host = new RuntimeTestHost();
        host.WriteCharter("researcher", children: "'" + name + "'");
        host.Write("agents/worker.agent.md", $"---\nname: '{name}'\nuser-invocable: false\n---\nOriginal worker fixture.");
        host.QueuePipeline("technical-writer");
        host.Client.Call("dispatch_child", new() { ["agentName"] = name, ["childTask"] = "One bounded lane." });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write the bounded local document.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("portable filesystem name", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
        Assert.Equal(2, host.Client.Calls.Count);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenWorker_WhenReadingPrivateSessionState_DoesNotExposeAnotherDispatchsHistory()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.Client.Call("project_read", new() { ["path"] = host.Options.SquadRoot + "/state.json" });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Use only authorized dispatch inputs.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("Private sessions", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData("prepend")]
    [InlineData("append")]
    [InlineData("remove")]
    public async Task GivenAppendOnlyHistory_WhenUnrecordedBytesChange_BlocksBeforeAnotherProviderCall(string mutation)
    {
        using var host = new RuntimeTestHost();
        var original = await CompleteAsync(host);
        var history = Path.Combine(host.Project, original.Evidence[0].HistoryPath);
        var text = File.ReadAllText(history);
        File.WriteAllText(history, mutation switch
        {
            "prepend" => "Unrecorded prefix.\n" + text,
            "append" => text + "\nUnrecorded extra bytes.\n",
            _ => text.Replace(original.Evidence[0].Summary, "Changed historical summary.", StringComparison.Ordinal),
        });
        var changedBytes = File.ReadAllBytes(history);
        var calls = host.Client.Calls.Count;
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Begin another bounded document.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(changedBytes, File.ReadAllBytes(history));
        var reconciliation = await runtime.ReconcileEvidenceAsync();
        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, reconciliation.FailureClass);
        Assert.False(reconciliation.HistoryVerified);
        Assert.Equal(0, reconciliation.CorrectivePasses);
    }

    [Fact]
    public async Task GivenHistoricalEntries_WhenReordered_BlocksEvenIfEverySerializedDispatchStillExists()
    {
        using var host = new RuntimeTestHost();
        var first = await CompleteAsync(host);
        var second = await CompleteAsync(host);
        var path = Path.Combine(host.Project, first.Evidence[0].HistoryPath);
        var text = File.ReadAllText(path);
        var split = text.IndexOf("\n## Dispatch " + second.Evidence[0].DispatchId, StringComparison.Ordinal);
        Assert.True(split > 0);
        File.WriteAllText(path, text[split..] + text[..split]);
        var calls = host.Client.Calls.Count;
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Continue after invalid history ordering.");

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Equal(calls, host.Client.Calls.Count);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("evidence")]
    [InlineData("assignment")]
    [InlineData("budget")]
    public async Task GivenSealedCheckpoint_WhenAlteringIt_RejectsBeforeReusingPriorApprovals(string mutation)
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.Client.Call("ask_host", new() { ["question"] = "A human decision is required before planning." });
        using (var runtime = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.InputRequired, (await runtime.RunAsync("Write the document.")).Status);
        }
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!.AsObject();
        var checkpoint = node["Checkpoint"]!;
        switch (mutation)
        {
            case "route":
                checkpoint["Routing"]!["ProducingRole"] = "developer";
                break;
            case "evidence":
                checkpoint["Evidence"]![0]!["Summary"] = "Forged summary.";
                break;
            case "assignment":
                checkpoint["DispatchStack"]![0]!["Task"] = "Different task.";
                break;
            default:
                checkpoint["Budget"]!["ModelCalls"] = 0;
                break;
        }
        File.WriteAllText(StatePath(host), node.ToJsonString());
        var calls = host.Client.Calls.Count;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => host.CreateAsync());

        Assert.Contains("saved checkpoint changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(calls, host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenExactPartialWriterAppend_WhenReconciling_CompletesOnceWithoutReplayingModelWork()
    {
        using var host = new RuntimeTestHost();
        _ = await CompleteAsync(host);
        var (path, before, suffix) = PreparePartialAppend(host, damagePrefix: false);
        var calls = host.Client.Calls.Count;
        using var runtime = await host.CreateAsync();

        var result = await runtime.ReconcileEvidenceAsync();

        Assert.True(result.HistoryVerified);
        Assert.Equal(1, result.CorrectivePasses);
        Assert.Equal((byte[])[.. before, .. suffix], File.ReadAllBytes(path));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(0, (await runtime.ReconcileEvidenceAsync()).CorrectivePasses);
        Assert.Equal((byte[])[.. before, .. suffix], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GivenProtectedDispatchSession_WhenAlteringIt_BlocksBeforeAnyResumedModelCall()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.Client.Call("ask_host", new() { ["question"] = "Human input before planning." });
        SquadRunResult pending;
        using (var runtime = await host.CreateAsync())
        {
            pending = await runtime.RunAsync("Prepare the bounded document.");
            Assert.Equal(SquadRunStatus.InputRequired, pending.Status);
        }
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!.AsObject();
        var key = node["Checkpoint"]!["DispatchStack"]![0]!["SessionKey"]!.GetValue<string>();
        node["Sessions"]![key] = JsonSerializer.SerializeToNode(new { data = "An altered private session." });
        File.WriteAllText(StatePath(host), node.ToJsonString());
        var calls = host.Client.Calls.Count;
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(pending.RunId);

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Contains("protected session changed", result.ResponseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenChildWriter_WhenParentRequestsOverlappingOrUnscopedWrite_StopsBeforeInvokingTheSecondWriter(bool projected)
    {
        using var host = new RuntimeTestHost();
        if (!projected)
        {
            host.Options.Tools[0] = host.Options.Tools[0] with { ProjectWritePaths = null };
        }
        host.WriteCharter("developer", children: "Source Worker");
        host.Write("agents/source-worker.agent.md", "---\nname: Source Worker\nuser-invocable: false\n---\nOriginal source-worker fixture.");
        host.QueuePipeline("developer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.Client.Call("dispatch_child", new() { ["agentName"] = "Source Worker", ["childTask"] = "Own the bounded source write." });
        host.Client.Call("apply_change", []);
        host.QueueStage("source-worker");
        host.Client.Call("apply_change", []);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded source change.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(1, host.Writes);
        Assert.Single(result.Effects!);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        Assert.Contains(projected ? "Overlapping edits" : "ProjectWritePaths", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenPartialAppendWithChangedOriginalBytes_WhenReconciling_RefusesToRewriteHistory()
    {
        using var host = new RuntimeTestHost();
        _ = await CompleteAsync(host);
        var (path, _, _) = PreparePartialAppend(host, damagePrefix: true);
        var changed = File.ReadAllBytes(path);
        using var runtime = await host.CreateAsync();

        var result = await runtime.ReconcileEvidenceAsync();

        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.False(result.HistoryVerified);
        Assert.Equal(0, result.CorrectivePasses);
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GivenOriginalNativeV1EvidenceShape_WhenMigrating_PreservesHashedHistoryBytesWithoutInventingNewMetadata()
    {
        using var host = new RuntimeTestHost();
        var completed = await CompleteAsync(host);
        var node = JsonNode.Parse(File.ReadAllText(StatePath(host)))!.AsObject();
        node["Schema"] = "hve-squad-maf/native-single-squad/v1";
        foreach (var key in new[]
        {
            "SquadRoot", "Checkpoint", "CheckpointSha256", "AppendOnlyFiles", "PendingHistoryAppend",
            "DispatchRecords", "NativeOperations", "ModelCalls", "CoordinatorInvocations",
            "LedgerOrder", "LedgerNeedsReconciliation", "LedgerDiagnostics",
            "ModelCallCharges", "Consumption", "ConsumptionInputSha256", "ConsumptionReconciliations", "CostPreflights",
        })
        {
            node.Remove(key);
        }
        var history = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var run in node["Runs"]!.AsArray())
        {
            run!.AsObject().Remove("FailureClass");
            run.AsObject().Remove("Diagnostics");
            run.AsObject().Remove("Effects");
            run.AsObject().Remove("Consumption");
            run.AsObject().Remove("CostPreflight");
            foreach (var value in run["Evidence"]!.AsArray())
            {
                var evidence = value!.AsObject();
                foreach (var key in new[] { "Depth", "AssignmentSha256", "ParentDispatchId", "DispatchOrder" })
                {
                    evidence.Remove(key);
                }
                var oldPath = Path.Combine(host.Project, evidence["HistoryPath"]!.GetValue<string>());
                var name = evidence["Agent"]!.GetValue<string>();
                var hashed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];
                var relative = host.Options.SquadRoot + "/history/" + hashed + ".md";
                evidence["HistoryPath"] = relative;
                var path = Path.Combine(host.Project, relative);
                File.WriteAllText(path, "\n## Dispatch " + evidence["DispatchId"]!.GetValue<string>() +
                    "\n\n```json\n" + evidence.ToJsonString(IndentedJson) + "\n```\n");
                File.Delete(oldPath);
                history.Add(path, File.ReadAllBytes(path));
            }
        }
        foreach (var agent in new[] { "Squad Scribe", "Squad Coordinator" })
        {
            File.Delete(Path.Combine(host.Project, host.Options.SquadRoot, "history", agent + ".md"));
        }
        var ledgerPath = Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md");
        var rows = File.ReadAllLines(ledgerPath).Where(line => line.StartsWith("- Dispatch ", StringComparison.Ordinal));
        File.WriteAllText(ledgerPath,
            "# Consumption\n\nNative per-dispatch observations follow. Costs and credits are unknown; no rates are inferred from tiers.\n" +
            string.Concat(rows.Select(row => "\n" + row + "\n")));
        File.WriteAllText(StatePath(host), node.ToJsonString());
        var before = File.ReadAllBytes(StatePath(host));
        host.Options.AllowNativeV1Migration = true;
        host.Client.Text("No new producing work in this migration fixture.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Migrate the original native evidence.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "migrations", "native-v1-state.json")));
        foreach (var (path, bytes) in history)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        var verified = await runtime.ReconcileEvidenceAsync();
        Assert.True(verified.HistoryVerified);
        Assert.True(verified.LedgerVerified);
        Assert.Equal(4, completed.Evidence.Count);
    }

    [Fact]
    public async Task GivenLedgerOnlyDefect_WhenReconciling_RepairsOnceFromAllActualWorkWithoutModelScribeCalls()
    {
        using var host = new RuntimeTestHost();
        var original = await CompleteAsync(host);
        var histories = original.Evidence.ToDictionary(e => e.HistoryPath,
            e => File.ReadAllBytes(Path.Combine(host.Project, e.HistoryPath)), StringComparer.Ordinal);
        var ledger = Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md");
        File.WriteAllText(ledger, "False zero-cost ledger with missing dispatches.");
        var calls = host.Client.Calls.Count;
        using var runtime = await host.CreateAsync();

        var result = await runtime.ReconcileEvidenceAsync();

        Assert.Equal(SquadEvidenceFailureClass.LedgerOnly, result.FailureClass);
        Assert.True(result.HistoryVerified);
        Assert.True(result.LedgerVerified);
        Assert.Equal(1, result.CorrectivePasses);
        Assert.Equal(calls, host.Client.Calls.Count);
        var text = File.ReadAllText(ledger);
        Assert.All(original.Evidence, e => Assert.Contains(e.DispatchId, text, StringComparison.Ordinal));
        Assert.Contains("Coordinator invocation", text, StringComparison.Ordinal);
        Assert.Contains("model invocations none", text, StringComparison.Ordinal);
        foreach (var (path, bytes) in histories)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, path)));
        }
        var stateBefore = File.ReadAllBytes(StatePath(host));
        Assert.Equal(0, (await runtime.ReconcileEvidenceAsync()).CorrectivePasses);
        Assert.Equal(stateBefore, File.ReadAllBytes(StatePath(host)));
    }

    [Fact]
    public async Task GivenLockedLedger_WhenIndependentCouncilRuns_WarnsAndPreservesRealParallelDispatch()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = 2;
        using var council = new ConcurrentCouncil(host, 2);
        _ = await CompleteAsync(host);
        QueueCouncil(host);
        using var runtime = await host.CreateAsync();
        SquadRunResult result;
        using (var locked = new FileStream(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await runtime.RunAsync("Run the bounded independent council despite ledger-only contention.");
        }

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText + "\n" + string.Join("\n", host.Log.Messages));
        Assert.Equal(SquadEvidenceFailureClass.LedgerOnly, result.FailureClass);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("ledger-only", StringComparison.Ordinal));
        Assert.Equal(2, council.Peak);
        var calls = host.Client.Calls.Count + council.Calls;
        var reconciled = await runtime.ReconcileEvidenceAsync();
        Assert.True(reconciled.HistoryVerified);
        Assert.True(reconciled.LedgerVerified);
        Assert.Equal(1, reconciled.CorrectivePasses);
        Assert.Equal(calls, host.Client.Calls.Count + council.Calls);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    public void GivenConcurrencyFlag_WhenParsingSample_UsesTheSameNativeSetting(string value, int expected)
    {
        var command = SampleCommand.Parse(["--run", "request", "--project", ".", "--max-concurrent-dispatches", value]);
        Assert.Equal(expected, command.MaxConcurrentDispatches);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("unknown")]
    public void GivenInvalidConcurrencyFlag_WhenParsingSample_RejectsExplicitly(string value) =>
        Assert.Throws<ArgumentException>(() =>
            SampleCommand.Parse(["--run", "request", "--project", ".", "--max-concurrent-dispatches", value]));

    private sealed class ConcurrentCouncil : IDisposable
    {
        private readonly ConcurrentBag<DelayedClient> _clients = [];
        private readonly Probe _probe;
        private readonly TaskCompletionSource _pendingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _architectSelections;

        internal ConcurrentCouncil(RuntimeTestHost host, int requiredConcurrency, bool attackSibling = false,
            bool suspendArchitect = false, bool waitForPeerHandoffs = false)
        {
            _probe = new(requiredConcurrency);
            if (suspendArchitect || waitForPeerHandoffs)
            {
                host.Log.Observer = id =>
                {
                    if (id.Name != "DispatchCheckpointed")
                    {
                        return;
                    }
                    using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
                    if (state.RootElement.GetProperty("Checkpoint").GetProperty("PendingEvidence").EnumerateArray()
                        .Count(e => e.GetProperty("Stage").GetString() == "council" && e.GetProperty("Depth").GetInt32() == 1) >=
                        (waitForPeerHandoffs ? 2 : 3))
                    {
                        _pendingReady.TrySetResult();
                    }
                };
            }
            host.WriteCharter("architect", children: "Shared Worker");
            host.WriteCharter("security", children: "Shared Worker");
            host.Write("agents/shared-worker.agent.md", "---\nname: Shared Worker\nuser-invocable: false\n---\nOriginal bounded worker.");
            host.Options.ModelSelector = (role, charter) =>
            {
                if (role is null || role.Role is not ("architect" or "security" or "cost-manager" or "product-owner"))
                {
                    return null;
                }
                var scripted = new ScriptedRuntimeChatClient();
                var worker = charter.Name == "Shared Worker";
                var marker = worker ? "PRIVATE_WORKER_" + role.Role.ToUpperInvariant() : "Council " + role.Role;
                Func<CancellationToken, Task>? wait = null;
                if (!worker && role.Role == "architect" && suspendArchitect)
                {
                    scripted.Call("ask_host", new() { ["question"] = "A human must supply the council input." });
                    if (Interlocked.Increment(ref _architectSelections) == 1)
                    {
                        wait = token => _pendingReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    }
                }
                if (!worker && role.Role == "architect" && waitForPeerHandoffs)
                {
                    wait = token => _pendingReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                }
                if (!worker && attackSibling && role.Role == "security")
                {
                    scripted.Steps.Enqueue((_, _) =>
                    {
                        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
                        var peer = state.RootElement.GetProperty("Checkpoint").GetProperty("DispatchStack").EnumerateArray()
                            .Single(d => d.GetProperty("Role").GetString() == "architect" &&
                                d.GetProperty("ParentDispatchId").ValueKind == JsonValueKind.Null);
                        var path = peer.GetProperty("Allocations")[0].GetProperty("Path").GetString();
                        return RuntimeTestHost.Function("project_read", new() { ["path"] = path });
                    });
                }
                if (!worker && role.Role is "architect" or "security")
                {
                    scripted.Call("dispatch_child", new()
                    {
                        ["agentName"] = "Shared Worker",
                        ["childTask"] = "Own only the " + role.Role + " lane.",
                    });
                }
                if (role.Role != "cost-manager")
                {
                    scripted.Call("write_artifact", new() { ["content"] = marker });
                }
                var arguments = new Dictionary<string, object?>
                {
                    ["summary"] = marker,
                    ["verdict"] = "Approve",
                    ["risk"] = "Low",
                    ["blockingIssues"] = 0,
                };
                if (!worker)
                {
                    arguments["payload"] = new SquadCouncilFindings([], [], [],
                        JsonSerializer.SerializeToElement(new { findings = marker, costUsd = (decimal?)null }));
                }
                scripted.Call(role.Role == "cost-manager" ? "complete_return" : "complete_stage", arguments);
                scripted.Text(marker + " finished.");
                var client = new DelayedClient(scripted, _probe, role.Role == "architect" ? 50 : 5, wait);
                _clients.Add(client);
                return new(client, "fixture-" + role.Role);
            };
        }

        internal int Peak => _probe.Peak;
        internal int Calls => _clients.Sum(client => client.Calls);

        public void Dispose()
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }
    }

    private sealed class Probe(int required)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;

        internal int Peak => Volatile.Read(ref _peak);

        internal async Task EnterAsync(CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            var previous = Volatile.Read(ref _peak);
            while (active > previous)
            {
                var observed = Interlocked.CompareExchange(ref _peak, active, previous);
                if (observed == previous)
                {
                    break;
                }
                previous = observed;
            }
            if (active >= required)
            {
                _ready.TrySetResult();
            }
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }

        internal void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class DelayedClient(
        ScriptedRuntimeChatClient inner, Probe probe, int delay,
        Func<CancellationToken, Task>? wait = null) : DelegatingChatClient(inner)
    {
        private int _firstCall;
        internal int Calls => inner.Calls.Count;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await probe.EnterAsync(cancellationToken);
                if (Interlocked.Exchange(ref _firstCall, 1) == 0 && wait is not null)
                {
                    await wait(cancellationToken);
                }
                await Task.Delay(delay, cancellationToken);
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                response.ModelId = options?.ModelId;
                response.Usage = new() { InputTokenCount = 10, OutputTokenCount = 5 };
                return response;
            }
            finally
            {
                probe.Exit();
            }
        }
    }
}
