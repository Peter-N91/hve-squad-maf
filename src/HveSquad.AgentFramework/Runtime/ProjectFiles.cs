using System.Security.Cryptography;
using System.Text;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// No traversal, alternate streams or links. This protects against agent-selected paths, not a
/// hostile process concurrently replacing directories; hosts must isolate untrusted local writers.
/// </summary>
internal sealed class ProjectFiles
{
    internal const string StateRoot = ".copilot-tracking/squad";
    internal const int MaximumHostOutputBytes = 64 * 1024 * 1024;
    private readonly string _root;
    private readonly int _maximumCharacters;
    private Dictionary<string, string>? _historicalPaths;
    private static readonly string[] WriterFiles =
    [
        "state.json", "state.pending.json", "team.md", "routing.md", "decisions.md", "notifications.md",
        "consumption.md", "consumption-rates.md", "native.lock", "federation.md", "meta-routing.md",
        "federation.pending.json", "federation.run.lock",
    ];
    private static readonly string[] WriterDirectories = ["history", "returns", "payloads", "migrations", "memory"];

    internal ProjectFiles(string root, int maximumCharacters, string squadRoot = StateRoot)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _maximumCharacters = maximumCharacters;
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(_root);
        }

        RejectLinks(_root);
        SquadRoot = squadRoot.Replace('\\', '/').TrimEnd('/');
        _ = Resolve(SquadRoot);
        if (IsWithin(SquadRoot, ".git") || IsWithin(SquadRoot, ".github") || IsWithin(SquadRoot, ".agents") ||
            IsWithin(StateRoot, SquadRoot) && SquadRoot != StateRoot ||
            SquadRoot == StateRoot + "/members" ||
            IsWithin(SquadRoot, StateRoot) && SquadRoot != StateRoot &&
            !IsWithin(SquadRoot, StateRoot + "/members"))
        {
            throw new InvalidOperationException("SquadRoot must be a contained, isolated native state directory.");
        }

        if (IsWithin(SquadRoot, StateRoot + "/members"))
        {
            var name = SquadRoot[(StateRoot.Length + "/members/".Length)..];
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                throw new InvalidOperationException("A member root requires one lower-kebab-case member name.");
            }

            IsMemberRoot = true;
        }
    }

    internal string SquadRoot { get; }
    internal bool IsMemberRoot { get; }
    internal string ProjectRoot => _root;

    internal string Resolve(string relativePath, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only project-relative paths without alternate streams are allowed.");
        }

        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
        {
            throw new InvalidOperationException("Traversal and ambiguous path components are forbidden.");
        }

        if (_historicalPaths?.TryGetValue(string.Join('/', parts), out var relocated) == true)
        {
            parts = relocated.Split('/');
        }
        var path = Path.GetFullPath(Path.Combine(_root, Path.Combine(parts)));
        if ((!allowRoot || !path.Equals(_root, PathComparison)) &&
            !path.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException("The path is outside the project.");
        }

        RejectLinks(path);
        return path;
    }

    internal void BindHistoricalPaths(NativeRelocation? relocation)
    {
        _historicalPaths = null;
        if (relocation is null)
        {
            return;
        }
        if (!IsMemberRoot || relocation.SourceRoot != StateRoot || relocation.TargetRoot != SquadRoot ||
            !Guid.TryParseExact(relocation.OperationId, "N", out _) ||
            relocation.Files.Count == 0 || relocation.Files.Count > FederationPersistence.MaximumFiles ||
            relocation.Seal != HumanActions.Hash(relocation with { Seal = null }) ||
            relocation.OriginalStatePath != SquadRoot + "/migrations/federation-" + relocation.OperationId + "/original-state.json" ||
            HashFile(relocation.OriginalStatePath) != relocation.OriginalStateSha256)
        {
            throw new InvalidDataException("history-integrity: the native relocation lost its exact original-state binding.");
        }
        var paths = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in relocation.Files)
        {
            _ = Resolve(file.Source);
            _ = Resolve(file.Destination);
            if (!file.Source.StartsWith(".copilot-tracking/", StringComparison.Ordinal) ||
                !IsWithin(file.Destination, SquadRoot) || file.Source.Contains('\\', StringComparison.Ordinal) ||
                file.Destination.Contains('\\', StringComparison.Ordinal) ||
                file.Destination != FederationPersistence.RebaseRoot(file.Source, SquadRoot) ||
                !HumanActions.IsHash(file.Sha256) || file.Bytes < 0 || file.Bytes > MaximumHostOutputBytes ||
                !paths.TryAdd(file.Source, file.Destination))
            {
                throw new InvalidDataException("history-integrity: a historical file mapping escaped or duplicated its member scope.");
            }
        }
        _historicalPaths = paths;
    }

    internal bool SameEvidencePath(string actual, string expected) =>
        Resolve(actual).Equals(Resolve(expected), PathComparison);

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsWithin(string path, string directory) =>
        path.Equals(directory, PathComparison) ||
        path.StartsWith(directory.TrimEnd('/') + "/", PathComparison);

    internal string ValidateDeliverableRoot(string root)
    {
        var normalized = root.Replace('\\', '/').TrimEnd('/')
            .Replace("<date>", DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        _ = Resolve(normalized);
        if (IsProtectedStatePath(normalized) || IsWithin(SquadRoot, normalized) ||
            IsWithin(normalized, ".git") || normalized.Contains('(', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{root}' is not an isolated role deliverable root.");
        }

        if (IsMemberRoot && !IsWithin(normalized, SquadRoot) &&
            !IsWithin(normalized, "docs") && !IsWithin(normalized, "outputs"))
        {
            throw new InvalidOperationException($"'{root}' escapes the member's deliverable scope.");
        }

        return normalized;
    }

    internal bool IsProtectedStatePath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimEnd('/');
        foreach (var root in new[] { StateRoot, SquadRoot }.Distinct(StringComparer.Ordinal))
        {
            if (!IsWithin(path, root))
            {
                continue;
            }

            var child = path.Length == root.Length ? "" : path[(root.Length + 1)..];
            if (child.Length == 0 || WriterFiles.Any(file => child.Equals(file, PathComparison)) ||
                WriterDirectories.Any(directory => IsWithin(child, directory)))
            {
                return true;
            }

            if (root == StateRoot && child.StartsWith("members/", PathComparison))
            {
                var parts = child.Split('/');
                if (parts.Length < 3 || WriterFiles.Any(file => parts[2].Equals(file, PathComparison)) ||
                    WriterDirectories.Any(directory => parts[2].Equals(directory, PathComparison)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal void CheckAgentRead(string relativePath, IEnumerable<string> explicitInputs)
    {
        _ = Resolve(relativePath);
        var path = relativePath.Replace('\\', '/').TrimEnd('/');
        if (IsMemberRoot && !IsWithin(path, SquadRoot) &&
            !explicitInputs.Any(input => input.Replace('\\', '/').Equals(path, PathComparison)))
        {
            throw new InvalidOperationException(
                "Member reads are limited to their own root and explicit host-approved read-only input paths.");
        }
    }

    internal string Read(string relativePath, int? maximumCharacters = null)
    {
        var limit = maximumCharacters ?? _maximumCharacters;
        var path = Resolve(relativePath);
        if (new FileInfo(path).Length > (long)limit * 4)
        {
            throw new InvalidOperationException("File exceeds the configured read limit.");
        }

        var text = File.ReadAllText(path);
        if (text.Length > limit)
        {
            throw new InvalidOperationException("File exceeds the configured read limit.");
        }

        return text;
    }

    internal string[] List(string relativeDirectory)
    {
        var directory = relativeDirectory == "." ? _root : Resolve(relativeDirectory);
        RejectLinks(directory);
        return Directory.EnumerateFileSystemEntries(directory).Take(1000)
            .Select(path =>
            {
                RejectLinks(path);
                return Path.GetRelativePath(_root, path).Replace('\\', '/');
            }).ToArray();
    }

    internal string HashFile(string relativePath)
    {
        using var stream = new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumHostOutputBytes)
        {
            throw new InvalidDataException("Host output exceeds the 64 MiB byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var remaining = MaximumHostOutputBytes;
        int read;
        // Bound bytes actually read as well as the initial length, including a growing file.
        while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1))) != 0)
        {
            remaining -= read;
            if (remaining < 0)
            {
                throw new InvalidDataException("Host output exceeds the 64 MiB byte limit.");
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal byte[] ReadBytes(string relativePath)
    {
        using var stream = new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumHostOutputBytes)
        {
            throw new InvalidDataException("Protected evidence exceeds the 64 MiB byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Protected evidence grew during verification.");
        }

        return bytes;
    }

    internal void AppendBytes(string relativePath, ReadOnlySpan<byte> bytes)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (stream.Length + bytes.Length > MaximumHostOutputBytes)
        {
            throw new InvalidDataException("Protected history exceeds the 64 MiB byte limit.");
        }
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal void Write(string relativePath, string text, bool append = false, int? maximumCharacters = null)
    {
        if (text.Length > (maximumCharacters ?? _maximumCharacters))
        {
            throw new InvalidOperationException("Artifact exceeds the configured size limit.");
        }

        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        if (append)
        {
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(path, text, Encoding.UTF8);
        }
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            // GetAttributes also sees file links and dangling links, unlike File.Exists.
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Symbolic links/reparse points are forbidden: {current.FullName}");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
