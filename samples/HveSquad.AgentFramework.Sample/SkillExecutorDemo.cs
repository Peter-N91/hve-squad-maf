using System.Globalization;
using System.Net.Http.Headers;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Sample;

internal static class SkillExecutorDemo
{
    internal const string InputPath = "executor-input.xml";
    internal const string ResourcePath = "scripts/read-diff.ps1";
    private const string Input = """
        <pr-reference>
          <diff><![CDATA[
        diff --git a/example.txt b/example.txt
        --- a/example.txt
        +++ b/example.txt
        @@ -1 +1 @@
        -original synthetic input
        +original synthetic output
          ]]></diff>
        </pr-reference>
        """;

    internal static async Task<int> RunAsync(SampleCommand command, CancellationToken cancellationToken,
        SquadArtifactSource? source = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var project = Path.GetFullPath(command.Project!);
        if (!Directory.Exists(project))
        {
            throw new DirectoryNotFoundException("Create an explicit isolated demo directory first.");
        }
        var inputFile = Path.Combine(project, InputPath);
        if (File.Exists(inputFile))
        {
            throw new InvalidOperationException("The demo input already exists; use a new isolated directory. No existing input was overwritten.");
        }
        using var releaseHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        var releaseToken = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(releaseToken))
        {
            releaseHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", releaseToken);
        }
        var artifactsSource = source ?? new ReleaseArtifactSource(command.Version!, httpClient: releaseHttp);
        var roots = await artifactsSource.ResolveAsync(cancellationToken);
        var artifacts = SquadArtifactLoader.Load(roots.Roots, roots.RosterPath);
        var skill = artifacts.Skills.SingleOrDefault(skill => skill.Name == "pr-reference")
            ?? throw new InvalidDataException("The actual release must install the canonical pr-reference skill.");
        var resource = Path.Combine(skill.DirectoryPath, ResourcePath.Replace('/', Path.DirectorySeparatorChar));
        var environment = new Dictionary<string, string>();
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            environment.Add("PATH", path);
        }
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PATHEXT") is { } extensions)
        {
            environment.Add("PATHEXT", extensions);
        }
        var executor = new SquadProcessExecutor(command.ExecutorPath!, project, "read-diff-info:v1",
            TimeSpan.FromSeconds(30), environment: environment);
        var binding = SquadSkillScriptBinding.Create(roots, skill.Name, ResourcePath, executor.Binding);
        using (var stream = new FileStream(inputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(Input);
        }
        using var client = new ResearchClient();
        SquadProcessResult? observedProcess = null;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IChatClient>(client);
        services.AddHveSquad(configureHost: options =>
        {
            options.ProjectPath = project;
            options.Version = command.Version!;
            options.NamingPolicy = "skip";
            options.ApproveAsync = (request, _) => ValueTask.FromResult(
                request.Kind is SquadApprovalKind.Initialization or SquadApprovalKind.Routing);
            options.Tools.Add(new(
                AIFunctionFactory.Create(async (CancellationToken token) =>
                {
                    observedProcess = await executor.ExecuteAsync(new(
                        ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", resource,
                            "-InputPath", inputFile, "-Info"], []), token);
                    return observedProcess;
                },
                    "read_registered_diff", "Read the one local synthetic PR reference through the registered released script."),
                "execute/runInTerminal", ["researcher"], SquadToolEffect.ReadOnly,
                OutputEvidence: SquadProcessExecutor.GetOutputEvidence,
                SkillScript: binding, ProjectReadPaths: _ => [InputPath]));
        }, source: artifactsSource);
        using var provider = services.BuildServiceProvider();
        using var runtime = await provider.GetRequiredService<SquadRuntimeFactory>().CreateAsync(cancellationToken);
        var result = await runtime.RunAsync("Research the locally supplied synthetic PR reference. Stop after research.", cancellationToken);
        if (result.Status != SquadRunStatus.ResearchCompleted ||
            result.Evidence.SelectMany(evidence => evidence.ExecutedHostTools ?? []).Count(tool => tool.SkillScript is not null) != 1)
        {
            throw new InvalidOperationException("The registered script demonstration did not complete actual verified research: " +
                result.ResponseText + (observedProcess is null ? "" : " Actual process exit: " +
                    observedProcess.ExitCode.ToString(CultureInfo.InvariantCulture) + ". Private output was not printed."));
        }
        await output.WriteLineAsync("Status: " + result.Status);
        await output.WriteLineAsync("Release: " + (runtime.Provenance.Release?.Tag ?? "non-official directory artifacts"));
        await output.WriteLineAsync("Actual registered process, canonical resource and input/output receipts verified.");
        await output.WriteLineAsync("Scripted MAF evidence; not paid-model quality or IndustryIQ readiness.");
        return 0;
    }

    private sealed class ResearchClient : IChatClient
    {
        private int _calls;
        private bool _started;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options?.Tools?.Any(tool => tool.Name == "run_focused") == true)
            {
                if (_started)
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Registered script research completed.")));
                }
                _started = true;
                return Task.FromResult(Call("run_focused", new()
                {
                    ["stage"] = "research",
                    ["inputPaths"] = Array.Empty<string>(),
                    ["requiresCouncil"] = false,
                    ["includesAiRisk"] = false,
                }));
            }
            if (options?.Tools?.Any(tool => tool.Name == "write_artifact") != true)
            {
                throw new InvalidOperationException("The bounded demo supports only a real research dispatch.");
            }
            ChatResponse response;
            switch (_calls++)
            {
                case 0:
                    response = Call(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = "pr-reference" });
                    break;
                case 1:
                    response = Call(AgentSkillsProvider.RunSkillScriptToolName, new()
                    {
                        ["skillName"] = "pr-reference",
                        ["scriptName"] = ResourcePath,
                        ["arguments"] = new Dictionary<string, object?>(),
                    });
                    break;
                case 2:
                    var returned = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last().Result;
                    var process = SquadProcessExecutor.ReadResult(returned);
                    if (process.ExitCode != 0 || process.Output.Status != SquadToolOutputStatus.Succeeded ||
                        string.IsNullOrWhiteSpace(process.StandardOutput))
                    {
                        throw new InvalidDataException("The released reader did not return actual successful stdout.");
                    }
                    response = Call("write_artifact", new()
                    {
                        ["content"] = "# Registered released-script research\n\nOriginal synthetic input; actual process observation:\n\n" +
                            process.StandardOutput + "\n\nVerified process receipt: " + process.Output.Receipt,
                    });
                    break;
                case 3:
                    response = Call("complete_stage", new()
                    {
                        ["summary"] = "The registered released reader returned actual bounded local evidence.",
                        ["verdict"] = "Completed",
                        ["risk"] = "Low",
                        ["blockingIssues"] = 0,
                    });
                    break;
                case 4:
                    response = new(new ChatMessage(ChatRole.Assistant, "Registered script research completed."));
                    break;
                default:
                    throw new InvalidOperationException("Unexpected extra provider invocation in the bounded executor demo.");
            }
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The bounded demo uses native non-streaming MAF calls.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
            new(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, arguments)]));
    }
}
