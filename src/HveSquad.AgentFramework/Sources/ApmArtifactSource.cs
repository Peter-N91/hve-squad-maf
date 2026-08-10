using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Sources;

/// <summary>
/// Materializes an artifact tree by delegating to the APM CLI, which resolves the pinned dependency
/// graph that a plain git clone does not carry. Installs into a cache directory and reuses it on
/// later runs.
/// </summary>
/// <remarks>
/// Requires <c>apm</c> on PATH and network access on first use. Prefer
/// <see cref="ProjectArtifactSource"/> when the consumer already installed the package.
/// </remarks>
public sealed partial class ApmArtifactSource : SquadArtifactSource
{
    private readonly string _packageSpec;
    private readonly string _target;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _timeout;

    /// <param name="packageSpec">
    /// A pinned package reference such as <c>Peter-N91/hve-squad#v0.12.7</c>. Unpinned references
    /// are rejected: an artifact tree that changes underneath a run is not reproducible.
    /// </param>
    /// <param name="cacheDirectory">Defaults to a per-user cache keyed by the package spec.</param>
    public ApmArtifactSource(
        string packageSpec,
        string target = "copilot",
        string? cacheDirectory = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!PackageSpecPattern().IsMatch(packageSpec))
        {
            throw new ArgumentException(
                $"'{packageSpec}' is not a pinned package reference of the form 'owner/repo#ref'.",
                nameof(packageSpec));
        }

        if (!TargetPattern().IsMatch(target))
        {
            throw new ArgumentException($"'{target}' is not a valid target name.", nameof(target));
        }

        _packageSpec = packageSpec;
        _target = target;
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory(packageSpec);
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    public string CacheDirectory => _cacheDirectory;

    public override async ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
            await RunApmInstallAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsInstalled(_cacheDirectory))
        {
            throw new SquadArtifactsNotFoundException(
                $"apm install completed but produced no agent charters under '{_cacheDirectory}'.");
        }

        var roots = ProjectArtifactSource.ForProject(_cacheDirectory);

        return roots with { Origin = $"apm install '{_packageSpec}' into '{_cacheDirectory}'" };
    }

    private async Task RunApmInstallAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "apm",
            WorkingDirectory = _cacheDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList avoids any shell parsing, so the spec cannot inject extra arguments.
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add(_packageSpec);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(_target);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SquadArtifactsNotFoundException(
                "Could not start 'apm'. Install the APM CLI, or use ProjectArtifactSource against a " +
                "project where the package is already installed.",
                ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new SquadArtifactsNotFoundException(
                $"apm install exceeded {_timeout.TotalMinutes:0.#} minutes and was cancelled.");
        }

        if (process.ExitCode != 0)
        {
            var error = await stderr.ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);

            throw new SquadArtifactsNotFoundException(
                $"apm install '{_packageSpec}' failed with exit code {process.ExitCode}. " +
                $"{FirstLine(error) ?? FirstLine(output) ?? "No diagnostic output."}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string? FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static bool IsInstalled(string directory)
    {
        var agents = Path.Combine(directory, ".github", "agents");

        return Directory.Exists(agents)
            && Directory.EnumerateFiles(agents, "*.agent.md", SearchOption.AllDirectories).Any();
    }

    private static string DefaultCacheDirectory(string packageSpec)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageSpec)))[..16]
            .ToLower(CultureInfo.InvariantCulture);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hve-squad-maf",
            "artifacts",
            hash);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+#[A-Za-z0-9._/-]+$")]
    private static partial Regex PackageSpecPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex TargetPattern();
}
