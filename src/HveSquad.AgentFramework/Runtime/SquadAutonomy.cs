using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>One actual independent revalidation, including its immutable council decision.</summary>
/// <param name="Cycle">One or two; never a third cycle.</param>
/// <param name="Verdict">The most restrictive independent verdict.</param>
/// <param name="DecisionRef">Exact Scribe-written decision reference.</param>
/// <param name="DispatchIds">Actual independent seat dispatches, not model claims.</param>
public sealed record SquadAutonomousIteration(int Cycle, string Verdict, string DecisionRef,
    IReadOnlyList<string> DispatchIds);

/// <summary>Observable mode execution; completion is separate from human validation and release.</summary>
/// <param name="Mode">The trusted host's interactive, autonomous or autopilot selection.</param>
/// <param name="Outcome">Running, escalated, awaiting validation, or completed.</param>
/// <param name="Iterations">Only actually recorded autonomous revalidations.</param>
/// <param name="Converged">Whether independent validation cleared the implementation conditions.</param>
/// <param name="FinalValidated">Whether the exact final outcome was accepted by the human.</param>
/// <param name="PipelinedTransitions">Verified single-writer handoffs overlapped with a real role dispatch.</param>
public sealed record SquadModeResult(string Mode, string Outcome,
    IReadOnlyList<SquadAutonomousIteration> Iterations, bool Converged, bool FinalValidated,
    int PipelinedTransitions);

internal sealed class ModeCheckpoint
{
    public required string Mode { get; init; }
    public int ProducingIteration { get; set; } = 1;
    public List<string> ValidationRecords { get; set; } = [];
    public bool ValidationComplete { get; set; }
    public bool CycleLimitAcknowledged { get; set; }
    public bool FinalValidated { get; set; }
    public bool HumanChangesPending { get; set; }
    public string? ProducingChangeActionId { get; set; }
    public bool ChangeBlockedByCap { get; set; }
    public int PipelinedTransitions { get; set; }
    public string Outcome { get; set; } = "running";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadApprovalRequest? RiskApproval { get; set; }
}

internal sealed record AutonomousValidationOutcome(int Cycle, SquadCouncilVerdict Verdict,
    IReadOnlyList<string> ProducingDispatchIds, bool Converged, string? Escalation);

internal sealed record ModeHistoryOutcome(SquadModeResult Result, IReadOnlyList<SquadDispatchEvidence> Stages);

public sealed partial class SquadRuntime
{
    private sealed partial class RunContext
    {
        private SquadDispatchEvidence? _pipelineSource;
        private (string Role, string Stage, string Task)? _pipelineTarget;
        private Task? _scribeHandoff;

        private bool IsAutomaticWorkflow => runtime._options.Mode != "interactive" && Scope == SquadRunScope.Delivery;
        private bool UsesAutonomousValidation => IsAutomaticWorkflow &&
            (runtime._options.Mode == "autonomous" || checkpoint.Production is null || checkpoint.Production.Plan.Units.Count == 1);
        private int ProducingIteration => checkpoint.Mode?.ProducingIteration ?? 1;

        internal SquadModeResult? ModeResult()
        {
            if (checkpoint.Mode is not { } mode)
            {
                return null;
            }
            var iterations = mode.ValidationRecords.Select(id =>
            {
                var record = Gate(id) ?? throw new RuntimeHistoryIntegrityException("history-integrity: a mode validation receipt is missing.");
                var result = ReadinessContracts.Parse<AutonomousValidationOutcome>(record.Payload);
                return new SquadAutonomousIteration(result.Cycle, result.Verdict.Verdict,
                    result.Verdict.DecisionRef, result.Verdict.Findings.Select(item => item.DispatchId).ToArray());
            }).ToArray();
            return new(mode.Mode, mode.Outcome, iterations, mode.ValidationComplete, mode.FinalValidated,
                mode.PipelinedTransitions);
        }

        private void PrepareExecutionMode()
        {
            if (!IsAutomaticWorkflow)
            {
                return;
            }
            var first = checkpoint.Mode is null;
            checkpoint.Mode ??= new() { Mode = runtime._options.Mode };
            if (checkpoint.Mode.Mode != runtime._options.Mode || checkpoint.Mode.ProducingIteration is < 1 or > 2 ||
                checkpoint.Mode.ValidationRecords.Count > 2)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: the exact mode or bounded validation state changed.");
            }
            if (first)
            {
                RecordModeOutcome(checkpoint.Mode.Outcome);
            }
        }

        internal void RecordModeOutcome(string outcome)
        {
            if (checkpoint.Mode is not { } mode)
            {
                return;
            }
            VerifyEvidence();
            mode.Outcome = outcome;
            var result = ModeResult()!;
            var payload = new ModeHistoryOutcome(result, Evidence.Where(item => item.Depth == 1).ToArray());
            var id = ReadinessRunKey + "-mode-" + HumanActions.Hash(payload)[..16];
            var record = runtime._scribe.RecordGate(state, id, "Mode Outcome", Readiness.TopicId,
                Readiness.ScopeSha256, payload, "* Opt-In: mode=" + mode.Mode +
                "\n* Outcome: " + outcome + "\n* Revalidations: " + result.Iterations.Count.ToString(CultureInfo.InvariantCulture));
            runtime._scribe.RecordModeHistory(state, record, mode.Mode);
            runtime._scribe.Save(state);
        }

        private bool IsAutomaticSafeTool(SquadToolRegistration tool) =>
            IsAutomaticWorkflow && tool.Action == SquadToolAction.General &&
            tool.Impact == SquadToolImpact.Safe && tool.Effect is SquadToolEffect.ProjectWrite or SquadToolEffect.ExternalRead;

        private string ProducingSlot(SquadProducingUnit unit, int? iteration = null)
        {
            var slot = ProductionPlans.Slot(checkpoint.Production!.Plan, unit);
            return (iteration ?? ProducingIteration) == 1 ? slot : "autonomy:2:" + slot;
        }

        private string? ClosingReviewSlot() => checkpoint.Mode is not null && checkpoint.HumanRevision > 0 ?
            "review:human-revision:" + checkpoint.HumanRevision.ToString(CultureInfo.InvariantCulture) + ":tester" : null;

        private bool TryArchiveProducingRevision(string previousId, DispatchCheckpoint dispatch, string path)
        {
            if (ProducingIteration != 2 || checkpoint.Mode is not { } mode ||
                mode.ValidationRecords.Count != 1 && mode.ProducingChangeActionId is null)
            {
                return false;
            }
            var source = Evidence.SingleOrDefault(item => item.DispatchId == previousId);
            if (source is null)
            {
                return false;
            }
            var previous = new DispatchCheckpoint(source.DispatchId, source.Role, source.Agent, source.Stage, source.Depth, "",
                source.ParentDispatchId)
            { UnitSlot = source.UnitSlot };
            var previousUnit = ProductionUnit(previous);
            var currentUnit = ProductionUnit(dispatch);
            if (previousUnit is null || currentUnit?.Id != previousUnit.Id)
            {
                return false;
            }
            bool IterationMatches(string id, string slot)
            {
                var pending = checkpoint.DispatchStack.SingleOrDefault(item => item.Id == id);
                var recorded = pending is null ? Evidence.Single(item => item.DispatchId == id) : null;
                var parent = pending?.ParentDispatchId ?? recorded?.ParentDispatchId;
                if (parent is not null)
                {
                    return IterationMatches(parent, slot);
                }
                return (pending?.UnitSlot ?? recorded?.UnitSlot ?? UnitSlot("produce", pending?.Role ?? recorded!.Role)) == slot;
            }
            if (!IterationMatches(previousId, ProducingSlot(previousUnit, 1)) ||
                !IterationMatches(dispatch.Id, ProducingSlot(currentUnit, 2)))
            {
                return false;
            }
            var output = (source.ExecutedHostTools ?? []).Where(tool => tool.Status == SquadToolOutputStatus.Succeeded)
                .SelectMany(tool => tool.Outputs ?? []).LastOrDefault(output =>
                    runtime._files.Resolve(output.Path).Equals(runtime._files.Resolve(path), ProjectFiles.PathComparison));
            if (output is null)
            {
                return false;
            }
            runtime._scribe.ArchiveOutputRevision(state, previousId, dispatch.Id, output);
            return true;
        }

        private SquadDispatchEvidence[] ProducingOutputs(int iteration) =>
            checkpoint.Production!.Plan.Units.Select(unit => Evidence.Single(item =>
                item.Stage == "produce" && item.Depth == 1 &&
                (item.UnitSlot ?? UnitSlot(item.Stage, item.Role)) == ProducingSlot(unit, iteration))).ToArray();

        private async Task<SquadDispatchEvidence[]> RunAutonomousValidationAsync(SquadDispatchEvidence plan,
            SquadCouncilVerdict? council, SquadDispatchEvidence[] outputs)
        {
            if (!UsesAutonomousValidation || council is null || council.Verdict == "Waived")
            {
                if (runtime._options.Mode == "autonomous")
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired,
                        "The autonomous council was absent or waived; its validator loop ended without claiming convergence.");
                }
                return outputs;
            }
            var mode = checkpoint.Mode!;
            if (mode.ValidationComplete && !mode.HumanChangesPending)
            {
                return outputs;
            }
            outputs = ProducingOutputs(1);
            var prior = council;
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var id = ReadinessRunKey + "-autonomous-validation-" + cycle.ToString(CultureInfo.InvariantCulture);
                var savedId = mode.ValidationRecords.ElementAtOrDefault(cycle - 1);
                var saved = savedId is null ? state.GateRecords?.SingleOrDefault(record => record.Id == id + "-outcome") : Gate(savedId);
                AutonomousValidationOutcome outcome;
                if (saved is not null)
                {
                    outcome = ReadinessContracts.Parse<AutonomousValidationOutcome>(saved.Payload);
                    if (outcome.Cycle != cycle ||
                        !outcome.ProducingDispatchIds.SequenceEqual(outputs.Select(item => item.DispatchId), StringComparer.Ordinal))
                    {
                        throw new RuntimeHistoryIntegrityException("history-integrity: autonomous validation changed its cycle or producing inputs.");
                    }
                }
                else
                {
                    await DrainPipeliningAsync().ConfigureAwait(false);
                    VerifyEvidence();
                    var seats = council.Findings.Select(seat =>
                    {
                        var entry = ResolveRole(seat.Role);
                        var charter = runtime.Artifacts.FindCharter(seat.Agent)
                            ?? throw _guard.Stop(SquadRunStatus.Blocked, "The original independent council agent is unavailable.");
                        if (outputs.SelectMany(ProductionTree).Any(output => output.Agent == charter.Name))
                        {
                            throw _guard.Stop(SquadRunStatus.Blocked, "An autonomous validator cannot validate its own producing output.");
                        }
                        return new DispatchWork(entry, charter,
                            "Independent autonomous revalidation cycle " + cycle.ToString(CultureInfo.InvariantCulture) +
                            ". Reuse your exact council lens; inspect actual produced files and successful host receipts. " +
                            "Return every outstanding stable issue/condition. ResolvedConditions must name your prior condition " +
                            "and cite exact produced evidence with a description of the check. Never infer success from a draft's claim. " +
                            "Set RequiresCostConfirmation for a confirm-tier cost move and ComplianceViolation for an actual violation.\n" +
                            JsonSerializer.Serialize(new { Plan = plan, Prior = prior, Outputs = outputs }),
                            seat.AgentSelectionRationale, ValidationSlot(cycle, entry.Role));
                    }).ToArray();
                    var findings = await DispatchBatchAsync("council", seats).ConfigureAwait(false);
                    VerifyEvidence();
                    ValidateResolutions(findings, prior, outputs);
                    var verdict = SynthesizeCouncil(findings);
                    var proposal = council.Proposal with
                    {
                        ScopeSha256 = HumanActions.Hash(new { council.Proposal.ScopeSha256, cycle, Outputs = outputs }),
                    };
                    var result = new SquadCouncilVerdict(verdict, "", proposal, findings,
                        findings.SelectMany(item => ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload)
                            .Conditions.Select(condition => new SquadCouncilCondition(item.Role, condition))).ToArray());
                    var escalation = MandatoryCouncilRisk(result);
                    if (cycle == 2 && Diverged(prior, result))
                    {
                        escalation = "Consecutive validators changed their judgment on the same stable issue.";
                    }
                    var converged = escalation is null && result.Verdict is "Go" or "Go-With-Conditions" &&
                        result.Findings.All(item =>
                        {
                            var returned = ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload);
                            return returned.Conditions.All(condition => returned.ResolvedConditions?
                                .Any(resolution => resolution.ConditionId == condition.Id) == true) &&
                                returned.Issues.Count == 0 && (item.Verdict != "Concern" || returned.FollowUps.Count == 0);
                        });
                    var decision = runtime._scribe.RecordGate(state, id, "Autonomous Revalidation", Readiness.TopicId,
                        proposal.ScopeSha256, result, RuntimeScribe.RenderCouncil(proposal, plan, findings, verdict));
                    result = result with { DecisionRef = decision.DecisionRef };
                    outcome = new(cycle, result, outputs.Select(item => item.DispatchId).ToArray(), converged, escalation);
                    var outcomeRecord = runtime._scribe.RecordGate(state, id + "-outcome", "Autonomous Validation Outcome",
                        Readiness.TopicId, proposal.ScopeSha256, outcome,
                        "* Cycle: " + cycle.ToString(CultureInfo.InvariantCulture) + "\n* Verdict: " + verdict +
                        "\n* Converged: " + converged.ToString(CultureInfo.InvariantCulture));
                    saved = outcomeRecord;
                }
                if (mode.ValidationRecords.Count < cycle)
                {
                    mode.ValidationRecords.Add(saved!.Id);
                    runtime._scribe.Save(state);
                }
                if (outcome.Escalation is { } risk)
                {
                    await EscalateModeRiskAsync(risk, outcome.Verdict.Findings, outcome.Verdict).ConfigureAwait(false);
                }
                if (cycle == 2)
                {
                    RecordModeOutcome("cycle limit: awaiting human validation");
                    if (!mode.CycleLimitAcknowledged)
                    {
                        await ApproveAsync(new(SquadApprovalKind.AutonomousOutcome,
                            "Cycle two is the hard limit, including a converged result. Validate this exact result; " +
                            "approval never authorizes a third cycle or a release action.",
                            CouncilVerdict: outcome.Verdict)).ConfigureAwait(false);
                        mode.CycleLimitAcknowledged = true;
                        runtime._scribe.Save(state);
                    }
                    if (!outcome.Converged)
                    {
                        throw _guard.Stop(SquadRunStatus.Blocked,
                            "Cycle two was surfaced to the human but did not converge. No third validation or completed delivery is claimed.");
                    }
                }
                if (outcome.Converged && !(cycle == 1 && (mode.HumanChangesPending || mode.ProducingIteration == 2)))
                {
                    mode.ValidationComplete = true;
                    mode.HumanChangesPending = false;
                    runtime._scribe.Save(state);
                    return outputs;
                }
                if (cycle == 1)
                {
                    prior = outcome.Verdict;
                    mode.ProducingIteration = 2;
                    runtime._scribe.Save(state);
                    outputs = await RunProductionAsync(plan, prior).ConfigureAwait(false);
                }
            }
            throw new InvalidDataException("The bounded autonomous loop did not reach an explicit outcome.");
        }

        private static string ValidationSlot(int cycle, string role) =>
            "autonomy-validation:" + cycle.ToString(CultureInfo.InvariantCulture) + ":" + role;

        private static string SynthesizeCouncil(IReadOnlyList<SquadDispatchEvidence> findings) =>
            findings.Any(item => item.Verdict == "Block" || item.Risk == "High" || item.BlockingIssues > 0) ? "Stop" :
            findings.Any(item => item.Verdict is "Conditional" or "Concern" ||
                ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Conditions.Count > 0)
                ? "Go-With-Conditions" : "Go";

        private static string? MandatoryCouncilRisk(SquadCouncilVerdict result)
        {
            foreach (var item in result.Findings)
            {
                var findings = ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload);
                if (item.Verdict == "Block" || item.Risk == "High" || item.BlockingIssues > 0 ||
                    findings.Issues.Any(issue => issue.RequiresHuman) ||
                    findings.RequiresCostConfirmation || findings.ComplianceViolation)
                {
                    return "Mandatory council risk from " + item.Role +
                        ": Stop/high risk, human-required fact, confirm-tier cost move or compliance violation.";
                }
            }
            return result.Verdict == "Stop" ? "The council stopped this exact work." : null;
        }

        private static bool Diverged(SquadCouncilVerdict previous, SquadCouncilVerdict current)
        {
            foreach (var seat in current.Findings)
            {
                var before = previous.Findings.Single(item => item.Role == seat.Role);
                var oldIssues = ReadinessContracts.Parse<SquadCouncilFindings>(before.StructuredReturn!.Payload).Issues;
                var issues = ReadinessContracts.Parse<SquadCouncilFindings>(seat.StructuredReturn!.Payload).Issues;
                if (issues.Any(issue => oldIssues.Any(old => old.Id == issue.Id &&
                    (old.Blocking != issue.Blocking || old.RequiresHuman != issue.RequiresHuman ||
                        before.Verdict != seat.Verdict || before.Risk != seat.Risk))))
                {
                    return true;
                }
                var oldConditions = ReadinessContracts.Parse<SquadCouncilFindings>(before.StructuredReturn!.Payload).Conditions;
                var returned = ReadinessContracts.Parse<SquadCouncilFindings>(seat.StructuredReturn!.Payload);
                if (returned.Conditions.Any(condition => oldConditions.Any(old => old.Id == condition.Id) &&
                    returned.ResolvedConditions?.Any(resolution => resolution.ConditionId == condition.Id) != true &&
                    (before.Verdict != seat.Verdict || before.Risk != seat.Risk)))
                {
                    return true;
                }
            }
            return false;
        }

        private void ValidateResolutions(IReadOnlyList<SquadDispatchEvidence> findings, SquadCouncilVerdict prior,
            IReadOnlyList<SquadDispatchEvidence> outputs)
        {
            var sources = outputs.SelectMany(ProductionTree).SelectMany(item =>
                (item.Artifacts ?? []).Concat((item.ExecutedHostTools ?? [])
                    .Where(tool => tool.Status == SquadToolOutputStatus.Succeeded)
                    .SelectMany(tool => tool.Outputs ?? []))).ToArray();
            foreach (var seat in findings)
            {
                var returned = ReadinessContracts.Parse<SquadCouncilFindings>(seat.StructuredReturn!.Payload);
                var conditions = prior.Findings.Where(item => item.Role == seat.Role)
                    .SelectMany(item => ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Conditions).ToArray();
                var resolved = returned.ResolvedConditions ?? [];
                if (resolved.Count > 64 || resolved.Select(item => item.ConditionId).Distinct(StringComparer.Ordinal).Count() != resolved.Count ||
                    resolved.Any(item => !conditions.Any(condition => condition.Id == item.ConditionId) ||
                        returned.Conditions.Any(condition => condition.Id == item.ConditionId &&
                            condition.Description != conditions.Single(priorCondition => priorCondition.Id == item.ConditionId).Description)) ||
                    conditions.Any(condition => !resolved.Any(item => item.ConditionId == condition.Id) &&
                        !returned.Conditions.Any(item => item.Id == condition.Id)))
                {
                    throw new InvalidDataException("Revalidation must retain every unresolved prior condition or independently resolve it against actual output.");
                }
                foreach (var resolution in resolved)
                {
                    ReadinessContracts.Text(resolution.Summary, "condition verification");
                    ReadinessContracts.Sources(resolution.Sources, sources);
                }
            }
        }

        private async Task EscalateModeRiskAsync(string reason, IReadOnlyList<SquadDispatchEvidence> evidence,
            SquadCouncilVerdict? council = null)
        {
            await DrainPipeliningAsync().ConfigureAwait(false);
            var approval = new SquadApprovalRequest(SquadApprovalKind.RiskGate,
                reason + " Human review is mandatory. Acknowledgement does not clear a Stop, authorize another cycle, " +
                "or permit a specific impactful action.", CouncilVerdict: council,
                ActionId: "risk:" + HumanActions.Hash(new { reason, Dispatches = evidence.Select(item => item.DispatchId).ToArray() }));
            if (checkpoint.Mode is { } mode)
            {
                mode.RiskApproval = approval;
                runtime._scribe.Save(state);
            }
            RecordModeOutcome("escalated: " + reason);
            await ApproveAsync(approval).ConfigureAwait(false);
            throw _guard.Stop(SquadRunStatus.Blocked, reason + " The loop stopped for human resolution; no automatic continuation was attempted.");
        }

        private async Task ValidateFinalOutcomeAsync(IReadOnlyList<SquadDispatchEvidence> outputs,
            SquadDispatchEvidence review, SquadCouncilVerdict? council)
        {
            await DrainPipeliningAsync().ConfigureAwait(false);
            VerifyEvidence();
            var mode = checkpoint.Mode!;
            if (runtime._options.WatchInvocation is not null)
            {
                RecordModeOutcome("awaiting provider-attested draft surface; final human review remains outstanding");
                return;
            }
            if (runtime._options.FederationInvocation?.Aggregate == true)
            {
                RecordModeOutcome("awaiting consolidated federation validation");
                return;
            }
            RecordModeOutcome("awaiting final human validation");
            if (!mode.FinalValidated)
            {
                await ApproveAsync(new(SquadApprovalKind.FinalOutcome,
                    "Validate the complete actual output, independent review, exact council decision and open conditions. " +
                    "This final outcome is not deployment, push, merge, or blanket permission for any future effect.\n" +
                    JsonSerializer.Serialize(new { Outputs = outputs, Review = review, Council = council }),
                    CouncilVerdict: council, ProductionPlan: checkpoint.Production!.Plan)).ConfigureAwait(false);
                mode.FinalValidated = true;
                runtime._scribe.Save(state);
            }
        }

        private async Task<SquadDispatchEvidence> DispatchAfterHandoffAsync(SquadDispatchEvidence source,
            string role, string stage, string task, bool deferNext = false)
        {
            task += "\nVerified previous-stage artifact, awaiting its own Scribe handoff:\n" + JsonSerializer.Serialize(source);
            var entry = ResolveRole(role);
            var selected = runtime.SelectAgent(entry, request, task);
            var capable = runtime._options.Tools.Any(tool => tool.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase) &&
                PermissionMatches(selected.Charter, tool.CharterPermission) &&
                tool.Effect != SquadToolEffect.ReadOnly && !IsAutomaticSafeTool(tool));
            if (checkpoint.PipeliningDisabled || runtime._options.MaxConcurrentDispatches == 1 || capable ||
                !checkpoint.PendingEvidence.Any(item => item.DispatchId == source.DispatchId))
            {
                CommitSource(source);
                return await DispatchAgentAsync(entry, selected.Charter, stage, task, 1,
                    agentSelectionRationale: selected.Rationale).ConfigureAwait(false);
            }
            _pipelineSource = source;
            _pipelineTarget = (role, stage, task);
            try
            {
                return await DispatchAgentAsync(entry, selected.Charter, stage, task, 1, deferCommit: true,
                    agentSelectionRationale: selected.Rationale).ConfigureAwait(false);
            }
            finally
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
                if (!deferNext)
                {
                    using (runtime._scribe.SyncRoot.EnterScope())
                    {
                        CommitPending(checkpoint.DispatchStack.Where(dispatch => dispatch.ParentDispatchId is null &&
                            dispatch.Role == role && dispatch.Stage == stage && dispatch.Task == task)
                            .Select(dispatch => dispatch.Id).ToHashSet(StringComparer.Ordinal));
                    }
                }
                _pipelineTarget = null;
            }
        }

        private async Task<SquadDispatchEvidence[]> DispatchBatchAfterHandoffAsync(SquadDispatchEvidence source,
            string stage, IReadOnlyList<DispatchWork> work)
        {
            if (checkpoint.PipeliningDisabled || runtime._options.MaxConcurrentDispatches == 1 ||
                work.Any(item => runtime._options.Tools.Any(tool =>
                    tool.AllowedRoles.Contains(item.Entry.Role, StringComparer.OrdinalIgnoreCase) &&
                    PermissionMatches(item.Charter, tool.CharterPermission) &&
                    tool.Effect != SquadToolEffect.ReadOnly && !IsAutomaticSafeTool(tool))))
            {
                CommitSource(source);
                return await DispatchBatchAsync(stage, work).ConfigureAwait(false);
            }
            _pipelineSource = source;
            _pipelineTarget = (work[0].Entry.Role, stage, work[0].Task);
            try
            {
                return await DispatchBatchAsync(stage, work).ConfigureAwait(false);
            }
            finally
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
                _pipelineTarget = null;
            }
        }

        private async ValueTask StartPipelinedHandoffAsync(DispatchCheckpoint? dispatch)
        {
            if (_pipelineSource is not { } source || _pipelineTarget is not { } target ||
                dispatch is null || dispatch.Role != target.Role || dispatch.Stage != target.Stage || dispatch.Task != target.Task ||
                _scribeHandoff is not null)
            {
                return;
            }
            await _costGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (checkpoint.Cost?.Rounds.LastOrDefault()?.Decision == "approved-over-ceiling")
                {
                    CommitSource(source);
                    _pipelineSource = null;
                    return;
                }
                _scribeHandoff = Task.Run(() =>
                {
                    runtime._scribe.NotifyPipelinedHandoff();
                    CommitSource(source);
                    using (runtime._scribe.SyncRoot.EnterScope())
                    {
                        checkpoint.Mode!.PipelinedTransitions++;
                        runtime._scribe.Save(state);
                    }
                }, CancellationToken.None);
            }
            finally
            {
                _costGate.Release();
            }
        }

        private void CommitSource(SquadDispatchEvidence source)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                CommitPending(new(StringComparer.Ordinal) { source.DispatchId });
                runtime._scribe.Verify(source, state);
            }
        }

        private async Task DrainPipeliningAsync()
        {
            if (_scribeHandoff is { } handoff)
            {
                try
                {
                    await handoff.ConfigureAwait(false);
                }
                catch (RuntimeHistoryIntegrityException)
                {
                    checkpoint.PipeliningDisabled = true;
                    runtime._scribe.Save(state);
                    throw;
                }
                finally
                {
                    _scribeHandoff = null;
                    _pipelineSource = null;
                }
            }
            else if (_pipelineSource is { } source)
            {
                CommitSource(source);
                _pipelineSource = null;
            }
        }
    }
}
