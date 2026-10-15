using System.Diagnostics;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeBoundaryTests
{
    private static readonly string[] IntakeStages = ["intake"];
    private static readonly string[] ResearchAndPlanStages = ["research", "plan"];

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

        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText + "\n" + string.Join("\n", host.Log.Messages));
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

        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText + "\n" + string.Join("\n", host.Log.Messages));
        Assert.Equal(0, host.Writes);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredWorkerExecutesWithInheritedOwnershipAndDepthLimit(bool limited)
    {
        using var host = new RuntimeTestHost();
        using var log = new RuntimeTestLogger();
        host.Options.LoggerFactory = log;
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

        Assert.True(result.Status == (limited ? SquadRunStatus.LimitReached : SquadRunStatus.Completed),
            result.ResponseText + "\n" + string.Join("\n", log.Messages));
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreateAsync());

        Assert.Contains("not an isolated role deliverable root", exception.Message, StringComparison.Ordinal);
        Assert.Empty(host.Client.Calls);
        Assert.Equal(0, host.Writes);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
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
        Assert.Equal((int)SquadRunStatus.Failed, state.RootElement.GetProperty("ActiveRun").GetProperty("Status").GetInt32());
        Assert.Equal(1, state.RootElement.GetProperty("ActiveRun").GetProperty("Evidence").GetArrayLength());
        Assert.Equal("suspended", state.RootElement.GetProperty("Checkpoint").GetProperty("Status").GetString());
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
    public async Task MissingCouncilSeatRequiresConfirmedExtensionOrExplicitScopedWaiver()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            ", architect, security, cost-manager, product-owner, rai", "", StringComparison.Ordinal));
        host.Options.Council = new() { Roles = ["architect", "security"] };
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.RosterUpdate);
        };
        host.QueuePipeline(council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("developer");
        host.QueueStage("tester");
        host.Client.Text("Verified delivery with a recorded scoped waiver.");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement an architecture and security source change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var extension = Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.RosterUpdate);
        Assert.Equal(["architect", "security"], extension.Council!.MissingRoles);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "council");
        Assert.Contains("Council Waiver", File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md")),
            StringComparison.Ordinal);
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public async Task UnansweredQuestionRetainsActualMafConversationForNextTurn()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("ask_host", new() { ["question"] = "Which environment?" });
        string runId;
        using (var first = await host.CreateAsync())
        {
            var pending = await first.RunAsync("Implement the original task.");
            Assert.Equal(SquadRunStatus.InputRequired, pending.Status);
            runId = pending.RunId;
        }

        host.Client.Steps.Enqueue((messages, _) =>
        {
            Assert.Contains(messages, m => m.Text.Contains("original task", StringComparison.Ordinal));
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
                c => c.Name == "ask_host");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No pipeline for this history test."));
        });
        using var next = await host.CreateAsync();
        var result = await next.ResumeAsync(runId);
        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText);
    }

    [Theory]
    [Trait("Category", "ReleaseCompatibility")]
    [InlineData("default", false, "interactive")]
    [InlineData("full", true, "interactive")]
    [InlineData("default", false, "autonomous")]
    [InlineData("full", true, "autonomous")]
    [InlineData("default", false, "autopilot")]
    [InlineData("full", true, "autopilot")]
    public async Task ReleasedArtifactTreeRunsWhenExplicitlyProvided(string profile, bool intakeAndCouncil, string mode)
    {
        var path = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_ROOT");
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        using var releaseClient = ReleaseTestHttpClient.Create(
            Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        SquadArtifactSource source;
        if (!string.IsNullOrWhiteSpace(version))
        {
            source = ReleaseTestHttpClient.Source(version, releaseClient);
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
        host.Options.NamingPolicy = "assigned";
        host.Options.Mode = mode;
        if (mode != "interactive")
        {
            host.Options.MaxConcurrentDispatches = 1;
            host.Options.AdditionalRoles.Add("architect");
            host.Options.Tools[0] = host.Options.Tools[0] with { Impact = SquadToolImpact.Safe };
            host.Options.Council = new() { Roles = ["architect"] };
        }
        if (intakeAndCouncil)
        {
            host.Options.Council = new() { Roles = ["architect", "security", "cost-manager", "product-owner"] };
        }
        using var runtime = await SquadRuntime.CreateAsync(host.Client, host.Options, source);
        Assert.Equal("Alpha", runtime.Roster.Resolve("researcher")!.MemberName);
        Assert.Equal("Beta", runtime.Roster.Resolve("lead")!.MemberName);
        Assert.Equal(runtime.Roster.Members.Count,
            runtime.Roster.Members.Select(member => member.MemberName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (runtime.Roster.Members.Count > 24)
        {
            Assert.Contains(runtime.Roster.Members, member => member.MemberName == "Alpha-2");
        }
        var planSkill = Assert.Single(runtime.Artifacts.Skills, s => s.Name == "rpi-plan");
        if (runtime.Provenance.Release?.Tag == SupportedSquadRelease.Current.Tag)
        {
            var squadSkill = Assert.Single(runtime.Artifacts.Skills, skill => skill.Name == "squad");
            var catalog = HveSquad.AgentFramework.Artifacts.SquadModelCatalog.Parse(
                File.ReadAllText(Path.Combine(squadSkill.DirectoryPath, "references", "model-catalog.md")),
                File.ReadAllText(Path.Combine(squadSkill.DirectoryPath, "references", "model-routing.md")));
            var advertised = catalog.Models.Select(model => model.Id).ToArray();
            Assert.Equal("claude-opus-5.5", catalog.Rank(advertised, "research", "default")[0].Id);
            Assert.Equal("gpt-5.6-sol", catalog.Rank(advertised, "planning", "default")[0].Id);
            Assert.Equal("gpt-5.3-codex", catalog.Rank(advertised, "implementation", "default")[0].Id);
            Assert.Equal("gpt-6-sol", catalog.Rank(advertised, "review", "fast")[0].Id);
            Assert.Equal("gpt-5.5", catalog.Rank(advertised, "council", "default")[0].Id);
            Assert.Equal("claude-sonnet-5.5", catalog.Rank(advertised, "intake", "fast")[0].Id);
            Assert.Equal("claude-haiku-4.5", catalog.Rank(advertised, "bookkeeping", "fast")[0].Id);
        }
        host.SinglePlan = !File.Exists(Path.Combine(planSkill.DirectoryPath, "templates", "implementation-details.md"));
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
        if (!host.SinglePlan)
        {
            host.QueueSkillResource("rpi-plan", "templates/implementation-details.md", "P01");
        }
        host.QueueStage("lead");
        var seats = intakeAndCouncil ? new[] { "architect", "security", "cost-manager", "product-owner" } :
            mode == "interactive" ? [] : new[] { "architect" };
        if (seats.Length > 0)
        {
            foreach (var seat in seats)
            {
                host.QueueStage(seat, "Approve");
            }
        }
        host.QueueStage("developer");
        if (mode != "interactive")
        {
            foreach (var seat in seats)
            {
                host.QueueStage(seat, "Approve");
            }
        }
        host.QueueStage("tester");
        host.Client.Text("Coordinator synthesis.");
        var result = await runtime.RunAsync("Implement a small local source change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        string[] expected = intakeAndCouncil
            ? ["intake", "research", "plan", "council", "council", "council", "council", "produce", "review"]
            : ["research", "plan", "produce", "review"];
        if (mode != "interactive")
        {
            expected = (intakeAndCouncil ? IntakeStages : []).Concat(ResearchAndPlanStages)
                .Concat(seats.Select(_ => "council")).Append("produce")
                .Concat(seats.Select(_ => "council")).Append("review").ToArray();
            Assert.Single(result.ModeOutcome!.Iterations);
            Assert.True(result.ModeOutcome.Converged);
            Assert.Equal(mode == "autopilot", result.ModeOutcome.FinalValidated);
            Assert.DoesNotContain(host.Approvals, approval => approval.Kind is SquadApprovalKind.Plan or
                SquadApprovalKind.Implementation or SquadApprovalKind.ConsequentialTool);
        }
        Assert.Equal(expected, result.Evidence.Select(e => e.Stage));
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "squad");
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "rpi-plan");
        Assert.Contains(runtime.Artifacts.Skills, s => s.Name == "rpi-research");
        Assert.Empty(runtime.Artifacts.Warnings);
        foreach (var charter in runtime.Artifacts.Charters)
        {
            foreach (var child in charter.DelegateAgents.Where(name => runtime.Artifacts.FindCharter(name) is null))
            {
                var role = Assert.Single(runtime.Catalog.Roles, r =>
                    string.Equals(r.PrimaryAgent, child, StringComparison.OrdinalIgnoreCase) ||
                    r.AlternateAgents.Contains(child, StringComparer.OrdinalIgnoreCase));
                Assert.Contains(runtime.Catalog.RegisteredResources, resource =>
                    resource.Role == role.Name && resource.Kind == "agent" && resource.Tier == "opt-in" &&
                    !string.IsNullOrWhiteSpace(resource.InstallOrEntry));
            }
        }
        if (!string.IsNullOrWhiteSpace(version))
        {
            Assert.NotNull(runtime.Provenance.Release);
            if (version != "latest")
            {
                Assert.Equal(version, runtime.Provenance.Release.Tag);
            }
        }
        Assert.Equal(host.SinglePlan ? 1 : 2, Assert.Single(result.Evidence, e => e.Stage == "plan").Artifacts!.Count);
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
        await ReleaseTestHttpClient.RecordObservationAsync(runtime, host.Project, result,
            "native-" + profile + "-" + mode, host.Options.SquadRoot);
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), name, arguments)]));
}
