namespace HveSquad.AgentFramework.Sources;

/// <summary>Artifact roots resolved from some acquisition strategy, ready to hand to the loader.</summary>
/// <param name="Roots">Directories to search, in precedence order.</param>
/// <param name="RosterPath">Path to <c>team.md</c>, or null when the squad has not run yet.</param>
/// <param name="Origin">Human-readable description of where these roots came from, for diagnostics.</param>
public sealed record SquadArtifactRoots(IReadOnlyList<string> Roots, string? RosterPath, string Origin);

/// <summary>
/// Supplies the artifact directories the loader reads.
/// </summary>
/// <remarks>
/// This adapter never fetches hve-squad from git. A clone contains only <c>squad-src/</c>; the
/// complete charter set is produced by <c>apm install</c>, which resolves the pinned dependency
/// graph. Sources therefore either locate an already-installed tree or delegate to the package
/// manager.
/// </remarks>
public abstract class SquadArtifactSource
{
    public abstract ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default);
}
