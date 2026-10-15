using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Sample;

internal static class NativeHelpers
{
    private const int MaximumDataBytes = 1_000_000;
    private static readonly JsonSerializerOptions DataJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    internal static async Task<int> RunAsync(NativeHelperCommand command, CancellationToken cancellationToken,
        TextReader? input = null, TextWriter? output = null, SquadArtifactSource? source = null)
    {
        if (command.Kind == "--learn" && input is null && Console.IsInputRedirected)
        {
            throw new UnauthorizedAccessException("Human-only learning needs an interactive local operator; redirected or piped approvals are not human initiation.");
        }
        input ??= Console.In;
        output ??= Console.Out;
        var project = Path.GetFullPath(command.Project);
        if (!Directory.Exists(project))
        {
            throw new DirectoryNotFoundException("Native helpers require the existing authorized case directory.");
        }
        async ValueTask<bool> Confirm(string description, CancellationToken token)
        {
            await output.WriteLineAsync(description);
            await output.WriteLineAsync("Approve exactly this local process-account operation? Type yes [no]:");
            return string.Equals((await input.ReadLineAsync(token))?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }
        var services = new ServiceCollection();
        services.AddHveSquad(configureHost: options =>
        {
            options.ProjectPath = project;
            options.SquadRoot = command.SquadRoot;
        }, source: source);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<SquadRuntimeFactory>();
        if (command.Kind == "--report")
        {
            var reports = factory.CreateReports(new()
            {
                IncludeCaseContent = command.IncludePrivate,
                Renderers = [new SquadLocalDocumentRenderer()],
                AuthorizeAsync = (access, token) => Confirm("Report case/source/output authorization:\n" +
                    JsonSerializer.Serialize(access, DataJson), token),
            });
            var result = command.Operation == "document"
                ? await reports.DocumentAsync(new(command.Request!, command.Format, command.OutputPath), cancellationToken)
                : await reports.GovernanceAsync(new(command.Period, command.OutputPath), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(result, DataJson));
            return result.Fallback ? 3 : 0;
        }
        if (command.Kind == "--memory")
        {
            var memory = factory.CreateMemory(new()
            {
                Binding = "console-local-memory-v1",
                AuthorizeAsync = (access, token) => Confirm("Consumer-local memory authorization:\n" +
                    JsonSerializer.Serialize(access, DataJson), token),
            });
            if (command.Operation == "read")
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(await memory.ReadAsync(command.Role!, cancellationToken), DataJson));
            }
            else
            {
                if (!await Confirm("Read the explicit local note file for this case and installed role?", cancellationToken))
                {
                    throw new UnauthorizedAccessException("Memory note disclosure was not approved.");
                }
                var note = await ReadAsync<SquadMemoryNote>(project, command.NotePath!, cancellationToken);
                if (note.Role != command.Role)
                {
                    throw new ArgumentException("The note role differs from the exact command-line role.");
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(await memory.RememberAsync(note, cancellationToken), DataJson));
            }
            return 0;
        }
        if (!await Confirm("Human-only Squad Learn initiation for this exact local case. Report, Watch or session ownership is not consent.",
            cancellationToken))
        {
            throw new UnauthorizedAccessException("An actual human did not initiate learning; nothing was read.");
        }
        var invocation = new SquadLearningInvocation("console-human:" + Guid.NewGuid().ToString("N"));
        var channel = ConsoleCallbacks.CreateDurableChannel(project, command.SquadRoot, output, invocation);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var tokenValue = command.GitHubConnector ? Environment.GetEnvironmentVariable("HVE_SQUAD_LEARNING_GITHUB_TOKEN") : null;
        if (command.GitHubConnector && string.IsNullOrWhiteSpace(tokenValue))
        {
            throw new InvalidOperationException("The explicit learning connector needs host-owned HVE_SQUAD_LEARNING_GITHUB_TOKEN; no credential is read from JSON.");
        }
        ISquadLearningConnector? connector = null;
        if (command.GitHubConnector)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenValue);
            connector = new GitHubLearningConnector(http, command.Target!,
                (mutation, token) => Confirm("Independent resource rights for this exact GitHub learning action:\n" +
                    JsonSerializer.Serialize(new { mutation.Kind, mutation.Repository, mutation.Branch, mutation.Path }, DataJson), token),
                "console-learning-resources-v1");
        }
        var learning = factory.CreateLearning(new()
        {
            Binding = "console-human-learning-v1",
            TenantRepository = command.Target?.Kind == "tenant" ? command.Target.Repository : null,
            Connector = connector,
            SecretValues = tokenValue is null ? [] : [tokenValue],
            HumanApprovalChannel = channel,
            AuthorizeInvocationAsync = channel.AuthorizeLearningInvocationAsync,
            AuthorizeSourceAsync = (access, token) => Confirm("Learning source disclosure, not report permission:\n" +
                JsonSerializer.Serialize(access, DataJson), token),
            ConfirmAsync = async (confirmation, token) =>
                await Confirm(JsonSerializer.Serialize(confirmation, DataJson), token)
                    ? SquadHumanResponse.FromCommand(confirmation.Action, "/approve", Guid.NewGuid().ToString("N")) : null,
        });
        if (command.Operation == "discover")
        {
            var discovered = await learning.DiscoverAsync(invocation, command.Focus, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(discovered, DataJson));
            return 0;
        }
        SquadLearningResult outcome;
        if (command.Operation == "prepare")
        {
            var discovery = await learning.DiscoverAsync(invocation, cancellationToken: cancellationToken);
            var candidate = discovery.Candidates.SingleOrDefault(candidate => candidate.Id == command.Candidate)
                ?? throw new ArgumentException("The selected candidate is absent or changed. Discover again; no source is guessed.");
            var snapshot = command.SnapshotPath is null ? null :
                await ReadAsync<SquadLearningTargetSnapshot>(project, command.SnapshotPath, cancellationToken);
            outcome = await learning.PrepareAsync(invocation, candidate, command.Target!, snapshot, cancellationToken: cancellationToken);
            if (outcome.Prepared is { } prepared)
            {
                await WritePreparedAsync(project, command.PreparedPath!, prepared, cancellationToken);
            }
        }
        else
        {
            var prepared = await ReadAsync<SquadLearningPrepared>(project, command.PreparedPath!, cancellationToken);
            if (prepared.Target != command.Target)
            {
                throw new ArgumentException("The prepared target differs from the explicit source/fork selection. Reprepare; never change its authority.");
            }
            outcome = command.Operation == "reconcile"
                ? await learning.ReconcileAsync(invocation, prepared, cancellationToken)
                : await learning.PromoteAsync(invocation, prepared, cancellationToken);
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(outcome, DataJson));
        return outcome.Status switch
        {
            "prepared" or "pull-request-created" => 0,
            "reconciliation-required" => 3,
            _ => 1,
        };
    }

    private static async Task<T> ReadAsync<T>(string project, string path, CancellationToken token)
    {
        var resolved = ResolveLocal(project, path);
        await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaximumDataBytes)
        {
            throw new InvalidDataException("Helper input must be nonempty and at most one million bytes.");
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        if (stream.Length != bytes.Length)
        {
            throw new InvalidDataException("The explicit helper input changed during its bounded read.");
        }
        var preamble = System.Text.Encoding.UTF8.Preamble;
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(preamble))
        {
            json = json[preamble.Length..];
        }
        return JsonSerializer.Deserialize<T>(json, DataJson) ?? throw new InvalidDataException("The explicit helper input is empty.");
    }

    private static async Task WritePreparedAsync(string project, string path, SquadLearningPrepared prepared, CancellationToken token)
    {
        var resolved = ResolveLocal(project, path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared, DataJson);
        if (bytes.Length > MaximumDataBytes)
        {
            throw new InvalidDataException("Prepared helper data exceeds the one-million-byte local bound.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        _ = ResolveLocal(project, path);
        var temporary = resolved + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, resolved, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string ResolveLocal(string project, string path) => new ProjectFileWriter(project).Resolve(path);
}
