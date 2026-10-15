namespace HveSquad.AgentFramework.Artifacts;

/// <summary>A released squad profile and the ordered roles it seeds.</summary>
public sealed record SquadProfile(
    string Name,
    IReadOnlyList<string> Roles,
    string? SelectionCue);

/// <summary>A released additive squad pack and the ordered roles it adds.</summary>
public sealed record SquadPack(
    string Name,
    IReadOnlyList<string> Roles,
    string? SelectionCue);

/// <summary>The released cast definition for one role.</summary>
public sealed record SquadRole(
    string Name,
    string? PrimaryAgent,
    IReadOnlyList<string> AlternateAgents,
    string? SelectionCue,
    string? Invocation,
    string? DefaultTier,
    string? DeliverableRoot);

/// <summary>A canonical registered external resource; registration never installs or authorizes it.</summary>
public sealed record SquadRegisteredResource(
    string Role, string Tier, string Resource, string Kind, string Origin,
    string InstallOrEntry, string License, string Prerequisites, string Dispatchability, string VerifiedOn);

/// <summary>A canonical intent rule. Roles are identifiers, never concrete agent names.</summary>
public sealed record SquadRoutingRule(IReadOnlyList<string> Patterns, IReadOnlyList<string> Roles,
    IReadOnlyList<string> OptionalRoles, string AutonomyTier, bool ParallelEligible);

/// <summary>
/// Parses the released cast, profile, and pack tables without embedding a release-specific map.
/// </summary>
public sealed class SquadCatalog
{
    private const string RosterFileName = "squad-roster.instructions.md";
    private readonly SquadArtifacts _artifacts;

    private SquadCatalog(
        SquadArtifacts artifacts,
        IReadOnlyList<SquadProfile> profiles,
        IReadOnlyList<SquadPack> packs,
        IReadOnlyList<SquadRole> roles,
        IReadOnlyList<SquadRegisteredResource> registeredResources,
        IReadOnlyList<SquadRoutingRule> routingRules)
    {
        _artifacts = artifacts;
        Profiles = profiles;
        Packs = packs;
        Roles = roles;
        RegisteredResources = registeredResources;
        RoutingRules = routingRules;
    }

    public IReadOnlyList<SquadProfile> Profiles { get; }

    public IReadOnlyList<SquadPack> Packs { get; }

    public IReadOnlyList<SquadRole> Roles { get; }

    /// <summary>Gets declared prerequisites and acquisition instructions, not inferred permissions.</summary>
    public IReadOnlyList<SquadRegisteredResource> RegisteredResources { get; }
    /// <summary>Declared intent rules, including rules whose required role is not yet selected.</summary>
    public IReadOnlyList<SquadRoutingRule> RoutingRules { get; }

    /// <summary>Loads the canonical split catalog, or the explicitly recognized legacy inline table.</summary>
    public static SquadCatalog Load(SquadArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var matches = artifacts.Instructions
            .Where(i => i.FileName.Equals(RosterFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
        {
            var detail = matches.Count == 0
                ? "none was loaded"
                : $"multiple files have that exact name: {string.Join(", ", matches.Select(m => m.SourcePath))}";
            throw new InvalidDataException(
                $"Expected exactly one '{RosterFileName}' after root precedence was applied; {detail}.");
        }

        var tables = MarkdownTables.Parse(matches[0].Body);
        var castTables = CastTables(artifacts, tables);
        var roleRows = RequireTable(
            castTables,
            "Cast Catalog",
            ["Role", "Primary Agent", "Alternate Agents", "Selection Cue"]);
        var profileRows = RequireTable(
            tables,
            "Squad Profiles",
            ["Profile", "Members"]);
        var packRows = RequireTable(
            tables,
            "Registered Packs",
            ["Pack", "Adds"]);

        var roleBuilders = ParseRoles(roleRows);
        ApplyDeliverableRoots(tables, roleBuilders);
        var routingRules = ApplyRoutingMetadata(artifacts, roleBuilders);
        ApplySeedMetadata(artifacts, roleBuilders);

        var roles = roleBuilders.Values
            .Select(r => r.Build())
            .ToList();
        var knownRoles = roles.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = ParseProfiles(profileRows, knownRoles);
        var packs = ParsePacks(packRows, knownRoles);

        return new SquadCatalog(artifacts, profiles, packs, roles, ParseRegisteredResources(castTables, knownRoles), routingRules);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<SquadRegisteredResource> ParseRegisteredResources(
        IReadOnlyList<MarkdownTable> tables, HashSet<string> knownRoles)
    {
        var matches = tables.Where(t => t.Heading.Equals("Registered External Cast", StringComparison.OrdinalIgnoreCase) &&
            t.HasColumns(["Role", "Resource", "Resource Kind", "Origin", "Install or Entry", "License", "Prerequisites",
                "Dispatchability Verified", "Verified On"])).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException("Multiple canonical Registered External Cast tables were found.");
        }

        if (matches.Length == 0)
        {
            return Array.AsReadOnly(Array.Empty<SquadRegisteredResource>());
        }

        var result = new List<SquadRegisteredResource>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in matches[0].Rows)
        {
            string Required(string column) => RequiredCell(row, column, "Registered External Cast");
            var role = Required("Role");
            var kind = Required("Resource Kind");
            var resource = Required("Resource");
            var tier = matches[0].HasColumns(["Tier"]) ? Required("Tier") : "opt-in";
            if (!knownRoles.Contains(role) || kind is not ("agent" or "skill" or "instruction" or "plugin") ||
                tier is not ("bundled" or "opt-in") || !keys.Add(role + ":" + kind + ":" + resource))
            {
                throw new InvalidDataException($"Invalid or duplicate registered resource '{resource}' for role '{role}'.");
            }

            result.Add(new(role, tier, resource, kind, Required("Origin"), Required("Install or Entry"),
                Required("License"), Required("Prerequisites"), Required("Dispatchability Verified"), Required("Verified On")));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<MarkdownTable> CastTables(
        SquadArtifacts artifacts, IReadOnlyList<MarkdownTable> rosterTables)
    {
        var skills = artifacts.Skills.Where(s => s.Name.Equals("squad", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (skills.Length > 1)
        {
            throw new InvalidDataException("Multiple canonical 'squad' skills remain after artifact precedence.");
        }

        if (skills.Length == 1)
        {
            var files = new Runtime.ProjectFiles(skills[0].DirectoryPath, 200_000);
            const string resource = "references/roster-catalog.md";
            if (File.Exists(files.Resolve(resource)))
            {
                return MarkdownTables.Parse(files.Read(resource));
            }
        }

        if (rosterTables.Any(t => t.Heading.Equals("Cast Catalog", StringComparison.OrdinalIgnoreCase)))
        {
            return rosterTables;
        }

        throw new InvalidDataException(
            "The split cast catalog is missing canonical skill 'squad' resource " +
            "'references/roster-catalog.md'. Reacquire the complete pinned artifact tree; " +
            "profiles, packs and deliverable roots still come from 'squad-roster.instructions.md'.");
    }

    /// <summary>
    /// Composes one profile with zero or more packs. This returns catalog definitions even when an
    /// opt-in agent is not installed; call <see cref="ValidateRoster"/> when dispatch is required.
    /// </summary>
    public IReadOnlyList<SquadRosterEntry> ResolveRoster(
        string profile,
        IEnumerable<string>? packs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var selectedProfile = Profiles.SingleOrDefault(
            p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Unknown squad profile '{profile}'. Available profiles: " +
                $"{string.Join(", ", Profiles.Select(p => p.Name))}.");

        var roleNames = new List<string>(selectedProfile.Roles);
        var seen = roleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var packName in packs ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packName);
            var selectedPack = Packs.SingleOrDefault(
                p => p.Name.Equals(packName, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException(
                    $"Unknown squad pack '{packName}'. Available packs: " +
                    $"{string.Join(", ", Packs.Select(p => p.Name))}.");

            foreach (var role in selectedPack.Roles)
            {
                if (seen.Add(role))
                {
                    roleNames.Add(role);
                }
            }
        }

        return ResolveRoles(roleNames);
    }

    /// <summary>Resolves an explicitly selected custom role subset from the canonical cast.</summary>
    /// <param name="roleNames">Exact canonical role identifiers.</param>
    /// <returns>Ordered canonical definitions, without installing or authorizing missing agents.</returns>
    public IReadOnlyList<SquadRosterEntry> ResolveRoles(IEnumerable<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        var names = roleNames.ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw new InvalidDataException("Custom role identifiers must be nonempty and unique.");
        }
        return
        [
            .. names.Select(name =>
            {
                var role = Roles.SingleOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Unknown custom cast role '{name}'; no invented role or substitute is allowed.");
                if (string.IsNullOrWhiteSpace(role.PrimaryAgent))
                {
                    throw new InvalidOperationException(
                        $"Role '{role.Name}' is selected but has no Primary Agent in the cast catalog.");
                }

                return new SquadRosterEntry
                {
                    Role = role.Name,
                    PrimaryAgent = role.PrimaryAgent,
                    AlternateAgents = role.AlternateAgents,
                    SelectionCue = role.SelectionCue,
                    Invocation = role.Invocation,
                    ModelTier = role.DefaultTier,
                    DeliverableRoot = role.DeliverableRoot,
                };
            })
        ];
    }

    /// <summary>Validates only the selected roster's primary agents, not optional catalog roles.</summary>
    public void ValidateRoster(SquadRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ValidateRoster(roster.Members);
    }

    /// <summary>Validates only the selected entries' primary agents.</summary>
    public void ValidateRoster(IEnumerable<SquadRosterEntry> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        foreach (var entry in roster)
        {
            _ = ResolveAgent(entry);
        }
    }

    /// <summary>
    /// Resolves a concrete installed agent. An alternate is used only when its exact name is
    /// explicitly supplied; selection-cue prose is never interpreted automatically.
    /// </summary>
    public AgentCharter ResolveAgent(
        SquadRosterEntry entry,
        string? alternateAgent = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var agentName = entry.PrimaryAgent;
        if (!string.IsNullOrWhiteSpace(alternateAgent))
        {
            if (!entry.AlternateAgents.Contains(alternateAgent, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Agent '{alternateAgent}' is not a declared alternate for role '{entry.Role}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.SelectionCue))
            {
                throw new InvalidDataException(
                    $"Role '{entry.Role}' declares alternates but has no Selection Cue.");
            }

            agentName = entry.AlternateAgents.Single(
                a => a.Equals(alternateAgent, StringComparison.OrdinalIgnoreCase));
        }

        var charter = _artifacts.FindCharter(agentName);
        if (charter is null)
        {
            var registered = RegisteredResources.Where(r =>
                r.Role.Equals(entry.Role, StringComparison.OrdinalIgnoreCase) && r.Kind == "agent").ToArray();
            var guidance = registered.Length == 0
                ? " Restore the complete pinned installation or explicitly register the required resource; no substitute is allowed."
                : " Registered installation prerequisites:\n" + string.Join("\n", registered.Select(r =>
                    $"{r.Resource}: {r.InstallOrEntry}; prerequisites: {r.Prerequisites}"));
            throw new InvalidOperationException(
                $"Role '{entry.Role}' resolves to agent '{agentName}', but that agent is not installed." + guidance);
        }

        if (charter.DisableModelInvocation)
        {
            throw new InvalidOperationException(
                $"Role '{entry.Role}' resolves to agent '{agentName}', but its charter sets " +
                "'disable-model-invocation: true'.");
        }

        return charter;
    }

    /// <summary>Resolves an owned roster row and then its explicitly selected agent.</summary>
    public AgentCharter ResolveAgent(
        SquadRoster roster,
        string role,
        string? owner = null,
        string? alternateAgent = null)
    {
        ArgumentNullException.ThrowIfNull(roster);

        var entry = roster.Resolve(role, owner)
            ?? throw new KeyNotFoundException($"Role '{role}' is not in the selected roster.");
        return ResolveAgent(entry, alternateAgent);
    }

    private static Dictionary<string, RoleBuilder> ParseRoles(MarkdownTable table)
    {
        var roles = new Dictionary<string, RoleBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var name = RequiredCell(row, "Role", "Cast Catalog");
            if (!roles.TryAdd(name, new RoleBuilder
            {
                Name = name,
                PrimaryAgent = SemanticCell(row, "Primary Agent"),
                AlternateAgents = SplitNames(SemanticCell(row, "Alternate Agents")),
                SelectionCue = SemanticCell(row, "Selection Cue"),
                Invocation = SemanticCell(row, "Invocation"),
                DefaultTier = SemanticCell(row, "Model Tier") ?? SemanticCell(row, "Default Tier"),
                DeliverableRoot = SemanticCell(row, "Deliverable Root"),
            }))
            {
                throw new InvalidDataException($"Cast Catalog defines role '{name}' more than once.");
            }
        }

        if (roles.Count == 0)
        {
            throw new InvalidDataException("Cast Catalog has no role rows.");
        }

        return roles;
    }

    private static List<SquadProfile> ParseProfiles(
        MarkdownTable table,
        IReadOnlySet<string> knownRoles)
    {
        var profiles = new List<SquadProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var name = RequiredCell(row, "Profile", "Squad Profiles");
            var roleNames = SplitNames(RequiredCell(row, "Members", "Squad Profiles"));
            ValidateDefinition("profile", name, roleNames, knownRoles);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Squad Profiles defines '{name}' more than once.");
            }

            profiles.Add(new SquadProfile(
                name,
                roleNames,
                SemanticCell(row, "Choose when") ?? SemanticCell(row, "Use When")));
        }

        return profiles.Count > 0
            ? profiles
            : throw new InvalidDataException("Squad Profiles has no profile rows.");
    }

    private static List<SquadPack> ParsePacks(
        MarkdownTable table,
        IReadOnlySet<string> knownRoles)
    {
        var packs = new List<SquadPack>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var name = RequiredCell(row, "Pack", "Registered Packs");
            var roleNames = SplitNames(RequiredCell(row, "Adds", "Registered Packs"));
            ValidateDefinition("pack", name, roleNames, knownRoles);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Registered Packs defines '{name}' more than once.");
            }

            packs.Add(new SquadPack(
                name,
                roleNames,
                SemanticCell(row, "Choose when") ?? SemanticCell(row, "Use When")));
        }

        return packs.Count > 0
            ? packs
            : throw new InvalidDataException("Registered Packs has no pack rows.");
    }

    private static void ValidateDefinition(
        string kind,
        string name,
        IReadOnlyList<string> roleNames,
        IReadOnlySet<string> knownRoles)
    {
        if (roleNames.Count == 0)
        {
            throw new InvalidDataException($"Squad {kind} '{name}' has no roles.");
        }

        var unknown = roleNames.Where(r => !knownRoles.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException(
                $"Squad {kind} '{name}' references undefined cast role(s): " +
                $"{string.Join(", ", unknown)}.");
        }
    }

    private static void ApplyDeliverableRoots(
        IReadOnlyList<MarkdownTable> tables,
        IReadOnlyDictionary<string, RoleBuilder> roles)
    {
        var candidates = tables
            .Where(t => t.Heading.Equals("Deliverable Roots", StringComparison.OrdinalIgnoreCase)
                && t.HasColumns(["Role", "Deliverable Root"]))
            .ToList();
        if (candidates.Count > 1)
        {
            throw new InvalidDataException("Multiple compatible Deliverable Roots tables were found.");
        }

        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var row in candidates[0].Rows)
        {
            var root = SemanticCell(row, "Deliverable Root");
            foreach (var roleName in SplitNames(RequiredCell(row, "Role", "Deliverable Roots")))
            {
                if (!roles.TryGetValue(roleName, out var role))
                {
                    throw new InvalidDataException(
                        $"Deliverable Roots references undefined cast role '{roleName}'.");
                }

                role.DeliverableRoot = root;
            }
        }
    }

    private static void ApplySeedMetadata(
        SquadArtifacts artifacts,
        IReadOnlyDictionary<string, RoleBuilder> roles)
    {
        var squadSkills = artifacts.Skills
            .Where(s => s.Name.Equals("squad", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (squadSkills.Count > 1)
        {
            throw new InvalidDataException(
                $"Multiple skills named 'squad' were loaded: " +
                $"{string.Join(", ", squadSkills.Select(s => s.SourcePath))}.");
        }

        if (squadSkills.Count == 0)
        {
            return;
        }

        var skill = squadSkills[0];
        var referencePath = SafeChildPath(
            skill.DirectoryPath,
            Path.Combine("references", "seed-templates.md"));
        if (!File.Exists(referencePath))
        {
            return;
        }

        MarkdownFrontmatter.Split(File.ReadAllText(referencePath), out _, out var body);
        var candidates = MarkdownTables.Parse(body)
            .Where(t => t.Heading.Equals("Members", StringComparison.OrdinalIgnoreCase)
                && t.HasColumns(["Role", "Agent Name", "Model Tier"]))
            .ToList();
        if (candidates.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one compatible team.md seed table in '{referencePath}', " +
                $"but found {candidates.Count}.");
        }

        foreach (var row in candidates[0].Rows)
        {
            var name = RequiredCell(row, "Role", "team.md seed");
            if (!roles.TryGetValue(name, out var role))
            {
                throw new InvalidDataException(
                    $"team.md seed references undefined cast role '{name}'.");
            }

            role.Invocation = SemanticCell(row, "Invocation") ?? role.Invocation;
            role.DefaultTier = SemanticCell(row, "Model Tier") ?? role.DefaultTier;
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<SquadRoutingRule> ApplyRoutingMetadata(
        SquadArtifacts artifacts,
        IReadOnlyDictionary<string, RoleBuilder> roles)
    {
        var instructions = artifacts.Instructions
            .Where(i => i.FileName.Equals(
                "squad-routing.instructions.md",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (instructions.Count > 1)
        {
            throw new InvalidDataException(
                "Multiple exact 'squad-routing.instructions.md' files remain after root precedence.");
        }

        if (instructions.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<SquadRoutingRule>());
        }

        var tables = MarkdownTables.Parse(instructions[0].Body)
            .Where(t => t.Heading.Equals("Default Routing Rules", StringComparison.OrdinalIgnoreCase)
                && t.HasColumns(["Role", "Autonomy Tier"]))
            .ToList();
        if (tables.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one compatible Default Routing Rules table in " +
                $"'{instructions[0].SourcePath}', but found {tables.Count}.");
        }

        var tiers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<SquadRoutingRule>();
        foreach (var row in tables[0].Rows)
        {
            var autonomy = RequiredCell(row, "Autonomy Tier", "Default Routing Rules");
            if (autonomy is not ("auto" or "confirm" or "escalate" or "auto-validated"))
            {
                throw new InvalidDataException("Default Routing Rules has an unknown autonomy tier.");
            }
            var ruleRoles = new List<string>();
            var optionalRoles = new List<string>();
            foreach (var rawName in SplitNames(RequiredCell(row, "Role", "Default Routing Rules")))
            {
                var roleName = rawName.Split('(', 2)[0].Trim();
                if (!roles.ContainsKey(roleName))
                {
                    throw new InvalidDataException(
                        $"Default Routing Rules references undefined cast role '{roleName}'.");
                }

                if (!tiers.TryGetValue(roleName, out var values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tiers[roleName] = values;
                }

                values.Add(autonomy);
                ruleRoles.Add(roleName);
                if (rawName.Contains("(optional)", StringComparison.OrdinalIgnoreCase))
                {
                    optionalRoles.Add(roleName);
                }
            }
            var patterns = SemanticCell(row, "Pattern / Keyword");
            if (patterns is not null)
            {
                rules.Add(new(patterns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    ruleRoles.AsReadOnly(), optionalRoles.AsReadOnly(), autonomy,
                    SemanticCell(row, "Parallel-Eligible")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true));
            }
        }

        foreach (var (name, autonomyTiers) in tiers)
        {
            var role = roles[name];
            if (!string.IsNullOrWhiteSpace(role.PrimaryAgent))
            {
                role.Invocation ??= "runSubagent / task";
            }

            role.DefaultTier ??= autonomyTiers.Contains("confirm")
                || autonomyTiers.Contains("auto-validated")
                    ? "default"
                    : autonomyTiers.SetEquals(["auto"])
                        ? "fast"
                        : null;
        }
        return rules.AsReadOnly();
    }

    private static string SafeChildPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Resolved catalog reference '{relativePath}' escapes skill directory '{root}'.");
        }

        return candidate;
    }

    private static MarkdownTable RequireTable(
        IReadOnlyList<MarkdownTable> tables,
        string heading,
        IReadOnlyList<string> columns)
    {
        var matches = tables
            .Where(t => t.Heading.Equals(heading, StringComparison.OrdinalIgnoreCase)
                && t.HasColumns(columns))
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one '{heading}' table with columns " +
                $"[{string.Join(", ", columns)}], but found {matches.Count}. " +
                "The released catalog schema may have changed.");
        }

        return matches[0];
    }

    private static string RequiredCell(
        IReadOnlyDictionary<string, string> row,
        string column,
        string table)
    {
        var value = SemanticCell(row, column);
        return value ?? throw new InvalidDataException(
            $"{table} contains a row with a blank '{column}' cell.");
    }

    private static string? SemanticCell(
        IReadOnlyDictionary<string, string> row,
        string column)
    {
        var match = row.FirstOrDefault(
            p => MarkdownTables.Normalize(p.Key).StartsWith(
                MarkdownTables.Normalize(column),
                StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            return null;
        }

        var value = match.Value.Trim();
        if (value is "" or "—" or "-")
        {
            return null;
        }

        return value.Length >= 2 && value[0] == '`' && value[^1] == '`'
            ? value[1..^1].Trim()
            : value;
    }

    private static IReadOnlyList<string> SplitNames(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            :
            [
                .. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => v.Trim('`', ' '))
                    .Where(v => v.Length > 0 && v != "—")
            ];

    private sealed class RoleBuilder
    {
        public required string Name { get; init; }

        public string? PrimaryAgent { get; init; }

        public IReadOnlyList<string> AlternateAgents { get; init; } = [];

        public string? SelectionCue { get; init; }

        public string? Invocation { get; set; }

        public string? DefaultTier { get; set; }

        public string? DeliverableRoot { get; set; }

        public SquadRole Build() => new(
            Name,
            PrimaryAgent,
            AlternateAgents,
            SelectionCue,
            Invocation,
            DefaultTier,
            DeliverableRoot);
    }
}

internal sealed record MarkdownTable(
    string Heading,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)
{
    public bool HasColumns(IReadOnlyList<string> expected) =>
        expected.All(e => Headers.Any(
            h => MarkdownTables.Normalize(h).StartsWith(
                MarkdownTables.Normalize(e),
                StringComparison.Ordinal)));
}

internal static class MarkdownTables
{
    public static IReadOnlyList<MarkdownTable> Parse(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var tables = new List<MarkdownTable>();
        var heading = string.Empty;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('#'))
            {
                heading = line.TrimStart('#').Trim();
                continue;
            }

            if (!line.StartsWith('|') || index + 1 >= lines.Length)
            {
                continue;
            }

            var headers = SplitRow(line);
            var separator = SplitRow(lines[index + 1].Trim());
            if (headers.Count == 0
                || separator.Count != headers.Count
                || separator.Any(c => !IsSeparator(c)))
            {
                continue;
            }

            var rows = new List<IReadOnlyDictionary<string, string>>();
            index += 2;
            while (index < lines.Length && lines[index].Trim().StartsWith('|'))
            {
                var cells = SplitRow(lines[index].Trim());
                if (cells.Count != headers.Count)
                {
                    throw new InvalidDataException(
                        $"Markdown table under '{heading}' has {cells.Count} cells; " +
                        $"expected {headers.Count}.");
                }

                rows.Add(headers
                    .Select((header, cellIndex) => (header, cells[cellIndex]))
                    .ToDictionary(p => p.header, p => p.Item2, StringComparer.OrdinalIgnoreCase));
                index++;
            }

            index--;
            tables.Add(new MarkdownTable(heading, headers, rows));
        }

        return tables;
    }

    public static string Normalize(string value)
    {
        var normalized = new string(
            value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var escaped = false;

        foreach (var character in line.Trim().Trim('|'))
        {
            if (escaped)
            {
                cell.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        if (escaped)
        {
            cell.Append('\\');
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static bool IsSeparator(string cell)
    {
        var value = cell.Trim();
        return value.Length >= 3
            && value.Trim(':').Length >= 3
            && value.Trim(':').All(c => c == '-');
    }
}
