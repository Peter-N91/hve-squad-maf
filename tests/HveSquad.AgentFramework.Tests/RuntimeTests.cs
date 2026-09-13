using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task RunsRealMafFunctionsAndAgentsInEvidenceGatedOrder()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement a small source change.");

        Assert.Equal(SquadRunStatus.Completed, result.Status);
        Assert.Equal(["research", "plan", "produce", "review"], result.Evidence.Select(e => e.Stage));
        Assert.Equal(1, host.Writes);
        Assert.Equal("apply_change", Assert.Single(result.Evidence[2].ExecutedHostTools!).Name);
        Assert.All(result.Evidence, e =>
        {
            Assert.True(File.Exists(Path.Combine(host.Project, e.ArtifactPath)));
            Assert.Contains(e.DispatchId, File.ReadAllText(Path.Combine(host.Project, e.HistoryPath)), StringComparison.Ordinal);
            Assert.Equal("unknown", e.Model);
        });
        Assert.Equal([SquadApprovalKind.Initialization, SquadApprovalKind.Routing, SquadApprovalKind.Plan,
            SquadApprovalKind.Implementation, SquadApprovalKind.ConsequentialTool], host.Approvals.Select(a => a.Kind));
        Assert.Empty(host.Client.Steps);
        Assert.False(host.Client.Disposed);
        Assert.Equal("v0.16.2", runtime.Provenance.Release?.Tag);
        Assert.Contains("apm-renamed-squad", runtime.ArtifactPathMap["skill:squad"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationWithoutApprovalMakesNoWritesOrModelCalls(bool deny)
    {
        using var host = new RuntimeTestHost();
        host.Options.ApproveAsync = deny ? (_, _) => ValueTask.FromResult(false) : null;
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(deny ? SquadRunStatus.ApprovalDenied : SquadRunStatus.ApprovalRequired, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task InitializationConfirmsProfileNamingAndChannelExplicitly()
    {
        using var host = new RuntimeTestHost();
        host.Options.MemberNames["researcher"] = "Robin";
        host.Options.NamingPolicy = "provided";
        host.Options.ApprovalChannel = "in-chat";
        host.Client.Text("No dispatch");
        using var runtime = await host.CreateAsync();
        await runtime.RunAsync("Document a topic.");

        var proposal = host.Approvals[0].Initialization!;
        Assert.Equal("default", proposal.Profile);
        Assert.Equal("provided", proposal.NamingPolicy);
        Assert.Equal("Robin", proposal.MemberNames["researcher"]);
        Assert.Equal("in-chat", proposal.ApprovalChannel);
        Assert.Equal("single-squad", proposal.Scope);
    }

    [Fact]
    public async Task ProseWithoutArtifactDoesNotAdvance()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline();
        host.Client.Text("Research done, trust me.");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
        Assert.Equal(2, host.Client.Calls.Count);
    }

    [Fact]
    public async Task DeclinedRoutingDoesNotDispatchAnyRole()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        host.Options.ApproveAsync = (approval, _) => ValueTask.FromResult(approval.Kind != SquadApprovalKind.Routing);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
        Assert.Single(host.Client.Calls);
    }

    [Fact]
    public async Task MissingHistoryStopsBeforeNextStage()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                foreach (var path in Directory.EnumerateFiles(Path.Combine(host.Project, ".copilot-tracking", "squad", "history")))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.FromResult(true);
        };
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.NotEqual(SquadRunStatus.Completed, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.Equal("research", Assert.Single(result.Evidence).Stage);
    }

    [Fact]
    public async Task DenyingConsequentialToolNeverExecutesIt()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        host.Options.ApproveAsync = (approval, _) =>
            ValueTask.FromResult(approval.Kind != SquadApprovalKind.ConsequentialTool);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.Equal(0, host.Writes);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
        Assert.Equal(["research", "plan"], result.Evidence.Select(e => e.Stage));
    }

    [Fact]
    public async Task RosterAndDeclaredDelegationAreEnforced()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline("not-on-roster");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Produce something.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("off-roster", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDisabledRosterAgentFailsBeforeAnyExecution(bool disabled)
    {
        using var host = new RuntimeTestHost();
        if (disabled)
        {
            host.WriteCharter("researcher", disabled: true);
        }
        else
        {
            File.Delete(Path.Combine(host.Artifacts, "agents", "researcher.agent.md"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreateAsync());
        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task CouncilHighRiskStopsBeforeProduction()
    {
        using var host = new RuntimeTestHost();
        host.QueuePipeline(council: true);
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("architect", "Approve");
        host.QueueStage("security", "Approve", "High");
        host.QueueStage("cost-manager", "Approve");
        host.QueueStage("product-owner", "Approve");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a change.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("Council verdict Stop", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "produce");
        Assert.Contains("Stop", File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "decisions.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnansweredQuestionStopsRatherThanInventingInput()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("ask_host", new() { ["question"] = "Which environment?" });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Contains("Which environment?", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".copilot-tracking/../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("safe.txt:stream")]
    public async Task ModelCannotReadEscapingPaths(string path)
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("project_read", new() { ["path"] = path });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Read a file.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task SessionPersistsActualMafHistoryAndRefusesDifferentRelease()
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess("technical-writer");
        using (var first = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.Completed, (await first.RunAsync("Write documentation for alpha.")).Status);
        }

        var initialCalls = host.Client.Calls.Count;
        host.QueueSuccess("technical-writer");
        using (var resumed = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.Completed, (await resumed.RunAsync("Now extend it for beta.")).Status);
        }

        Assert.Contains(host.Client.Calls[initialCalls].Messages, m => m.Text.Contains("alpha", StringComparison.Ordinal));
        Assert.Equal(1, host.Approvals.Count(a => a.Kind == SquadApprovalKind.Initialization));
        var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
        using (state)
        {
            Assert.Equal(2, state.RootElement.GetProperty("Runs").GetArrayLength());
            Assert.NotEmpty(state.RootElement.GetProperty("Sessions").EnumerateObject());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => host.CreateAsync("v0.16.3"));
    }

    [Fact]
    public async Task ArtifactChangesCannotHotSwapAnExistingRuntime()
    {
        using var host = new RuntimeTestHost();
        using var runtime = await host.CreateAsync();
        host.Write("instructions/squad/new.instructions.md", "A changed rule.");

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.RunAsync("Implement it."));
        Assert.Empty(host.Client.Calls);
    }

    [Theory]
    [InlineData("dispatch")]
    [InlineData("model")]
    [InlineData("tool")]
    public async Task LimitsStopTheRun(string limit)
    {
        using var host = new RuntimeTestHost();
        host.QueueSuccess();
        if (limit == "dispatch")
        {
            host.Options.MaxDispatches = 1;
        }
        else if (limit == "model")
        {
            host.Options.MaxModelCalls = 1;
        }
        else
        {
            host.Options.MaxToolCalls = 1;
        }

        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement it.");

        Assert.Equal(SquadRunStatus.LimitReached, result.Status);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsSuccess()
    {
        using var host = new RuntimeTestHost();
        using var runtime = await host.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync("Implement it.", cancellation.Token));
        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Theory]
    [InlineData("mode=autopilot do everything")]
    [InlineData("mode=watch")]
    [InlineData("/squad-federation")]
    public async Task UnsupportedModesAreExplicitAndHaveNoSideEffects(string request)
    {
        using var host = new RuntimeTestHost();
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync(request);

        Assert.Equal(SquadRunStatus.Unsupported, result.Status);
        Assert.Empty(host.Client.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task DynamicInstructionsReferencesAndActualSkillToolsAreAttached()
    {
        using var host = new RuntimeTestHost();
        using var log = new RuntimeTestLogger();
        host.Options.LoggerFactory = log;
        host.Client.Steps.Enqueue((messages, options) =>
        {
            var prompt = (options?.Instructions ?? "") + string.Join("\n", messages.Select(m => m.Text));
            Assert.Contains("SYNTHETIC_NEW_RULE", prompt, StringComparison.Ordinal);
            Assert.Contains("SYNTHETIC_REFERENCE_gates-and-modes", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("IRRELEVANT_RULE", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("SPECIALIST_BODY", prompt, StringComparison.Ordinal);
            var load = Assert.Single(options!.Tools!.OfType<AIFunction>(), t => t.Name == AgentSkillsProvider.LoadSkillToolName);
            Assert.DoesNotContain(options.Tools!, t => t.Name == AgentSkillsProvider.RunSkillScriptToolName);
            var argument = load.JsonSchema.GetProperty("properties").EnumerateObject().First().Name;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("load-squad", load.Name, new Dictionary<string, object?> { [argument] = "squad" })]));
        });
        host.Client.Steps.Enqueue((messages, _) =>
        {
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                r => r.Result?.ToString()?.Contains("SYNTHETIC_SQUAD_SKILL", StringComparison.Ordinal) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No dispatch in this binding test."));
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Inspect capabilities.");

        Assert.True(result.Status == SquadRunStatus.Blocked, result.ResponseText + "\n" + string.Join("\n", log.Messages));
        Assert.Equal(2, host.Client.Calls.Count);
    }
}
