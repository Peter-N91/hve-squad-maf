using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record ReportHumanGate(string Id, string RunId, string Kind, string? Role,
    string? DispatchId, string Mode, bool IsQuestion, string Status, string Channel,
    DateTimeOffset RecordedAt, string DeliveryStatus = "not-sent", string OutboundStatus = "not-enabled",
    bool GitHubIssueObserved = false);

internal sealed partial class RuntimeScribe
{
    internal SquadRoster ReadReportRoster()
    {
        var table = MarkdownTables.Parse(files.Read(files.SquadRoot + "/team.md", MaximumStateCharacters))
            .SingleOrDefault(candidate => candidate.Headers.Contains("Role", StringComparer.OrdinalIgnoreCase) &&
                candidate.Headers.Contains("Primary Agent", StringComparer.OrdinalIgnoreCase) &&
                candidate.Headers.Contains("Deliverable Root", StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The native report roster needs its complete Primary Agent/Deliverable Root table.");
        return new()
        {
            Members = table.Rows.Select(row => new SquadRosterEntry
            {
                Role = row["Role"],
                PrimaryAgent = row["Primary Agent"],
                MemberName = row["Member Name"],
                ModelTier = row["Model Tier"],
                DeliverableRoot = row["Deliverable Root"] is "" or "-" or "\u2014"
                    ? null : row["Deliverable Root"],
            }).ToArray(),
        };
    }

    internal void RecordReportHumanGate(RuntimeState state, SquadPendingHumanAction action) =>
        RecordReportHumanGate(state, new ReportHumanGate(action.Binding.Id, action.Binding.RunId, action.Binding.Gate,
            action.Binding.Role, action.Binding.DispatchId, action.Binding.Mode, action.IsQuestion,
            action.Status, action.Channel, action.CreatedAt, action.DeliveryStatus, action.OutboundStatus,
            action.Channel == "github-issue" && action.DeliveryStatus == "sent" && action.Reference is not null));

    internal void RecordReportHumanGate(RuntimeState state, ReportHumanGate gate)
    {
        using (SyncRoot.EnterScope())
        {
            var prior = state.NativeOperations.LastOrDefault(operation => operation.HumanGate?.Id == gate.Id)?.HumanGate;
            if (prior == gate)
            {
                return;
            }
            if (prior is not null && (prior.RunId != gate.RunId || prior.Kind != gate.Kind ||
                prior.Role != gate.Role || prior.DispatchId != gate.DispatchId || prior.Mode != gate.Mode ||
                prior.IsQuestion != gate.IsQuestion || prior.Channel != gate.Channel))
            {
                throw new RuntimeHistoryIntegrityException("human-gate: reportable gate identity changed.");
            }
            RecordNativeOperation(state, "RecordHumanGate", gate.RunId, gate.DispatchId, humanGate: gate);
            Save(state);
        }
    }

    internal bool VerifyReportSnapshot(RuntimeState state)
    {
        if (state.SquadRoot != files.SquadRoot || File.Exists(files.Resolve(files.SquadRoot + "/state.pending.json")))
        {
            throw new RuntimeHistoryIntegrityException("report-snapshot: a partial or differently rooted native snapshot cannot be exported.");
        }
        VerifyIntegrity(state);
        VerifyRecordedHistory(state);
        return state.Federation is not null ||
            VerifyRates(state) && files.Read(files.SquadRoot + "/consumption.md", MaximumStateCharacters) == RenderLedger(state);
    }
}

internal sealed record ReportCase(string? Name, ProjectFiles Files, RuntimeScribe Scribe,
    RuntimeState State, SquadRoster Roster, IReadOnlyDictionary<string, string?> Roots, bool LedgerVerified);

internal sealed record ReportDispatch(string? Squad, SquadDispatchEvidence Evidence, DateTimeOffset? RecordedAt,
    string? RunId, string AutonomyTier, bool OutputsVerified);

internal sealed record ReportCall(string? Squad, ModelCallObservation Observation, ModelCallCharge? Charge,
    DateTimeOffset? RecordedAt);

internal sealed record ReportGate(string? Squad, SquadGateRecord Record);
internal sealed record ReportHuman(string? Squad, ReportHumanGate Gate, DateTimeOffset? FiredAt);
internal sealed record ReportTurn(string? Squad, CoordinatorInvocationEvidence Invocation, DateTimeOffset? RecordedAt);

internal sealed class ReportObservation
{
    internal required IReadOnlyList<ReportCase> Cases { get; init; }
    internal required IReadOnlyList<ReportDispatch> Dispatches { get; init; }
    internal required IReadOnlyList<ReportCall> Calls { get; init; }
    internal required IReadOnlyList<ReportGate> Gates { get; init; }
    internal required IReadOnlyList<ReportHuman> Humans { get; init; }
    internal required IReadOnlyList<ReportTurn> Turns { get; init; }
    internal required IReadOnlyList<string> Diagnostics { get; init; }

    internal static ReportObservation Read(IReadOnlyList<ReportCase> cases, DateTimeOffset? since, DateTimeOffset now)
    {
        var dispatches = new List<ReportDispatch>();
        var calls = new List<ReportCall>();
        var gates = new List<ReportGate>();
        var humans = new List<ReportHuman>();
        var turns = new List<ReportTurn>();
        var diagnostics = new List<string>();
        var undated = 0;
        foreach (var item in cases)
        {
            var state = item.State;
            if (!item.LedgerVerified)
            {
                diagnostics.Add(Label(item.Name) + "Derived consumption/rates need reconciliation. This read-only helper did not repair them; metrics use immutable provider receipts.");
            }
            var nativeAttempts = state.NativeOperations.Where(operation => operation.ObservedAttempt is not null)
                .ToDictionary(operation => operation.ObservedAttempt!.Id, operation => operation.RecordedAt, StringComparer.Ordinal);
            var chargeIndex = (state.ModelCallCharges ?? []).ToDictionary(charge => charge.CallId, StringComparer.Ordinal);
            var callTimes = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            foreach (var call in state.ModelCalls)
            {
                DateTimeOffset? at = call.RecordedAt ?? (nativeAttempts.TryGetValue(call.Id, out var completedAt) ? completedAt : null);
                callTimes.Add(call.Id, at);
                if (Includes(at, since, now, ref undated))
                {
                    calls.Add(new(item.Name, call, chargeIndex.GetValueOrDefault(call.Id), at));
                }
            }
            var recordedDispatches = state.NativeOperations.Where(operation => operation.Operation == "RecordDispatchEvidence" &&
                operation.DispatchId is not null).ToDictionary(operation => operation.DispatchId!, StringComparer.Ordinal);
            foreach (var evidence in state.DispatchRecords)
            {
                recordedDispatches.TryGetValue(evidence.DispatchId, out var receipt);
                var at = receipt?.RecordedAt;
                if (!Includes(at, since, now, ref undated))
                {
                    continue;
                }
                var verified = VerifyOutputs(item, evidence);
                if (!verified)
                {
                    diagnostics.Add(Label(item.Name) + "A recorded dispatch has changed/missing current output bytes; it is historical activity, not current delivered capability.");
                }
                var runId = receipt?.RunId ?? state.ModelCalls.FirstOrDefault(call => call.DispatchId == evidence.DispatchId)?.RunId;
                var approvals = state.NativeOperations.Select(operation => operation.HumanGate)
                    .OfType<ReportHumanGate>().Where(gate => gate.DispatchId == evidence.DispatchId).ToArray();
                var recordedMode = state.GateRecords?.LastOrDefault(gate => gate.RunId == runId && gate.Kind == "Mode Outcome");
                var mode = recordedMode?.Payload.TryGetProperty("Result", out var modeResult) == true &&
                    modeResult.TryGetProperty("Mode", out var modeName) ? modeName.GetString() : null;
                mode ??= state.NativeOperations.Select(operation => operation.HumanGate)
                    .OfType<ReportHumanGate>().FirstOrDefault(gate => gate.RunId == runId)?.Mode;
                var tier = approvals.Length != 0 ? "gated" : mode is "autonomous" or "autopilot" ? "auto" :
                    mode == "interactive" ? "confirm" : "unknown";
                dispatches.Add(new(item.Name, evidence, at, runId, tier, verified));
            }
            foreach (var invocation in state.CoordinatorInvocations)
            {
                var known = invocation.Calls.Select(call => callTimes.GetValueOrDefault(call.Id))
                    .OfType<DateTimeOffset>().ToArray();
                DateTimeOffset? at = known.Length == invocation.Calls.Count && known.Length != 0 ? known.Min() : null;
                if (Includes(at, since, now, ref undated))
                {
                    turns.Add(new(item.Name, invocation, at));
                }
            }
            gates.AddRange((state.GateRecords ?? []).Where(gate => gate.RecordedAt <= now &&
                (since is null || gate.RecordedAt >= since)).Select(gate => new ReportGate(item.Name, gate)));
            var gateEvents = state.NativeOperations.Where(operation => operation.HumanGate is not null)
                .GroupBy(operation => operation.HumanGate!.Id, StringComparer.Ordinal);
            var knownActions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in gateEvents)
            {
                var ordered = group.OrderBy(operation => operation.RecordedAt).ToArray();
                var first = ordered[0].HumanGate!;
                var last = ordered.LastOrDefault(operation => operation.RecordedAt <= now)?.HumanGate;
                knownActions.Add(first.Id);
                if (last is not null && first.RecordedAt <= now && (since is null || first.RecordedAt >= since))
                {
                    humans.Add(new(item.Name, last, first.RecordedAt));
                }
            }
            // Older durable decisions remain authoritative; never reconstruct their private payloads.
            foreach (var decision in state.HumanDecisions ?? [])
            {
                var original = state.Checkpoint?.HumanActions?.SingleOrDefault(action =>
                    action.Action.Binding.Id == decision.Binding.Id)?.Action;
                DateTimeOffset? firedAt = original?.CreatedAt;
                if (knownActions.Add(decision.Binding.Id) && Includes(firedAt, since, now, ref undated))
                {
                    humans.Add(new(item.Name, new(decision.Binding.Id, decision.Binding.RunId,
                        decision.Binding.Gate, decision.Binding.Role, decision.Binding.DispatchId,
                        decision.Binding.Mode, decision.Decision == SquadHumanDecision.Answer,
                        decision.Decision.ToString().ToLowerInvariant(), decision.Provider, decision.RecordedAt),
                        firedAt));
                }
            }
            foreach (var pending in state.Checkpoint?.HumanActions ?? [])
            {
                var action = pending.Action;
                if (knownActions.Add(action.Binding.Id) && action.CreatedAt <= now &&
                    (since is null || action.CreatedAt >= since))
                {
                    humans.Add(new(item.Name, new(action.Binding.Id, action.Binding.RunId, action.Binding.Gate,
                        action.Binding.Role, action.Binding.DispatchId, action.Binding.Mode, action.IsQuestion,
                        action.Status, action.Channel, action.CreatedAt, action.DeliveryStatus, action.OutboundStatus),
                        action.CreatedAt));
                }
            }
        }
        if (undated != 0)
        {
            diagnostics.Add(undated.ToString(CultureInfo.InvariantCulture) +
                " undated legacy observations are included only for period all; a finite window cannot infer their date.");
        }
        return new()
        {
            Cases = cases,
            Dispatches = dispatches,
            Calls = calls,
            Gates = gates,
            Humans = humans,
            Turns = turns,
            Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    internal SquadGovernanceMetrics Metrics()
    {
        var knownCost = Calls.Sum(call => call.Charge?.CostUsd ?? 0m);
        var unpriced = Calls.Count(call => call.Charge?.CostUsd is null);
        var unobserved = Dispatches.Count(dispatch => Cases.Single(item => item.Name == dispatch.Squad).State.ModelCalls
            .All(call => call.DispatchId != dispatch.Evidence.DispatchId));
        var unknownTokens = Calls.Count(call => call.Observation.InputTokens is null || call.Observation.OutputTokens is null);
        var knownTokens = Calls.Sum(call => checked((call.Observation.InputTokens ?? 0) + (call.Observation.OutputTokens ?? 0)));
        var escalations = Gates.Count(gate => Verdict(gate.Record) is "Stop" or "Not-Ready") +
            Humans.Count(human => !Passed(human.Gate.Status));
        var calibrated = Calls.Count(call => call.Charge?.Pricing?.Calibration.Observations > 0);
        return new(Turns.Count, Dispatches.Count, unpriced == 0 && unobserved == 0 ? ConsumptionMath.Round(knownCost) : null,
            ConsumptionMath.Round(knownCost), unknownTokens == 0 ? knownTokens : null, knownTokens,
            Humans.Count, Humans.Count(human => Passed(human.Gate.Status)), escalations,
            Calls.Count, unpriced, unobserved, unknownTokens, calibrated, Calls.Count - calibrated);
    }

    internal (SquadWatchTerminalReceipt Receipt, DateTimeOffset RecordedAt)[] TerminalObservations(DateTimeOffset? since, DateTimeOffset now)
    {
        var receipts = Cases.SelectMany(item => item.State.Runs.Select(run => run.WatchTerminal)
            .Append(item.State.ActiveRun?.WatchTerminal)
            .Concat((item.State.WatchRuns ?? []).Select(watch => watch.Result?.Terminal)))
            .OfType<SquadWatchTerminalReceipt>().GroupBy(receipt => receipt.OperationId, StringComparer.Ordinal);
        var effects = Cases.SelectMany(item => item.State.NativeOperations)
            .Where(operation => operation.ExecutedEffect is not null)
            .ToDictionary(operation => operation.ExecutedEffect!.OperationId, StringComparer.Ordinal);
        return receipts.Select(group => group.First()).Where(receipt =>
            receipt.Status == SquadToolOutputStatus.Succeeded && receipt.ReceiptKind == SquadToolReceiptKind.HostObservation &&
            effects.TryGetValue(receipt.OperationId, out var effect) &&
            effect.ExecutedEffect!.Role == WatchContracts.TerminalRole &&
            effect.ExecutedEffect.Receipt.Name == WatchContracts.TerminalTool &&
            effect.RecordedAt <= now && (since is null || effect.RecordedAt >= since))
            .Select(receipt => (receipt, effects[receipt.OperationId].RecordedAt)).ToArray();
    }

    internal static string? Verdict(SquadGateRecord gate) =>
        gate.Payload.TryGetProperty("Verdict", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool Passed(string status) => status is "approved" or "approve" or "answer";
    internal static string Label(string? squad) => squad is null ? "" : "[" + squad + "] ";

    private static bool Includes(DateTimeOffset? at, DateTimeOffset? since, DateTimeOffset now, ref int undated)
    {
        if (at is null)
        {
            undated++;
            return since is null;
        }
        return at <= now && (since is null || at >= since);
    }

    private static bool VerifyOutputs(ReportCase item, SquadDispatchEvidence evidence)
    {
        try
        {
            item.Scribe.VerifyOutputs(evidence, item.State);
            return true;
        }
        catch (Exception exception) when (exception is RuntimeHistoryIntegrityException or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
