using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// Deterministic, single-writer native persistence, not a model Scribe dispatch. Its versioned
/// state format is intentionally distinct from Copilot host state; importing that state is rejected.
/// No token prices or concrete model identities are inferred from a tier or preference.
/// </summary>
internal sealed class RuntimeScribe(ProjectFiles files)
{
    private const string StatePath = ProjectFiles.StateRoot + "/state.json";
    private const int MaximumStateCharacters = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal RuntimeState? Load()
    {
        if (!File.Exists(files.Resolve(StatePath)))
        {
            if (File.Exists(files.Resolve(ProjectFiles.StateRoot + "/team.md")))
            {
                throw new InvalidDataException("Existing non-native squad state cannot be adopted implicitly.");
            }

            return null;
        }

        var state = JsonSerializer.Deserialize<RuntimeState>(files.Read(StatePath, MaximumStateCharacters), JsonOptions)
            ?? throw new InvalidDataException("Empty squad state.");
        if (state.Schema != RuntimeState.SchemaVersion)
        {
            throw new InvalidDataException("Unsupported squad state format; use a separate project or an explicit host migration.");
        }

        return state;
    }

    internal FileStream AcquireLock()
    {
        var path = files.Resolve(ProjectFiles.StateRoot + "/native.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProjectFiles.RejectLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal void Initialize(RuntimeState state, SquadRoster roster)
    {
        var table = new StringBuilder("# Native single-squad roster\n\n");
        table.AppendLine("Persistence owner: deterministic runtime Scribe (no model invocation).\n");
        table.AppendLine("| Role | Member Name | Primary Agent | Model Tier | Deliverable Root |");
        table.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var member in roster.Members)
        {
            table.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"| {Cell(member.Role)} | {Cell(member.MemberName)} | {Cell(member.PrimaryAgent)} | {Cell(member.ModelTier)} | {Cell(member.DeliverableRoot)} |");
        }

        files.Write(ProjectFiles.StateRoot + "/team.md", table.ToString());
        files.Write(ProjectFiles.StateRoot + "/routing.md",
            "# Native routing\n\nResearch → host-approved plan → conditional council → host-approved producing role → review.\n" +
            "Roles and concrete agents resolve only through the confirmed catalog roster. Declared child agents inherit their owner's scope.\n" +
            "Federation, Watch, autonomous/autopilot loops, remote notifications and CLI execution require a separate host integration.\n");
        files.Write(ProjectFiles.StateRoot + "/decisions.md", "# Native decisions\n\nConfirmed initialization:\n" +
            JsonSerializer.Serialize(state.Initialization, JsonOptions) + "\n");
        files.Write(ProjectFiles.StateRoot + "/notifications.md", "# Native host approvals\n\nApproval is delivered by host callback, never this file.\n");
        files.Write(ProjectFiles.StateRoot + "/consumption-rates.md", "# Consumption rates\n\nNo pricing configured. Costs and credits are unknown, not zero.\n");
        files.Write(ProjectFiles.StateRoot + "/consumption.md",
            "# Consumption\n\nNative per-dispatch observations follow. Costs and credits are unknown; no rates are inferred from tiers.\n");
        Directory.CreateDirectory(files.Resolve(ProjectFiles.StateRoot + "/history"));
        Save(state);
    }

    internal void RecordEvidence(SquadDispatchEvidence evidence)
    {
        files.Write(evidence.HistoryPath,
            $"\n## Dispatch {evidence.DispatchId}\n\n```json\n{JsonSerializer.Serialize(evidence, JsonOptions)}\n```\n",
            append: true);
        files.Write(ProjectFiles.StateRoot + "/consumption.md",
            $"\n- Dispatch `{evidence.DispatchId}`; role `{evidence.Role}`; agent `{evidence.Agent}`; model `{evidence.Model}` " +
            $"({evidence.ModelSource}); tier `{evidence.ModelTier ?? "unknown"}`; input {evidence.InputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; " +
            $"output {evidence.OutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; cost/credits unknown.\n", append: true);
    }

    internal void Verify(SquadDispatchEvidence evidence)
    {
        VerifyArtifact(new("primary", evidence.ArtifactPath, evidence.ArtifactSha256));
        foreach (var artifact in evidence.Artifacts ?? [])
        {
            VerifyArtifact(artifact);
        }

        VerifyHostOutputs(evidence.DispatchId, evidence.ExecutedHostTools ?? []);

        if (!files.Read(evidence.HistoryPath, MaximumStateCharacters).Contains(JsonSerializer.Serialize(evidence, JsonOptions), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Artifact/history evidence for '{evidence.DispatchId}' is missing or changed.");
        }
    }

    internal void VerifyArtifact(SquadArtifactEvidence artifact)
    {
        if (ProjectFiles.Hash(files.Read(artifact.Path)) != artifact.Sha256)
        {
            throw new InvalidDataException($"Output evidence '{artifact.Path}' is missing or changed.");
        }
    }

    internal void VerifyHostOutputs(string dispatchId, IEnumerable<SquadToolExecution> executions)
    {
        // Every receipt remains in history, but an authorized edit supersedes that dispatch's
        // previous observation of the same file. Never supersede evidence from another dispatch.
        var latest = new Dictionary<string, SquadArtifactEvidence>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var output in executions.SelectMany(t => t.Outputs ?? []))
        {
            latest[files.Resolve(output.Path)] = output;
        }

        foreach (var output in latest.Values)
        {
            if (files.HashFile(output.Path) != output.Sha256)
            {
                throw new InvalidDataException($"Host output evidence '{output.Path}' for dispatch '{dispatchId}' is changed. " +
                    "Overlapping edits across distinct dispatches are unsupported; prior evidence cannot be superseded.");
            }
        }
    }

    internal string RecordReturn(string runId, string dispatchId, string role, object report, SquadStructuredReturn payload)
    {
        var path = $"{ProjectFiles.StateRoot}/returns/{runId}/{dispatchId}.json";
        files.Write(path, JsonSerializer.Serialize(new
        {
            EvidenceKind = "StructuredReturn", PersistenceOwner = "deterministic-runtime-scribe",
            DispatchId = dispatchId, Role = role, Report = report, Return = payload,
        }, JsonOptions));
        return path;
    }

    internal void Decision(string text) => files.Write(ProjectFiles.StateRoot + "/decisions.md", "\n" + text + "\n", append: true);
    internal void Notification(string text) => files.Write(ProjectFiles.StateRoot + "/notifications.md", "\n" + text + "\n", append: true);

    internal void Save(RuntimeState state)
    {
        var path = files.Resolve(StatePath);
        var pending = files.Resolve(ProjectFiles.StateRoot + "/state.pending.json");
        files.Write(ProjectFiles.StateRoot + "/state.pending.json", JsonSerializer.Serialize(state, JsonOptions),
            maximumCharacters: MaximumStateCharacters);
        ProjectFiles.RejectLinks(path);
        File.Move(pending, path, overwrite: true);
    }

    private static string Cell(string? value) => (value ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
}

internal sealed class RuntimeState
{
    internal const string SchemaVersion = "hve-squad-maf/native-single-squad/v1";
    public string Schema { get; set; } = SchemaVersion;
    public string Fingerprint { get; set; } = "";
    public string Binding { get; set; } = "";
    public string? ReleaseTag { get; set; }
    public string? ReleaseCommitSha { get; set; }
    public string ArtifactOrigin { get; set; } = "";
    public SquadInitializationProposal? Initialization { get; set; }
    public Dictionary<string, JsonElement> Sessions { get; set; } = new(StringComparer.Ordinal);
    public List<SquadRunResult> Runs { get; set; } = [];
    public List<SquadConfirmedAnswer> ConfirmedAnswers { get; set; } = [];
    public List<SquadQuestion> OpenQuestions { get; set; } = [];
    public List<string> UserTurns { get; set; } = [];
    public SquadRunResult? ActiveRun { get; set; }
}
