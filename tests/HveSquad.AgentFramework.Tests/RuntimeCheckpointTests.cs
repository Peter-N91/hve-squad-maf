using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeCheckpointTests
{
    private static string StatePath(RuntimeTestHost host) =>
        Path.Combine(host.Project, host.Options.SquadRoot.Replace('/', Path.DirectorySeparatorChar), "state.json");

    private static async Task<SquadDispatchEvidence> SuspendAfterResearchAsync(
        RuntimeTestHost host, string request = "Implement authorized private case facts.", string[]? inputs = null)
    {
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
        host.QueuePipeline("technical-writer", inputs: inputs);
        if (inputs?.Length > 0)
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueStage("researcher");
        using var runtime = await host.CreateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync(request, cancellation.Token));
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var evidence = state.RootElement.GetProperty("Checkpoint").GetProperty("Evidence").EnumerateArray()
            .Single(e => e.GetProperty("Stage").GetString() == "research");
        return evidence.Deserialize<SquadDispatchEvidence>()!;
    }

    [Fact]
    public async Task GivenProcessRestart_WhenResuming_ReusesSameRunAndCompletedResearch()
    {
        using var host = new RuntimeTestHost();
        var research = await SuspendAfterResearchAsync(host);
        var historyBefore = File.ReadAllBytes(Path.Combine(host.Project, research.HistoryPath));
        using var initial = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var runId = initial.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString()!;
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(true);
        };
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        using var next = await host.CreateAsync();

        var result = await next.ResumeAsync(runId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(runId, result.RunId);
        Assert.Equal(["research", "plan", "produce", "review"], result.Evidence.Select(e => e.Stage));
        Assert.Equal(research.DispatchId, result.Evidence[0].DispatchId);
        Assert.Equal(historyBefore, File.ReadAllBytes(Path.Combine(host.Project, research.HistoryPath)));
        Assert.Single(host.Approvals, a => a.Kind == SquadApprovalKind.Routing);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("Checkpoint").ValueKind);
        Assert.Single(state.RootElement.GetProperty("UserTurns").EnumerateArray());
        Assert.Empty(host.Client.Steps);
    }

    [Fact]
    public async Task GivenSuspendedQuestion_WhenNewRequestArrives_DoesNotRestartOrInvokeModel()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("ask_host", new() { ["question"] = "Which approved case input?" });
        using var runtime = await host.CreateAsync();
        var pending = await runtime.RunAsync("Handle authorized private customer facts.");
        Assert.Equal(SquadRunStatus.InputRequired, pending.Status);
        var calls = host.Client.Calls.Count;

        var newRun = await runtime.RunAsync("Try to start again.");

        Assert.Equal(SquadRunStatus.Blocked, newRun.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ResumeAsync(new string('a', 32)));
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        Assert.Equal(pending.RunId, state.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString());
        Assert.Single(state.RootElement.GetProperty("UserTurns").EnumerateArray());
    }

    [Fact]
    public async Task GivenChangedInput_WhenResuming_RejectsOldReadinessBeforeModelCalls()
    {
        using var host = new RuntimeTestHost();
        var input = Path.Combine(host.Project, "requirements.txt");
        File.WriteAllText(input, "Original approved private requirement.");
        _ = await SuspendAfterResearchAsync(host, inputs: ["requirements.txt"]);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var runId = state.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString()!;
        File.WriteAllText(input, "Changed requirement, not previously approved.");
        var calls = host.Client.Calls.Count;
        using var next = await host.CreateAsync();

        var result = await next.ResumeAsync(runId);

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("bound input changed", result.ResponseText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(calls, host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenModelBudget_WhenResuming_DoesNotResetTheSameRunLimit()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxModelCalls = 5;
        _ = await SuspendAfterResearchAsync(host);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var runId = state.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString()!;
        host.Options.ApproveAsync = (_, _) => ValueTask.FromResult(true);
        host.QueueStage("lead");
        using var next = await host.CreateAsync();

        var result = await next.ResumeAsync(runId);

        Assert.Equal(SquadRunStatus.LimitReached, result.Status);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "plan");
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenChangedExecutionLimits_WhenResuming_RejectsTheChangedRunBinding()
    {
        using var host = new RuntimeTestHost();
        _ = await SuspendAfterResearchAsync(host);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        var runId = state.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString()!;
        host.Options.MaxModelCalls++;
        var calls = host.Client.Calls.Count;
        using var next = await host.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => next.ResumeAsync(runId));
        Assert.Equal(calls, host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenApprovedRootOverride_WhenProducing_UsesAndConfirmsTheExactOverride()
    {
        using var host = new RuntimeTestHost();
        host.Options.DeliverableRoots["technical-writer"] = "outputs/approved-case";
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Produce the authorized local document.");
        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.StartsWith("outputs/approved-case/", Assert.Single(result.Evidence, e => e.Stage == "produce").ArtifactPath,
            StringComparison.Ordinal);
        Assert.Equal("outputs/approved-case",
            Assert.Single(host.Approvals, a => a.Kind == SquadApprovalKind.Initialization)
                .Initialization!.DeliverableRoots!["technical-writer"]);
    }

    [Fact]
    public async Task GivenTransientStateReadLock_WhenWriting_CommitsTheSameSnapshotAfterRelease()
    {
        using var host = new RuntimeTestHost();
        using var retryObserved = new ManualResetEventSlim();
        host.Log.Observer = id =>
        {
            if (id.Name == "StateWriteContention")
            {
                retryObserved.Set();
            }
        };
        Task? release = null;
        host.Options.AskAsync = (_, _) =>
        {
            var stream = new FileStream(StatePath(host), FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Factory.StartNew(() =>
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Assert.True(retryObserved.Wait(TimeSpan.FromSeconds(10)), "The protected state replacement must actually encounter the read lock.");
                    }
                }
                finally
                {
                    stream.Dispose();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return ValueTask.FromResult<string?>("Authorized private answer.");
        };
        host.Client.Call("ask_host", new() { ["question"] = "Confirm the case fact." });
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Produce the authorized document.");
        if (release is not null)
        {
            await release;
        }
        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("Checkpoint").ValueKind);
        Assert.Single(state.RootElement.GetProperty("ConfirmedAnswers").EnumerateArray());
    }

    [Fact]
    public async Task GivenMemberRoots_WhenRunning_PersistsAndProducesOnlyInsideEachMember()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        foreach (var name in new[] { "alpha", "beta" })
        {
            host.Options.SquadRoot = $".copilot-tracking/squad/members/{name}";
            host.QueueSuccess("technical-writer");
            using var runtime = await host.CreateAsync();
            var result = await runtime.RunAsync("Produce the authorized local document.");
            Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
            Assert.All(result.Evidence, e =>
            {
                Assert.StartsWith(host.Options.SquadRoot + "/", e.ArtifactPath, StringComparison.Ordinal);
                Assert.StartsWith(host.Options.SquadRoot + "/", e.HistoryPath, StringComparison.Ordinal);
            });
            using var state = JsonDocument.Parse(File.ReadAllText(StatePath(host)));
            Assert.Equal(host.Options.SquadRoot, state.RootElement.GetProperty("SquadRoot").GetString());
        }
        Assert.False(File.Exists(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMemberInput_WhenReading_RequiresExplicitHostHandoff(bool approved)
    {
        using var host = new RuntimeTestHost();
        const string input = ".copilot-tracking/squad/members/alpha/research/private.txt";
        var path = Path.Combine(host.Project, input.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "AUTHORIZED_CASE_FACT");
        host.Options.SquadRoot = ".copilot-tracking/squad/members/beta";
        if (approved)
        {
            host.Options.InputPaths.Add(input);
        }
        host.Client.Call("project_read", new() { ["path"] = input });
        if (approved)
        {
            host.Client.Steps.Enqueue((messages, _) =>
            {
                Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                    result => result.Result?.ToString()?.Contains("AUTHORIZED_CASE_FACT", StringComparison.Ordinal) == true);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No dispatch for this read test."));
            });
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Inspect the explicit input only.");

        Assert.NotEqual(SquadRunStatus.Completed, result.Status);
        if (!approved)
        {
            Assert.Contains("Member reads are limited", result.ResponseText, StringComparison.Ordinal);
            Assert.DoesNotContain("AUTHORIZED_CASE_FACT", result.ResponseText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".git/private")]
    [InlineData(".copilot-tracking")]
    [InlineData(".copilot-tracking/squad/state.json")]
    [InlineData(".copilot-tracking/squad/members/Upper")]
    [InlineData(".copilot-tracking/squad/members/alpha/nested")]
    public async Task GivenInvalidSquadRoot_WhenCreating_RejectsWithoutCaseWrites(string root)
    {
        using var host = new RuntimeTestHost();
        host.Options.SquadRoot = root;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreateAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenAuthorizedPrivateContext_WhenPersisting_RetainsFactsNotHostCredentials()
    {
        using var host = new RuntimeTestHost();
        const string credential = "fixture-host-credential-not-real";
        host.Options.ApproveAsync = (_, _) =>
        {
            GC.KeepAlive(credential);
            return ValueTask.FromResult(true);
        };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("AUTHORIZED_PRIVATE_CASE_FACT must remain in this protected case.");
        Assert.Equal(SquadRunStatus.Completed, result.Status);
        var text = File.ReadAllText(StatePath(host));
        Assert.Contains("AUTHORIZED_PRIVATE_CASE_FACT", text, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, text, StringComparison.Ordinal);
        using var next = await host.CreateAsync();
        Assert.False(host.Client.Disposed);
    }

    [Fact]
    public async Task GivenNativeV1_WhenMigrating_RequiresApprovalAndPreservesOriginalBytesAndExtensions()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess("technical-writer");
        using (var runtime = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.Completed, (await runtime.RunAsync("Create original case history.")).Status);
        }
        var path = StatePath(host);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["Schema"] = "hve-squad-maf/native-single-squad/v1";
        node.Remove("SquadRoot");
        node.Remove("Checkpoint");
        node["retained_private_fact"] = "authorized prior case fact";
        File.WriteAllText(path, node.ToJsonString());
        var original = File.ReadAllBytes(path);
        var decisions = File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md"));
        var consumption = File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"));
        await Assert.ThrowsAsync<InvalidDataException>(() => host.CreateAsync());
        host.Options.AllowNativeV1Migration = true;
        host.Options.ApproveAsync = (approval, _) => ValueTask.FromResult(approval.Kind != SquadApprovalKind.StateMigration);
        using (var denied = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.ApprovalDenied, (await denied.RunAsync("Continue after explicit migration.")).Status);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        host.Options.ApproveAsync = (_, _) => ValueTask.FromResult(true);
        host.Client.Text("No new work for the migration test.");
        using var migrated = await host.CreateAsync();

        _ = await migrated.RunAsync("Continue after explicit migration.");

        var backup = Path.Combine(host.Project, host.Options.SquadRoot, "migrations", "native-v1-state.json");
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(decisions, File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md")));
        Assert.Equal(consumption, File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "migrations", "native-v1-consumption.md")));
        var continuedConsumption = File.ReadAllBytes(Path.Combine(host.Project, host.Options.SquadRoot, "consumption.md"));
        var continuedText = System.Text.Encoding.UTF8.GetString(continuedConsumption);
        foreach (var dispatchRow in System.Text.Encoding.UTF8.GetString(consumption).Split('\n')
                     .Where(line => line.StartsWith("- Dispatch ", StringComparison.Ordinal)))
        {
            Assert.Contains(dispatchRow, continuedText, StringComparison.Ordinal);
        }
        using var state = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("hve-squad-maf/native-squad/v2", state.RootElement.GetProperty("Schema").GetString());
        Assert.Equal("authorized prior case fact", state.RootElement.GetProperty("retained_private_fact").GetString());
        Assert.Single(state.RootElement.GetProperty("Runs").EnumerateArray(), run =>
            run.GetProperty("Status").GetInt32() == (int)SquadRunStatus.Completed);
    }

    [Fact]
    public async Task GivenUncertainEffect_WhenRestarting_ReconcilesAndNeverExecutesItTwice()
    {
        using var host = new RuntimeTestHost();
        var executions = 0;
        var function = AIFunctionFactory.Create((Func<string>)(() =>
        {
            executions++;
            File.WriteAllText(Path.Combine(host.Project, "implementation.txt"), "actual write before lost response");
            throw new IOException("Fixture lost its success response.");
        }), "apply_change", "Fixture consequential writer.");
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(function, "edit/project", ["developer"], SquadToolEffect.ProjectWrite,
            _ => new(["implementation.txt"]), ReconcileAsync: (request, _) =>
            {
                Assert.Equal("apply_change", request.ToolName);
                Assert.Equal(JsonValueKind.Object, request.Arguments.ValueKind);
                return ValueTask.FromResult(new SquadEffectReconciliation(SquadEffectDisposition.Executed,
                    new(["implementation.txt"]), JsonSerializer.SerializeToElement("source modification executed")));
            }));
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.Client.Call("apply_change", []);
        string runId;
        using (var first = await host.CreateAsync())
        {
            var interrupted = await first.RunAsync("Implement the bounded approved change.");
            Assert.Equal(SquadRunStatus.Failed, interrupted.Status);
            runId = interrupted.RunId;
        }
        using var next = await host.CreateAsync();
        var calls = host.Client.Calls.Count;
        Assert.Equal(SquadRunStatus.ReconciliationRequired, (await next.ResumeAsync(runId)).Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1, executions);
        Assert.Equal(SquadRunStatus.ApprovalRequired, (await next.ReconcileAsync(runId)).Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        host.QueueStage("developer");
        host.QueueStage("tester");

        var completed = await next.ResumeAsync(runId);

        Assert.True(completed.Status == SquadRunStatus.Completed, completed.ResponseText);
        Assert.Equal(runId, completed.RunId);
        Assert.Equal(1, executions);
        Assert.Equal(["research", "plan", "produce", "review"], completed.Evidence.Select(e => e.Stage));
        Assert.Single(Assert.Single(completed.Evidence, e => e.Stage == "produce").ExecutedHostTools!);
    }
}
