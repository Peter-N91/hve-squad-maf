using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeProductionTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string StatePath(RuntimeTestHost host) => Path.Combine(host.Project, host.Options.SquadRoot, "state.json");
    private static RuntimeState State(RuntimeTestHost host) => JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath(host)))!;

    private static string TaskBlock(string id, string dependencies = "None") => $"""
        <!-- rpi:task id={id} -->
        #### [ ] {id}: Produce the assigned local output
        Goals:
        * Produce the bounded specialist output.
        Requirements:
        * FR-001
        Details:
        * Preserve actual host permissions and exact local scope.
        References:
        * The verified lead plan and native research receipt.
        Dependencies:
        * {dependencies}

        """;

    private static void Plan(RuntimeTestHost host, string rows, string? extraTasks = null)
    {
        host.UseCurrentPlanContract();
        host.Options.AdditionalRoles.Add("analyst");
        host.PlanTransform = content => content.Replace("## User Decisions and Requirements", (extraTasks ?? "") + """
            ## Deliverables
            | Id | Role | Tasks | Depends On | Project Outputs |
            | --- | --- | --- | --- | --- |
            """ + "\n" + rows + "\n\n## User Decisions and Requirements", StringComparison.Ordinal);
    }

    private static void QueueDelivery(RuntimeTestHost host, string focus = "technical-writer")
    {
        host.QueuePipeline(focus);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("tester");
        host.Client.Text("Actual reviewed multi-owner delivery.");
    }

    private static void Bind(RuntimeTestHost host, ProducingClient client, params string[] roles)
    {
        host.Options.ModelSelector = (entry, _) => roles.Contains(entry?.Role, StringComparer.Ordinal)
            ? new(client, "model-fixed") : new(host.Client, "model-fixed");
    }

    private static ConcurrentQueue<string> Writer(RuntimeTestHost host, SquadToolOutputStatus status = SquadToolOutputStatus.Succeeded)
    {
        var writes = new ConcurrentQueue<string>();
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create((string path, string content) =>
        {
            if (status == SquadToolOutputStatus.Succeeded)
            {
                var target = Path.Combine(host.Project, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
                writes.Enqueue(path);
            }
            return path;
        }, "write_project"), "edit/project", ["developer"], SquadToolEffect.ProjectWrite,
            result => new([result!.ToString()!], "Actual bounded host result.", status), ProjectWritePaths: args => [args["path"]!.ToString()!]));
        return writes;
    }

    private static void AddBacklogExecutor(RuntimeTestHost host, bool independentOwner = false)
    {
        host.WriteCharter("backlog-executor");
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        var catalog = File.ReadAllText(roster);
        var coordinator = Path.Combine(host.Artifacts, "agents", "coordinator.agent.md");
        var agents = File.ReadAllText(coordinator);
        if (independentOwner)
        {
            host.Write("agents/independent-product-owner.agent.md",
                "---\nname: Independent Product Owner\nuser-invocable: false\ndisable-model-invocation: false\n---\n" +
                "Original independently installed finalized-handoff author fixture.");
            catalog = catalog.Replace("| product-owner | Test product-owner | \u2014 | testing only |",
                "| product-owner | Test product-owner | Independent Product Owner | finalized backlog handoff \u2192 Independent Product Owner |",
                StringComparison.Ordinal);
            agents = agents.Replace("agents:\n", "agents:\n  - Independent Product Owner\n", StringComparison.Ordinal);
        }
        File.WriteAllText(roster, catalog.Replace("## Squad Profiles",
            "| backlog-executor | Test backlog-executor | none | finalized backlog handoff | .copilot-tracking/backlog/ |\n\n## Squad Profiles",
            StringComparison.Ordinal));
        File.WriteAllText(coordinator, agents.Replace("agents:\n",
            "agents:\n  - Test backlog-executor\n", StringComparison.Ordinal));
        host.Options.AdditionalRoles.Add("backlog-executor");
    }

    private static void QueueBacklog(RuntimeTestHost host, bool owner, string[]? inputs = null,
        bool automatic = false, bool validate = false)
    {
        host.QueuePipeline("backlog-executor", inputs: inputs);
        if (inputs?.Length > 0)
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (automatic)
        {
            RuntimeModeTests.Council(host, role: "product-owner");
        }
        if (owner)
        {
            host.QueueStage("product-owner");
        }
        host.Client.Call("apply_backlog", []);
        host.QueueStage("backlog-executor");
        if (validate)
        {
            RuntimeModeTests.Council(host, role: "product-owner");
        }
        host.QueueStage("tester");
        host.Client.Text("Synthetic actual backlog execution, not a live tracker.");
    }

    [Theory]
    [InlineData(false, "interactive")]
    [InlineData(true, "interactive")]
    [InlineData(false, "autonomous")]
    [InlineData(true, "autonomous")]
    [InlineData(false, "autopilot")]
    [InlineData(true, "autopilot")]
    public async Task GivenBacklogExecutionWithoutFinalizedHandoff_WhenDelivering_DispatchesProductOwnerFirstAndKeepsEffectApproval(bool deny, string mode)
    {
        using var host = new RuntimeTestHost();
        AddBacklogExecutor(host, independentOwner: mode == "autonomous");
        var mutations = 0;
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => ++mutations, "apply_backlog"),
            "write/backlog", ["backlog-executor"], SquadToolEffect.External,
            _ => new([], "Actual synthetic tracker counter changed; not a live provider.")));
        RuntimeModeTests.Mode(host, mode, safe: false);
        if (mode != "interactive")
        {
            host.Options.Council = new() { Roles = ["product-owner"] };
        }
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            if (approval.Kind == SquadApprovalKind.ConsequentialTool)
            {
                var source = Assert.Single(State(host).DispatchRecords, evidence =>
                    evidence.Stage == "produce" && evidence.Role == "product-owner");
                Assert.True(File.Exists(Path.Combine(host.Project, source.ArtifactPath)));
                Assert.Single(State(host).ProductionHandoffs!);
                return ValueTask.FromResult(!deny);
            }
            return ValueTask.FromResult(true);
        };
        QueueBacklog(host, owner: true, automatic: mode != "interactive", validate: mode == "autonomous");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Create the approved bounded work items.");

        Assert.True(result.Status == (deny ? SquadRunStatus.ApprovalDenied : SquadRunStatus.Completed), result.ResponseText);
        Assert.Equal(deny ? 0 : 1, mutations);
        Assert.Equal(2, result.ProductionPlan!.Units.Count);
        Assert.False(result.ProductionPlan.ExplicitDeliverables);
        var handoff = Assert.Single(result.ProductionHandoffs!);
        Assert.Equal("product-owner", Assert.Single(handoff.Evidence).Role);
        Assert.Equal("backlog-handoff", handoff.Producer);
        Assert.Equal("primary", handoff.Consumer);
        Assert.Equal(deny ? ["product-owner"] : ["product-owner", "backlog-executor"],
            result.Evidence.Where(evidence => evidence.Stage == "produce").Select(evidence => evidence.Role));
    }

    [Fact]
    public async Task GivenExplicitBacklogWithoutOwningDependency_WhenPlanning_BlocksBeforeAnyProducingCall()
    {
        using var host = new RuntimeTestHost();
        AddBacklogExecutor(host);
        Plan(host, "| executor | backlog-executor | P01-T01 | None | None |");
        host.QueuePipeline("backlog-executor");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Create the bounded work items.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("finalized product-owner handoff", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "produce");
    }

    [Fact]
    public async Task GivenBacklogAuthorAlsoCouncilSeat_WhenAutonomous_StopsInsteadOfSelfValidating()
    {
        using var host = new RuntimeTestHost();
        AddBacklogExecutor(host);
        host.Options.Tools.Clear();
        var mutations = 0;
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => ++mutations, "apply_backlog"),
            "write/backlog", ["backlog-executor"], SquadToolEffect.External,
            _ => new([], "Actual approved synthetic tracker mutation; not a live provider.")));
        RuntimeModeTests.Mode(host, "autonomous", safe: false);
        host.Options.Council = new() { Roles = ["product-owner"] };
        QueueBacklog(host, owner: true, automatic: true, validate: true);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Create the approved bounded work items.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("cannot validate its own producing output", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(1, mutations);
        Assert.Single(result.Evidence, evidence => evidence.Stage == "council");
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "review");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenExistingHandoffInput_WhenExecutingBacklog_RequiresVerifiedReviewedProductOwnerProvenance(bool verified)
    {
        using var host = new RuntimeTestHost();
        AddBacklogExecutor(host);
        var mutations = 0;
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => ++mutations, "apply_backlog"),
            "write/backlog", ["backlog-executor"], SquadToolEffect.External,
            _ => new([], "Actual synthetic tracker counter changed; not a live provider.")));
        string input;
        if (verified)
        {
            host.QueueSuccess("product-owner");
            using var preparation = await host.CreateAsync();
            var prepared = await preparation.RunAsync("Prepare the bounded local backlog handoff.");
            Assert.Equal(SquadRunStatus.Completed, prepared.Status);
            input = prepared.Evidence.Single(evidence => evidence.Stage == "produce").ArtifactPath;
        }
        else
        {
            input = "handoff.md";
            File.WriteAllText(Path.Combine(host.Project, input), "# Finalized backlog handoff\nUntrusted claim without a native owner/review receipt.");
        }
        var prior = File.Exists(StatePath(host)) ? State(host).DispatchRecords.Count : 0;
        QueueBacklog(host, owner: !verified, [input]);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Create the approved bounded work items from the explicit input.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText + "\nPlanned owners: " +
            JsonSerializer.Serialize(result.ProductionPlan?.Units.Select(unit => unit.Role)));
        Assert.Equal(1, mutations);
        Assert.Equal(verified ? ["backlog-executor"] : ["product-owner", "backlog-executor"],
            result.Evidence.Where(evidence => evidence.Stage == "produce").Select(evidence => evidence.Role));
        Assert.True(State(host).DispatchRecords.Count > prior);
    }

    [Theory]
    [InlineData(1, "interactive")]
    [InlineData(2, "interactive")]
    [InlineData(4, "interactive")]
    [InlineData(1, "autopilot")]
    [InlineData(2, "autopilot")]
    [InlineData(4, "autopilot")]
    public async Task GivenIndependentOwnersAndDependentOutput_WhenDelivering_UsesRealBoundedWavesAndVerifiedHandoffs(int concurrency, string mode)
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxConcurrentDispatches = concurrency;
        host.Options.Mode = mode;
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            | summary | product-owner | P01-T03 | None | None |
            """, TaskBlock("P01-T02") + TaskBlock("P01-T03", "P01-T01, P01-T02"));
        using var client = new ProducingClient(host, Math.Min(concurrency, 2), ["guide", "analysis"]);
        Bind(host, client, "technical-writer", "analyst", "product-owner");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the bounded local specialist documents.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.InRange(client.Peak, Math.Min(concurrency, 2), concurrency);
        var produced = result.Evidence.Where(item => item.Stage == "produce" && item.Depth == 1).ToArray();
        Assert.Equal(["technical-writer", "analyst", "product-owner"], produced.Select(item => item.Role));
        Assert.Equal(["produce:guide:technical-writer", "produce:analysis:analyst", "produce:summary:product-owner"],
            produced.Select(item => item.UnitSlot));
        Assert.All(produced, item => Assert.Equal(RuntimeTestHost.Agent(item.Role), item.Agent));
        Assert.Equal(3, produced.Select(item => item.ArtifactPath).Distinct(StringComparer.Ordinal).Count());
        var proposed = mode == "interactive" ?
            Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.Implementation).ProductionPlan! :
            result.ProductionPlan!;
        if (mode == "autopilot")
        {
            Assert.True(result.ModeOutcome!.FinalValidated);
            Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.Implementation);
        }
        Assert.Equal(["analysis", "guide"], proposed.Units.Single(unit => unit.Id == "summary").Dependencies);
        Assert.Equal(2, result.ProductionHandoffs!.Count);
        var received = Assert.Single(client.Received, pair => pair.Key == "summary").Value;
        Assert.Equal(2, received.Length);
        Assert.All(received, handoff => Assert.Contains(produced, output => handoff.Evidence.Any(item => item.DispatchId == output.DispatchId)));
        var state = State(host);
        foreach (var independent in produced.Take(2))
        {
            var session = state.Sessions["dispatch:" + independent.DispatchId].GetRawText();
            Assert.Contains("PRIVATE_" + (independent.Role == "technical-writer" ? "guide" : "analysis"), session, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_" + (independent.Role == "technical-writer" ? "analysis" : "guide"), session, StringComparison.Ordinal);
        }
        Assert.Equal(2, state.NativeOperations.Count(operation => operation.ProductionHandoff is not null));
        Assert.All(state.NativeOperations.Where(operation => operation.ProductionHandoff is not null),
            operation => Assert.Equal(0, operation.ModelInvocations));
        Assert.Equal(produced.Select(item => item.DispatchId), state.DispatchRecords
            .Where(item => item.Stage == "produce" && item.Depth == 1).Select(item => item.DispatchId));
        Assert.Single(result.Evidence, item => item.Stage == "review");
        Assert.Equal(0, host.Writes);
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
        runtime.Dispose();
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task GivenAuthenticatedFanoutFinalChanges_WhenResuming_RevisesOnlySamePlanUnitsAndPreservesExactEarlierEffectBytes()
    {
        using var host = new RuntimeTestHost();
        host.Options.Mode = "autopilot";
        host.Options.MaxConcurrentDispatches = 1;
        Plan(host, """
            | code | developer | P01-T01 | None | bounded.cs |
            | guide | technical-writer | P01-T02 | code | None |
            """, TaskBlock("P01-T02"));
        var writes = Writer(host);
        host.Options.Tools[0] = host.Options.Tools[0] with { Impact = SquadToolImpact.Safe };
        using var notifications = RuntimeModeTests.Durable(host);
        using var firstClient = new ProducingClient(host, 1, ["code"]);
        using var secondClient = new ProducingClient(host, 1, ["code"]) { OutputSuffix = " corrected" };
        var selected = firstClient;
        host.Options.ModelSelector = (entry, _) => entry?.Role is "developer" or "technical-writer" ?
            new(selected, "model-fixed") : new(host.Client, "model-fixed");
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("tester");
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("Implement bounded local code and its guide.");
        Assert.True(first.Status == SquadRunStatus.ApprovalRequired, first.ResponseText);
        var originalBytes = File.ReadAllBytes(Path.Combine(host.Project, "bounded.cs"));
        _ = await RuntimeModeTests.RespondAsync(runtime, "finaloutcome", "/changes: Correct the bounded local implementation.");
        selected = secondClient;
        host.QueueStage("tester");

        var second = await runtime.ResumeAsync(first.RunId);

        Assert.True(second.Status == SquadRunStatus.ApprovalRequired, second.ResponseText);
        Assert.Equal(["bounded.cs", "bounded.cs"], writes.ToArray());
        Assert.Equal(2, second.Effects!.Count);
        Assert.Empty(second.ModeOutcome!.Iterations);
        var snapshot = Assert.Single(State(host).OutputRevisions!);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(host.Project, snapshot.ArchivePath)));
        Assert.Equal("Actual host content for code corrected", File.ReadAllText(Path.Combine(host.Project, "bounded.cs")));
        Assert.NotNull(snapshot.HumanActionId);
        Assert.Null(snapshot.ValidationRecordId);
        var newHandoff = Assert.Single(second.ProductionHandoffs!, handoff => handoff.Iteration == 2);
        Assert.Equal("code", newHandoff.Producer);
        Assert.Equal("guide", newHandoff.Consumer);
        Assert.Equal("autonomy:2:produce:code:developer", newHandoff.Evidence[0].UnitSlot);
        _ = await RuntimeModeTests.RespondAsync(runtime, "finaloutcome");
        var calls = host.Client.Calls.Count;
        var done = await runtime.ResumeAsync(first.RunId);
        Assert.True(done.Status == SquadRunStatus.Completed, done.ResponseText);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(2, writes.Count);
        Assert.True(done.ModeOutcome!.FinalValidated);
    }

    [Theory]
    [InlineData("| guide | absent-role | P01-T01 | None | None |", "")]
    [InlineData("| guide | tester | P01-T01 | None | None |", "")]
    [InlineData("| guide | technical-writer | P01-T01 | missing | None |", "")]
    [InlineData("| guide | technical-writer | P01-T01 | guide | None |", "")]
    [InlineData("| guide | technical-writer | P01-T01 | None | None |\n| guide | analyst | P01-T01 | None | None |", "")]
    [InlineData("| guide | technical-writer | P01-T99 | None | None |", "")]
    [InlineData("| guide | developer | P01-T01 | None | None |", "")]
    [InlineData("| guide | technical-writer | P01-T01 | analysis | None |\n| analysis | analyst | P01-T01 | guide | None |", "")]
    public async Task GivenUnresolvedDeliverables_WhenPlanning_BlocksBeforeAnyProducingCall(string rows, string extraTasks)
    {
        using var host = new RuntimeTestHost();
        Plan(host, rows, extraTasks);
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "developer", "technical-writer", "analyst");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded local outputs.");

        Assert.Contains(result.Status, new[] { SquadRunStatus.Blocked, SquadRunStatus.Failed });
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "produce");
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.Implementation);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenUnownedTaskPrerequisite_WhenDerivingOwners_BlocksRatherThanIgnoringIt()
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| guide | technical-writer | P01-T02 | None | None |", TaskBlock("P01-T02", "P01-T01"));
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce a bounded local output.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("no producing owner", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "produce");
    }

    [Theory]
    [InlineData("source//same.txt", "source/same.txt")]
    [InlineData("source/file.txt", "source/file.txt/child.txt")]
    public async Task GivenCollidingPlannedPaths_WhenConfirmingProduction_InvokesNoWriter(string first, string second)
    {
        using var host = new RuntimeTestHost();
        Plan(host, $"| first | developer | P01-T01 | None | {first} |\n| second | developer | P01-T02 | None | {second} |",
            TaskBlock("P01-T02"));
        var writes = Writer(host);
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "developer");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded local outputs.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("collide", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task GivenSourceOutputOutsideItsUnit_WhenInvoking_BlocksBeforeHostEffect()
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| first | developer | P01-T01 | None | source/first.txt |");
        var writes = Writer(host);
        using var client = new ProducingClient(host, 1, []) { WritePathOverride = "source/unapproved.txt" };
        Bind(host, client, "developer");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the bounded source output.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("approved plan outputs", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.ConsequentialTool);
    }

    [Theory]
    [InlineData(SquadToolOutputStatus.Failed)]
    [InlineData(SquadToolOutputStatus.DryRun)]
    public async Task GivenNonAppliedHostResult_WhenProducing_RecordsTruthAndCannotComplete(SquadToolOutputStatus status)
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| first | developer | P01-T01 | None | source/first.txt |");
        var writes = Writer(host, status);
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "developer");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the bounded source output.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains(status.ToString(), result.ResponseText, StringComparison.Ordinal);
        var receipt = Assert.Single(result.Effects!).Receipt;
        Assert.Equal(status, receipt.Status);
        Assert.Empty(receipt.Outputs!);
        Assert.Empty(writes);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "review");
    }

    [Fact]
    public async Task GivenRepeatedOwner_WhenSecondUnitSkipsItsEffect_CannotBorrowTheFirstReceipt()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | first | developer | P01-T01 | None | source/first.txt |
            | second | developer | P01-T02 | first | source/second.txt |
            """, TaskBlock("P01-T02"));
        var writes = Writer(host);
        using var client = new ProducingClient(host, 1, []) { SkipWriteUnit = "second" };
        Bind(host, client, "developer");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce two bounded source outputs.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("this producing unit", result.ResponseText, StringComparison.Ordinal);
        Assert.Single(writes);
        Assert.Single(result.Effects!);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "review");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenProducerHistoryNotYetHandedOff_WhenIndependentOwnerReadsIt_BlocksTheCrossScopeRead(bool history)
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            """, TaskBlock("P01-T02"));
        using var client = new ProducingClient(host, 1, []) { AttackUnit = "analysis", AttackHistory = history };
        host.Options.MaxConcurrentDispatches = 1;
        Bind(host, client, "technical-writer", "analyst");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded independent documents.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("no verified handoff", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "review");
    }

    [Fact]
    public async Task GivenPartialFanout_WhenRestarting_ReusesCompletedUnitsAndExactHandoffs()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | guide | None |
            """, TaskBlock("P01-T02"));
        using var client = new ProducingClient(host, 1, []) { SuspendUnit = "analysis" };
        Bind(host, client, "technical-writer", "analyst");
        QueueDelivery(host);
        SquadRunResult suspended;
        using (var runtime = await host.CreateAsync())
        {
            suspended = await runtime.RunAsync("Produce bounded dependent documents.");
        }
        Assert.True(suspended.Status == SquadRunStatus.InputRequired, suspended.ResponseText);
        var guide = Assert.Single(suspended.Evidence, item => item.Stage == "produce");
        var history = File.ReadAllBytes(Path.Combine(host.Project, guide.HistoryPath));
        var originalHandoff = Assert.Single(suspended.ProductionHandoffs!);
        var originalGuideCalls = client.UnitCalls["guide"];
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Confirmed local-only continuation.");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(suspended.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(originalGuideCalls, client.UnitCalls["guide"]);
        Assert.Equal(guide.DispatchId, Assert.Single(result.Evidence, item => item.Role == "technical-writer" && item.Stage == "produce").DispatchId);
        Assert.True(File.ReadAllBytes(Path.Combine(host.Project, guide.HistoryPath)).AsSpan().StartsWith(history));
        Assert.Equal(JsonSerializer.Serialize(originalHandoff), JsonSerializer.Serialize(Assert.Single(result.ProductionHandoffs!)));
        Assert.Single(State(host).NativeOperations, operation => operation.ProductionHandoff is not null);
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.Implementation);
        Assert.Single(result.Evidence, item => item.Stage == "review");
    }

    [Fact]
    public async Task GivenChangedHandoffIndex_WhenResuming_RejectsItWithoutAnotherModelCall()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | guide | None |
            """, TaskBlock("P01-T02"));
        using var client = new ProducingClient(host, 1, []) { SuspendUnit = "analysis" };
        Bind(host, client, "technical-writer", "analyst");
        QueueDelivery(host);
        SquadRunResult suspended;
        using (var runtime = await host.CreateAsync())
        {
            suspended = await runtime.RunAsync("Produce bounded dependent documents.");
        }
        Assert.True(suspended.Status == SquadRunStatus.InputRequired, suspended.ResponseText);
        var state = JsonNode.Parse(File.ReadAllText(StatePath(host)))!;
        state["ProductionHandoffs"]![0]!["Consumer"] = "unrelated";
        File.WriteAllText(StatePath(host), state.ToJsonString(Indented));
        var calls = client.Calls + host.Client.Calls.Count;
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(suspended.RunId);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(SquadEvidenceFailureClass.HistoryIntegrity, result.FailureClass);
        Assert.Equal(calls, client.Calls + host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenNamedFanoutCostDemand_WhenAdmitting_ProjectsEveryExactUnitThroughReview()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            | summary | product-owner | P01-T03 | guide, analysis | None |
            """, TaskBlock("P01-T02") + TaskBlock("P01-T03"));
        using var client = new ProducingClient(host, 2, ["guide", "analysis"]);
        Bind(host, client, "technical-writer", "analyst", "product-owner");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded independent documents.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, client.Peak);
        var rounds = State(host).CostPreflights!;
        var fanout = rounds.Where(round => round.Demand.Any(row => row.Slot == "produce:guide:technical-writer")).ToArray();
        Assert.NotEmpty(fanout);
        Assert.Contains(fanout, round => round.Demand.Any(row => row.Slot == "produce:analysis:analyst") &&
            round.Demand.Any(row => row.Slot == "review:tester"));
        Assert.DoesNotContain(fanout.SelectMany(round => round.Demand), row => row.Slot == "produce:developer");
        Assert.Contains(fanout, round => round.Demand.Any(row => row.Slot == "handoff:guide:summary" &&
            row.ProjectedCostUsd == 0m) && round.Demand.Any(row => row.Slot == "handoff:analysis:summary" &&
            row.ProjectedCostUsd == 0m));
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot);
        Assert.NotNull(result.Consumption!.EstimatedCostUsd);
    }

    [Fact]
    public async Task GivenPhasePrerequisite_WhenDerivingWaves_OrdersItsTasksBeforeTheDependentPhase()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P02-T01 | None | None |
            """, """
            <!-- rpi:phase id=P02 -->
            ### [ ] P02: Dependent local analysis
            Goals:
            * Consume the verified guide.
            Dependencies:
            * P01

            """ + TaskBlock("P02-T01"));
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "technical-writer", "analyst");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded dependent documents.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(["guide"], result.ProductionPlan!.Units.Single(unit => unit.Id == "analysis").Dependencies);
        Assert.Equal("guide", Assert.Single(result.ProductionHandoffs!).Producer);
        Assert.Equal(1, client.Peak);
    }

    [Fact]
    public async Task GivenFencedTaskDetails_WhenAssigning_PreservesRealContextWithoutParsingExampleMarkers()
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| guide | technical-writer | P01-T01 | None | None |");
        var transform = host.PlanTransform!;
        host.PlanTransform = content => transform(content.Replace(
            "* Use the registered host capability; no external actions.", """
            * Use the registered host capability; no external actions.
            ```text
            PRESERVE_THIS_TASK_EXAMPLE
            Dependencies:
            * P99-T99
            <!-- rpi:task id=P99-T99 -->
            #### [ ] P99-T99: Not a real task
            ```
            """, StringComparison.Ordinal));
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "technical-writer");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the bounded local document.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var unit = Assert.Single(result.ProductionPlan!.Units);
        Assert.Equal(["P01-T01"], unit.Tasks);
        Assert.Empty(unit.Dependencies);
        Assert.Contains("PRESERVE_THIS_TASK_EXAMPLE", unit.PlanContext, StringComparison.Ordinal);
        Assert.Contains("```text", unit.PlanContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenMultipleRequiredFiles_WhenOnlyOneIsWritten_CannotCompleteTheUnit()
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| first | developer | P01-T01 | None | source/first.txt, source/missing.txt |");
        var writes = Writer(host);
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "developer");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce the bounded source outputs.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("source/missing.txt", result.ResponseText, StringComparison.Ordinal);
        Assert.Single(writes);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "review");
    }

    [Fact]
    public async Task GivenDeclaredProducingWorker_WhenRunningParallelOwners_RecordsItsActualEffectUnderOnlyItsParentUnit()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | source | developer | P01-T01 | None | source/result.txt |
            | guide | technical-writer | P01-T02 | None | None |
            """, TaskBlock("P01-T02"));
        var writes = Writer(host);
        const string workerName = "Declared Producing Worker";
        host.WriteCharter("developer", children: workerName);
        host.Write("agents/producing-worker.agent.md",
            "---\nname: Declared Producing Worker\nuser-invocable: false\ntools:\n  - edit/project\n---\nOriginal bounded child.");
        using var worker = new ScriptedRuntimeChatClient();
        worker.Call("write_project", new() { ["path"] = "source/result.txt", ["content"] = "Actual child source bytes." });
        worker.Call("write_artifact", new() { ["content"] = "Actual bounded child evidence." });
        worker.Call("complete_stage", new() { ["summary"] = "Verified child write.", ["verdict"] = "Completed", ["risk"] = "Low", ["blockingIssues"] = 0 });
        worker.Text("Declared child finished.");
        using var client = new ProducingClient(host, 2, ["source", "guide"]) { WorkerUnit = "source", WorkerAgent = workerName };
        host.Options.ModelSelector = (entry, charter) => new(
            charter.Name == workerName ? worker :
            entry?.Role is "developer" or "technical-writer" ? client : host.Client, "model-fixed");
        QueueDelivery(host, "developer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded independent outputs.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var child = Assert.Single(result.Evidence, item => item.Agent == workerName);
        var parent = Assert.Single(result.Evidence, item => item.Stage == "produce" && item.Depth == 1 && item.Role == "developer");
        Assert.Equal(parent.DispatchId, child.ParentDispatchId);
        Assert.Equal(2, child.Depth);
        Assert.Single(child.ExecutedHostTools!);
        Assert.Empty(parent.ExecutedHostTools!);
        Assert.Single(writes);
        Assert.Equal("Actual child source bytes.", File.ReadAllText(Path.Combine(host.Project, "source", "result.txt")));
        Assert.Equal(2, client.Peak);
        Assert.True((await runtime.ReconcileEvidenceAsync()).HistoryVerified);
        Assert.False(worker.Disposed);
    }

    [Theory]
    [InlineData("Undeclared Worker", "Allowed Worker")]
    [InlineData("Missing Worker", "Missing Worker")]
    [InlineData("Test analyst", "Test analyst")]
    public async Task GivenInvalidProducingChild_WhenRequested_RejectsMissingAllowlistAndRoleImpersonation(string requested, string declared)
    {
        using var host = new RuntimeTestHost();
        Plan(host, "| guide | technical-writer | P01-T01 | None | None |");
        host.WriteCharter("technical-writer", children: declared);
        using var client = new ProducingClient(host, 1, []) { WorkerUnit = "guide", WorkerAgent = requested };
        Bind(host, client, "technical-writer");
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce a bounded local document.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.DoesNotContain(result.Evidence, item => item.Depth > 1);
        Assert.DoesNotContain(result.Evidence, item => item.Stage == "review");
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task GivenParallelUnitQuestion_WhenRestarting_PreservesCompletedSiblingWithoutSharingItsSession()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            """, TaskBlock("P01-T02"));
        using var client = new ProducingClient(host, 1, []) { SuspendUnit = "analysis", WaitForGuide = true };
        host.Log.Observer = id =>
        {
            if (id.Name == "DispatchCheckpointed" &&
                State(host).Checkpoint!.PendingEvidence.Any(item => item.UnitSlot == "produce:guide:technical-writer"))
            {
                client.ReleaseGuide();
            }
        };
        Bind(host, client, "technical-writer", "analyst");
        QueueDelivery(host);
        SquadRunResult suspended;
        using (var runtime = await host.CreateAsync())
        {
            suspended = await runtime.RunAsync("Produce bounded independent documents.");
        }
        Assert.True(suspended.Status == SquadRunStatus.InputRequired, suspended.ResponseText);
        var guide = Assert.Single(suspended.Evidence, item => item.Stage == "produce");
        var guideCalls = client.UnitCalls["guide"];
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Confirmed local continuation.");
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(suspended.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(guideCalls, client.UnitCalls["guide"]);
        Assert.Contains(result.Evidence, item => item.DispatchId == guide.DispatchId);
        var analysis = Assert.Single(result.Evidence, item => item.Stage == "produce" && item.Role == "analyst");
        Assert.DoesNotContain("PRIVATE_guide", State(host).Sessions["dispatch:" + analysis.DispatchId].GetRawText(), StringComparison.Ordinal);
        Assert.Single(result.Evidence, item => item.Stage == "review");
    }

    [Fact]
    public async Task GivenFanoutCouncilConditions_WhenProducing_AttachesExactConditionsToEveryOwnerAndReview()
    {
        using var host = new RuntimeTestHost();
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            """, TaskBlock("P01-T02"));
        host.Options.MaxConcurrentDispatches = 1;
        host.Options.Council = new() { Roles = ["security", "product-owner"] };
        using var client = new ProducingClient(host, 1, []);
        Bind(host, client, "technical-writer", "analyst");
        host.QueuePipeline("technical-writer", council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("security", "Conditional");
        host.QueueStage("product-owner", "Approve");
        host.QueueStage("tester");
        host.Client.Text("Reviewed exact council conditions.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded documents with security council.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var approval = Assert.Single(host.Approvals, item => item.Kind == SquadApprovalKind.Implementation);
        var condition = Assert.Single(approval.CouncilVerdict!.Conditions);
        Assert.Equal("security", condition.Role);
        Assert.All(client.Assignments.Values, assignment => Assert.Contains(JsonSerializer.Serialize(condition), assignment, StringComparison.Ordinal));
        var reviewPrompt = host.Client.Calls.SelectMany(call => call.Messages).Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text).First(text => text.Contains("stage=review", StringComparison.Ordinal));
        Assert.Contains(JsonSerializer.Serialize(condition), reviewPrompt, StringComparison.Ordinal);
        var proposal = approval.CouncilVerdict.Proposal;
        Assert.Contains(proposal.Lenses, lens => lens.Role == "product-owner" && lens.Needed);
    }

    [Fact]
    public async Task GivenOvershootApproval_WhenFanoutRuns_UsesSequentialUnitsWithFullUnchangedDemand()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ".05");
        Plan(host, """
            | guide | technical-writer | P01-T01 | None | None |
            | analysis | analyst | P01-T02 | None | None |
            """, TaskBlock("P01-T02"));
        using var client = new ProducingClient(host, 1, []);
        using var metered = new MeteredClient(host.Client);
        host.Options.ModelSelector = (entry, _) => new(entry?.Role is "technical-writer" or "analyst" ? client : metered, "model-fixed");
        host.Options.TokenUsageSelector = response => new(response.Usage!.InputTokenCount!.Value, 0, 0, response.Usage.OutputTokenCount!.Value);
        QueueDelivery(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Produce bounded independent documents.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(1, client.Peak);
        Assert.Contains(host.Approvals, approval => approval.Kind == SquadApprovalKind.CostOvershoot &&
            approval.CostPreflight!.Demand.Any(row => row.Slot == "produce:guide:technical-writer") &&
            approval.CostPreflight.Demand.Any(row => row.Slot == "produce:analysis:analyst"));
        Assert.True(result.Consumption!.EstimatedCostUsd < .05m);
        Assert.Single(result.Evidence, item => item.Stage == "review");
    }

    private sealed class MeteredClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.ModelId = "model-fixed";
            response.Usage = new() { InputTokenCount = 10, OutputTokenCount = 1 };
            return response;
        }
    }

    private sealed class ProducingClient(RuntimeTestHost host, int barrierSize, string[] firstWave) : IChatClient
    {
        private readonly ConcurrentDictionary<string, int> _steps = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _guideCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _arrivals;
        private int _calls;
        private int _peak;

        internal ConcurrentDictionary<string, int> UnitCalls { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, SquadProductionHandoff[]> Received { get; } = new(StringComparer.Ordinal);
        internal string OutputSuffix { get; init; } = "";
        internal ConcurrentDictionary<string, string> Assignments { get; } = new(StringComparer.Ordinal);
        internal string? AttackUnit { get; init; }
        internal bool AttackHistory { get; init; }
        internal string? SkipWriteUnit { get; init; }
        internal string? SuspendUnit { get; init; }
        internal string? WritePathOverride { get; init; }
        internal string? WorkerAgent { get; init; }
        internal string? WorkerUnit { get; init; }
        internal bool WaitForGuide { get; init; }
        internal int Calls => _calls;
        internal int Peak => _peak;
        internal bool Disposed { get; private set; }
        internal void ReleaseGuide() => _guideCompleted.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = messages.Last(message => message.Role == ChatRole.User).Text;
            var match = Regex.Match(prompt, @"Execute only this plan-derived unit[^\n]*\n(\{[^\n]*\})",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var unit = JsonSerializer.Deserialize<SquadProducingUnit>(match.Groups[1].Value)!;
            Assignments[unit.Id] = prompt;
            var handoffMatch = Regex.Match(prompt, @"Explicit verified dependency handoffs[^\n]*\n(\[[^\n]*\])",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var handoffs = JsonSerializer.Deserialize<SquadProductionHandoff[]>(handoffMatch.Groups[1].Value)!;
            Received[unit.Id] = handoffs;
            var step = _steps.AddOrUpdate(unit.Id, 0, (_, previous) => previous + 1);
            UnitCalls.AddOrUpdate(unit.Id, 1, (_, previous) => previous + 1);
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int prior;
            do
            {
                prior = _peak;
            } while (active > prior && Interlocked.CompareExchange(ref _peak, active, prior) != prior);
            try
            {
                if (step == 0 && firstWave.Contains(unit.Id, StringComparer.Ordinal))
                {
                    if (Interlocked.Increment(ref _arrivals) >= barrierSize)
                    {
                        _barrier.TrySetResult();
                    }
                    await _barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                ChatResponse response;
                if (step == 0 && WaitForGuide && SuspendUnit == unit.Id)
                {
                    await _guideCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                if (step == 0 && AttackUnit == unit.Id)
                {
                    var guide = State(host).Checkpoint!.PendingEvidence.Concat(State(host).Checkpoint!.Evidence)
                        .First(item => item.Stage == "produce" && item.Role == "technical-writer");
                    response = RuntimeTestHost.Function("project_read", new() { ["path"] = AttackHistory ? guide.HistoryPath : guide.ArtifactPath });
                }
                else if (step == 0 && SuspendUnit == unit.Id)
                {
                    response = RuntimeTestHost.Function("ask_host", new() { ["question"] = "Confirm this unit's local-only scope." });
                }
                else if (step == 0 && WorkerUnit == unit.Id)
                {
                    response = RuntimeTestHost.Function("dispatch_child", new() { ["agentName"] = WorkerAgent, ["childTask"] = "Produce only the parent's assigned bounded output." });
                }
                else if (step == 0 && unit.ProjectOutputs.Count > 0 && SkipWriteUnit != unit.Id)
                {
                    response = RuntimeTestHost.Function("write_project", new()
                    {
                        ["path"] = WritePathOverride ?? unit.ProjectOutputs[0],
                        ["content"] = "Actual host content for " + unit.Id + OutputSuffix,
                    });
                }
                else if (step == 0 && handoffs.Length > 0)
                {
                    var state = State(host);
                    Assert.All(handoffs.SelectMany(handoff => handoff.Evidence), evidence =>
                        Assert.Contains(state.DispatchRecords, recorded => recorded.DispatchId == evidence.DispatchId));
                    response = RuntimeTestHost.Function("project_read", new() { ["path"] = handoffs[0].Evidence[0].ArtifactPath });
                }
                else
                {
                    var shifted = (SuspendUnit == unit.Id || WorkerUnit == unit.Id || unit.ProjectOutputs.Count > 0 && SkipWriteUnit != unit.Id ||
                        handoffs.Length > 0) ? step - 1 : step;
                    response = shifted switch
                    {
                        0 => RuntimeTestHost.Function("write_artifact", new() { ["content"] = "Actual independent output PRIVATE_" + unit.Id }),
                        1 => RuntimeTestHost.Function("complete_stage", new()
                        {
                            ["summary"] = "Actual specialist output " + unit.Id,
                            ["verdict"] = "Completed",
                            ["risk"] = "Low",
                            ["blockingIssues"] = 0,
                        }),
                        _ => new(new ChatMessage(ChatRole.Assistant, "Completed actual unit " + unit.Id)),
                    };
                }
                response.ModelId = "model-fixed";
                response.Usage = new() { InputTokenCount = 10, OutputTokenCount = 1 };
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Scripted native tests do not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
