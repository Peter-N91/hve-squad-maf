using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

if (args is not ["--case", var casePath, "--package-version", var expectedVersion] || !Path.IsPathFullyQualified(casePath) ||
    Directory.Exists(casePath) || File.Exists(casePath))
{
    Console.Error.WriteLine("Use --case <new-absolute-isolated-directory> --package-version <exact-version>. No existing case is accepted.");
    return 2;
}

try
{
    await VerifyAsync(casePath, expectedVersion);
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine("Package verification failed: " + exception.Message);
    return 1;
}

static async Task VerifyAsync(string project, string expectedVersion)
{
    Directory.CreateDirectory(project);
    var supported = SupportedSquadRelease.Current;
    Require(supported.Assertions.Count == 279 && !string.IsNullOrWhiteSpace(supported.ConformanceStatus),
        "The embedded release inventory or explicit conformance status is missing.");
    var version = typeof(SquadRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Require(version == expectedVersion || version?.StartsWith(expectedVersion + "+", StringComparison.Ordinal) == true,
        "The actual loaded package has an unexpected independent library version.");

    // This synthetic case grants only local fixture rights, not production user authentication.
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "isolated-package-fixture")], "isolated-package-fixture"));
    var nativeRoot = Path.Combine(project, ".copilot-tracking", "squad");
    var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? nativeRoot.ToUpperInvariant() : nativeRoot)));
    var channel = new InChatApprovalChannel("package-fixture", _ => ValueTask.FromResult(principal),
        (_, action, _) => ValueTask.FromResult(action.Binding.RootSha256 == rootHash), TextWriter.Null,
        authorizeLearningInvocation: (_, access, _) => ValueTask.FromResult(
            access.SquadRoot == ".copilot-tracking/squad" && access.Invocation.Reference == "package-human-event"));
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ProjectPath"] = project,
        ["NamingPolicy"] = "skip",
        ["Discovery"] = "skip",
        ["Version"] = supported.Tag,
        ["MaxConcurrentDispatches"] = "1",
    }).Build();
    using var client = new ResearchClient();
    var services = new ServiceCollection();
    services.AddSingleton<IChatClient>(client);
    var source = new ReleaseArtifactSource();
    services.AddHveSquad(configuration, options =>
    {
        options.HumanApprovalChannel = channel;
        options.ApproveAsync = (request, _) => ValueTask.FromResult(request.Kind == SquadApprovalKind.Initialization);
    }, source);
    using var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<SquadRuntimeFactory>();
    string runId;
    using (var runtime = await factory.CreateAsync())
    {
        Require(supported.Matches(runtime.Provenance.Release), "The acquired release does not match the embedded pin.");
        var pending = await runtime.RunAsync("Research this isolated synthetic case. Stop after research.");
        Require(pending.Status == SquadRunStatus.ApprovalRequired && pending.Evidence.Count == 0 && client.Calls == 1,
            $"The original routing gate did not suspend after classification and before role dispatch: {pending.Status}; calls={client.Calls}.");
        runId = pending.RunId;
    }
    using (var resumed = await factory.CreateAsync())
    {
        var action = (await resumed.GetPendingHumanActionsAsync()).Single(item => item.Status == "pending");
        Require(action.Binding.RunId == runId, "The recreated runtime did not retain the original run.");
        var submitted = await resumed.SubmitHumanResponseAsync(
            SquadHumanResponse.FromCommand(action, "/approve", "package-fixture-response"));
        Require(submitted.Accepted, submitted.Diagnostic);
        var completed = await resumed.ResumeAsync(runId);
        Require(completed.Status == SquadRunStatus.ResearchCompleted && completed.RunId == runId,
            "Actual same-run package execution failed: " + completed.ResponseText);
        Require(completed.Evidence.Count == 1 && completed.Evidence[0].Role == "researcher" && client.Calls == 4,
            "Actual MAF dispatch/output evidence or bounded scripted calls are missing.");
    }
    Require(!client.Disposed, "Runtime disposal took ownership of the caller's client.");

    var deniedSource = new DeniedSource();
    var helperServices = new ServiceCollection();
    helperServices.AddHveSquad(configuration, source: deniedSource);
    using var helperProvider = helperServices.BuildServiceProvider();
    var helpers = helperProvider.GetRequiredService<SquadRuntimeFactory>();
    var memory = helpers.CreateMemory(new()
    {
        Binding = "package-local-memory",
        AuthorizeAsync = (access, _) => ValueTask.FromResult(
            access.SquadRoot == ".copilot-tracking/squad" && access.Role == "researcher"),
    });
    await memory.RememberAsync(new("researcher", "When independent evidence is missing",
        "Keep unknown observations explicit and require independent verification before reuse."));
    Require((await memory.ReadAsync("researcher")).Count == 1, "Public consumer-local memory failed.");

    var before = StateHashes(project);
    var reports = helpers.CreateReports(new()
    {
        AuthorizeAsync = (access, _) => ValueTask.FromResult(!access.IncludeCaseContent),
        Renderers = [new SquadLocalDocumentRenderer()],
    });
    var governance = await reports.GovernanceAsync(new("all"));
    Require(governance.Metrics is { RoleDispatches: 1 } && File.Exists(governance.Path),
        "The package governance reader did not reduce the real recorded dispatch.");
    string[] formats = ["md", "html", "pdf", "docx"];
    foreach (var format in formats)
    {
        var report = await reports.DocumentAsync(new("Summarize the verified research evidence.", format));
        Require(report.Format == format && !report.Fallback && new FileInfo(report.Path).Length > 0,
            "The registered local renderer did not produce the requested actual format: " + format);
    }
    var learning = helpers.CreateLearning(new()
    {
        Binding = "package-human-learning",
        AuthorizeInvocationAsync = channel.AuthorizeLearningInvocationAsync,
        AuthorizeSourceAsync = (_, _) => ValueTask.FromResult(true),
        HumanApprovalChannel = channel,
    });
    var discovery = await learning.DiscoverAsync(new("package-human-event"));
    Require(discovery.Candidates.Count == 1, "Human-only local memory discovery failed.");
    Require(before.OrderBy(pair => pair.Key).SequenceEqual(StateHashes(project).OrderBy(pair => pair.Key)),
        "State-read-only helpers changed native evidence.");
    Require(deniedSource.Calls == 0 && client.Calls == 4, "Helpers resolved a source or a chat client.");
    await VerifyFederationAsync(Path.Combine(project, "federation"), source);

    await File.WriteAllTextAsync(Path.Combine(project, "verification.json"), JsonSerializer.Serialize(new
    {
        status = "passed",
        libraryVersion = version,
        packageAssembly = typeof(SquadRuntime).Assembly.Location,
        release = new { supported.Tag, supported.CommitSha, assertions = supported.Assertions.Count, supported.ConformanceStatus },
        sameRunResume = true,
        nativeDispatches = 1,
        modelCalls = client.Calls,
        helperAcquisitions = deniedSource.Calls,
        actualFormats = formats,
        learningCandidates = discovery.Candidates.Count,
        federation = "actual member execution",
        externalMutations = 0,
        quality = "scripted mechanical verification, not model or product quality",
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Package consumer passed: original authenticated resume, real MAF outputs, federation, local formats, memory and human-only discovery.");
}

static async Task VerifyFederationAsync(string project, SquadArtifactSource source)
{
    Directory.CreateDirectory(project);
    using var client = new ResearchClient();
    var services = new ServiceCollection();
    services.AddSingleton<IChatClient>(client);
    services.AddHveSquad(configureHost: options =>
    {
        options.ProjectPath = project;
        options.NamingPolicy = "skip";
        options.Discovery = "skip";
        options.ApproveAsync = (_, _) => ValueTask.FromResult(true);
    }, source: source);
    using var provider = services.BuildServiceProvider();
    using var federation = await provider.GetRequiredService<SquadRuntimeFactory>().CreateFederationAsync();
    var initialized = await federation.InitializeAsync([
        new() { Name = "research", Description = "Synthetic local research", Patterns = ["research"] },
        new() { Name = "review", Description = "Independent synthetic review", Patterns = ["review"] },
    ]);
    Require(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
    var result = await federation.RunAsync("Research the isolated synthetic evidence.", new() { Target = "research" });
    Require(result.Status == SquadRunStatus.Completed && result.Members.Single().Result?.Status == SquadRunStatus.ResearchCompleted &&
        client.Calls == 5, "The package federation did not execute the real member engine: " + result.Diagnostic);
}

static Dictionary<string, string> StateHashes(string project) =>
    Directory.EnumerateFiles(Path.Combine(project, ".copilot-tracking", "squad"), "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(project, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

static void Require(bool condition, string diagnostic)
{
    if (!condition)
    {
        throw new InvalidOperationException(diagnostic);
    }
}

sealed class DeniedSource : SquadArtifactSource
{
    public int Calls { get; private set; }
    public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException("Read-only/helper package APIs must never acquire artifacts.");
    }
}

sealed class ResearchClient : IChatClient
{
    private int _coordinatorCalls;
    private int _researchCalls;
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (options?.Tools?.Any(tool => tool.Name == "run_focused") == true)
        {
            if (_coordinatorCalls++ != 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Verified local member research completed.")));
            }
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
            throw new InvalidOperationException("Unexpected public-package model contract.");
        }
        return Task.FromResult(_researchCalls++ switch
        {
            0 => Call("write_artifact", new() { ["content"] = "# Package verification\n\nOriginal synthetic local evidence. No customer data or external action." }),
            1 => Call("complete_stage", new()
            {
                ["summary"] = "Actual synthetic research written.",
                ["verdict"] = "Completed",
                ["risk"] = "Low",
                ["blockingIssues"] = 0,
            }),
            2 => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Actual synthetic research completed.")),
            _ => throw new InvalidOperationException("Unexpected extra scripted call."),
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This isolated scripted consumer uses non-streaming MAF execution.");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Disposed = true;
    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), name, arguments)]));
}
