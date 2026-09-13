namespace HveSquad.AgentFramework.Artifacts;

/// <summary>Everything loaded from a squad artifact tree.</summary>
public sealed class SquadArtifacts
{
    public required IReadOnlyList<AgentCharter> Charters { get; init; }

    public required SquadRoster Roster { get; init; }

    /// <summary>Instruction documents after artifact-root override precedence is applied.</summary>
    public IReadOnlyList<SquadInstruction> Instructions { get; init; } = [];

    /// <summary>Skills discovered by their <c>name:</c> metadata, independent of folder name.</summary>
    public IReadOnlyList<SquadSkill> Skills { get; init; } = [];

    /// <summary>Directories containing a <c>SKILL.md</c>, consumable as MAF file skills.</summary>
    public required IReadOnlyList<string> SkillDirectories { get; init; }

    /// <summary>Non-fatal problems encountered while loading, such as a charter missing a name.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public AgentCharter? FindCharter(string name) =>
        Charters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reports <c>agents:</c> entries that name a charter absent from this artifact set. These are
    /// dangling delegation edges and would fail at dispatch time.
    /// </summary>
    public IReadOnlyList<string> FindUnresolvedDelegations()
    {
        var known = Charters.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. from charter in Charters
               from delegateName in charter.DelegateAgents
               where !known.Contains(delegateName)
               select $"'{charter.Name}' delegates to unknown agent '{delegateName}'"
        ];
    }

}

/// <summary>A parsed <c>*.instructions.md</c> artifact.</summary>
public sealed record SquadInstruction(
    string FileName,
    string? Description,
    string? ApplyTo,
    string Body,
    string SourcePath);

/// <summary>A parsed skill entry point and its containing directory.</summary>
public sealed record SquadSkill(
    string Name,
    string? Description,
    string Body,
    string SourcePath)
{
    public string DirectoryPath => Path.GetDirectoryName(SourcePath)!;
}
