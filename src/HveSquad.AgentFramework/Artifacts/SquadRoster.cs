namespace HveSquad.AgentFramework.Artifacts;

/// <summary>
/// One row of the <c>## Members</c> table in <c>.copilot-tracking/squad/team.md</c>.
/// </summary>
public sealed class SquadRosterEntry
{
    public required string Role { get; init; }

    public string? MemberName { get; init; }

    public required string PrimaryAgent { get; init; }

    public IReadOnlyList<string> AlternateAgents { get; init; } = [];

    /// <summary>
    /// The catalog condition under which a caller may explicitly choose an alternate. The
    /// framework does not interpret this prose or choose an alternate automatically.
    /// </summary>
    public string? SelectionCue { get; init; }

    public string? Invocation { get; init; }

    public string? ModelTier { get; init; }

    /// <summary>Directory this role writes its artifact into; scopes the agent's file access.</summary>
    public string? DeliverableRoot { get; init; }

    /// <summary>Roster uniqueness key: roles may repeat only when member names differ.</summary>
    public string Key => string.IsNullOrWhiteSpace(MemberName) ? Role : $"{Role}/{MemberName}";
}

/// <summary>The durable list of roles a coordinator can dispatch.</summary>
public sealed class SquadRoster
{
    public required IReadOnlyList<SquadRosterEntry> Members { get; init; }

    public static SquadRoster Empty { get; } = new() { Members = [] };

    public SquadRosterEntry? FindByRole(string role) => Resolve(role);

    /// <summary>
    /// Resolves a role and optional owner. Repeated roles must be disambiguated explicitly.
    /// </summary>
    public SquadRosterEntry? Resolve(string role, string? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var matches = Members
            .Where(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(owner))
        {
            var owned = matches
                .Where(m => string.Equals(m.MemberName, owner, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return owned.Count switch
            {
                0 => throw new InvalidOperationException(
                    $"Role '{role}' has no member named '{owner}'. Available owners: " +
                    $"{string.Join(", ", matches.Select(m => m.MemberName ?? "(unnamed)"))}."),
                1 => owned[0],
                _ => throw new InvalidDataException(
                    $"Roster contains duplicate owner '{owner}' for role '{role}'."),
            };
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Role '{role}' has multiple members. Specify owner=<Member Name>; available " +
                $"owners: {string.Join(", ", matches.Select(m => m.MemberName ?? "(unnamed)"))}."),
        };
    }
}
