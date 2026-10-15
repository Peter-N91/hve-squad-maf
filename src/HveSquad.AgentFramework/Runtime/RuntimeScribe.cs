using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Globalization;
using HveSquad.AgentFramework.Artifacts;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed class RuntimeHistoryIntegrityException(
    string message, SquadRunStatus status = SquadRunStatus.Blocked) : Exception(message)
{
    internal SquadRunStatus Status { get; } = status;
}

/// <summary>
/// Deterministic, single-writer native persistence, not a model Scribe dispatch. Its versioned
/// state format is intentionally distinct from Copilot host state; importing that state is rejected.
/// No token prices or concrete model identities are inferred from a tier or preference.
/// </summary>
internal sealed partial class RuntimeScribe(ProjectFiles files, ILogger? logger = null, TimeProvider? timeProvider = null)
{
    private const int MaximumStateCharacters = 64 * 1024 * 1024;
    private const string ConsumptionHeader = "# Consumption\n\nNative per-dispatch observations follow. Provider work without exact rates remains unpriced; deterministic native writes consume no model requests or tokens.\n";
    private const string NativeAgentName = "Squad Scribe";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Action<ILogger, Exception?> LogWriteContention = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, "StateWriteContention"),
        "Filesystem temporarily blocked atomic state replacement; retrying the same pending snapshot.");
    private static readonly Action<ILogger, Exception?> LogLedgerWarning = LoggerMessage.Define(
        LogLevel.Warning, new EventId(3, "LedgerOnly"),
        "Consumption ledger needs reconciliation. Verified history remains an evidence barrier; no model Scribe is dispatched.");
    private static readonly Action<ILogger, Exception?> LogDispatchCheckpointed = LoggerMessage.Define(
        LogLevel.Information, new EventId(4, "DispatchCheckpointed"),
        "Verified dispatch output evidence is durably checkpointed; final run completion remains a separate outcome.");
    private readonly Lock _writeLock = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private string? _savedStateSha256;

    internal Lock SyncRoot => _writeLock;
    private string StatePath => files.SquadRoot + "/state.json";

    internal RuntimeState? Load(bool allowLegacy = false, bool allowFederation = false)
    {
        if (!File.Exists(files.Resolve(StatePath)))
        {
            if (File.Exists(files.Resolve(files.SquadRoot + "/team.md")))
            {
                throw new InvalidDataException("Existing non-native squad state cannot be adopted implicitly.");
            }

            return null;
        }

        var snapshotBytes = files.ReadBytes(StatePath);
        var snapshot = Encoding.UTF8.GetString(snapshotBytes);
        if (snapshot.StartsWith('\uFEFF'))
        {
            snapshot = snapshot[1..];
        }
        if (snapshot.Length > MaximumStateCharacters)
        {
            throw new InvalidDataException("Native state exceeds its bounded snapshot size.");
        }
        var state = JsonSerializer.Deserialize<RuntimeState>(snapshot, JsonOptions)
            ?? throw new InvalidDataException("Empty squad state.");
        if (state.Federation is { } federation)
        {
            federation.OutputOwners = new(federation.OutputOwners, StringComparer.OrdinalIgnoreCase);
        }
        if (state.Schema != RuntimeState.SchemaVersion &&
            !(allowLegacy && state.Schema == RuntimeState.LegacySchemaVersion) &&
            !(allowFederation && state.Schema == RuntimeState.FederationSchemaVersion && state.Federation is not null))
        {
            throw new InvalidDataException(
                "Unsupported or legacy squad state format. Native v1 requires AllowNativeV1Migration and " +
                "a StateMigration host approval; Copilot state cannot be imported implicitly.");
        }

        files.BindHistoricalPaths(state.Relocation);
        if (state.CheckpointSha256 is { } seal &&
            (state.Checkpoint is null || seal != ProjectFiles.Hash(JsonSerializer.Serialize(state.Checkpoint, JsonOptions))))
        {
            throw new InvalidDataException("history-integrity: the saved checkpoint changed; no prior authorization can be reused.");
        }

        var pending = files.SquadRoot + "/state.pending.json";
        if (state.Checkpoint?.Cost is not null && File.Exists(files.Resolve(pending)) &&
            files.HashFile(pending) != HashBytes(snapshotBytes))
        {
            throw new RuntimeHistoryIntegrityException("cost-admission: an incomplete atomic state write needs explicit reconciliation; no slot may execute.");
        }
        _savedStateSha256 = HashBytes(snapshotBytes);
        return state;
    }

    internal FileStream AcquireLock()
    {
        var path = files.Resolve(files.SquadRoot + "/native.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProjectFiles.RejectLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal void Initialize(RuntimeState state, SquadRoster roster, IReadOnlyList<SquadRoutingRule> rules)
    {
        WriteRoster(roster, rules, state.ModelRouting);
        files.Write(files.SquadRoot + "/decisions.md", "# Native decisions\n\nConfirmed initialization:\n" +
            JsonSerializer.Serialize(state.Initialization is { } proposal
                ? proposal with { Notifications = null, NotificationQuestion = null } : null, JsonOptions) + "\n");
        files.Write(files.SquadRoot + "/notifications.md", "# Native host approvals\n\nApproval is delivered by a trusted host callback or authenticated channel, never this file.\n");
        files.Write(files.SquadRoot + "/consumption-rates.md", RenderRates(state.Consumption));
        files.Write(files.SquadRoot + "/consumption.md", ConsumptionHeader);
        Directory.CreateDirectory(files.Resolve(files.SquadRoot + "/history"));
        foreach (var path in new[] { files.SquadRoot + "/decisions.md", files.SquadRoot + "/notifications.md" })
        {
            CaptureBaseline(state, path);
        }
        RecordNativeOperation(state, "Initialize", null, null);
        _ = ReconcileLedger(state, expectedNewWork: true);
        Save(state);
    }

    internal void RefreshRoster(RuntimeState state, SquadRoster roster, IReadOnlyList<string> added, IReadOnlyList<string> removed,
        IReadOnlyList<SquadRoutingRule> rules)
    {
        using (_writeLock.EnterScope())
        {
            if (state.ModelRouting is { } policy)
            {
                foreach (var role in removed)
                {
                    policy.Models.Remove(role);
                }
            }
            WriteRoster(roster, rules, state.ModelRouting);
            Decision(state, "Confirmed roster refresh:\n" + JsonSerializer.Serialize(new
            {
                Profile = state.Initialization?.Profile,
                Packs = state.Initialization?.Packs,
                AddedRoles = added,
                RemovedRoles = removed,
                Members = roster.Members,
            }, JsonOptions));
            RecordNativeOperation(state, "RefreshRoster", null, null);
            _ = ReconcileLedger(state, expectedNewWork: true);
            Save(state);
        }
    }

    internal void SetModelRouting(RuntimeState state, SquadRoster roster, IReadOnlyList<SquadRoutingRule> rules,
        SquadModelRoutingConfiguration configuration, string reason)
    {
        using (_writeLock.EnterScope())
        {
            if (state.ModelRouting?.Mode == "manual" && configuration.Mode == "off")
            {
                state.PreviousManualModels = new(state.ModelRouting.Models, StringComparer.OrdinalIgnoreCase);
            }
            state.ModelRouting = SquadRuntime.CopyModelRouting(configuration);
            if (state.Checkpoint is { } checkpoint)
            {
                checkpoint.ModelRouting = SquadRuntime.CopyModelRouting(configuration);
            }
            WriteRoster(roster, rules, state.ModelRouting);
            Decision(state, reason + "\n" + JsonSerializer.Serialize(state.ModelRouting, JsonOptions));
            RecordNativeOperation(state, "RefreshModelRouting", state.Checkpoint?.RunId, null);
            _ = ReconcileLedger(state, expectedNewWork: true);
            Save(state);
        }
    }

    private void WriteRoster(SquadRoster roster, IReadOnlyList<SquadRoutingRule> rules,
        SquadModelRoutingConfiguration? modelRouting)
    {
        var table = new StringBuilder("# Native single-squad roster\n");
        var routed = modelRouting?.Mode is "ranked" or "manual";
        if (routed)
        {
            table.AppendLine("Model routing: " + modelRouting!.Mode);
        }
        table.AppendLine();
        table.AppendLine("Persistence owner: deterministic runtime Scribe (no model invocation).\n");
        table.AppendLine("## Members\n");
        table.AppendLine("| Role | Member Name | Primary Agent | Alternate Agents | Selection Cue | Invocation | Model Tier |" +
            (routed ? " Model |" : "") + " Deliverable Root |");
        table.AppendLine("| --- | --- | --- | --- | --- | --- | --- |" + (routed ? " --- |" : "") + " --- |");
        foreach (var member in roster.Members)
        {
            var model = routed ? " " + Cell(modelRouting!.Models.TryGetValue(member.Role, out var pick) ? pick : null) + " |" : "";
            table.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"| {Cell(member.Role)} | {Cell(member.MemberName)} | {Cell(member.PrimaryAgent)} | {Cell(string.Join(", ", member.AlternateAgents))} | {Cell(member.SelectionCue ?? "none")} | {Cell(member.Invocation)} | {Cell(member.ModelTier)} |{model} {Cell(member.DeliverableRoot)} |");
        }

        files.Write(files.SquadRoot + "/team.md", table.ToString());
        var routing = new StringBuilder(
            "# Native routing\n\nResearch → host-approved plan → conditional council → host-approved producing role → review.\n" +
            "Roles and concrete agents resolve only through the confirmed catalog roster. Declared child agents inherit their owner's scope.\n" +
            "Federation, Watch, autonomous/autopilot loops, remote notifications and CLI execution require a separate host integration.\n\n");
        routing.AppendLine("| Pattern / Keyword | Role(s) | Autonomy Tier | Parallel-Eligible |");
        routing.AppendLine("| --- | --- | --- | --- |");
        foreach (var rule in rules.Where(rule => rule.Roles.Except(rule.OptionalRoles, StringComparer.OrdinalIgnoreCase)
                     .All(role => roster.Resolve(role) is not null)))
        {
            routing.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(string.Join(", ", rule.Patterns))} | {Cell(string.Join(", ", rule.Roles.Where(role => roster.Resolve(role) is not null)))} | {rule.AutonomyTier} | {(rule.ParallelEligible ? "yes" : "no")} |");
        }
        files.Write(files.SquadRoot + "/routing.md", routing.ToString());
    }

    internal string AgentHistoryPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.EndsWith(' ') || name.EndsWith('.') || Encoding.UTF8.GetByteCount(name) > 250 ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal)) ||
            Regex.IsMatch(name, @"^(?:CON|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidDataException($"Agent identity '{name}' is not a portable filesystem name; it cannot be replaced by a hash or alias.");
        }

        var path = $"{files.SquadRoot}/history/{name}.md";
        _ = files.Resolve(path);
        return path;
    }

    internal void RecordEvidence(RuntimeState state, SquadDispatchEvidence evidence)
    {
        using (_writeLock.EnterScope())
        {
            VerifyIntegrity(state);
            VerifyOutputs(evidence, state);
            var prior = state.DispatchRecords.SingleOrDefault(e => e.DispatchId == evidence.DispatchId);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior, JsonOptions) != JsonSerializer.Serialize(evidence, JsonOptions))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: a recorded dispatch identity cannot be replaced.");
                }
                Verify(evidence, state);
                return;
            }

            if (evidence.HistoryPath != AgentHistoryPath(evidence.Agent) ||
                state.DispatchRecords.Any(e => e.HistoryPath.Equals(evidence.HistoryPath, StringComparison.OrdinalIgnoreCase) &&
                    e.Agent != evidence.Agent))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: literal agent history identity is invalid or collides.");
            }

            state.DispatchRecords.Add(evidence);
            state.LedgerOrder.Add(evidence.DispatchId);
            var frame = Frame("Dispatch", evidence.DispatchId, evidence);
            if (evidence.ModelRouting is { } identity)
            {
                frame += "\n- Requested model: " + (identity.RequestedModel ?? "none (parameter omitted)") +
                    "\n- Effective model: " + (identity.EffectiveModel ?? "unknown") +
                    "\n- Observed model: " + (identity.ObservedModel ?? "unreported") +
                    "\n- Route rationale: " + identity.Rationale + "\n";
            }
            if (evidence.AgentSelectionRationale is { } rationale)
            {
                frame += "\n- Agent selection: " + rationale + "\n";
            }
            Append(state, evidence.HistoryPath, frame);
            RecordNativeOperation(state, "RecordDispatchEvidence", state.Checkpoint?.RunId, evidence.DispatchId,
                state.ModelCalls.Where(c => c.DispatchId == evidence.DispatchId).OrderBy(c => c.CallOrdinal).ToArray());
        }
    }

    internal void CheckpointEvidence(RuntimeState state, SquadDispatchEvidence evidence)
    {
        using (_writeLock.EnterScope())
        {
            var checkpoint = state.Checkpoint ?? throw new InvalidDataException("No active checkpoint owns this dispatch.");
            checkpoint.PendingEvidence.Add(evidence);
            Save(state);
            if (logger is not null)
            {
                LogDispatchCheckpointed(logger, null);
            }
        }
    }

    internal void Verify(SquadDispatchEvidence evidence, RuntimeState? state = null)
    {
        VerifyOutputs(evidence, state);
        var text = files.Read(evidence.HistoryPath, MaximumStateCharacters);
        if (evidence.AssignmentSha256 is null && evidence.DispatchOrder is null)
        {
            _ = LegacyDispatchFrame(text, evidence);
        }
        else if (!text.Contains(JsonSerializer.Serialize(evidence, JsonOptions), StringComparison.Ordinal))
        {
            throw new RuntimeHistoryIntegrityException($"history-integrity: dispatch '{evidence.DispatchId}' is missing or changed.");
        }
    }

    internal void VerifyOutputs(SquadDispatchEvidence evidence, RuntimeState? state = null)
    {
        VerifyArtifact(new("primary", evidence.ArtifactPath, evidence.ArtifactSha256));
        foreach (var artifact in evidence.Artifacts ?? [])
        {
            VerifyArtifact(artifact);
        }

        VerifyHostOutputs(evidence.DispatchId, evidence.ExecutedHostTools ?? [], state);

    }

    internal void VerifyArtifact(SquadArtifactEvidence artifact)
    {
        try
        {
            if (ProjectFiles.Hash(files.Read(artifact.Path)) != artifact.Sha256)
            {
                throw new RuntimeHistoryIntegrityException(
                    $"history-integrity: Output evidence '{artifact.Path}' is missing or changed.", SquadRunStatus.Failed);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RuntimeHistoryIntegrityException(
                $"history-integrity: Output evidence '{artifact.Path}' is missing.", SquadRunStatus.Failed);
        }
    }

    internal void VerifyHostOutputs(string dispatchId, IEnumerable<SquadToolExecution> executions, RuntimeState? state = null)
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
            if (files.HashFile(output.Path) != output.Sha256 && !HasArchivedOutput(state, dispatchId, output))
            {
                throw new RuntimeHistoryIntegrityException($"history-integrity: Host output evidence '{output.Path}' for dispatch '{dispatchId}' is changed. " +
                    "Overlapping edits across distinct dispatches are unsupported; prior evidence cannot be superseded.", SquadRunStatus.Failed);
            }
        }
    }

    internal string RecordReturn(string runId, string dispatchId, string role, object report, SquadStructuredReturn payload)
    {
        var path = $"{files.SquadRoot}/returns/{runId}/{dispatchId}.json";
        files.Write(path, JsonSerializer.Serialize(new
        {
            EvidenceKind = "StructuredReturn",
            PersistenceOwner = "deterministic-runtime-scribe",
            DispatchId = dispatchId,
            Role = role,
            Report = report,
            Return = payload,
        }, JsonOptions));
        return path;
    }

    internal void Decision(RuntimeState state, string text) => Append(state, files.SquadRoot + "/decisions.md", "\n" + text + "\n");
    internal void Notification(RuntimeState state, string text) => Append(state,
        files.SquadRoot + (state.Federation is null ? "/notifications.md" : "/decisions.md"), "\n" + text + "\n");

    internal void RecordModelCall(RuntimeState state, ModelCallObservation observation, DateOnly today)
    {
        using (_writeLock.EnterScope())
        {
            var index = state.ModelCalls.FindIndex(c => c.Id == observation.Id);
            if (index < 0)
            {
                state.ModelCalls.Add(observation);
            }
            else
            {
                var original = state.ModelCalls[index];
                if (original.Status != "started" || original with
                {
                    Status = observation.Status,
                    ObservedModel = observation.ObservedModel,
                    InputTokens = observation.InputTokens,
                    OutputTokens = observation.OutputTokens,
                    CachedInputTokens = observation.CachedInputTokens,
                    CacheWriteTokens = observation.CacheWriteTokens,
                    RouteRationale = observation.RouteRationale,
                } != observation)
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: a model-call observation cannot change its dispatch identity.");
                }
                state.ModelCalls[index] = observation;
            }
            state.ModelCallCharges ??= [];
            var charge = ConsumptionMath.Charge(observation, state.Consumption, today);
            var chargeIndex = state.ModelCallCharges.FindIndex(c => c.CallId == observation.Id);
            if (chargeIndex < 0)
            {
                state.ModelCallCharges.Add(charge);
            }
            else
            {
                state.ModelCallCharges[chargeIndex] = charge;
            }
            if (state.Checkpoint?.Cost is { } cost)
            {
                cost.Revision++;
            }
            if (observation.Status != "started")
            {
                RecordNativeOperation(state, "RecordProviderAttempt", observation.RunId, observation.DispatchId,
                    observedAttempt: observation, observedCharge: charge);
            }
            Save(state);
        }
    }

    internal void RecordCoordinator(RuntimeState state, string runId, string agent, string status)
    {
        using (_writeLock.EnterScope())
        {
            var alreadyRecorded = state.CoordinatorInvocations.SelectMany(i => i.Calls).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var calls = state.ModelCalls.Where(c => c.RunId == runId && c.DispatchId is null &&
                !alreadyRecorded.Contains(c.Id)).OrderBy(c => c.CallOrdinal).ToArray();
            if (calls.Length == 0)
            {
                return;
            }

            var invocation = new CoordinatorInvocationEvidence(Guid.NewGuid().ToString("N"), runId, agent, status, calls);
            state.CoordinatorInvocations.Add(invocation);
            state.LedgerOrder.Add(invocation.Id);
            Append(state, AgentHistoryPath(agent), Frame("Invocation", invocation.Id, invocation));
            RecordNativeOperation(state, "RecordCoordinatorInvocation", runId, null);
            _ = ReconcileLedger(state, expectedNewWork: true);
        }
    }

    internal void RecordEffect(RuntimeState state, string runId, EffectCheckpoint effect)
    {
        using (_writeLock.EnterScope())
        {
            if (effect.Status != "completed" || effect.Receipt is null)
            {
                throw new InvalidDataException("Only a completed verified host effect can be recorded as executed.");
            }
            var receipt = new SquadExecutedEffect(effect.OperationId, effect.DispatchId, effect.Role, effect.Receipt);
            var prior = state.NativeOperations.SingleOrDefault(o => o.ExecutedEffect?.OperationId == effect.OperationId);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior.ExecutedEffect, JsonOptions) != JsonSerializer.Serialize(receipt, JsonOptions))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: an executed effect receipt changed.");
                }
                return;
            }
            RecordNativeOperation(state, "RecordHostEffect", runId, effect.DispatchId, executedEffect: receipt);
        }
    }

    internal SquadEvidenceReconciliation PrepareEvidence(RuntimeState state)
    {
        using (_writeLock.EnterScope())
        {
            var correctedAppend = state.PendingHistoryAppend is not null;
            if (correctedAppend)
            {
                FinishPendingAppend(state);
            }
            correctedAppend |= RecoverGateReceipts(state);
            correctedAppend |= RecoverModeHistory(state);
            correctedAppend |= RecoverRoleMemoryReceipts(state);

            if (state.AppendOnlyFiles.Count == 0)
            {
                state.DispatchRecords = state.Runs.SelectMany(r => r.Evidence)
                    .Concat(state.Checkpoint?.Evidence ?? [])
                    .Concat(state.ActiveRun?.Evidence ?? [])
                    .DistinctBy(e => e.DispatchId).ToList();
                state.LedgerOrder = state.DispatchRecords.Select(e => e.DispatchId).ToList();
                VerifyRecordedHistory(state);
                foreach (var path in state.DispatchRecords.Select(e => e.HistoryPath)
                    .Concat([files.SquadRoot + "/decisions.md", files.SquadRoot + "/notifications.md"])
                    .Distinct(StringComparer.Ordinal))
                {
                    CaptureBaseline(state, path);
                }
                Save(state);
            }

            VerifyIntegrity(state);
            VerifyRecordedHistory(state);
            var ledger = state.Federation is null ? ReconcileLedger(state) :
                new SquadEvidenceReconciliation(SquadEvidenceFailureClass.None, 0, true, true,
                    ["Federation uses verified inner ledgers; no second consumption ledger is created."]);
            return ledger with
            {
                CorrectivePasses = correctedAppend ? 1 : ledger.CorrectivePasses,
                Diagnostics = correctedAppend
                    ? ["Recovered the exact pending append once; prior bytes were preserved and no model work was replayed.", .. ledger.Diagnostics]
                    : ledger.Diagnostics,
            };
        }
    }

    internal void VerifyIntegrity(RuntimeState state)
    {
        using (_writeLock.EnterScope())
        {
            if (state.PendingHistoryAppend is not null)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a pending writer append must be reconciled before the next evidence barrier.");
            }
            foreach (var (path, baseline) in state.AppendOnlyFiles)
            {
                VerifyBaseline(path, baseline);
            }

            var checkpointEvidence = (state.Checkpoint?.Evidence ?? []).Concat(state.Checkpoint?.PendingEvidence ?? []).ToArray();
            if (checkpointEvidence.Select(e => e.DispatchId).Distinct(StringComparer.Ordinal).Count() != checkpointEvidence.Length)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: checkpoint evidence has duplicate dispatch identities.");
            }
            foreach (var evidence in state.Checkpoint?.Evidence ?? [])
            {
                var recorded = state.DispatchRecords.SingleOrDefault(e => e.DispatchId == evidence.DispatchId);
                if (recorded is null || JsonSerializer.Serialize(recorded, JsonOptions) != JsonSerializer.Serialize(evidence, JsonOptions))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: checkpoint evidence differs from the recorded dispatch.");
                }
            }
            foreach (var (key, digest) in state.Checkpoint?.SessionBindings ?? [])
            {
                if (!state.Sessions.TryGetValue(key, out var session) ||
                    ProjectFiles.Hash(JsonSerializer.Serialize(session)) != digest)
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: a dispatch's protected session changed.");
                }
            }
            VerifyProductionHandoffs(state);
            VerifyHumanDecisions(state);
            VerifyOutputRevisions(state);
            VerifyModeHistory(state);
        }
    }

    internal SquadEvidenceReconciliation ReconcileLedger(RuntimeState state, bool expectedNewWork = false)
    {
        using (_writeLock.EnterScope())
        {
            var path = files.SquadRoot + "/consumption.md";
            try
            {
                var expected = RenderLedger(state);
                if (File.Exists(files.Resolve(path)) && files.Read(path, MaximumStateCharacters) == expected)
                {
                    state.LedgerNeedsReconciliation = false;
                    return new(SquadEvidenceFailureClass.None, 0, true, true, []);
                }

                RecordNativeOperation(state, "ReconcileLedger", state.Checkpoint?.RunId, null);
                expected = RenderLedger(state);
                files.Write(path, expected, maximumCharacters: MaximumStateCharacters);
                if (files.Read(path, MaximumStateCharacters) != expected)
                {
                    throw new IOException("The corrected ledger does not match the recorded work.");
                }
                state.LedgerNeedsReconciliation = false;
                Save(state);
                if (expectedNewWork)
                {
                    return new(SquadEvidenceFailureClass.None, 1, true, true, []);
                }

                const string diagnostic = "ledger-only: consumption differed from recorded work and was corrected once; evidence barriers and parallel dispatch remain available.";
                WarnLedger(state, diagnostic, null);
                return new(SquadEvidenceFailureClass.LedgerOnly, 1, true, true, [diagnostic]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.LedgerNeedsReconciliation = true;
                const string diagnostic = "ledger-only: the derived consumption ledger could not be corrected in one pass; verified history permits continuation and the next handoff will reconcile it.";
                WarnLedger(state, diagnostic, ex);
                Save(state);
                return new(SquadEvidenceFailureClass.LedgerOnly, 1, true, false, [diagnostic]);
            }
        }
    }

    private void RecordNativeOperation(RuntimeState state, string operation, string? runId, string? dispatchId,
        IReadOnlyList<ModelCallObservation>? observedCalls = null, SquadExecutedEffect? executedEffect = null,
        ModelCallObservation? observedAttempt = null, ModelCallCharge? observedCharge = null, SquadGateRecord? gateRecord = null,
        SquadProductionHandoff? productionHandoff = null, HumanDecisionReceipt? humanDecision = null,
        OutputRevisionSnapshot? outputRevision = null, JsonElement? watchRecord = null, ReportHumanGate? humanGate = null,
        SquadRoleMemoryEntry? roleMemory = null, LearningOperationRecord? learningRecord = null)
    {
        var record = new NativeOperationEvidence(Guid.NewGuid().ToString("N"), operation, runId, dispatchId, _clock.GetUtcNow(),
            ObservedDispatchCalls: observedCalls, ExecutedEffect: executedEffect,
            ObservedAttempt: observedAttempt, ObservedCharge: observedCharge, GateRecord: gateRecord,
            ProductionHandoff: productionHandoff, HumanDecision: humanDecision, OutputRevision: outputRevision,
            WatchRecord: watchRecord, HumanGate: humanGate, RoleMemory: roleMemory, LearningRecord: learningRecord);
        state.NativeOperations.Add(record);
        state.LedgerOrder.Add(record.Id);
        Append(state, AgentHistoryPath(NativeAgentName), Frame("Operation", record.Id, record));
    }

    private void Append(RuntimeState state, string path, string content)
    {
        using (_writeLock.EnterScope())
        {
            if (state.PendingHistoryAppend is not null)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: only one Scribe append may be in flight.");
            }
            if (state.AppendOnlyFiles.TryGetValue(path, out var baseline))
            {
                VerifyBaseline(path, baseline);
            }
            else if (File.Exists(files.Resolve(path)))
            {
                throw new RuntimeHistoryIntegrityException($"history-integrity: unbound writer file '{path}' cannot be adopted or overwritten.");
            }
            var bytes = File.Exists(files.Resolve(path)) ? files.ReadBytes(path) : [];
            var suffix = Encoding.UTF8.GetBytes(content);
            if ((long)bytes.Length + suffix.Length > ProjectFiles.MaximumHostOutputBytes)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: append exceeds the protected history byte limit.");
            }
            state.PendingHistoryAppend = new(path, new(bytes.Length, HashBytes(bytes)), content, HashBytes([.. bytes, .. suffix]));
            Save(state);
            FinishPendingAppend(state);
        }
    }

    private void FinishPendingAppend(RuntimeState state)
    {
        var pending = state.PendingHistoryAppend ?? throw new InvalidOperationException("No pending append exists.");
        if (!ProjectFiles.IsWithin(pending.Path, files.SquadRoot + "/history") &&
            pending.Path != files.SquadRoot + "/decisions.md" && pending.Path != files.SquadRoot + "/notifications.md" &&
            !MemoryContracts.IsMemoryPath(files, pending.Path))
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: pending append escaped the writer's append-only scope.");
        }
        var bytes = File.Exists(files.Resolve(pending.Path)) ? files.ReadBytes(pending.Path) : [];
        var suffix = Encoding.UTF8.GetBytes(pending.Content);
        if (pending.Before.Length < 0 || pending.Before.Length > bytes.Length ||
            pending.Before.Length + suffix.Length > ProjectFiles.MaximumHostOutputBytes)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: pending append has an invalid byte boundary.");
        }
        var offset = checked((int)pending.Before.Length);
        var written = bytes.Length - offset;
        if (written > suffix.Length || HashBytes(bytes.AsSpan(0, offset)) != pending.Before.Sha256 ||
            !bytes.AsSpan(offset).SequenceEqual(suffix.AsSpan(0, written)) ||
            HashBytes([.. bytes.AsSpan(0, offset), .. suffix]) != pending.AfterSha256)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: original bytes or pending append content changed; automatic correction is forbidden.");
        }

        if (written < suffix.Length)
        {
            files.AppendBytes(pending.Path, suffix.AsSpan(written));
        }
        if (files.HashFile(pending.Path) != pending.AfterSha256)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: the completed append did not preserve its exact byte binding.");
        }
        state.AppendOnlyFiles[pending.Path] = new(offset + suffix.Length, pending.AfterSha256);
        state.PendingHistoryAppend = null;
        Save(state);
    }

    private void CaptureBaseline(RuntimeState state, string path)
    {
        var bytes = files.ReadBytes(path);
        state.AppendOnlyFiles[path] = new(bytes.Length, HashBytes(bytes));
    }

    private void VerifyBaseline(string path, AppendOnlyFileEvidence baseline)
    {
        try
        {
            var bytes = files.ReadBytes(path);
            if (bytes.LongLength != baseline.Length || HashBytes(bytes) != baseline.Sha256)
            {
                throw new RuntimeHistoryIntegrityException($"history-integrity: append-only bytes or order changed in '{path}'.");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RuntimeHistoryIntegrityException($"history-integrity: protected file '{path}' is missing.");
        }
    }

    private void VerifyRecordedHistory(RuntimeState state)
    {
        VerifyWatchIndexes(state);
        VerifyProductionHandoffs(state);
        VerifyHumanDecisions(state);
        VerifyOutputRevisions(state);
        VerifyRoleMemory(state);
        VerifyLearningOperations(state);
        var decisions = state.GateRecords is { Count: > 0 }
            ? files.Read(files.SquadRoot + "/decisions.md", MaximumStateCharacters) : "";
        if (state.GateRecords is { } gates &&
            (gates.Select(gate => gate.Id).Distinct(StringComparer.Ordinal).Count() != gates.Count ||
                gates.Any(gate => state.NativeOperations.Count(operation => operation.GateRecord?.Id == gate.Id) != 1 ||
                    !decisions.Contains(JsonSerializer.Serialize(gate, JsonOptions), StringComparison.Ordinal))))
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: a readiness index has no exact immutable decision and native receipt.");
        }
        var records = new Dictionary<string, (string Path, string Kind, string Json, SquadDispatchEvidence? Legacy)>(StringComparer.Ordinal);
        foreach (var record in state.DispatchRecords)
        {
            records.Add(record.DispatchId, (record.HistoryPath, "Dispatch", JsonSerializer.Serialize(record, JsonOptions),
                record.AssignmentSha256 is null && record.DispatchOrder is null ? record : null));
        }
        foreach (var record in state.NativeOperations)
        {
            if (record.GateRecord is { } gate &&
                (state.GateRecords is not { } recordedGates || recordedGates.Count(item => item.Id == gate.Id) != 1 ||
                    JsonSerializer.Serialize(recordedGates.Single(item => item.Id == gate.Id), JsonOptions) !=
                    JsonSerializer.Serialize(gate, JsonOptions)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a readiness gate changed after its durable receipt.");
            }
            if (record.ObservedAttempt is { } attempt &&
                (state.ModelCalls.Count(call => call.Id == attempt.Id) != 1 ||
                 JsonSerializer.Serialize(state.ModelCalls.Single(call => call.Id == attempt.Id), JsonOptions) !=
                    JsonSerializer.Serialize(attempt, JsonOptions)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a completed provider attempt changed after its durable receipt.");
            }
            if (record.ObservedCharge is { } charge &&
                (state.ModelCallCharges is not { } recordedCharges || recordedCharges.Count(item => item.CallId == charge.CallId) != 1 ||
                 JsonSerializer.Serialize(recordedCharges.Single(item => item.CallId == charge.CallId), JsonOptions) !=
                    JsonSerializer.Serialize(charge, JsonOptions)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: provider pricing/calibration provenance changed after its durable receipt.");
            }
            if (record.ObservedDispatchCalls is { } calls &&
                JsonSerializer.Serialize(calls, JsonOptions) != JsonSerializer.Serialize(
                    state.ModelCalls.Where(c => c.DispatchId == record.DispatchId).OrderBy(c => c.CallOrdinal).ToArray(), JsonOptions))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: recorded provider observations changed after dispatch completion.");
            }
            records.Add(record.Id, (AgentHistoryPath(NativeAgentName), "Operation", JsonSerializer.Serialize(record, JsonOptions), null));
        }
        foreach (var record in state.CoordinatorInvocations)
        {
            records.Add(record.Id, (AgentHistoryPath(record.Agent), "Invocation", JsonSerializer.Serialize(record, JsonOptions), null));
        }
        if (state.LedgerOrder.Count != records.Count ||
            state.LedgerOrder.Distinct(StringComparer.Ordinal).Count() != records.Count ||
            state.LedgerOrder.Any(id => !records.ContainsKey(id)))
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: the full recorded-work identity/order index is incomplete or duplicated.");
        }

        foreach (var group in state.LedgerOrder.GroupBy(id => files.Resolve(records[id].Path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var text = files.Read(records[group.First()].Path, MaximumStateCharacters);
            var offset = 0;
            foreach (var id in group)
            {
                var record = records[id];
                var frame = record.Legacy is { } legacy ? LegacyDispatchFrame(text, legacy) :
                    $"\n## {record.Kind} {id}\n\n```json\n{record.Json}\n```\n";
                var index = text.IndexOf(frame, offset, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new RuntimeHistoryIntegrityException($"history-integrity: recorded entries are missing, reordered or changed in '{group.Key}'.");
                }
                offset = index + frame.Length;
            }
            if (Regex.Count(text, @"(?m)^## (?:Dispatch|Operation|Invocation) [a-f0-9]{32}\s*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) != group.Count())
            {
                throw new RuntimeHistoryIntegrityException($"history-integrity: history contains unrecorded or duplicate entries in '{group.Key}'.");
            }
        }
    }

    private static string Frame(string kind, string id, object record) =>
        $"\n## {kind} {id}\n\n```json\n{JsonSerializer.Serialize(record, JsonOptions)}\n```\n";

    private static string LegacyDispatchFrame(string text, SquadDispatchEvidence expected)
    {
        var match = Regex.Match(text,
            @"\r?\n## Dispatch " + Regex.Escape(expected.DispatchId) + @"\r?\n\r?\n```json\r?\n(?<json>[\s\S]*?)\r?\n```\r?\n",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: a native-v1 dispatch frame is missing.");
        }
        try
        {
            var observed = JsonSerializer.Deserialize<SquadDispatchEvidence>(match.Groups["json"].Value, JsonOptions);
            if (observed is null || JsonSerializer.Serialize(observed, JsonOptions) != JsonSerializer.Serialize(expected, JsonOptions))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: native-v1 dispatch facts changed; old bytes cannot be rewritten.");
            }
            return match.Value;
        }
        catch (JsonException)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: the native-v1 dispatch frame is malformed.");
        }
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string RenderLedger(RuntimeState state)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var evidence in state.DispatchRecords)
        {
            var removed = state.RemovedMembers.Any(member => member.Role.Equals(evidence.Role, StringComparison.OrdinalIgnoreCase))
                ? " (removed)" : "";
            rows.Add(evidence.DispatchId,
                $"\n- Dispatch `{evidence.DispatchId}`; role `{evidence.Role}`{removed}; agent `{evidence.Agent}`; model `{evidence.Model}` " +
                $"({evidence.ModelSource}); tier `{evidence.ModelTier ?? "unknown"}`; input {Tokens(evidence.InputTokens)}; " +
                $"output {Tokens(evidence.OutputTokens)}; cost/credits unknown.\n");
        }
        foreach (var operation in state.NativeOperations)
        {
            rows.Add(operation.Id, $"\n- Native operation `{operation.Id}`; agent `{NativeAgentName}`; {operation.Operation}; " +
                "model invocations none; model tokens not-applicable; model cost USD 0.0000.\n");
        }
        foreach (var invocation in state.CoordinatorInvocations)
        {
            rows.Add(invocation.Id, $"\n- Coordinator invocation `{invocation.Id}`; agent `{invocation.Agent}`; " +
                $"status `{invocation.Status}`; actual provider attempts {invocation.Calls.Count}; input " +
                $"{Tokens(SumKnown(invocation.Calls.Select(c => c.InputTokens)))}; output " +
                $"{Tokens(SumKnown(invocation.Calls.Select(c => c.OutputTokens)))}; cost/credits unknown.\n");
        }
        var header = state.Consumption is null ? ConsumptionHeader :
            "# Consumption\n\nHost-rate-priced estimates and honest unknowns follow. Billed cost and credits are not inferred from model tiers.\n";
        return header + RenderRoleTotals(state) + string.Concat(state.LedgerOrder.Select(id => rows[id])) + RenderCharges(state);
    }

    private static long? SumKnown(IEnumerable<long?> observations)
    {
        var values = observations.ToArray();
        return values.Any(value => value is null) ? null : values.Sum(value => value!.Value);
    }

    private static string Tokens(long? tokens) => tokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    private void WarnLedger(RuntimeState state, string diagnostic, Exception? exception)
    {
        if (!state.LedgerDiagnostics.Contains(diagnostic, StringComparer.Ordinal))
        {
            state.LedgerDiagnostics.Add(diagnostic);
        }
        if (logger is not null)
        {
            LogLedgerWarning(logger, exception);
        }
    }

    internal void MigrateV1(RuntimeState state)
    {
        if (state.Schema != RuntimeState.LegacySchemaVersion || files.SquadRoot != ProjectFiles.StateRoot)
        {
            throw new InvalidDataException("Only native v1 at its original single-squad root can be migrated.");
        }

        var originalHash = files.HashFile(StatePath);
        var backup = files.SquadRoot + "/migrations/native-v1-state.json";
        if (File.Exists(files.Resolve(backup)) && files.HashFile(backup) != originalHash)
        {
            throw new InvalidDataException("A different native-v1 migration backup already exists; reconcile before writing.");
        }

        if (!File.Exists(files.Resolve(backup)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(files.Resolve(backup))!);
            File.Copy(files.Resolve(StatePath), files.Resolve(backup), overwrite: false);
        }
        if (files.HashFile(backup) != originalHash)
        {
            throw new InvalidDataException("Native-v1 migration backup verification failed.");
        }
        foreach (var name in new[] { "consumption.md", "consumption-rates.md" })
        {
            var source = files.SquadRoot + "/" + name;
            var destination = files.SquadRoot + "/migrations/native-v1-" + name;
            if (!File.Exists(files.Resolve(source)))
            {
                throw new InvalidDataException("Native-v1 migration requires its original consumption files.");
            }
            if (!File.Exists(files.Resolve(destination)))
            {
                File.Copy(files.Resolve(source), files.Resolve(destination), overwrite: false);
            }
            if (files.HashFile(source) != files.HashFile(destination))
            {
                throw new InvalidDataException("Native-v1 consumption backup differs; reconcile before writing.");
            }
        }

        state.Schema = RuntimeState.SchemaVersion;
        state.SquadRoot = files.SquadRoot;
        Save(state);
    }

    internal string StorePayload(string operationId, object? payload, string suffix)
    {
        if (!Guid.TryParseExact(operationId, "N", out _) || suffix is not ("arguments" or "result"))
        {
            throw new InvalidDataException("Invalid protected effect payload identity.");
        }

        var path = $"{files.SquadRoot}/payloads/{operationId}-{suffix}.json";
        files.Write(path, JsonSerializer.Serialize(payload, JsonOptions), maximumCharacters: MaximumStateCharacters);
        return path;
    }

    internal ProtectedEffectPayload ReadPayload(EffectCheckpoint effect)
    {
        if (effect.PayloadPath != $"{files.SquadRoot}/payloads/{effect.OperationId}-arguments.json")
        {
            throw new InvalidDataException("Effect payload escaped its protected case binding.");
        }

        var payload = JsonSerializer.Deserialize<ProtectedEffectPayload>(
            files.Read(effect.PayloadPath, MaximumStateCharacters), JsonOptions)
            ?? throw new InvalidDataException("Missing protected effect payload.");
        if (payload.ArgumentsSha256 != effect.ArgumentsSha256 ||
            ProjectFiles.Hash(JsonSerializer.Serialize(payload.Arguments)) != effect.ArgumentsSha256)
        {
            throw new InvalidDataException("Protected effect arguments changed; old authorization is invalid.");
        }

        return payload;
    }

    internal JsonElement ReadEffectResult(EffectCheckpoint effect)
    {
        if (effect.ResultPath != $"{files.SquadRoot}/payloads/{effect.OperationId}-result.json")
        {
            throw new InvalidDataException("Effect result escaped its protected case binding.");
        }

        if (effect.ResultSha256 is null || files.HashFile(effect.ResultPath) != effect.ResultSha256)
        {
            throw new InvalidDataException("Protected effect result changed; it cannot be used for continuation.");
        }

        using var document = JsonDocument.Parse(files.Read(effect.ResultPath, MaximumStateCharacters));
        return document.RootElement.Clone();
    }

    internal void Save(RuntimeState state)
    {
        using (_writeLock.EnterScope())
        {
            state.CheckpointSha256 = state.Checkpoint is null
                ? null : ProjectFiles.Hash(JsonSerializer.Serialize(state.Checkpoint, JsonOptions));
            var path = files.Resolve(StatePath);
            var pending = files.Resolve(files.SquadRoot + "/state.pending.json");
            AssertSavedSnapshot();
            if (File.Exists(pending))
            {
                throw new RuntimeHistoryIntegrityException(
                    "cost-admission: an existing partial native snapshot cannot be overwritten; explicit reconciliation is required.");
            }
            files.Write(files.SquadRoot + "/state.pending.json", JsonSerializer.Serialize(state, JsonOptions),
                maximumCharacters: MaximumStateCharacters);
            ProjectFiles.RejectLinks(path);
            var expected = files.HashFile(files.SquadRoot + "/state.pending.json");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(pending, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 9 && OperatingSystem.IsWindows() &&
                    ex is IOException or UnauthorizedAccessException &&
                    (ex.HResult & 0xFFFF) is 5 or 32 or 33)
                {
                    if (logger is not null)
                    {
                        LogWriteContention(logger, ex);
                    }
                    Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
                }
            }

            if (files.HashFile(StatePath) != expected)
            {
                throw new InvalidDataException("Atomic state replacement did not preserve the prepared snapshot.");
            }
            _savedStateSha256 = expected;
        }
    }

    private static string Cell(string? value) => (value ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
}

internal sealed class RuntimeState
{
    internal const string LegacySchemaVersion = "hve-squad-maf/native-single-squad/v1";
    internal const string SchemaVersion = "hve-squad-maf/native-squad/v2";
    internal const string FederationSchemaVersion = "hve-squad-maf/native-federation/v1";
    public string Schema { get; set; } = SchemaVersion;
    public string SquadRoot { get; set; } = ProjectFiles.StateRoot;
    public string Fingerprint { get; set; } = "";
    public string Binding { get; set; } = "";
    public string? ReleaseTag { get; set; }
    public string? ReleaseCommitSha { get; set; }
    public string ArtifactOrigin { get; set; } = "";
    public SquadInitializationProposal? Initialization { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NativeRelocation? Relocation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NativeFederation? Federation { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("currentRun")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadFederationCost? FederationCurrentRun { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FederationRunCheckpoint? FederationRun { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadFederationRunResult>? FederationRuns { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("trigger")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadWatchTrigger? Trigger { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<WatchRunCheckpoint>? WatchRuns { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("notify")]
    public SquadNotificationConfiguration? Notify { get; set; }
    public List<SquadRosterEntry> RemovedMembers { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadModelRoutingConfiguration? ModelRouting { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? PreviousManualModels { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ModelRoutingDiagnostics { get; set; }
    public Dictionary<string, JsonElement> Sessions { get; set; } = new(StringComparer.Ordinal);
    public List<SquadRunResult> Runs { get; set; } = [];
    public List<SquadConfirmedAnswer> ConfirmedAnswers { get; set; } = [];
    public List<SquadQuestion> OpenQuestions { get; set; } = [];
    public List<string> UserTurns { get; set; } = [];
    public SquadRunResult? ActiveRun { get; set; }
    public RunCheckpoint? Checkpoint { get; set; }
    public string? CheckpointSha256 { get; set; }
    public Dictionary<string, AppendOnlyFileEvidence> AppendOnlyFiles { get; set; } = new(StringComparer.Ordinal);
    public PendingHistoryAppend? PendingHistoryAppend { get; set; }
    public List<SquadDispatchEvidence> DispatchRecords { get; set; } = [];
    public List<NativeOperationEvidence> NativeOperations { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadRoleMemoryEntry>? RoleMemories { get; set; }
    public List<ModelCallObservation> ModelCalls { get; set; } = [];
    public List<CoordinatorInvocationEvidence> CoordinatorInvocations { get; set; } = [];
    public List<string> LedgerOrder { get; set; } = [];
    public bool LedgerNeedsReconciliation { get; set; }
    public List<string> LedgerDiagnostics { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadConsumptionConfiguration? Consumption { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ConsumptionInputSha256 { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ModelCallCharge>? ModelCallCharges { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadConsumptionReconciliation>? ConsumptionReconciliations { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadCostPreflight>? CostPreflights { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadGateRecord>? GateRecords { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SquadProductionHandoff>? ProductionHandoffs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HumanDecisionReceipt>? HumanDecisions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<OutputRevisionSnapshot>? OutputRevisions { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
