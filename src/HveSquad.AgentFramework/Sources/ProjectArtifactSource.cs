namespace HveSquad.AgentFramework.Sources;

/// <summary>
/// Finds an artifact tree already installed in a project, by walking up from a starting directory
/// until it sees a deployed <c>.github/agents</c> folder. This is the offline default: a consumer
/// who has run <c>apm install</c> needs no configuration and no network.
/// </summary>
public sealed class ProjectArtifactSource : SquadArtifactSource
{
    private readonly string _startDirectory;

    /// <param name="startDirectory">Defaults to the current working directory.</param>
    public ProjectArtifactSource(string? startDirectory = null) =>
        _startDirectory = startDirectory ?? Directory.GetCurrentDirectory();

    public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectRoot = FindProjectRoot(_startDirectory)
            ?? throw new SquadArtifactsNotFoundException(
                $"No installed squad artifacts found at or above '{_startDirectory}'. " +
                "Install the package into the project first, for example: " +
                "apm install \"Peter-N91/hve-squad#vX.Y.Z\" --target copilot");

        return ValueTask.FromResult(Describe(projectRoot));
    }

    /// <summary>Builds roots for a project root that is already known, skipping the upward walk.</summary>
    public static SquadArtifactRoots ForProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        if (!HasDeployedAgents(projectRoot))
        {
            throw new SquadArtifactsNotFoundException(
                $"'{projectRoot}' has no .github/agents/*.agent.md; the package is not installed there.");
        }

        return Describe(projectRoot);
    }

    private static SquadArtifactRoots Describe(string projectRoot)
    {
        var roots = new List<string> { Path.Combine(projectRoot, ".github") };

        // Skills deploy alongside, not inside, the .github tree.
        var skillRoot = Path.Combine(projectRoot, ".agents");
        if (Directory.Exists(skillRoot))
        {
            roots.Add(skillRoot);
        }

        var rosterPath = Path.Combine(projectRoot, ".copilot-tracking", "squad", "team.md");

        return new SquadArtifactRoots(
            roots,
            File.Exists(rosterPath) ? rosterPath : null,
            $"installed project at '{projectRoot}'");
    }

    private static string? FindProjectRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (dir is not null)
        {
            if (HasDeployedAgents(dir.FullName))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool HasDeployedAgents(string projectRoot)
    {
        var agents = Path.Combine(projectRoot, ".github", "agents");

        return Directory.Exists(agents)
            && Directory.EnumerateFiles(agents, "*.agent.md", SearchOption.AllDirectories).Any();
    }
}

public sealed class SquadArtifactsNotFoundException : Exception
{
    public SquadArtifactsNotFoundException()
    {
    }

    public SquadArtifactsNotFoundException(string message)
        : base(message)
    {
    }

    public SquadArtifactsNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
