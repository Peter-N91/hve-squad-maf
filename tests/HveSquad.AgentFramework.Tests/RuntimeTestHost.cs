using System.Globalization;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Tests;

/// <summary>Synthetic protocol documents, not copied upstream content. No network or paid model.</summary>
internal sealed class RuntimeTestHost : IDisposable
{
    internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "runtime-test-data", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    internal string Project => Path.Combine(Root, "project");
    internal string Artifacts => Path.Combine(Root, "artifacts");
    internal ScriptedRuntimeChatClient Client { get; } = new();
    internal List<SquadApprovalRequest> Approvals { get; } = [];
    internal SquadRuntimeOptions Options { get; }
    internal int Writes { get; private set; }

    internal RuntimeTestHost()
    {
        Directory.CreateDirectory(Project);
        Options = new SquadRuntimeOptions
        {
            ProjectPath = Project,
            ApproveAsync = (request, _) =>
            {
                Approvals.Add(request);
                return ValueTask.FromResult(true);
            },
            Tools =
            [
                new SquadToolRegistration(AIFunctionFactory.Create(() =>
                {
                    Writes++;
                    File.WriteAllText(Path.Combine(Project, "implementation.txt"), "actual host write");
                    return "Source modification executed.";
                }, "apply_change", "A trusted test source writer."), "edit/project", ["developer"], SquadToolEffect.ProjectWrite,
                    _ => new(["implementation.txt"])),
            ],
        };
        var roles = new[]
        {
            "researcher", "lead", "developer", "tester", "scribe", "technical-writer",
            "architect", "security", "cost-manager", "product-owner", "rai", "intake-validator", "iac-author", "deployer", "asbuilt-author",
        };
        foreach (var role in roles)
        {
            WriteCharter(role);
        }

        Write("agents/coordinator.agent.md",
            "---\nname: Squad Coordinator\nuser-invocable: true\ndisable-model-invocation: true\nagents:\n" +
            string.Join("\n", roles.Select(r => $"  - {Agent(r)}")) +
            "\n---\nSynthetic coordinator. Classify and dispatch only.\n");
        var catalog = """
            # Synthetic cast
            ## Cast Catalog
            | Role | Primary Agent (`name:`) | Alternate Agents (`name:`) | Selection Cue | Deliverable Root |
            | --- | --- | --- | --- | --- |
            """;
        foreach (var role in roles)
        {
            var root = ReturnsOnly(role) ? "—" : $".copilot-tracking/{role}/";
            catalog += $"\n| {role} | {Agent(role)} | — | testing only | {root} |";
        }

        catalog += """

            ## Squad Profiles
            | Profile | Members (roles) | Choose when |
            | --- | --- | --- |
            """;
        catalog += "\n| default | " + string.Join(", ", roles) + " | tests |\n";
        catalog += """

            ## Registered Packs
            | Pack | Adds (roles) | Choose when |
            | --- | --- | --- |
            | test-pack | technical-writer | tests |
            """;
        Write("instructions/squad/squad-roster.instructions.md", catalog);
        Write("instructions/squad/squad-new-rule.instructions.md", "---\napplyTo: '**'\n---\nSYNTHETIC_NEW_RULE");
        Write("instructions/irrelevant.instructions.md", "---\napplyTo: '**/*.other'\n---\nIRRELEVANT_RULE");
        Write("skills/apm-renamed-squad/SKILL.md", "---\nname: squad\ndescription: Synthetic orchestration skill\n---\nSYNTHETIC_SQUAD_SKILL");
        foreach (var reference in new[] { "00-index", "profiles-and-packs", "operating-procedure", "gates-and-modes", "seed-templates", "scribe-procedure", "entry-schemas" })
        {
            Write($"skills/apm-renamed-squad/references/{reference}.md", $"SYNTHETIC_REFERENCE_{reference}");
        }

        Write("skills/apm-renamed-squad/references/seed-templates.md",
            "# Synthetic seed\n## Members\n| Role | Agent Name | Model Tier |\n| --- | --- | --- |\n" +
            string.Join("\n", roles.Select(r => $"| {r} | {Agent(r)} | fast |")));
        Write("skills/apm-renamed-specialist/SKILL.md", "---\nname: specialist-test\ndescription: Synthetic specialist skill\n---\nSPECIALIST_BODY");
        Write("skills/apm-renamed-specialist/references/note.md", "SPECIALIST_RESOURCE");
    }

    internal static string Agent(string role) => role == "scribe" ? "Squad Scribe" : $"Test {role}";

    internal Task<SquadRuntime> CreateAsync(string tag = "v0.16.2") =>
        SquadRuntime.CreateAsync(Client, Options, new TestSource(Artifacts, tag));

    internal void WriteCharter(string role, bool disabled = false, string? children = null, string? tools = null) =>
        Write($"agents/{role}.agent.md",
            $"---\nname: {Agent(role)}\nuser-invocable: false\ndisable-model-invocation: {disabled.ToString().ToLowerInvariant()}\n" +
            (children is null ? "" : $"agents:\n  - {children}\n") +
            (tools is null ? "" : $"tools:\n  - {tools}\n") +
            $"---\nSynthetic agent for {role}. Complete only its assigned stage.\n");

    internal void Write(string relative, string content)
    {
        var path = Path.Combine(Artifacts, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal void QueuePipeline(string producer = "developer", bool council = false, string[]? inputs = null)
    {
        Client.Call("run_pipeline", new()
        {
            ["producingRole"] = producer,
            ["inputPaths"] = inputs ?? [],
            ["requiresCouncil"] = council,
            ["includesAiRisk"] = false,
        });
    }

    internal void QueueStage(string role, string verdict = "Completed", string risk = "Low", int blockers = 0)
    {
        if (role == "developer")
        {
            Client.Call("apply_change", []);
        }

        if (!ReturnsOnly(role))
        {
            if (role == "lead")
            {
                QueuePlanArtifacts();
            }
            else
            {
                Client.Call("write_artifact", new() { ["content"] = $"Synthetic {role} evidence." });
            }
        }
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = $"{role} actual result",
            ["verdict"] = verdict,
            ["risk"] = risk,
            ["blockingIssues"] = blockers,
        };
        if (ReturnsOnly(role))
        {
            arguments["payload"] = role == "intake-validator"
                ? new { findings = Array.Empty<string>(), readiness = verdict }
                : role == "cost-manager"
                    ? (object)new { estimated_monthly_usd = (decimal?)null, assumptions = new[] { "No paid pricing lookup; scripted protocol test." } }
                    : new { findings = $"{role} scripted findings" };
        }
        Client.Call(ReturnsOnly(role) ? "complete_return" : "complete_stage", arguments);
        Client.Text($"{role} finished");
    }

    private static bool ReturnsOnly(string role) => role is "intake-validator" or "cost-manager" or "deployer" or "asbuilt-author";

    internal void QueuePlanArtifacts()
    {
        Client.Call("list_artifacts", []);
        string? primary = null;
        string? details = null;
        Client.Steps.Enqueue((messages, _) =>
        {
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result!;
            using var manifest = System.Text.Json.JsonDocument.Parse(result.ToString()!);
            primary = manifest.RootElement.GetProperty("Outputs").EnumerateArray().Single(a => a.GetProperty("Name").GetString() == "primary").GetProperty("Path").GetString();
            details = manifest.RootElement.GetProperty("Outputs").EnumerateArray().Single(a => a.GetProperty("Name").GetString() == "phase-details").GetProperty("Path").GetString();
            return Function("write_artifact", new() { ["content"] = $"Plan with actual phase details: [{details}]({details})." });
        });
        Client.Steps.Enqueue((_, _) => Function("write_artifact", new()
        {
            ["artifactName"] = "phase-details", ["content"] = $"P01: bounded task. Parent plan: [{primary}]({primary}).",
        }));
    }

    internal void QueueSkillResource(string skillName, string resourcePath, string expectedContent)
    {
        Client.Steps.Enqueue((_, options) =>
        {
            var tool = Assert.Single(options!.Tools!.OfType<AIFunction>(), t => t.Name == AgentSkillsProvider.LoadSkillToolName);
            var argument = tool.JsonSchema.GetProperty("properties").EnumerateObject().First().Name;
            return Function(tool.Name, new() { [argument] = skillName });
        });
        Client.Steps.Enqueue((messages, options) =>
        {
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                r => r.Result?.ToString()?.Contains(skillName, StringComparison.Ordinal) == true);
            var tool = Assert.Single(options!.Tools!.OfType<AIFunction>(), t => t.Name == AgentSkillsProvider.ReadSkillResourceToolName);
            var arguments = tool.JsonSchema.GetProperty("properties").EnumerateObject().ToDictionary(
                p => p.Name, p => (object?)(p.Name.Contains("skill", StringComparison.OrdinalIgnoreCase) ? skillName : resourcePath));
            return Function(tool.Name, arguments);
        });
        Client.Steps.Enqueue((messages, _) =>
        {
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                r => r.Result?.ToString()?.Contains(expectedContent, StringComparison.Ordinal) == true);
            return Function("list_artifacts", []);
        });
    }

    internal static ChatResponse Function(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, arguments)]));

    internal void QueueSuccess(string producer = "developer")
    {
        QueuePipeline(producer);
        QueueStage("researcher");
        QueueStage("lead");
        QueueStage(producer);
        QueueStage("tester");
        Client.Text("Coordinator synthesis");
    }

    public void Dispose()
    {
        Client.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestSource(string root, string tag) : SquadArtifactSource
    {
        public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SquadArtifactRoots([root], null, "Synthetic test release")
            {
                Release = new SquadRelease(tag, new string(tag.EndsWith('2') ? 'a' : 'b', 40)),
            });
    }
}

internal sealed class ScriptedRuntimeChatClient : IChatClient
{
    internal Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> Steps { get; } = new();
    internal List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];
    internal bool Disposed { get; private set; }

    internal void Call(string name, Dictionary<string, object?> arguments) => Steps.Enqueue((_, _) =>
        new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, arguments)])));

    internal void Text(string text) => Steps.Enqueue((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = messages.ToArray();
        Calls.Add((snapshot, options));
        if (!Steps.TryDequeue(out var step))
        {
            throw new InvalidOperationException("No scripted model response remains.");
        }

        return Task.FromResult(step(snapshot, options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Tests use native non-streaming agent execution.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Disposed = true;
}

internal sealed class RuntimeTestLogger : ILoggerFactory, ILogger
{
    internal List<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    public void Dispose() { }
}
