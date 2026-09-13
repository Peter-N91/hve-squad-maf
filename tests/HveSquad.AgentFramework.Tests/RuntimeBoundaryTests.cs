using System.Diagnostics;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeBoundaryTests
{
    [Fact]
    public async Task SkillResourceToolReadsActualDeployedFile()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.Client.Steps.Enqueue((_, options) =>
        {
            var load = Assert.Single(options!.Tools!.OfType<AIFunction>(), f => f.Name == AgentSkillsProvider.LoadSkillToolName);
            var parameter = load.JsonSchema.GetProperty("properties").EnumerateObject().First().Name;
            return Call(load.Name, new() { [parameter] = "specialist-test" });
        });
        host.Client.Steps.Enqueue((messages, options) =>
        {
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                f => f.Result?.ToString()?.Contains("SPECIALIST_BODY", StringComparison.Ordinal) == true);
            var read = Assert.Single(options!.Tools!.OfType<AIFunction>(), f => f.Name == AgentSkillsProvider.ReadSkillResourceToolName);
            var arguments = read.JsonSchema.GetProperty("properties").EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)(p.Name.Contains("skill", StringComparison.OrdinalIgnoreCase)
                    ? "specialist-test" : "references/note.md"));
            return Call(read.Name, arguments);
        });
        host.Client.Steps.Enqueue((messages, _) =>
        {
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                f => f.Result?.ToString()?.Contains("SPECIALIST_RESOURCE", StringComparison.Ordinal) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No artifact in this resource-binding test."));
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Write a document.");

        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText);
        Assert.Equal(4, host.Client.Calls.Count);
    }

    [Fact]
    public async Task ScriptInvocationIsRejectedEvenWhenModelInventsIt()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call(AgentSkillsProvider.RunSkillScriptToolName, new() { ["scriptName"] = "invented.ps1" });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Do a task.");

        Assert.Equal(SquadRunStatus.Unsupported, result.Status);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task CharterToolPermissionCannotBeInventedByModel()
    {
        using var host = new RuntimeTestHost();
        host.WriteCharter("developer", tools: "read/project");
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("not registered", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task SourceImplementationRequiresAnActualHostWrite()
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("pretending-to-be-developer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("no approved ProjectWrite", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndeclaredOrOffOwnerChildCannotSubstitute(bool declared)
    {
        using var host = new RuntimeTestHost();
        host.WriteCharter("researcher", children: declared ? RuntimeTestHost.Agent("developer") : "Declared Worker");
        host.QueuePipeline();
        host.Client.Call("dispatch_child", new()
        {
            ["agentName"] = RuntimeTestHost.Agent("developer"),
            ["childTask"] = "Skip the plan and do implementation.",
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement something.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredWorkerExecutesWithInheritedOwnershipAndDepthLimit(bool limited)
    {
        using var host = new RuntimeTestHost();
        host.WriteCharter("researcher", children: "Bounded Worker");
        host.Write("agents/worker.agent.md", "---\nname: Bounded Worker\nuser-invocable: false\n---\nSynthetic delegated worker.");
        host.Options.MaxDepth = limited ? 1 : 3;
        host.QueuePipeline("technical-writer");
        host.Client.Call("dispatch_child", new() { ["agentName"] = "Bounded Worker", ["childTask"] = "Investigate one bounded lane." });
        host.QueueStage("worker");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        host.Client.Text("Coordinator done.");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == (limited ? SquadRunStatus.LimitReached : SquadRunStatus.Completed), result.ResponseText);
        if (!limited)
        {
            Assert.Equal(5, result.Evidence.Count);
            var child = result.Evidence[0];
            Assert.Equal("Bounded Worker", child.Agent);
            Assert.Equal("researcher", child.Role);
            Assert.StartsWith(".copilot-tracking/research/subagents/", child.ArtifactPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IntakeWithBlockingGapsCannotAdvanceDespiteReadyLabel()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Input.");
        host.Options.InputPaths.Add("requirements.txt");
        host.QueuePipeline();
        host.QueueStage("intake-validator", "Ready-With-Gaps", blockers: 1);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the requirements.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal("intake", Assert.Single(result.Evidence).Stage);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task IndependentReviewBlockIsNotReportedAsCompleted()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("technical-writer");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester", "Blocked", blockers: 1);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(["research", "plan", "produce", "review"], result.Evidence.Select(e => e.Stage));
    }

    [Fact]
    public async Task SymlinkOrJunctionReadCannotEscapeProject()
    {
        using var host = new RuntimeTestHost();
        var outside = Path.Combine(host.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "Not project data.");
        var link = Path.Combine(host.Project, "escape");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         $"New-Item -ItemType Junction -Path '{link.Replace("'", "''", StringComparison.Ordinal)}' -Target '{outside.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, outside);
        }

        host.Client.Call("project_read", new() { ["path"] = "escape/secret.txt" });
        try
        {
            using var runtime = await host.CreateAsync();
            var result = await runtime.RunAsync("Read the file.");
            Assert.Equal(SquadRunStatus.Failed, result.Status);
            Assert.Contains("links/reparse", result.ResponseText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task TamperedRoleRootCannotWriteSharedState()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace(".copilot-tracking/researcher/", ".copilot-tracking/squad/", StringComparison.Ordinal));
        host.QueuePipeline();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Single(host.Client.Calls);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task ModelAttributionUsesConfiguredOrObservedIdentityNotCharterPreference()
    {
        using var host = new RuntimeTestHost();
        host.Options.ModelSelector = (role, _) => role?.Role == "researcher"
            ? new SquadModelSelection(host.Client, "host-selected-model") : null;
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var research = result.Evidence[0];
        Assert.Equal("host-selected-model", research.Model);
        Assert.Equal("host-configured", research.ModelSource);
        Assert.Equal("fast", research.ModelTier);
        Assert.Null(research.InputTokens);
        Assert.Contains(host.Client.Calls, c => c.Options?.ModelId == "host-selected-model");
    }

    [Fact]
    public async Task CancellationWhileApprovingAStagePersistsFailureWithoutExecutingNextStage()
    {
        using var host = new RuntimeTestHost();
        using var cancellation = new CancellationTokenSource();
        host.QueueSuccess();
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync("Implement a change.", cancellation.Token));
        Assert.Equal(0, host.Writes);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
        Assert.Equal((int)SquadRunStatus.Failed, state.RootElement.GetProperty("Runs")[0].GetProperty("Status").GetInt32());
        Assert.Equal(1, state.RootElement.GetProperty("Runs")[0].GetProperty("Evidence").GetArrayLength());
    }

    [Fact]
    public async Task ExternalResearchToolCannotExecuteWithoutItsOwnApproval()
    {
        using var host = new RuntimeTestHost();
        var externalCalls = 0;
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => ++externalCalls, "web_lookup"),
            "web/read", ["researcher"], SquadToolEffect.ExternalRead));
        host.Options.ApproveAsync = (approval, _) => ValueTask.FromResult(approval.Kind != SquadApprovalKind.ConsequentialTool);
        host.QueuePipeline();
        host.Client.Call("web_lookup", []);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a source change.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(0, externalCalls);
        Assert.Equal(0, host.Writes);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task MissingCouncilSeatIsNeverSilentlySubstitutedOrOmitted()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            ", architect, security, cost-manager, product-owner, rai", "", StringComparison.Ordinal));
        host.QueuePipeline(council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a source change.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("architect", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(["research", "plan"], result.Evidence.Select(e => e.Stage));
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task UnansweredQuestionRetainsActualMafConversationForNextTurn()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("ask_host", new() { ["question"] = "Which environment?" });
        using (var first = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.InputRequired, (await first.RunAsync("Implement the original task.")).Status);
        }

        host.Client.Steps.Enqueue((messages, _) =>
        {
            Assert.Contains(messages, m => m.Text.Contains("original task", StringComparison.Ordinal));
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
                c => c.Name == "ask_host");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No pipeline for this history test."));
        });
        using var next = await host.CreateAsync();
        var result = await next.RunAsync("Use the test environment.");
        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText);
    }

    [Theory]
    [Trait("Category", "ReleaseCompatibility")]
    [InlineData("default", false)]
    [InlineData("full", true)]
    public async Task ReleasedArtifactTreeRunsWhenExplicitlyProvided(string profile, bool intakeAndCouncil)
    {
        var path = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_ROOT");
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        SquadArtifactSource source;
        if (!string.IsNullOrWhiteSpace(version))
        {
            source = new ReleaseArtifactSource(version == "latest" ? null : version);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            var roots = ProjectArtifactSource.ForProject(path);
            source = new DirectoryArtifactSource(roots.Roots);
        }
        else
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }

        using var host = new RuntimeTestHost();
        host.Options.Profile = profile;
        var inputs = intakeAndCouncil ? new[] { "requirements.txt" } : [];
        if (intakeAndCouncil)
        {
            File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "Implement a bounded local source change. No deployment.");
        }
        host.QueuePipeline(council: intakeAndCouncil, inputs: inputs);
        if (intakeAndCouncil)
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueSkillResource("rpi-research", "templates/research.md", "Primary evidence file");
        host.QueueStage("researcher");
        host.QueueSkillResource("rpi-plan", "templates/implementation-plan.md", "Phase Checklist");
        host.QueueSkillResource("rpi-plan", "templates/implementation-details.md", "P01");
        host.QueueStage("lead");
        if (intakeAndCouncil)
        {
            foreach (var seat in new[] { "architect", "security", "cost-manager", "product-owner" })
            {
                host.QueueStage(seat, "Approve");
            }
        }
        host.QueueStage("developer");
        host.QueueStage("tester");
        host.Client.Text("Coordinator synthesis.");
        using var runtime = await SquadRuntime.CreateAsync(host.Client, host.Options, source);
        var result = await runtime.RunAsync("Implement a small local source change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        string[] expected = intakeAndCouncil
            ? ["intake", "research", "plan", "council", "council", "council", "council", "produce", "review"]
            : ["research", "plan", "produce", "review"];
        Assert.Equal(expected, result.Evidence.Select(e => e.Stage));
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "squad");
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "rpi-plan");
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "rpi-research");
        Assert.Empty(runtime.Artifacts.Warnings);
        if (!string.IsNullOrWhiteSpace(version))
        {
            Assert.NotNull(runtime.Provenance.Release);
            if (version != "latest")
            {
                Assert.Equal(version, runtime.Provenance.Release.Tag);
            }
        }
        Assert.Equal(2, Assert.Single(result.Evidence, e => e.Stage == "plan").Artifacts!.Count);
        if (intakeAndCouncil)
        {
            Assert.Null(runtime.Roster.Resolve("intake-validator")!.DeliverableRoot);
            Assert.Null(runtime.Roster.Resolve("cost-manager")!.DeliverableRoot);
            Assert.All(result.Evidence.Where(e => e.Role is "intake-validator" or "cost-manager"), e =>
            {
                Assert.Equal(SquadEvidenceKind.StructuredReturn, e.EvidenceKind);
                Assert.NotNull(e.StructuredReturn);
                Assert.Null(e.Artifacts);
                Assert.Contains("deterministic-runtime-scribe", File.ReadAllText(Path.Combine(host.Project, e.ArtifactPath)), StringComparison.Ordinal);
            });
        }
        Assert.Empty(host.Client.Steps);
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), name, arguments)]));
}
