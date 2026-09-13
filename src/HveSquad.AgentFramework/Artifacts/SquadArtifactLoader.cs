using YamlDotNet.Serialization;

namespace HveSquad.AgentFramework.Artifacts;

/// <summary>
/// Loads squad agent charters, the roster, and skill directories from a deployed or source
/// artifact tree. The markdown artifacts remain the source of truth; this type only reads them.
/// </summary>
public static class SquadArtifactLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Loads every artifact under <paramref name="artifactRoot"/>, which should point at a
    /// <c>.github</c> directory (deployed) or a <c>squad-src/.github</c> directory (source).
    /// </summary>
    /// <param name="rosterPath">
    /// Optional path to <c>team.md</c>. The roster is created at runtime by the coordinator, so an
    /// absent file yields an empty roster rather than an error.
    /// </param>
    public static SquadArtifacts Load(string artifactRoot, string? rosterPath = null) =>
        Load([artifactRoot], rosterPath);

    /// <summary>
    /// Loads and merges several artifact roots. Roots are searched in order and the first root to
    /// define a charter name wins, so a local source tree can override an installed dependency.
    /// A squad source tree alone is incomplete: its charters delegate to HVE Core agents that only
    /// exist once the package is installed, so pass the dependency roots too.
    /// </summary>
    public static SquadArtifacts Load(IEnumerable<string> artifactRoots, string? rosterPath = null)
    {
        ArgumentNullException.ThrowIfNull(artifactRoots);

        var roots = artifactRoots.ToList();
        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one artifact root is required.", nameof(artifactRoots));
        }

        var warnings = new List<string>();
        var charters = new List<AgentCharter>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var instructions = new List<SquadInstruction>();
        var claimedInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<SquadSkill>();
        var claimedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Artifact root not found: {root}");
            }

            var fromThisRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(root, "*.agent.md", SearchOption.AllDirectories).Order())
            {
                AgentCharter charter;

                try
                {
                    charter = LoadCharter(file);
                }
                catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException)
                {
                    warnings.Add($"Skipped '{file}': {ex.Message}");
                    continue;
                }

                if (fromThisRoot.TryGetValue(charter.Name, out var sibling))
                {
                    warnings.Add($"Duplicate charter name '{charter.Name}' within '{root}': {sibling} and {file}");
                    continue;
                }

                fromThisRoot[charter.Name] = file;

                // Cross-root collisions are precedence, not error: the earlier root already won.
                if (claimed.ContainsKey(charter.Name))
                {
                    continue;
                }

                claimed[charter.Name] = file;
                charters.Add(charter);
            }

            var rootInstructionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory
                .EnumerateFiles(root, "*.instructions.md", SearchOption.AllDirectories)
                .Order())
            {
                var fileName = Path.GetFileName(file);
                if (claimedInstructions.Contains(fileName))
                {
                    continue;
                }

                instructions.Add(LoadInstruction(file));
                rootInstructionNames.Add(fileName);
            }

            claimedInstructions.UnionWith(rootInstructionNames);

            var rootSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories).Order())
            {
                SquadSkill skill;
                try
                {
                    skill = LoadSkill(file);
                }
                catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException)
                {
                    warnings.Add($"Skipped '{file}': {ex.Message}");
                    continue;
                }

                if (claimedSkills.Contains(skill.Name))
                {
                    continue;
                }

                skills.Add(skill);
                rootSkillNames.Add(skill.Name);
            }

            claimedSkills.UnionWith(rootSkillNames);
        }

        var roster = rosterPath is not null && File.Exists(rosterPath)
            ? LoadRoster(rosterPath)
            : SquadRoster.Empty;

        return new SquadArtifacts
        {
            Charters = charters,
            Roster = roster,
            Instructions = instructions,
            Skills = skills,
            SkillDirectories =
            [
                .. skills.Select(s => s.DirectoryPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order()
            ],
            Warnings = warnings,
        };
    }

    /// <summary>Parses a single <c>*.agent.md</c> file into a charter.</summary>
    public static AgentCharter LoadCharter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MarkdownFrontmatter.Split(File.ReadAllText(path), out var yaml, out var body);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidDataException("Agent charter has no YAML frontmatter.");
        }

        var fields = Yaml.Deserialize<Dictionary<string, object?>>(yaml)
            ?? throw new InvalidDataException("Agent charter frontmatter is empty.");

        // Some charters omit `name:`; the host derives it from the filename, so mirror that.
        var name = AsString(fields.GetValueOrDefault("name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = DeriveNameFromPath(path);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidDataException($"Agent charter '{name}' has no body to use as instructions.");
        }

        return new AgentCharter
        {
            Name = name,
            Description = AsString(fields.GetValueOrDefault("description")),
            UserInvocable = AsBool(fields.GetValueOrDefault("user-invocable")) ?? true,
            DisableModelInvocation =
                AsBool(fields.GetValueOrDefault("disable-model-invocation")) ?? false,
            Tools = AsStringList(fields.GetValueOrDefault("tools")),
            ModelPreferences = AsStringList(fields.GetValueOrDefault("model")),
            DelegateAgents = AsStringList(fields.GetValueOrDefault("agents")),
            Instructions = body.TrimEnd(),
            SourcePath = path,
        };
    }

    /// <summary>Parses one <c>*.instructions.md</c> document.</summary>
    public static SquadInstruction LoadInstruction(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MarkdownFrontmatter.Split(File.ReadAllText(path), out var yaml, out var body);
        var fields = string.IsNullOrWhiteSpace(yaml)
            ? []
            : Yaml.Deserialize<Dictionary<string, object?>>(yaml) ?? [];

        return new SquadInstruction(
            Path.GetFileName(path),
            AsString(fields.GetValueOrDefault("description")),
            AsString(fields.GetValueOrDefault("applyTo")),
            body.TrimEnd(),
            path);
    }

    /// <summary>Parses a skill entry point and requires its metadata name.</summary>
    public static SquadSkill LoadSkill(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MarkdownFrontmatter.Split(File.ReadAllText(path), out var yaml, out var body);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidDataException("Skill has no YAML frontmatter.");
        }

        var fields = Yaml.Deserialize<Dictionary<string, object?>>(yaml)
            ?? throw new InvalidDataException("Skill frontmatter is empty.");
        var name = AsString(fields.GetValueOrDefault("name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("Skill frontmatter has no name.");
        }

        return new SquadSkill(
            name,
            AsString(fields.GetValueOrDefault("description")),
            body.TrimEnd(),
            path);
    }

    /// <summary>Parses the <c>## Members</c> table out of a <c>team.md</c> roster file.</summary>
    public static SquadRoster LoadRoster(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var lines = File.ReadAllLines(path);
        var inMembers = false;
        List<string>? headers = null;
        var entries = new List<SquadRosterEntry>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                inMembers = line.TrimStart('#').Trim().Equals("Members", StringComparison.OrdinalIgnoreCase);
                headers = null;
                continue;
            }

            if (!inMembers || !line.StartsWith('|'))
            {
                continue;
            }

            var cells = SplitRow(line);

            if (headers is null)
            {
                headers = cells;
                continue;
            }

            if (cells.Count > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                continue;
            }

            var primary = Cell(headers, cells, "Agent Name (Primary)");
            if (string.IsNullOrWhiteSpace(primary))
            {
                continue;
            }

            entries.Add(new SquadRosterEntry
            {
                Role = Cell(headers, cells, "Role") ?? string.Empty,
                MemberName = NullIfBlank(Cell(headers, cells, "Member Name")),
                PrimaryAgent = primary,
                AlternateAgents = SplitList(Cell(headers, cells, "Alternate Agents")),
                SelectionCue = NullIfBlank(Cell(headers, cells, "Selection Cue")),
                Invocation = NullIfBlank(Cell(headers, cells, "Invocation")),
                ModelTier = NullIfBlank(Cell(headers, cells, "Model Tier")),
                DeliverableRoot = NullIfBlank(Cell(headers, cells, "Deliverable Root")),
            });
        }

        return new SquadRoster { Members = entries };
    }

    private static List<string> SplitRow(string line) =>
        [.. line.Trim('|').Split('|').Select(c => c.Trim())];

    private static string DeriveNameFromPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".agent.md".Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? Cell(List<string> headers, List<string> cells, string header)
    {
        var index = headers.FindIndex(h => h.Equals(header, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index < cells.Count ? cells[index] : null;
    }

    private static string? NullIfBlank(string? value) =>
        IsBlankOrDash(value) ? null : value!.Trim();

    private static IReadOnlyList<string> SplitList(string? value) =>
        IsBlankOrDash(value)
            ? []
            :
            [
                .. value!.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => !IsBlankOrDash(item))
            ];

    private static bool IsBlankOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() is "—" or "-";

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        _ => value.ToString()?.Trim(),
    };

    private static bool? AsBool(object? value) =>
        bool.TryParse(AsString(value), out var parsed) ? parsed : null;

    private static IReadOnlyList<string> AsStringList(object? value) => value switch
    {
        null => [],
        string s => string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()],
        IEnumerable<object?> items => [.. items.Select(AsString).Where(s => !string.IsNullOrWhiteSpace(s))!],
        _ => [],
    };
}
