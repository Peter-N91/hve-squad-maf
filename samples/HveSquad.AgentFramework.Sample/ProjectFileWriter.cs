using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sample;

internal sealed class ProjectFileWriter
{
    private static readonly HashSet<string> ForbiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".copilot-tracking",
        ".agents",
        ".github",
    };

    private readonly string _projectRoot;
    private readonly string _projectPrefix;

    internal ProjectFileWriter(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _projectPrefix = Path.EndsInDirectorySeparator(_projectRoot)
            ? _projectRoot
            : _projectRoot + Path.DirectorySeparatorChar;
        RejectLinks(_projectRoot);
    }

    internal async Task<string> WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var components = path.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\') ||
            path.Contains(':', StringComparison.Ordinal) ||
            components.Any(component => string.IsNullOrEmpty(component) || component is "." or ".." ||
                component.EndsWith(' ') || component.EndsWith('.') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            components.Any(ForbiddenDirectories.Contains))
        {
            throw new InvalidOperationException(
                "write_project_file accepts a contained relative path outside .git, .copilot-tracking, .agents, and .github.");
        }

        var destination = Path.GetFullPath(Path.Combine(_projectRoot, Path.Combine(components)));
        if (!destination.StartsWith(_projectPrefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested path is outside the configured project.");
        }

        RejectLinks(destination);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The requested path has no project-relative parent.");
        Directory.CreateDirectory(parent);
        RejectLinks(destination);
        await File.WriteAllTextAsync(destination, content, cancellationToken);
        return Path.GetRelativePath(_projectRoot, destination);
    }

    internal static SquadToolOutput GetOutputEvidence(object? result)
    {
        var path = result switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(path)
            ? new SquadToolOutput([path])
            : throw new InvalidOperationException("The source writer did not return a valid output path.");
    }

    private static void RejectLinks(string destination)
    {
        for (var current = new DirectoryInfo(destination); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("write_project_file does not traverse symbolic links or reparse points.");
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
