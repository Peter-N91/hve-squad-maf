using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Artifacts;

/// <summary>A dated, declared model snapshot, never evidence of host availability.</summary>
public sealed partial class SquadModelCatalog
{
    private static readonly string[] Classes =
        ["research", "planning", "implementation", "review", "council", "intake", "bookkeeping"];
    private readonly IReadOnlyDictionary<string, string> _roleClasses;

    private SquadModelCatalog(DateOnly retrieved, IReadOnlyList<SquadCatalogModel> models,
        IReadOnlyDictionary<string, string> roleClasses, IReadOnlyDictionary<string, string> capabilities)
    {
        Retrieved = retrieved;
        Models = models;
        _roleClasses = roleClasses;
        Capabilities = capabilities;
    }

    /// <summary>The source snapshot's retrieval date, not a live-provider heartbeat.</summary>
    public DateOnly Retrieved { get; }

    /// <summary>Declared capability and assignment-fit rows in their canonical order.</summary>
    public IReadOnlyList<SquadCatalogModel> Models { get; }
    /// <summary>All declared capability rows, including IDs excluded from automatic fit ranking.</summary>
    public IReadOnlyDictionary<string, string> Capabilities { get; }

    /// <summary>Parses the installed catalog and its own assignment-class mapping.</summary>
    /// <param name="catalog">The canonical model-catalog resource.</param>
    /// <param name="routing">The canonical model-routing resource.</param>
    /// <returns>A validated declared snapshot; clients and availability are supplied separately.</returns>
    /// <exception cref="InvalidDataException">A required table, date or row is invalid.</exception>
    public static SquadModelCatalog Parse(string catalog, string routing)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(routing);
        var date = RetrievedDate().Match(catalog);
        if (!date.Success || !DateOnly.TryParseExact(date.Groups["date"].Value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrieved))
        {
            throw new InvalidDataException("Model catalog has no valid Retrieved date.");
        }

        var tables = MarkdownTables.Parse(catalog);
        var capabilities = new Dictionary<string, (string Display, string Capability)>(StringComparer.Ordinal);
        foreach (var row in tables.Where(t => t.HasColumns(["Catalog ID", "Display name", "Capability class"]))
                     .SelectMany(t => t.Rows))
        {
            var id = Cell(row, "Catalog ID");
            var capability = Cell(row, "Capability class");
            if (!IsModelId(id) || capability is not ("fast-lightweight" or "balanced" or "code-specialized" or "frontier-reasoning") ||
                !capabilities.TryAdd(id, (Cell(row, "Display name"), capability)))
            {
                throw new InvalidDataException("Model catalog has an invalid or duplicate capability row.");
            }
        }

        var fitTables = tables.Where(t => t.Heading == "Catalog: Assignment Fit" &&
            t.HasColumns(["Catalog ID", "Family", "Generation", "Blended", .. Classes])).ToArray();
        if (fitTables.Length != 1 || fitTables[0].Rows.Count == 0)
        {
            throw new InvalidDataException("Model catalog requires exactly one nonempty Catalog: Assignment Fit table.");
        }

        var models = new List<SquadCatalogModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in fitTables[0].Rows)
        {
            var id = Cell(row, "Catalog ID");
            var generation = Cell(row, "Generation");
            if (!generation.Contains('.', StringComparison.Ordinal))
            {
                generation += ".0";
            }
            if (!capabilities.TryGetValue(id, out var capability) || !seen.Add(id) ||
                !Version.TryParse(generation, out var version) ||
                !decimal.TryParse(Cell(row, "Blended"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var blended) ||
                blended < 0)
            {
                throw new InvalidDataException("Model catalog has an invalid, unpriced or duplicate assignment-fit row.");
            }

            var scores = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var assignment in Classes)
            {
                if (!int.TryParse(Cell(row, assignment), NumberStyles.None, CultureInfo.InvariantCulture, out var score) ||
                    score is < 0 or > 3)
                {
                    throw new InvalidDataException("Model catalog assignment scores must be integers from zero through three.");
                }
                scores.Add(assignment, score);
            }
            models.Add(new(id, capability.Display, capability.Capability, Cell(row, "Family"), version,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(scores), blended, models.Count));
        }

        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classTables = MarkdownTables.Parse(routing).Where(t => t.Heading == "Assignment Classes" &&
            t.HasColumns(["Class", "Roles"])).ToArray();
        if (classTables.Length != 1)
        {
            throw new InvalidDataException("Model routing requires exactly one Assignment Classes table.");
        }
        foreach (var row in classTables[0].Rows)
        {
            var assignment = Cell(row, "Class");
            if (!Classes.Contains(assignment, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Model routing declares an unknown assignment class.");
            }
            foreach (Match role in RoleId().Matches(row["Roles"]))
            {
                mapping.TryAdd(role.Groups["role"].Value, assignment);
            }
        }
        return new(retrieved, models.AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(mapping),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                capabilities.ToDictionary(pair => pair.Key, pair => pair.Value.Capability, StringComparer.Ordinal)));
    }

    /// <summary>Exactly ninety days is current; a future retrieval date is not trusted.</summary>
    /// <param name="asOf">The host's current UTC date.</param>
    /// <returns>Whether ranking may use this snapshot.</returns>
    public bool IsCurrent(DateOnly asOf) => asOf.DayNumber - Retrieved.DayNumber is >= 0 and <= 90;

    /// <summary>Resolves the canonical class, including product-owner's council exception.</summary>
    /// <param name="role">The actual roster role.</param>
    /// <param name="stage">The actual dispatch stage.</param>
    /// <returns>The released assignment class, or the explicitly documented implementation fallback.</returns>
    public string AssignmentClass(string role, string stage) =>
        role.Equals("product-owner", StringComparison.OrdinalIgnoreCase) && stage == "council"
            ? "council" : _roleClasses.GetValueOrDefault(role, "implementation");

    /// <summary>Ranks actual available IDs by fit, cost, same-family generation and source order.</summary>
    /// <param name="available">Exact identifiers advertised by this host.</param>
    /// <param name="assignmentClass">The canonical assignment class.</param>
    /// <param name="floor">The confirmed roster's Model Tier.</param>
    /// <param name="manual">Manual menus may exceed a fast floor with frontier models.</param>
    /// <returns>Only evaluated, suitable, floor-admitted available models.</returns>
    public IReadOnlyList<SquadCatalogModel> Rank(IEnumerable<string> available, string assignmentClass,
        string? floor, bool manual = false)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (!Classes.Contains(assignmentClass, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown model assignment class.", nameof(assignmentClass));
        }
        var ids = available.ToHashSet(StringComparer.Ordinal);
        var candidates = Models.Where(m => ids.Contains(m.Id) &&
            Admits(floor, m.CapabilityClass, manual) && m.Fit[assignmentClass] >= 1).ToList();
        candidates.Sort((left, right) =>
        {
            var fit = right.Fit[assignmentClass].CompareTo(left.Fit[assignmentClass]);
            if (fit != 0)
            {
                return fit;
            }
            var cost = left.BlendedUsdPerMillion.CompareTo(right.BlendedUsdPerMillion);
            if (cost != 0)
            {
                return cost;
            }
            if (left.Family == right.Family)
            {
                var generation = right.Generation.CompareTo(left.Generation);
                if (generation != 0)
                {
                    return generation;
                }
            }
            return left.Order.CompareTo(right.Order);
        });
        return candidates.AsReadOnly();
    }

    /// <summary>Checks the released consequence floor independently of a model's price.</summary>
    /// <param name="floor">The confirmed fast, default or extended floor.</param>
    /// <param name="capability">A declared capability class.</param>
    /// <param name="manual">Whether an explicit manual pick may exceed the fast cost preference.</param>
    /// <returns>Whether this known capability is admitted.</returns>
    public static bool Admits(string? floor, string capability, bool manual = true) =>
        (capability is "fast-lightweight" or "balanced" or "code-specialized" or "frontier-reasoning") && (floor switch
        {
            "fast" => manual || capability != "frontier-reasoning",
            "default" => capability is "balanced" or "code-specialized" or "frontier-reasoning",
            "extended" => capability == "frontier-reasoning",
            _ => false,
        });

    /// <summary>Rejects whitespace, prompt/shell metacharacters and unbounded identifiers.</summary>
    /// <param name="id">An exact catalog or advertised model ID.</param>
    /// <returns>Whether the identifier can be validated against an exact host allowlist.</returns>
    public static bool IsModelId(string? id) => !string.IsNullOrEmpty(id) &&
        Encoding.UTF8.GetByteCount(id) <= 200 &&
        !id.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
            ";|&$<>\\`'\"(){}[]".Contains(character, StringComparison.Ordinal));

    private static string Cell(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.Trim('`', ' '))
            ? value.Trim('`', ' ') : throw new InvalidDataException($"Model catalog has an empty '{name}' cell.");

    [GeneratedRegex(@"\*\*Retrieved:\s*(?<date>\d{4}-\d{2}-\d{2})\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex RetrievedDate();

    [GeneratedRegex(@"`(?<role>[a-z][a-z0-9-]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex RoleId();

}

/// <summary>A declared fit row; its presence never makes the ID dispatchable on a host.</summary>
/// <param name="Id">The exact catalog ID.</param>
/// <param name="DisplayName">The source's display name.</param>
/// <param name="CapabilityClass">The consequence-floor capability class.</param>
/// <param name="Family">The same-family generation tie-break group.</param>
/// <param name="Generation">The source's numeric generation.</param>
/// <param name="Fit">The seven canonical assignment scores.</param>
/// <param name="BlendedUsdPerMillion">The source's representative ranking price, not billed spend.</param>
/// <param name="Order">The final canonical row-order tie-break.</param>
public sealed record SquadCatalogModel(string Id, string DisplayName, string CapabilityClass,
    string Family, Version Generation, IReadOnlyDictionary<string, int> Fit, decimal BlendedUsdPerMillion, int Order);
