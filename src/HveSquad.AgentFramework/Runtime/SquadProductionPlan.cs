using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>A producing unit declared in the verified lead plan, not coordinator-authored work.</summary>
/// <param name="Id">Stable lower-kebab-case deliverable identity.</param>
/// <param name="Role">Actual canonical roster owner.</param>
/// <param name="Tasks">Plan task identifiers whose complete context is assigned to this owner.</param>
/// <param name="Dependencies">Other producing units that must have verified handoffs first.</param>
/// <param name="ProjectOutputs">Exact planned host-written files, separate from allocated Markdown outputs.</param>
/// <param name="PlanContext">The actual task context from the verified plan.</param>
/// <param name="Agent">Concrete installed agent selected through the owner's canonical rules.</param>
/// <param name="AgentSelectionRationale">The verified primary or alternate selection basis.</param>
public sealed record SquadProducingUnit(
    string Id, string Role, IReadOnlyList<string> Tasks, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> ProjectOutputs, string PlanContext, string? Agent = null, string? AgentSelectionRationale = null);

/// <summary>Exact plan-derived production proposed for human implementation approval.</summary>
/// <param name="PlanDispatchId">The actual lead dispatch that wrote the plan.</param>
/// <param name="PlanSha256">Verified primary plan content hash.</param>
/// <param name="ExplicitDeliverables">Whether the plan supplies the native Deliverables table.</param>
/// <param name="Units">Bounded real owners, outputs and dependency order.</param>
public sealed record SquadProductionPlan(
    string PlanDispatchId, string PlanSha256, bool ExplicitDeliverables, IReadOnlyList<SquadProducingUnit> Units);

/// <summary>A deterministic, immutable transfer of Scribe-verified producer evidence to its dependent owner.</summary>
/// <param name="Id">Run/plan/edge-bound identity.</param>
/// <param name="RunId">Owning native run.</param>
/// <param name="PlanDispatchId">The verified plan declaring this edge.</param>
/// <param name="Producer">Producing-unit identity.</param>
/// <param name="Consumer">Dependent-unit identity.</param>
/// <param name="Evidence">Exact completed producer and child receipts, including output hashes.</param>
/// <param name="RecordedAt">Trusted native recording time.</param>
public sealed record SquadProductionHandoff(
    string Id, string RunId, string PlanDispatchId, string Producer, string Consumer,
    IReadOnlyList<SquadDispatchEvidence> Evidence, DateTimeOffset RecordedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int Iteration = 0);

internal sealed class ProductionCheckpoint
{
    public required SquadProductionPlan Plan { get; init; }
    public bool Confirmed { get; set; }
}

internal static partial class ProductionPlans
{
    private static readonly string[] Columns = ["Id", "Role", "Tasks", "Depends On", "Project Outputs"];

    internal static SquadProductionPlan Parse(string content, SquadDispatchEvidence plan, string fallbackRole,
        bool singlePlan, int maximumUnits, IReadOnlyList<string> verifiedInputs)
    {
        var section = PlanContractValidator.ReadSection(content, "Deliverables");
        if (section is null)
        {
            return new(plan.DispatchId, plan.ArtifactSha256, false,
                [new("primary", fallbackRole, [], [], [], "Execute only the approved plan.")]);
        }
        var rows = section.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('|')).ToArray();
        if (rows.Length < 3 || rows.Length > maximumUnits + 2 ||
            !Cells(rows[0]).SequenceEqual(Columns, StringComparer.OrdinalIgnoreCase) ||
            Cells(rows[1]).Length != Columns.Length || Cells(rows[1]).Any(cell => !Separator().IsMatch(cell)))
        {
            throw new InvalidDataException(
                "Deliverables requires one bounded table: Id | Role | Tasks | Depends On | Project Outputs.");
        }
        var tasks = singlePlan ? PlanContractValidator.ReadTasks(content).ToDictionary(task => task.Id, StringComparer.Ordinal) : [];
        var units = new List<SquadProducingUnit>();
        foreach (var row in rows.Skip(2))
        {
            var cells = Cells(row);
            if (cells.Length != Columns.Length || !Identity().IsMatch(cells[0]) ||
                !Identity().IsMatch(cells[1]) || units.Any(unit => unit.Id == cells[0]))
            {
                throw new InvalidDataException("Each deliverable needs a unique stable Id and one canonical Role.");
            }
            var taskIds = List(cells[2]);
            var dependencies = List(cells[3]);
            var outputs = List(cells[4]);
            if (taskIds.Any(id => !tasks.ContainsKey(id)) || singlePlan && taskIds.Length == 0 ||
                !singlePlan && taskIds.Length > 0 ||
                dependencies.Any(id => !Identity().IsMatch(id)) ||
                taskIds.Length != taskIds.Distinct(StringComparer.Ordinal).Count() ||
                dependencies.Length != dependencies.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidDataException(
                    "Deliverable Tasks must name actual plan tasks (None for legacy); dependency identities must be unique.");
            }
            units.Add(new(cells[0], cells[1], taskIds, dependencies, outputs,
                taskIds.Length == 0 ? "Legacy plan deliverable: " + row :
                    string.Join("\n\n", taskIds.Select(id => id + "\n" + tasks[id].Context))));
        }
        foreach (var unit in units.ToArray())
        {
            var dependencies = unit.Dependencies.ToHashSet(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void AddTask(string taskId)
            {
                if (!visited.Add(taskId))
                {
                    return;
                }
                var owners = units.Where(candidate => candidate.Tasks.Contains(taskId, StringComparer.Ordinal)).ToArray();
                if (owners.Length == 0 &&
                    (!tasks[taskId].Checked || !verifiedInputs.Any(path => tasks[taskId].References.Contains(path, StringComparison.Ordinal))))
                {
                    throw new InvalidDataException(
                        $"Task dependency '{taskId}' has no producing owner or checked, verified input reference.");
                }
                foreach (var owner in owners.Where(owner => owner.Id != unit.Id))
                {
                    dependencies.Add(owner.Id);
                }
                foreach (var prerequisite in tasks[taskId].Dependencies)
                {
                    AddTask(prerequisite);
                }
            }
            foreach (var prerequisite in unit.Tasks.SelectMany(id => tasks[id].Dependencies))
            {
                AddTask(prerequisite);
            }
            units[units.IndexOf(unit)] = unit with { Dependencies = dependencies.Order(StringComparer.Ordinal).ToArray() };
        }
        var byId = units.ToDictionary(unit => unit.Id, StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        string[] Visit(string id)
        {
            if (!byId.TryGetValue(id, out var unit))
            {
                throw new InvalidDataException($"Deliverable dependency '{id}' has no resolved owner.");
            }
            if (complete.Contains(id))
            {
                return unit.Dependencies.ToArray();
            }
            if (!active.Add(id))
            {
                throw new InvalidDataException($"Deliverable dependencies contain a cycle at '{id}'.");
            }
            var dependencies = unit.Dependencies.SelectMany(dependency => Visit(dependency).Prepend(dependency))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            byId[id] = unit with { Dependencies = dependencies };
            active.Remove(id);
            complete.Add(id);
            return dependencies;
        }
        foreach (var unit in units)
        {
            _ = Visit(unit.Id);
        }
        return new(plan.DispatchId, plan.ArtifactSha256, true, units.Select(unit => byId[unit.Id]).ToArray());
    }

    internal static string Slot(SquadProductionPlan plan, SquadProducingUnit unit) =>
        plan.ExplicitDeliverables ? $"produce:{unit.Id}:{unit.Role}" : "produce:" + unit.Role;

    internal static SquadProductionPlan Copy(SquadProductionPlan plan) => plan with
    {
        Units = plan.Units.Select(unit => unit with
        {
            Tasks = unit.Tasks.ToArray(),
            Dependencies = unit.Dependencies.ToArray(),
            ProjectOutputs = unit.ProjectOutputs.ToArray(),
        }).ToArray(),
    };

    private static string[] Cells(string row) => row.Trim('|').Split('|').Select(cell => cell.Trim().Trim('`')).ToArray();
    private static string[] List(string cell) => cell is "None" or "none" or "-" ? [] :
        cell.Split(',', StringSplitOptions.TrimEntries).Select(value => value.Trim('`')).ToArray();

    [GeneratedRegex(@"^(?=.{1,64}$)[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identity();
    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();
}
