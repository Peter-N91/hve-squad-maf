using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

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
        var destination = Resolve(path);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The requested path has no project-relative parent.");
        Directory.CreateDirectory(parent);
        RejectLinks(destination);
        await File.WriteAllTextAsync(destination, content, cancellationToken);
        return Path.GetRelativePath(_projectRoot, destination);
    }

    internal ValueTask<SquadEffectReconciliation> ReconcileAsync(
        SquadEffectReconciliationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Arguments.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
            !request.Arguments.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The protected write payload has an invalid argument shape.");
        }

        var destination = Resolve(path.GetString()!);
        var expected = System.Text.Encoding.UTF8.GetBytes(content.GetString()!);
        if (!File.Exists(destination) || new FileInfo(destination).Length != expected.Length ||
            !File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
        {
            return ValueTask.FromResult(new SquadEffectReconciliation(SquadEffectDisposition.CannotConfirm,
                Diagnostic: "The idempotent final-content postcondition is not established; no write was replayed."));
        }

        var relative = Path.GetRelativePath(_projectRoot, destination);
        return ValueTask.FromResult(new SquadEffectReconciliation(SquadEffectDisposition.Executed,
            new([relative]), JsonSerializer.SerializeToElement(relative),
            "The exact final bytes of the idempotent local write were verified."));
    }

    internal IReadOnlyList<string> ProjectWritePaths(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue("path", out var value))
        {
            throw new InvalidOperationException("The source writer requires a projected path.");
        }
        var path = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (path is null)
        {
            throw new InvalidOperationException("The source writer's projected path must be text.");
        }
        return [Path.GetRelativePath(_projectRoot, Resolve(path)).Replace('\\', '/')];
    }

    internal string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
        return destination;
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
