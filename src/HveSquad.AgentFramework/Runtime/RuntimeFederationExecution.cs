using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record FederationArtifactRepair(string Role, string Stage, string ArtifactName, string Reason);
internal sealed record FederationHumanRevision(string MetaRunId, string Member, string InnerRunId,
    string Stage, HumanDecisionReceipt Origin, string ApprovalRecordId);

internal sealed record FederationInvocation(string FederationRunId, string Member, string InnerRunId,
    bool Aggregate, IReadOnlyList<SquadFederationHandoffArtifact> Inputs,
    IReadOnlyList<SquadFederationGapAssumption> Assumptions, FederationArtifactRepair? Repair,
    Func<SquadApprovalRequest, CancellationToken, ValueTask<bool>> ApproveAsync,
    Func<IReadOnlyList<SquadCostDemandRow>, bool, CancellationToken, ValueTask>? AdmitAsync = null,
    FederationHumanRevision? Revision = null)
{
    internal object Identity => new
    {
        FederationRunId,
        Member,
        InnerRunId,
        Aggregate,
        Inputs,
        Assumptions,
        Repair,
    };
}

internal sealed class FederationMemberStep
{
    public required string Member { get; init; }
    public required string InnerRunId { get; init; }
    public required string Request { get; init; }
    public FederationArtifactRepair? Repair { get; set; }
    public bool Reuse { get; set; }
    public bool Started { get; set; }
    public string? OutcomeRecordId { get; set; }
    public SquadRunResult? Result { get; set; }
    public List<SquadFederationHandoffArtifact> Inputs { get; set; } = [];
    public List<SquadCostDemandRow> Demand { get; set; } = [];
}

internal sealed class FederationRunCheckpoint
{
    public required string RunId { get; init; }
    public required SquadFederationRunConfiguration Configuration { get; set; }
    public required string RegistrySha256 { get; set; }
    public required string RoutingSha256 { get; set; }
    public SquadFederationExecutionPlan? Plan { get; set; }
    public bool PlanConfirmed { get; set; }
    public bool FinalValidated { get; set; }
    public string? Discovery { get; set; }
    public List<FederationMemberStep> Steps { get; set; } = [];
    public List<SquadFederationHandoff> Handoffs { get; set; } = [];
    public List<string> RecoveryAttempts { get; set; } = [];
    public List<SquadFederationGapAssumption> Assumptions { get; set; } = [];
    public List<FederationHumanRevision> Revisions { get; set; } = [];
    public int Revision { get; set; }
    public string Status { get; set; } = "planning";
}

internal static class FederationExecution
{
    internal static bool Successful(SquadRunResult result) =>
        result.Status is SquadRunStatus.Completed or SquadRunStatus.ResearchCompleted or
            SquadRunStatus.PlanCompleted or SquadRunStatus.ReviewCompleted or SquadRunStatus.ArtifactCompleted;

    internal static (ProjectFiles Files, RuntimeScribe Scribe, RuntimeState State) ReadMember(
        ProjectFiles hub, string name, string fingerprint, TimeProvider? timeProvider = null)
    {
        FederationPersistence.ValidateName(name);
        var files = new ProjectFiles(hub.ProjectRoot, int.MaxValue, FederationPersistence.MemberRoot(name));
        var scribe = new RuntimeScribe(files, timeProvider: timeProvider);
        var state = scribe.Load() ?? throw new InvalidDataException("A selected member has no native state.");
        if (state.Fingerprint != fingerprint)
        {
            throw new InvalidDataException("The selected member changed its exact acquired release.");
        }
        scribe.VerifyNativeMember(state);
        return (files, scribe, state);
    }

    internal static SquadFederationHandoffArtifact[] FindArtifacts(ProjectFiles hub, string fingerprint,
        SquadFederationDependency dependency)
    {
        if (dependency.Producer is null)
        {
            return [];
        }
        var (files, scribe, state) = ReadMember(hub, dependency.Producer, fingerprint);
        var roster = new SquadRoster
        {
            Members = state.Initialization?.Roster ??
            throw new InvalidDataException("The producer lost its confirmed roster.")
        };
        var roots = scribe.ReadRosterRoots(roster);
        if (!roots.TryGetValue(dependency.Role, out var root) || root is null)
        {
            throw new InvalidDataException("The declared producer role has no current deliverable root; confirm its roster rather than substitute an owner.");
        }
        root = files.ValidateDeliverableRoot(root);
        if (!Directory.Exists(files.Resolve(root)))
        {
            return [];
        }
        var enumerated = FederationPersistence.Enumerate(files, root).Files
            .Where(file => !files.IsProtectedStatePath(file.Path)).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var completed = state.Runs.Concat(state.ActiveRun is { Status: SquadRunStatus.Completed } active ? [active] : [])
            .Where(Successful).SelectMany(run => run.Evidence)
            .Select(evidence => evidence.DispatchId).ToHashSet(StringComparer.Ordinal);
        foreach (var evidence in state.DispatchRecords.Where(evidence => evidence.Depth == 1 &&
            evidence.Role == dependency.Role && evidence.Stage == dependency.Stage &&
            completed.Contains(evidence.DispatchId)).Reverse())
        {
            var artifacts = (evidence.Artifacts ?? [new("primary", evidence.ArtifactPath, evidence.ArtifactSha256)])
                .Where(artifact => artifact.Name == dependency.ArtifactName &&
                    ProjectFiles.IsWithin(Path.GetRelativePath(hub.ProjectRoot, files.Resolve(artifact.Path)).Replace('\\', '/'), root))
                .ToArray();
            if (artifacts.Length == 0)
            {
                continue;
            }
            try
            {
                scribe.Verify(evidence, state);
            }
            catch (RuntimeHistoryIntegrityException exception) when (exception.Status == SquadRunStatus.Failed)
            {
                return [];
            }
            var result = new List<SquadFederationHandoffArtifact>();
            foreach (var artifact in artifacts)
            {
                var path = Path.GetRelativePath(hub.ProjectRoot, files.Resolve(artifact.Path)).Replace('\\', '/');
                if (!enumerated.TryGetValue(path, out var file) ||
                    dependency.ExpectedSha256 is not null && file.Sha256 != dependency.ExpectedSha256)
                {
                    return [];
                }
                result.Add(new(artifact.Name, path, file.Sha256, file.Bytes,
                    dependency.Producer, evidence.DispatchId, root, evidence.Role, evidence.Stage));
            }
            return result.ToArray();
        }
        return [];
    }

    internal static void VerifyInputs(ProjectFiles files, IEnumerable<SquadFederationHandoffArtifact> inputs)
    {
        foreach (var input in inputs)
        {
            if (!HumanActions.IsHash(input.Sha256) || input.Bytes < 0 ||
                input.Bytes > ProjectFiles.MaximumHostOutputBytes || files.IsProtectedStatePath(input.Path) ||
                files.HashFile(input.Path) != input.Sha256 || new FileInfo(files.Resolve(input.Path)).Length != input.Bytes)
            {
                throw new RuntimeHistoryIntegrityException("The exact read-only federation handoff changed or contains private state; no consumer work is authorized.");
            }
        }
    }

    internal static void VerifyGateSource(ProjectFiles hub, RuntimeState state,
        SquadFederationGateContext attribution, IReadOnlyList<SquadDispatchEvidence> evidence)
    {
        var meta = state.FederationRun ?? throw new InvalidDataException("No meta-run owns this attributed gate.");
        if (attribution.RunId != meta.RunId ||
            !meta.Steps.Any(step => step.Member == attribution.Member && step.InnerRunId == attribution.InnerRunId))
        {
            throw new RuntimeHistoryIntegrityException("The attributed gate belongs to another member or meta-run.");
        }
        var (_, scribe, member) = ReadMember(hub, attribution.Member, state.Fingerprint);
        if (member.Checkpoint?.RunId != attribution.InnerRunId &&
            member.Runs.All(run => run.RunId != attribution.InnerRunId))
        {
            throw new RuntimeHistoryIntegrityException("The actual member run does not own this federation gate.");
        }
        foreach (var source in evidence)
        {
            if (!member.DispatchRecords.Any(record => HumanActions.Serialize(record) == HumanActions.Serialize(source)))
            {
                throw new RuntimeHistoryIntegrityException("The lifted gate has no exact inner dispatch receipt.");
            }
            scribe.Verify(source, member);
        }
    }

    internal static void VerifyHumanRevision(ProjectFiles files, string fingerprint, FederationHumanRevision revision)
    {
        var hubFiles = new ProjectFiles(files.ProjectRoot, int.MaxValue);
        var hubScribe = new RuntimeScribe(hubFiles);
        var hub = hubScribe.Load(allowFederation: true) ?? throw new InvalidDataException("The revision lost its original federation case.");
        hubScribe.VerifyFederation(hub);
        var origin = (hub.HumanDecisions ?? []).SingleOrDefault(receipt => receipt.Binding.Id == revision.Origin.Binding.Id);
        var approval = (hub.GateRecords ?? []).SingleOrDefault(record => record.Id == revision.ApprovalRecordId);
        if (hub.Fingerprint != fingerprint || origin is null ||
            HumanActions.Serialize(origin) != HumanActions.Serialize(revision.Origin) ||
            origin.Decision != SquadHumanDecision.Changes || origin.Binding.RunId != revision.MetaRunId ||
            origin.Binding.Gate != "finaloutcome" || origin.Binding.Mode != "autopilot" ||
            approval is null || approval.Kind != "Federation Human Changes" ||
            !JsonElement.DeepEquals(approval.Payload, JsonSerializer.SerializeToElement(revision)) ||
            !ProjectFiles.IsWithin(files.SquadRoot, FederationPersistence.MemberRoot(revision.Member)) ||
            origin.InputPath is null || origin.InputSha256 is null)
        {
            throw new RuntimeHistoryIntegrityException("The named revision has no exact authenticated original human decision and confirmed owner/stage.");
        }
        _ = hubScribe.ReadHumanInput(origin.Binding.Id, origin.InputPath, origin.InputSha256);
    }

    internal static string HumanRevisionInput(ProjectFiles files, FederationHumanRevision revision)
    {
        var hubScribe = new RuntimeScribe(new ProjectFiles(files.ProjectRoot, int.MaxValue));
        return hubScribe.ReadHumanInput(revision.Origin.Binding.Id, revision.Origin.InputPath!, revision.Origin.InputSha256!);
    }

    internal static (decimal Spend, decimal Pending, bool Known, string Hash) Spend(ProjectFiles hub, RuntimeState state)
    {
        var meta = state.FederationRun ?? throw new InvalidDataException("No active federation run owns aggregate spend.");
        var charges = new List<ModelCallCharge>();
        var observations = new List<ModelCallObservation>();
        var known = true;
        foreach (var step in meta.Steps.Where(step => step.Started && !step.Reuse))
        {
            var (_, scribe, member) = ReadMember(hub, step.Member, state.Fingerprint);
            if (!scribe.VerifyRates(member))
            {
                known = false;
            }
            var calls = member.ModelCalls.Where(call => call.RunId == step.InnerRunId).ToArray();
            var priced = (member.ModelCallCharges ?? []).Where(charge => charge.RunId == step.InnerRunId).ToArray();
            observations.AddRange(calls);
            charges.AddRange(priced);
            known &= calls.All(call => priced.Count(charge => charge.CallId == call.Id) == 1) &&
                priced.All(charge => charge.CostUsd is not null) &&
                ConsumptionMath.UnobservedDispatches(step.InnerRunId, member) == 0;
        }
        if (charges.Select(charge => charge.CallId).Distinct(StringComparer.Ordinal).Count() != charges.Count)
        {
            throw new RuntimeHistoryIntegrityException("An inner provider attempt was counted more than once.");
        }
        return (charges.Sum(charge => charge.CostUsd ?? 0m), 0m, known,
            HumanActions.Hash(new { Observations = observations.OrderBy(call => call.Id, StringComparer.Ordinal), Charges = charges.OrderBy(charge => charge.CallId, StringComparer.Ordinal) }));
    }

    internal static SquadFederationCost Cost(ProjectFiles hub, RuntimeState state)
    {
        var meta = state.FederationRun ?? throw new InvalidDataException("No federation run owns current cost.");
        var attempts = 0;
        var unpriced = 0;
        var unobserved = 0;
        decimal known = 0;
        foreach (var step in meta.Steps.Where(step => step.Started && !step.Reuse))
        {
            var (_, _, member) = ReadMember(hub, step.Member, state.Fingerprint);
            var calls = member.ModelCalls.Where(call => call.RunId == step.InnerRunId).ToArray();
            var charges = (member.ModelCallCharges ?? []).Where(charge => charge.RunId == step.InnerRunId).ToArray();
            attempts += calls.Length;
            unpriced += calls.Count(call => charges.All(charge => charge.CallId != call.Id)) +
                charges.Count(charge => charge.CostUsd is null);
            unobserved += ConsumptionMath.UnobservedDispatches(step.InnerRunId, member);
            known += charges.Sum(charge => charge.CostUsd ?? 0m);
        }
        return new(unpriced == 0 && unobserved == 0 ? ConsumptionMath.Round(known) : null,
            ConsumptionMath.Round(known), attempts, unpriced, unobserved, 0m);
    }
}

internal sealed partial class RuntimeScribe
{
    private static void VerifyFederationTerminalOutcomes(RuntimeState state)
    {
        var outcomes = state.FederationRuns ?? [];
        var records = (state.GateRecords ?? []).Where(record => record.Kind == "Federation Run Outcome").ToArray();
        if (records.Length != outcomes.Count || outcomes.Select(outcome => outcome.RunId).Distinct(StringComparer.Ordinal).Count() != outcomes.Count)
        {
            throw new RuntimeHistoryIntegrityException("A terminal federation outcome receipt/index was removed or duplicated.");
        }
        foreach (var outcome in outcomes)
        {
            var record = records.SingleOrDefault(record => record.Id == outcome.RunId + "-terminal");
            if (record is null || !JsonElement.DeepEquals(record.Payload, JsonSerializer.SerializeToElement(outcome)))
            {
                throw new RuntimeHistoryIntegrityException("A terminal meta-run was changed without its original immutable outcome receipt.");
            }
        }
    }

    internal SquadGateRecord RecordFederationTransition(RuntimeState state, string id, string kind, object payload,
        string? member = null)
    {
        var checkpoint = state.Checkpoint ?? throw new InvalidDataException("A federation transition needs its current run.");
        var record = RecordGate(state, id, kind, checkpoint.Readiness!.TopicId,
            checkpoint.Readiness.ScopeSha256, payload, "* Meta-transition: serialized; no federation-root pipelining.");
        var paths = new List<string>();
        if (member is not null)
        {
            paths.Add(files.SquadRoot + "/history/" + member + ".md");
        }
        if (state.FederationRun?.Plan?.AggregateAutopilot == true)
        {
            paths.Add(files.SquadRoot + "/history/autopilot-run-" + checkpoint.RunId + ".md");
        }
        foreach (var path in paths)
        {
            var frame = Frame("Federation", record.Id, record);
            if (File.Exists(files.Resolve(path)) && files.Read(path, MaximumStateCharacters).Contains(frame, StringComparison.Ordinal))
            {
                continue;
            }
            Append(state, path, frame);
        }
        Save(state);
        return record;
    }

    internal void VerifyFederationRun(RuntimeState state)
    {
        if (state.FederationRun is not { } meta)
        {
            return;
        }
        if (state.Checkpoint?.RunId != meta.RunId || state.Checkpoint.SquadRoot != files.SquadRoot ||
            meta.RegistrySha256 != state.Federation!.RegistrySha256 || meta.RoutingSha256 != state.Federation.RoutingSha256 ||
            meta.Steps.Count > 64 || meta.Steps.Select(step => step.Member).Distinct(StringComparer.Ordinal).Count() != meta.Steps.Count)
        {
            throw new RuntimeHistoryIntegrityException("The active meta-run changed its exact topology or checkpoint.");
        }
        if (meta.PlanConfirmed)
        {
            var record = (state.GateRecords ?? []).SingleOrDefault(record => record.Id == meta.RunId + "-plan");
            if (record is null || !JsonElement.DeepEquals(record.Payload, JsonSerializer.SerializeToElement(meta.Plan)) ||
                meta.Steps.Select(step => step.Member).Except(meta.Plan!.Members.Select(member => member.Name), StringComparer.Ordinal).Any())
            {
                throw new RuntimeHistoryIntegrityException("The confirmed federation execution script changed.");
            }
        }
        foreach (var handoff in meta.Handoffs)
        {
            var record = (state.GateRecords ?? []).SingleOrDefault(record => record.Id == handoff.Id);
            if (record is null || !JsonElement.DeepEquals(record.Payload, JsonSerializer.SerializeToElement(handoff)))
            {
                throw new RuntimeHistoryIntegrityException("A recorded cross-squad handoff was changed or removed.");
            }
        }
        var handoffRecords = (state.GateRecords ?? []).Where(record => record.RunId == meta.RunId &&
            record.Kind == "Federation Handoff").ToArray();
        if (handoffRecords.Length != meta.Handoffs.Count)
        {
            throw new RuntimeHistoryIntegrityException("A handoff receipt was removed from the active index.");
        }
        foreach (var step in meta.Steps.Where(step => step.OutcomeRecordId is not null))
        {
            var record = (state.GateRecords ?? []).SingleOrDefault(record => record.Id == step.OutcomeRecordId);
            if (step.Result is null || record is null || !JsonElement.DeepEquals(record.Payload, JsonSerializer.SerializeToElement(step.Result)))
            {
                throw new RuntimeHistoryIntegrityException("A member outcome lost its immutable meta receipt.");
            }
        }
        if (meta.PlanConfirmed)
        {
            foreach (var step in meta.Steps)
            {
                var planned = meta.Plan!.Members.Single(member => member.Name == step.Member);
                var inputs = meta.Handoffs.Where(handoff => handoff.Consumer == step.Member).SelectMany(handoff => handoff.Artifacts).ToArray();
                if (planned.InnerRunId != step.InnerRunId || planned.Request != step.Request ||
                    planned.RepairRole != step.Repair?.Role || planned.RepairStage != step.Repair?.Stage ||
                    (planned.Work == "reuse verified artifact") != step.Reuse ||
                    !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(inputs), JsonSerializer.SerializeToElement(step.Inputs)))
                {
                    throw new RuntimeHistoryIntegrityException("The actual member invocation differs from its confirmed execution script or exact recorded inputs.");
                }
            }
        }
        foreach (var revision in meta.Revisions)
        {
            var record = (state.GateRecords ?? []).SingleOrDefault(record => record.Id == revision.ApprovalRecordId);
            if (record is null || !JsonElement.DeepEquals(record.Payload, JsonSerializer.SerializeToElement(revision)))
            {
                throw new RuntimeHistoryIntegrityException("A confirmed scoped federation revision was changed or removed.");
            }
        }
    }

    internal void FinalizeFederationMember(RuntimeState state, string innerRunId, string metaRunId,
        string finalReference, bool stopped)
    {
        var checkpoint = state.Checkpoint;
        if (checkpoint?.RunId != innerRunId || checkpoint.Status != "federation-awaiting-validation")
        {
            if (state.Runs.Any(run => run.RunId == innerRunId))
            {
                return;
            }
            throw new RuntimeHistoryIntegrityException("The inner run has not reached its actual federation validation barrier.");
        }
        var result = state.ActiveRun ?? throw new RuntimeHistoryIntegrityException("The completed inner outcome is missing.");
        RecordGate(state, innerRunId + "-federation-terminal", "Federation Validation Reference",
            checkpoint.Readiness!.TopicId, checkpoint.Readiness.ScopeSha256,
            new { MetaRunId = metaRunId, FinalReference = finalReference, Stopped = stopped },
            "* Inner stages completed; acceptance belongs to the referenced federation outcome, never release permission.");
        state.Runs.Add(stopped ? result with
        {
            Status = SquadRunStatus.Stopped,
            ResponseText = result.ResponseText + "\nThe owning federation was stopped without final acceptance."
        } : result);
        state.ActiveRun = null;
        state.Checkpoint = null;
        Save(state);
    }
}
