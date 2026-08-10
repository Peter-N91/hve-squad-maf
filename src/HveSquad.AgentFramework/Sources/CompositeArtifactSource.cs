namespace HveSquad.AgentFramework.Sources;

/// <summary>Merges several sources; earlier sources win when a charter name collides.</summary>
public sealed class CompositeArtifactSource : SquadArtifactSource
{
    private readonly IReadOnlyList<SquadArtifactSource> _sources;

    public CompositeArtifactSource(params SquadArtifactSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Length == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        _sources = sources;
    }

    public override async ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var roots = new List<string>();
        var origins = new List<string>();
        string? rosterPath = null;

        foreach (var source in _sources)
        {
            var resolved = await source.ResolveAsync(cancellationToken).ConfigureAwait(false);

            roots.AddRange(resolved.Roots);
            origins.Add(resolved.Origin);
            rosterPath ??= resolved.RosterPath;
        }

        return new SquadArtifactRoots(roots, rosterPath, string.Join(" then ", origins));
    }
}

/// <summary>Uses artifact directories that are already known, without any discovery.</summary>
public sealed class DirectoryArtifactSource : SquadArtifactSource
{
    private readonly SquadArtifactRoots _roots;

    public DirectoryArtifactSource(IEnumerable<string> roots, string? rosterPath = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var list = roots.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one root is required.", nameof(roots));
        }

        _roots = new SquadArtifactRoots(list, rosterPath, $"explicit directories: {string.Join(", ", list)}");
    }

    public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_roots);
}
