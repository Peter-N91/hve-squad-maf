using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeContractTests
{
    [Theory]
    [InlineData("Ready", 0, true)]
    [InlineData("Ready-With-Gaps", 0, true)]
    [InlineData("Ready", 1, false)]
    [InlineData("Not-Ready", 0, false)]
    public async Task ReturnsOnlyIntakeUsesScribeReceiptAndHonorsReadiness(string verdict, int blockers, bool advances)
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "requirements.txt"), "User-supplied requirements.");
        host.QueuePipeline("technical-writer", inputs: ["requirements.txt"]);
        host.QueueStage("intake-validator", verdict, blockers: blockers);
        if (advances)
        {
            host.QueueStage("researcher");
            host.QueueStage("lead");
            host.QueueStage("technical-writer");
            host.QueueStage("tester");
            host.Client.Text("Done");
        }
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Write the requested document.");
        Assert.True(result.Status == (advances ? SquadRunStatus.Completed : SquadRunStatus.Blocked), result.ResponseText);
        var evidence = result.Evidence[0];
        Assert.Equal(SquadEvidenceKind.StructuredReturn, evidence.EvidenceKind);
        Assert.StartsWith(".copilot-tracking/squad/returns/", evidence.ArtifactPath, StringComparison.Ordinal);
        Assert.Equal("intake-validator", evidence.StructuredReturn!.Contract);
        Assert.Null(evidence.Artifacts);
        Assert.False(Directory.Exists(Path.Combine(host.Project, ".copilot-tracking", "intake-validator")));
        Assert.All(host.Client.Calls.Where(c => c.Options?.Tools?.Any(t => t.Name == "complete_return") == true),
            c => Assert.DoesNotContain(c.Options!.Tools!, t => t.Name == "write_artifact"));
        VerifyHash(host, evidence.ArtifactPath, evidence.ArtifactSha256);
        Assert.Contains(evidence.DispatchId, File.ReadAllText(Path.Combine(host.Project, evidence.HistoryPath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeclaredMissingRootIsNotTreatedAsReturnsOnly()
    {
        using var host = new RuntimeTestHost();
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace(".copilot-tracking/researcher/", "—", StringComparison.Ordinal));
        host.QueuePipeline();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("not a supported released returns-only contract", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task ConfirmedCoordinatorAnswerReachesNextResearcherAndSurvivesContinuation()
    {
        using var host = new RuntimeTestHost();
        host.Options.AskAsync = (_, _) => ValueTask.FromResult<string?>("Use westeurope; local files only.");
        host.Client.Call("ask_host", new() { ["question"] = "Which region and scope?" });
        QueueAnswerAwareRun(host);
        using (var first = await host.CreateAsync())
        {
            var result = await first.RunAsync("Document the original workload.");
            Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        }
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json"))))
        {
            var answer = Assert.Single(state.RootElement.GetProperty("ConfirmedAnswers").EnumerateArray());
            Assert.Equal("Which region and scope?", answer.GetProperty("Question").GetString());
            Assert.Equal("Use westeurope; local files only.", answer.GetProperty("Answer").GetString());
        }
        host.Options.AskAsync = null;
        QueueAnswerAwareRun(host);
        using var next = await host.CreateAsync();
        var continued = await next.RunAsync("Continue with the previous constraints.");
        Assert.True(continued.Status == SquadRunStatus.Completed, continued.ResponseText);
        Assert.Equal(2, host.Approvals.Count(a => a.Kind == SquadApprovalKind.Implementation));
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanCannotAdvanceWithoutRealLinkedPhaseDetails(bool inventedLink)
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.Client.Call("write_artifact", new() { ["content"] = "Plan points to invented-phase-details.md" });
        if (inventedLink)
        {
            host.Client.Call("write_artifact", new() { ["artifactName"] = "phase-details", ["content"] = "Unlinked details." });
        }
        QueueCompletion(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");
        Assert.NotEqual(SquadRunStatus.Completed, result.Status);
        Assert.Contains(inventedLink ? "actual allocated" : "phase-details", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, e => e.Stage is "produce" or "plan");
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task CompanionArtifactsAreHashedAndCannotChangeAcrossApproval()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Implementation)
            {
                var plan = Assert.Single(approval.Evidence!, e => e.Stage == "plan");
                var details = Assert.Single(plan.Artifacts!, a => a.Name == "phase-details");
                VerifyHash(host, details.Path, details.Sha256);
                Assert.Contains(details.Sha256, File.ReadAllText(Path.Combine(host.Project, plan.HistoryPath)), StringComparison.Ordinal);
                File.AppendAllText(Path.Combine(host.Project, details.Path), "tampered");
            }
            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");
        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "produce");
    }

    [Fact]
    public async Task ResearchMayDeclareBoundedEvidenceWithoutProjectWriteTools()
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        QueueFocused(host, "research");
        host.Client.Call("declare_artifact", new() { ["name"] = "lane-one", ["kind"] = "research-evidence" });
        host.Client.Call("write_artifact", new() { ["artifactName"] = "lane-one", ["content"] = "Bounded lane evidence." });
        host.QueueStage("researcher");
        host.Client.Text("Research only.");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Research only.");
        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(2, evidence.Artifacts!.Count);
        var lane = Assert.Single(evidence.Artifacts, a => a.Name == "lane-one");
        Assert.StartsWith(".copilot-tracking/research/subagents/", lane.Path, StringComparison.Ordinal);
        VerifyHash(host, lane.Path, lane.Sha256);
        Assert.DoesNotContain(host.Approvals, a => a.Kind == SquadApprovalKind.ConsequentialTool);
    }

    [Theory]
    [InlineData("../state", "research-evidence")]
    [InlineData("lane", "source")]
    [InlineData("lane", ".copilot-tracking/squad")]
    public async Task ArtifactAllocationCannotEscapeItsDeclaredMethodologyKinds(string name, string kind)
    {
        using var host = new RuntimeTestHost();
        QueueFocused(host, "research");
        host.Client.Call("declare_artifact", new() { ["name"] = name, ["kind"] = kind });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Research only.");
        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task ArtifactCountIsBounded()
    {
        using var host = new RuntimeTestHost();
        host.Options.MaxArtifactsPerDispatch = 2;
        QueueFocused(host, "research");
        host.Client.Call("declare_artifact", new() { ["name"] = "one", ["kind"] = "research-evidence" });
        host.Client.Call("declare_artifact", new() { ["name"] = "two", ["kind"] = "research-evidence" });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Research only.");
        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("artifact limit", result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData("../source.cs")]
    [InlineData(".copilot-tracking/squad/state.json")]
    public async Task ArtifactNamesNeverGrantFilesystemPaths(string name)
    {
        using var host = new RuntimeTestHost();
        QueueFocused(host, "research");
        host.Client.Call("write_artifact", new() { ["artifactName"] = name, ["content"] = "Cannot write here." });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Research only.");
        Assert.Equal(SquadRunStatus.Failed, result.Status);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
        Assert.Equal("hve-squad-maf/native-single-squad/v1", state.RootElement.GetProperty("Schema").GetString());
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IacRequiresActualSourceOutputNotJustAChangeRecord(bool registered, bool correctRoot)
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        var calls = 0;
        var path = correctRoot ? "infra/bicep/sample/main.bicep" : "docs/claimed-iac.md";
        if (registered)
        {
            host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
            {
                calls++;
                var file = Path.Combine(host.Project, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "targetScope = 'resourceGroup'");
                return "Local source file written; no deployment.";
            }, "write_iac"), "edit/project", ["iac-author"], SquadToolEffect.ProjectWrite, _ => new([path])));
        }
        host.QueuePipeline("iac-author");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (registered)
        {
            host.Client.Call("write_iac", []);
        }
        host.QueueStage("iac-author");
        if (registered && correctRoot)
        {
            host.QueueStage("tester");
            host.Client.Text("Source authoring completed.");
        }
        var release = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_ROOT");
        host.Options.Profile = string.IsNullOrWhiteSpace(release) ? "default" : "azure";
        using var runtime = string.IsNullOrWhiteSpace(release) ? await host.CreateAsync() :
            await SquadRuntime.CreateAsync(host.Client, host.Options, new DirectoryArtifactSource(ProjectArtifactSource.ForProject(release).Roots));
        var result = await runtime.RunAsync("Author local IaC source, do not deploy.");
        Assert.True(result.Status == (registered && correctRoot ? SquadRunStatus.Completed : SquadRunStatus.Blocked), result.ResponseText);
        Assert.Equal(registered ? 1 : 0, calls);
        if (registered && correctRoot)
        {
            var receipt = Assert.Single(Assert.Single(result.Evidence, e => e.Stage == "produce").ExecutedHostTools!);
            var output = Assert.Single(receipt.Outputs!);
            Assert.Equal(path, output.Path);
            Assert.Equal(output.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(host.Project, output.Path)))));
            Assert.Contains("not deployed", result.ResponseText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("ProjectWrite output receipt", result.ResponseText, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        }
        if (!string.IsNullOrWhiteSpace(release))
        {
            Assert.Equal(".copilot-tracking/changes/", runtime.Roster.Resolve("iac-author")!.DeliverableRoot);
        }
    }

    [Fact]
    public async Task HostFunctionCallWithoutOutputAttestationDoesNotProveImplementation()
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools[0] = host.Options.Tools[0] with { OutputEvidence = null };
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(1, host.Writes);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
    }

    [Theory]
    [InlineData("deployer", false)]
    [InlineData("deployer", true)]
    [InlineData("asbuilt-author", false)]
    [InlineData("asbuilt-author", true)]
    public async Task ReturnsCannotImpersonateExternalExecutionOrObservation(string role, bool attested)
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        var effect = role == "deployer" ? SquadToolEffect.External : SquadToolEffect.ExternalRead;
        var calls = 0;
        if (attested)
        {
            host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
            {
                calls++;
                return "Credential-free local test double, no real deployment or cloud access.";
            }, "host_operation"), "host/operation", [role], effect, _ => new([], "Test host attests a simulated operation only.")));
        }
        host.QueuePipeline(role);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (attested)
        {
            host.Client.Call("host_operation", []);
        }
        host.QueueStage(role);
        if (attested)
        {
            host.QueueStage("tester");
            host.Client.Text("Simulated host test completed.");
        }
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Perform the requested bounded role work.");
        Assert.True(result.Status == (attested ? SquadRunStatus.Completed : SquadRunStatus.Blocked), result.ResponseText);
        Assert.Equal(attested ? 1 : 0, calls);
        var evidence = Assert.Single(result.Evidence, e => e.Stage == "produce");
        Assert.Equal(SquadEvidenceKind.StructuredReturn, evidence.EvidenceKind);
        if (attested)
        {
            Assert.NotNull(Assert.Single(evidence.ExecutedHostTools!).Receipt);
            Assert.Contains("not deployed", result.ResponseText, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        }
    }

    [Fact]
    public async Task MerelyRegisteringAnOptionalExternalToolDoesNotRequireAnExternalAction()
    {
        using var host = new RuntimeTestHost();
        var calls = 0;
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => ++calls, "optional_publish"),
            "host/publish", ["developer"], SquadToolEffect.External));
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a local source change only.");
        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HostMayRequireOutputEvidenceForAnAdditionalProducingContract()
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() => "No real write.", "publish_document"),
            "edit/project", ["technical-writer"], SquadToolEffect.ProjectWrite, RequireOutputForCompletion: true));
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Publish a document using the configured host.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("ProjectWrite output receipt", result.ResponseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("research", SquadRunStatus.ResearchCompleted)]
    [InlineData("plan", SquadRunStatus.PlanCompleted)]
    [InlineData("review", SquadRunStatus.ReviewCompleted)]
    public async Task FocusedStagesDoNotForceADeveloperOrClaimFullDelivery(string stage, SquadRunStatus expected)
    {
        using var host = new RuntimeTestHost();
        host.Options.Tools.Clear();
        File.WriteAllText(Path.Combine(host.Project, "input.txt"), "Existing evidence for the scoped request.");
        QueueFocused(host, stage, ["input.txt"]);
        if (stage == "plan")
        {
            host.QueueStage("intake-validator", "Ready");
        }
        host.QueueStage(stage == "research" ? "researcher" : stage == "plan" ? "lead" : "tester");
        host.Client.Text("Focused scope complete.");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync($"{stage} only using existing inputs.");
        Assert.True(result.Status == expected, result.ResponseText);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "produce");
        Assert.DoesNotContain(host.Approvals, a => a.Kind == SquadApprovalKind.Implementation);
        Assert.Contains("No implementation, full delivery", result.ResponseText, StringComparison.Ordinal);
        var completed = result.Evidence.Last(e => e.Stage == stage);
        Assert.Contains(completed.Summary, result.ResponseText, StringComparison.Ordinal);
        Assert.All(result.Evidence, e => Assert.Contains(e.ArtifactPath, result.ResponseText, StringComparison.Ordinal));
        Assert.All(completed.Artifacts!, a => Assert.Contains(a.Path, result.ResponseText, StringComparison.Ordinal));
        Assert.Equal(stage == "plan", host.Approvals.Any(a => a.Kind == SquadApprovalKind.Plan));
        Assert.NotEqual(SquadRunScope.Delivery, result.Scope);
        Assert.Empty(host.Client.Steps);
    }

    [Fact]
    public async Task PlanOnlyWithoutSuppliedEvidenceResearchesFirstThenStopsBeforeProduction()
    {
        using var host = new RuntimeTestHost();
        QueueFocused(host, "plan");
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.Client.Text("Plan ready, no implementation.");
        var release = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_ROOT");
        using var runtime = string.IsNullOrWhiteSpace(release) ? await host.CreateAsync() :
            await SquadRuntime.CreateAsync(host.Client, host.Options, new DirectoryArtifactSource(ProjectArtifactSource.ForProject(release).Roots));
        var result = await runtime.RunAsync("Plan only for a local source change.");
        Assert.True(result.Status == SquadRunStatus.PlanCompleted, result.ResponseText);
        Assert.Equal(["research", "plan"], result.Evidence.Select(e => e.Stage));
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(host.Approvals, a => a.Kind == SquadApprovalKind.Implementation);
    }

    private static void QueueAnswerAwareRun(RuntimeTestHost host)
    {
        host.QueuePipeline("technical-writer");
        host.Client.Steps.Enqueue((messages, options) =>
        {
            Assert.Contains("Synthetic agent for researcher.", options!.Instructions, StringComparison.Ordinal);
            var actualDispatch = messages.Last(m => m.Role == ChatRole.User).Text;
            Assert.Contains("role=researcher", actualDispatch, StringComparison.Ordinal);
            Assert.Contains("Which region and scope?", actualDispatch, StringComparison.Ordinal);
            Assert.Contains("Use westeurope; local files only.", actualDispatch, StringComparison.Ordinal);
            Assert.Contains("original workload", actualDispatch, StringComparison.Ordinal);
            return RuntimeTestHost.Function("list_artifacts", []);
        });
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        host.Client.Text("Done");
    }

    private static void QueueFocused(RuntimeTestHost host, string stage, string[]? inputs = null) =>
        host.Client.Call("run_focused", new()
        {
            ["stage"] = stage, ["inputPaths"] = inputs ?? [], ["requiresCouncil"] = false, ["includesAiRisk"] = false,
        });

    private static void QueueCompletion(RuntimeTestHost host) => host.Client.Call("complete_stage", new()
    {
        ["summary"] = "Plan completed", ["verdict"] = "Completed", ["risk"] = "Low", ["blockingIssues"] = 0,
    });

    private static void VerifyHash(RuntimeTestHost host, string path, string sha) =>
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(host.Project, path))))));
}
