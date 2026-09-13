using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Sources;

/// <summary>Describes one isolated APM installation.</summary>
public sealed record ApmInstallRequest(
    string PackageSpec,
    string Target,
    string WorkingDirectory,
    TimeSpan Timeout);

/// <summary>Installs an APM package into the request's fresh working directory.</summary>
public delegate ValueTask ApmInstaller(
    ApmInstallRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Materializes an artifact tree through APM and publishes the completed installation to a
/// source-and-target-specific cache.
/// </summary>
public sealed partial class ApmArtifactSource : SquadArtifactSource
{
    private const int MaximumDiagnosticCharacters = 16 * 1024;
    private const int MaximumInventoryFiles = 50_000;
    private const long MaximumInventoryBytes = 1024L * 1024 * 1024;
    private const long MaximumArtifactFileBytes = 64L * 1024 * 1024;
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const string ManifestFileName = ".hve-squad-artifacts.json";
    private const int ManifestFormatVersion = 2;
    private static readonly string[] ArtifactDirectories =
    [
        ".github/agents",
        ".github/instructions",
        ".agents/skills",
    ];

    private readonly string _packageSpec;
    private readonly string _target;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _timeout;
    private readonly ApmInstaller _installer;
    private readonly string? _apmExecutable;

    /// <param name="packageSpec">
    /// An immutable package reference ending in a 40-character commit SHA or semantic-version tag.
    /// </param>
    /// <param name="target">The APM deployment target.</param>
    /// <param name="cacheDirectory">
    /// Optional cache root. A source-and-target-specific child is always used beneath this root.
    /// </param>
    /// <param name="timeout">Maximum duration for lock acquisition and the external APM process.</param>
    /// <param name="installer">Optional installer seam. The default invokes the APM CLI.</param>
    /// <param name="apmExecutable">Optional path to the native APM executable.</param>
    public ApmArtifactSource(
        string packageSpec,
        string target = "copilot",
        string? cacheDirectory = null,
        TimeSpan? timeout = null,
        ApmInstaller? installer = null,
        string? apmExecutable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var match = PackageSpecPattern().Match(packageSpec);
        if (!match.Success
            || (!CommitPattern().IsMatch(match.Groups["ref"].Value)
                && !VersionTagPattern().IsMatch(match.Groups["ref"].Value)))
        {
            throw new ArgumentException(
                $"'{packageSpec}' is not an immutable APM reference. Use owner/repo#vX.Y.Z or a 40-character commit SHA.",
                nameof(packageSpec));
        }

        if (!TargetPattern().IsMatch(target))
        {
            throw new ArgumentException($"'{target}' is not a valid target name or target list.", nameof(target));
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        _packageSpec = packageSpec;
        _target = target;
        _timeout = effectiveTimeout;
        _installer = installer ?? InstallWithCliAsync;
        _apmExecutable = apmExecutable;

        var cacheRoot = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hve-squad-maf",
            "artifacts");
        _cacheDirectory = Path.Combine(Path.GetFullPath(cacheRoot), CacheKey(packageSpec, target));
    }

    public string CacheDirectory => _cacheDirectory;

    public string CacheManifestPath => Path.Combine(_cacheDirectory, ManifestFileName);

    public override async ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheDirectory)!);

        await using var cacheLock = await AcquireCacheLockAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsValidCacheAsync(cancellationToken).ConfigureAwait(false))
        {
            await InstallAndPublishAsync(cancellationToken).ConfigureAwait(false);
        }

        var roots = ProjectArtifactSource.ForProject(_cacheDirectory);
        return roots with
        {
            Origin = $"apm install '{_packageSpec}' for target '{_target}' into '{_cacheDirectory}'",
        };
    }

    private async Task InstallAndPublishAsync(CancellationToken cancellationToken)
    {
        var stagingDirectory = $"{_cacheDirectory}.staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var request = new ApmInstallRequest(_packageSpec, _target, stagingDirectory, _timeout);
            try
            {
                await _installer(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SquadArtifactsNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SquadArtifactsNotFoundException(
                    $"APM acquisition of '{_packageSpec}' for target '{_target}' failed.",
                    ex);
            }

            IReadOnlyList<CacheFile> files;
            try
            {
                files = await CreateInventoryAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new SquadArtifactsNotFoundException(
                    $"APM acquisition of '{_packageSpec}' produced an unsafe or unsupported artifact tree.",
                    ex);
            }

            if (!HasCompleteArtifactSet(files))
            {
                throw new SquadArtifactsNotFoundException(
                    $"apm install completed but did not produce agents, instructions, and skills under '{stagingDirectory}'.");
            }

            // These hashes detect local cache changes after a trusted APM acquisition. They do not
            // independently authenticate upstream release content.
            var manifest = new CacheManifest(ManifestFormatVersion, _packageSpec, _target, files);
            var manifestJson = JsonSerializer.Serialize(manifest);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, ManifestFileName),
                manifestJson,
                cancellationToken).ConfigureAwait(false);

            PublishStagingDirectory(stagingDirectory);
        }
        finally
        {
            TryDeleteFileSystemEntry(stagingDirectory);
        }
    }

    private async Task<bool> IsValidCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            RejectReparsePoint(_cacheDirectory);
            if (!File.Exists(CacheManifestPath))
            {
                return false;
            }

            var manifestInfo = new FileInfo(CacheManifestPath);
            if (manifestInfo.Length > MaximumManifestBytes
                || (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(CacheManifestPath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<CacheManifest>(json);
            if (manifest is null
                || manifest.FormatVersion != ManifestFormatVersion
                || !string.Equals(manifest.PackageSpec, _packageSpec, StringComparison.Ordinal)
                || !string.Equals(manifest.Target, _target, StringComparison.Ordinal)
                || manifest.Files is null
                || manifest.Files.Count > MaximumInventoryFiles)
            {
                return false;
            }

            var actual = await CreateInventoryAsync(_cacheDirectory, cancellationToken).ConfigureAwait(false);
            return HasCompleteArtifactSet(actual)
                && manifest.Files.SequenceEqual(actual);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async ValueTask<FileStream> AcquireCacheLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = $"{_cacheDirectory}.lock";
        var started = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (started.Elapsed < _timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new SquadArtifactsNotFoundException(
                    $"Timed out waiting for the artifact cache lock '{lockPath}'.",
                    ex);
            }
        }
    }

    private async ValueTask InstallWithCliAsync(
        ApmInstallRequest request,
        CancellationToken cancellationToken)
    {
        var executable = ResolveApmExecutable(_apmExecutable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add(request.PackageSpec);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(request.Target);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SquadArtifactsNotFoundException(
                $"Could not start the native APM executable '{executable}'. Install APM or provide an ApmInstaller.",
                ex);
        }

        var stdoutTask = CaptureOutputAsync(process.StandardOutput);
        var stderrTask = CaptureOutputAsync(process.StandardError);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            throw new SquadArtifactsNotFoundException(
                $"apm install exceeded {request.Timeout.TotalMinutes:0.#} minutes and was cancelled.");
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new SquadArtifactsNotFoundException(
                $"apm install '{request.PackageSpec}' failed with exit code {process.ExitCode}. " +
                $"{FirstDiagnostic(error) ?? FirstDiagnostic(output) ?? "No diagnostic output."}");
        }
    }

    private static async Task<string> CaptureOutputAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;

        while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) != 0)
        {
            var remaining = MaximumDiagnosticCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(count, remaining));
            }

            truncated |= count > remaining;
        }

        if (truncated)
        {
            result.AppendLine().Append("[output truncated]");
        }

        return result.ToString();
    }

    private static string ResolveApmExecutable(string? configuredExecutable)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            if (OperatingSystem.IsWindows()
                && (configuredExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || configuredExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SquadArtifactsNotFoundException(
                    "APM must be invoked through its native apm.exe, not a command script.");
            }

            return configuredExecutable;
        }

        if (OperatingSystem.IsWindows())
        {
            var releases = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "apm",
                "releases");
            if (Directory.Exists(releases))
            {
                var installed = Directory
                    .EnumerateFiles(releases, "apm.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (installed is not null)
                {
                    return installed;
                }
            }

            return FindOnPath("apm.exe") ?? "apm.exe";
        }

        return FindOnPath("apm") ?? "apm";
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<CacheFile>> CreateInventoryAsync(
        string installationDirectory,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(installationDirectory);

        var files = new List<CacheFile>();
        long totalBytes = 0;
        foreach (var artifactDirectory in ArtifactDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveArtifactDirectory(installationDirectory, artifactDirectory);
            if (root is null)
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(directory);

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            $"Artifact inventory cannot follow the symbolic link or reparse point '{entry}'.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (string.Equals(
                            Path.GetFileName(entry),
                            "apm_modules",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        pending.Push(entry);
                        continue;
                    }

                    var length = new FileInfo(entry).Length;
                    if (length > MaximumArtifactFileBytes)
                    {
                        throw new InvalidDataException(
                            $"Artifact file '{entry}' exceeds the {MaximumArtifactFileBytes}-byte inventory limit.");
                    }

                    totalBytes = checked(totalBytes + length);
                    if (totalBytes > MaximumInventoryBytes || files.Count >= MaximumInventoryFiles)
                    {
                        throw new InvalidDataException(
                            "The installed artifact tree exceeds the cache inventory resource limits.");
                    }

                    await using var stream = new FileStream(
                        entry,
                        new FileStreamOptions
                        {
                            Access = FileAccess.Read,
                            Mode = FileMode.Open,
                            Share = FileShare.Read,
                            BufferSize = 64 * 1024,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        });
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

                    var finalInfo = new FileInfo(entry);
                    if ((finalInfo.Attributes & FileAttributes.ReparsePoint) != 0
                        || finalInfo.Length != length)
                    {
                        throw new InvalidDataException(
                            $"Artifact file '{entry}' changed while its cache inventory was being created.");
                    }

                    files.Add(new CacheFile(
                        Path.GetRelativePath(installationDirectory, entry).Replace('\\', '/'),
                        length,
                        Convert.ToHexString(hash)));
                }
            }
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return files;
    }

    private static string? ResolveArtifactDirectory(string installationDirectory, string relativePath)
    {
        var current = installationDirectory;
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                return null;
            }

            RejectReparsePoint(current);
        }

        return current;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Artifact inventory cannot follow the symbolic link or reparse point '{path}'.");
        }
    }

    private static bool HasCompleteArtifactSet(IReadOnlyList<CacheFile> files)
    {
        return files.Any(static file =>
                file.Path.StartsWith(".github/agents/", StringComparison.Ordinal)
                && file.Path.EndsWith(".agent.md", StringComparison.Ordinal))
            && files.Any(static file =>
                file.Path.StartsWith(".github/instructions/", StringComparison.Ordinal)
                && file.Path.EndsWith(".instructions.md", StringComparison.Ordinal))
            && files.Any(static file =>
                file.Path.StartsWith(".agents/skills/", StringComparison.Ordinal)
                && file.Path.EndsWith("/SKILL.md", StringComparison.Ordinal));
    }

    private void PublishStagingDirectory(string stagingDirectory)
    {
        var replacedDirectory = $"{_cacheDirectory}.replaced-{Guid.NewGuid():N}";
        var movedExistingCache = false;
        var published = false;

        try
        {
            if (TryGetAttributes(_cacheDirectory, out var attributes)
                && (attributes & FileAttributes.Directory) != 0)
            {
                Directory.Move(_cacheDirectory, replacedDirectory);
                movedExistingCache = true;
            }
            else if (attributes is not null)
            {
                File.Move(_cacheDirectory, replacedDirectory);
                movedExistingCache = true;
            }

            Directory.Move(stagingDirectory, _cacheDirectory);
            published = true;
        }
        catch
        {
            if (movedExistingCache
                && !TryGetAttributes(_cacheDirectory, out _))
            {
                MoveFileSystemEntry(replacedDirectory, _cacheDirectory);
                movedExistingCache = false;
            }

            throw;
        }
        finally
        {
            if (published && movedExistingCache)
            {
                TryDeleteFileSystemEntry(replacedDirectory);
            }
        }
    }

    private static void MoveFileSystemEntry(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
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

    private static void TryDeleteFileSystemEntry(string path)
    {
        try
        {
            if (!TryGetAttributes(path, out var attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingLinks(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes? attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = null;
            return false;
        }
    }

    private static void DeleteDirectoryWithoutFollowingLinks(string directory)
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    DeleteDirectoryWithoutFollowingLinks(entry);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(directory);
    }

    private static string CacheKey(string packageSpec, string target)
    {
        var value = Encoding.UTF8.GetBytes($"{packageSpec}\0{target}");
        return Convert.ToHexString(SHA256.HashData(value))[..16].ToLowerInvariant();
    }

    private static string? FirstDiagnostic(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private sealed record CacheManifest(
        int FormatVersion,
        string PackageSpec,
        string Target,
        IReadOnlyList<CacheFile> Files);

    private sealed record CacheFile(string Path, long Length, string Sha256);

    [GeneratedRegex(
        @"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)/(?<repo>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)#(?<ref>[A-Za-z0-9.+-]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackageSpecPattern();

    [GeneratedRegex(@"^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(
        @"^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionTagPattern();

    [GeneratedRegex(
        @"^[a-z][a-z0-9-]*(?:,[a-z][a-z0-9-]*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}
