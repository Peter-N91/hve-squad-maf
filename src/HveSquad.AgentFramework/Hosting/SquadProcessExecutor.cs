using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>A trusted host's argument list and declared outputs; never interpolated shell text.</summary>
/// <param name="Arguments">Individual arguments for the fixed executable.</param>
/// <param name="ProjectOutputs">Existing output files which successful execution must attest.</param>
/// <param name="StandardOutputPath">Optional contained file written from successfully captured stdout.</param>
public sealed record SquadProcessInvocation(
    IReadOnlyList<string> Arguments, IReadOnlyList<string> ProjectOutputs, string? StandardOutputPath = null);

/// <summary>An actual process result. Stdout/stderr are protected host/case data, not notification or telemetry fields.</summary>
/// <param name="ProcessId">The actual process identity, useful for host diagnostics.</param>
/// <param name="ExitCode">The observed exit code.</param>
/// <param name="StandardOutput">Bounded actual stdout.</param>
/// <param name="StandardError">Bounded actual stderr.</param>
/// <param name="Output">The existing runtime's successful/failed output contract.</param>
public sealed record SquadProcessResult(
    [property: System.Text.Json.Serialization.JsonRequired] int ProcessId,
    [property: System.Text.Json.Serialization.JsonRequired] int ExitCode,
    [property: System.Text.Json.Serialization.JsonRequired] string StandardOutput,
    [property: System.Text.Json.Serialization.JsonRequired] string StandardError,
    [property: System.Text.Json.Serialization.JsonRequired] SquadToolOutput Output);

/// <summary>A bounded, reusable reference executor with no default executable, shell, credentials or resource authority.</summary>
/// <remarks>
/// The host supplies this executor through a registered function, binds its command-builder revision,
/// declares input/output projections, and owns any supplied ActivitySource. Each invocation owns and
/// disposes its process. This is not a sandbox: trusted scripts must not detach children, access
/// undeclared resources, or mutate native state. Hosts provide OS/container isolation when required.
/// </remarks>
public sealed class SquadProcessExecutor
{
    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter<SquadToolOutputStatus>(),
            new System.Text.Json.Serialization.JsonStringEnumConverter<SquadToolReceiptKind>(),
        },
    };
    private readonly string _executable;
    private readonly string _executableSha256;
    private readonly ProjectFiles _files;
    private readonly string _project;
    private readonly TimeSpan _timeout;
    private readonly int _maximumOutputCharacters;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly ActivitySource? _activitySource;

    /// <summary>Binds one exact executable, working root, timeout, output bound and explicit environment allowlist.</summary>
    /// <param name="executablePath">An existing absolute host-selected executable path.</param>
    /// <param name="projectPath">The fixed contained working directory.</param>
    /// <param name="configurationBinding">Non-secret revision of the trusted command builder and resource scopes.</param>
    /// <param name="timeout">Positive timeout of at most ten minutes.</param>
    /// <param name="maximumOutputCharacters">Maximum characters in each output stream, at most one MiB.</param>
    /// <param name="environment">Explicit environment allowlist; inherited credentials are never copied.</param>
    /// <param name="squadRoot">The native root whose writer-owned paths must remain protected.</param>
    /// <param name="activitySource">Optional caller-owned source for a PII-free process span.</param>
    public SquadProcessExecutor(
        string executablePath, string projectPath, string configurationBinding, TimeSpan timeout,
        int maximumOutputCharacters = 65_536, IReadOnlyDictionary<string, string>? environment = null,
        string squadRoot = ".copilot-tracking/squad", ActivitySource? activitySource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationBinding);
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            throw new ArgumentException("A trusted executor requires an existing absolute executable path; no PATH or shell guessing is performed.", nameof(executablePath));
        }
        if (configurationBinding.Length > 128 || configurationBinding.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not (':' or '_' or '-')) ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10) ||
            maximumOutputCharacters is < 1 or > 1_048_576)
        {
            throw new ArgumentException("A process requires a non-secret configuration revision, a bounded positive timeout and bounded output.");
        }
        _executable = Path.GetFullPath(executablePath);
        ProjectFiles.RejectLinks(_executable);
        _executableSha256 = FileDigest(_executable);
        _project = Path.GetFullPath(projectPath);
        _files = new ProjectFiles(_project, 200_000, squadRoot);
        _timeout = timeout;
        _maximumOutputCharacters = maximumOutputCharacters;
        _activitySource = activitySource;
        var allowed = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            allowed.Add("SystemRoot", systemRoot);
        }
        foreach (var pair in environment ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=', StringComparison.Ordinal) ||
                pair.Key.Contains('\0', StringComparison.Ordinal) || pair.Value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("The explicit process environment contains an invalid key or value.", nameof(environment));
            }
            allowed[pair.Key] = pair.Value;
        }
        _environment = allowed;
        Binding = ProjectFiles.Hash(JsonSerializer.Serialize(new
        {
            Executable = _executable,
            ExecutableSha256 = _executableSha256,
            Project = _project,
            configurationBinding,
            timeout,
            maximumOutputCharacters,
            squadRoot,
            Environment = allowed.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }));
    }

    /// <summary>Opaque digest of the exact executable and trusted host configuration; suitable for ExecutorBinding.</summary>
    public string Binding { get; }

    /// <summary>Extracts evidence only from the actual typed result of this trusted executor.</summary>
    /// <param name="result">The real registered function's return value.</param>
    /// <returns>The observed successful or failed output contract.</returns>
    public static SquadToolOutput GetOutputEvidence(object? result) => ReadResult(result).Output;

    /// <summary>Reads a real typed result or MAF's serialized result, requiring the complete actual process/output shape.</summary>
    /// <param name="result">The actual registered function's return value, not a model assertion.</param>
    /// <returns>The verified observed process result.</returns>
    public static SquadProcessResult ReadResult(object? result)
    {
        SquadProcessResult process;
        if (result is SquadProcessResult typed)
        {
            process = typed;
        }
        else if (result is JsonElement { ValueKind: JsonValueKind.Object } payload)
        {
            var output = payload.EnumerateObject().SingleOrDefault(property =>
                property.Name.Equals("output", StringComparison.OrdinalIgnoreCase)).Value;
            if (output.ValueKind != JsonValueKind.Object ||
                !output.EnumerateObject().Any(property => property.Name.Equals("status", StringComparison.OrdinalIgnoreCase)) ||
                !output.EnumerateObject().Any(property => property.Name.Equals("receiptKind", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The serialized process result has no explicit actual output status and receipt kind.");
            }
            process = payload.Deserialize<SquadProcessResult>(ResultJson)
                ?? throw new InvalidDataException("The serialized process result is missing.");
        }
        else
        {
            throw new InvalidDataException("The trusted process function did not return its actual execution result.");
        }
        if (process.ProcessId <= 0 || process.StandardOutput is null || process.StandardError is null ||
            process.Output?.ProjectPaths is null || !Enum.IsDefined(process.Output.Status) ||
            process.Output.ReceiptKind != SquadToolReceiptKind.Process ||
            string.IsNullOrWhiteSpace(process.Output.Receipt) ||
            process.ExitCode != 0 && process.Output.Status != SquadToolOutputStatus.Failed)
        {
            throw new InvalidDataException("The process result has incomplete or contradictory execution/output evidence.");
        }
        return process;
    }

    /// <summary>Runs the fixed executable without a shell, captures bounded output and attests only a successful exit.</summary>
    /// <param name="invocation">The trusted host-built argument list and output declaration.</param>
    /// <param name="cancellationToken">Cancellation terminates the actual process tree before returning.</param>
    /// <returns>An observed exit/result; nonzero exit never attests an applied output.</returns>
    /// <exception cref="SquadProcessExecutionException">Start, timeout, output-bound, termination or attestation failure.</exception>
    public async Task<SquadProcessResult> ExecuteAsync(
        SquadProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = invocation.Arguments.ToArray();
        var outputs = invocation.ProjectOutputs.ToArray();
        if (outputs.Length > 128 || arguments.Length > 128 || arguments.Any(argument => argument is null ||
                argument.Length > 32_768 || argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Process arguments require a bounded explicit argument list.", nameof(invocation));
        }
        foreach (var path in outputs.Concat(invocation.StandardOutputPath is { } stdoutPath ? [stdoutPath] : []))
        {
            ValidateOutput(path);
        }
        ProjectFiles.RejectLinks(_executable);
        if (FileDigest(_executable) != _executableSha256)
        {
            throw new SquadProcessExecutionException("The host-bound executable changed; review and rebind it before execution.");
        }
        var commandHash = ProjectFiles.Hash(JsonSerializer.Serialize(new { Binding, Arguments = arguments, Outputs = outputs, invocation.StandardOutputPath }));
        using var activity = _activitySource?.StartActivity("run.skill_script", ActivityKind.Internal);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(_timeout);
        var originalOutputs = outputs.Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => File.Exists(_files.Resolve(path)) ? _files.HashFile(path) : null, StringComparer.Ordinal);
        CheckDeadline();
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _project,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        foreach (var pair in _environment)
        {
            start.Environment.Add(pair.Key, pair.Value);
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new SquadProcessExecutionException("The trusted process did not start.");
            }
        }
        catch (Win32Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Process start failed.");
            throw new SquadProcessExecutionException("The host-selected executable could not start.", exception);
        }
        var processId = process.Id;
        var stdout = CaptureAsync(process.StandardOutput, execution);
        var stderr = CaptureAsync(process.StandardError, execution);
        try
        {
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(execution.Token), stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SquadProcessExecutionException or IOException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Process execution failed.");
            await execution.CancelAsync().ConfigureAwait(false);
            await TerminateAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Trusted process execution cancelled and terminated.", exception, cancellationToken);
            }
            var outputFailure = new[] { stdout.Exception, stderr.Exception }.OfType<AggregateException>()
                .SelectMany(error => error.InnerExceptions).OfType<SquadProcessExecutionException>().FirstOrDefault();
            throw outputFailure ?? new SquadProcessExecutionException(
                exception is OperationCanceledException ? "Trusted process exceeded its configured timeout and was terminated." :
                "Trusted process output capture failed.", exception);
        }
        var standardOutput = await stdout.ConfigureAwait(false);
        var standardError = await stderr.ConfigureAwait(false);
        CheckDeadline();
        if (process.ExitCode != 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Process returned a nonzero exit code.");
            foreach (var original in originalOutputs)
            {
                ValidateOutput(original.Key);
                var current = File.Exists(_files.Resolve(original.Key)) ? _files.HashFile(original.Key) : null;
                CheckDeadline();
                if (current != original.Value)
                {
                    throw new SquadProcessExecutionException("Trusted process failed after changing a declared output; preserve the partial bytes and reconcile before resuming.");
                }
            }
            return new(processId, process.ExitCode, standardOutput, standardError,
                new([], $"trusted-process:{Binding}; command:{commandHash}; exit-code:{process.ExitCode}",
                    SquadToolOutputStatus.Failed, SquadToolReceiptKind.Process));
        }
        if (invocation.StandardOutputPath is { } outputPath)
        {
            try
            {
                ValidateOutput(outputPath);
                _files.Write(outputPath, standardOutput);
                CheckDeadline();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Process stdout publication failed.");
                throw new SquadProcessExecutionException("Trusted process stdout publication failed; preserve any partial bytes and reconcile before resuming.", exception);
            }
            outputs = [.. outputs, outputPath];
        }
        foreach (var path in outputs.Distinct(StringComparer.Ordinal))
        {
            try
            {
                ValidateOutput(path);
                _ = _files.HashFile(path);
                CheckDeadline();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Process output attestation failed.");
                throw new SquadProcessExecutionException("Trusted process did not produce a readable declared output; reconciliation is required.", exception);
            }
        }
        return new(processId, process.ExitCode, standardOutput, standardError,
            new(outputs.Distinct(StringComparer.Ordinal).ToArray(),
                $"trusted-process:{Binding}; command:{commandHash}; exit-code:0; " +
                $"stdout:{ProjectFiles.Hash(standardOutput)}; stderr:{ProjectFiles.Hash(standardError)}",
                ReceiptKind: SquadToolReceiptKind.Process));

        void CheckDeadline()
        {
            if (execution.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Process output attestation cancelled or timed out.");
                cancellationToken.ThrowIfCancellationRequested();
                throw new SquadProcessExecutionException("Trusted process exceeded its configured timeout during output attestation.");
            }
        }
    }

    private void ValidateOutput(string path)
    {
        _ = _files.Resolve(path);
        var normalized = path.Replace('\\', '/');
        if (_files.IsProtectedStatePath(normalized) || ProjectFiles.IsWithin(normalized, ".git") ||
            _files.IsMemberRoot && !ProjectFiles.IsWithin(normalized, _files.SquadRoot) &&
            !ProjectFiles.IsWithin(normalized, "docs") && !ProjectFiles.IsWithin(normalized, "outputs"))
        {
            throw new InvalidDataException("A generic process output escaped the contained dispatch scope or targeted writer-owned state.");
        }
    }

    private async Task<string> CaptureAsync(StreamReader reader, CancellationTokenSource execution)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), execution.Token).ConfigureAwait(false) is var count && count != 0)
            {
                if (result.Length + count > _maximumOutputCharacters)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    throw new SquadProcessExecutionException("Trusted process exceeded its configured output bound and was terminated.");
                }
                result.Append(buffer, 0, count);
            }
        }
        catch (IOException)
        {
            await execution.CancelAsync().ConfigureAwait(false);
            throw;
        }
        return result.ToString();
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            throw new SquadProcessExecutionException("Termination of the trusted process tree could not be confirmed; host intervention is required.", exception);
        }
    }

    private static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>An explicit bounded process failure; never a successful or dry-run output receipt.</summary>
public sealed class SquadProcessExecutionException : Exception
{
    /// <summary>Creates an actionable process failure without copying raw output or arguments into diagnostics.</summary>
    /// <param name="message">A non-secret diagnostic.</param>
    /// <param name="innerException">The underlying host/process failure, when available.</param>
    public SquadProcessExecutionException(string message, Exception? innerException = null) : base(message, innerException) { }
}
