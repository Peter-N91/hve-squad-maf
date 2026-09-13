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

    internal ProjectFiles(string root, int maximumCharacters)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _maximumCharacters = maximumCharacters;
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(_root);
        }

        RejectLinks(_root);
    }

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

        var path = Path.GetFullPath(Path.Combine(_root, Path.Combine(parts)));
        if ((!allowRoot || !path.Equals(_root, PathComparison)) &&
            !path.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException("The path is outside the project.");
        }

        RejectLinks(path);
        return path;
    }

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
        if (IsWithin(normalized, StateRoot) || IsWithin(StateRoot, normalized) ||
            IsWithin(normalized, ".git") || normalized.Contains('(', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{root}' is not an isolated role deliverable root.");
        }

        return normalized;
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
