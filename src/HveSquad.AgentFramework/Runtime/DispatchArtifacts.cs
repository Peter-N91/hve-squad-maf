using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Finite, host-allocated Markdown outputs. No model-supplied filesystem paths.</summary>
internal sealed partial class DispatchArtifacts
{
    private readonly ProjectFiles _files;
    private readonly int _limit;
    private readonly string _id;
    private readonly string _date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private readonly Dictionary<string, string> _allowedKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Allocation> _allocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SquadArtifactEvidence> _written = new(StringComparer.Ordinal);
    private readonly bool _requiresDetails;

    internal DispatchArtifacts(ProjectFiles files, SquadRosterEntry entry, string stage, string runId,
        string dispatchId, int depth, int limit)
    {
        _files = files;
        _limit = limit;
        _id = $"{runId}-{dispatchId}";
        var root = files.ValidateDeliverableRoot(entry.DeliverableRoot
            ?? throw new InvalidDataException($"Role '{entry.Role}' has no deliverable root."));
        var role = entry.Role.ToLowerInvariant();
        _requiresDetails = role == "lead" && stage == "plan" && depth == 1;
        var primary = role == "researcher" && depth > 1
            ? $".copilot-tracking/research/subagents/{_date}/{_id}-subagent-research.md"
            : role == "researcher" ? $"{root}/{_id}-research.md"
            : _requiresDetails ? $"{root}/{_date}/{_id}-plan.md"
            : role is "developer" or "iac-author" ? $"{root}/{_date}/{_id}-changes.md"
            : role == "tester" ? $"{root}/{_date}/{_id}-review.md"
            : $"{root}/{runId}/{dispatchId}.md";
        _allocations.Add("primary", new("primary", "primary", primary));
        if (role == "researcher")
        {
            _allowedKinds.Add("research-evidence", $".copilot-tracking/research/subagents/{_date}");
        }

        if (role == "lead" && stage == "plan")
        {
            _allowedKinds.Add("phase-details", $".copilot-tracking/details/{_date}");
            _allowedKinds.Add("plan-critique", $".copilot-tracking/reviews/{_date}");
            _allowedKinds.Add("research-evidence", $".copilot-tracking/research/subagents/{_date}");
        }

        if (_requiresDetails)
        {
            Declare("phase-details", "phase-details");
        }
    }

    internal string PrimaryPath => _allocations["primary"].Path;
    internal string Manifest => JsonSerializer.Serialize(new
    {
        Outputs = _allocations.Values, AdditionalKinds = _allowedKinds.Keys,
        MaximumOutputs = _limit,
    });

    internal string Declare(string name, string kind)
    {
        if (!OutputName().IsMatch(name) || !_allowedKinds.TryGetValue(kind, out var root))
        {
            throw new InvalidDataException("Only a listed methodology kind and a lowercase name (no paths) are allowed.");
        }

        if (_allocations.TryGetValue(name, out var existing))
        {
            return existing.Kind == kind ? existing.Path : throw new InvalidDataException("An output name cannot change kind.");
        }

        if (_allocations.Count >= _limit)
        {
            throw new InvalidDataException("Per-dispatch artifact limit reached.");
        }

        var path = $"{root}/{_id}-{name}.md";
        _allocations.Add(name, new(name, kind, path));
        return path;
    }

    internal string Write(string content, string artifactName = "primary")
    {
        if (string.IsNullOrWhiteSpace(content) || !_allocations.TryGetValue(artifactName, out var allocation))
        {
            throw new InvalidDataException("A nonempty artifact and a declared output name are required.");
        }

        _files.Write(allocation.Path, content);
        _written[artifactName] = new(artifactName, allocation.Path, ProjectFiles.Hash(content));
        return allocation.Path;
    }

    internal IReadOnlyList<SquadArtifactEvidence> Verify()
    {
        if (_allocations.Keys.Any(name => !_written.ContainsKey(name)))
        {
            throw new InvalidDataException("Every declared output must be written, including primary and required phase-details.");
        }

        foreach (var output in _written.Values)
        {
            if (ProjectFiles.Hash(_files.Read(output.Path)) != output.Sha256)
            {
                throw new InvalidDataException($"Output '{output.Path}' changed after write_artifact.");
            }
        }

        if (_requiresDetails &&
            (!_files.Read(PrimaryPath).Contains(_allocations["phase-details"].Path, StringComparison.Ordinal) ||
             !_files.Read(_allocations["phase-details"].Path).Contains(PrimaryPath, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Plan and phase-details must reference each other's actual allocated workspace-relative paths.");
        }

        return _written.Values.ToArray();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OutputName();
    private sealed record Allocation(string Name, string Kind, string Path);
}
