using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    /// <summary>Confirms one host billing observation and updates calibration exactly once for that run.</summary>
    /// <param name="observation">Actual host billing delta and its non-secret evidence reference.</param>
    /// <param name="cancellationToken">Cancellation while awaiting host confirmation.</param>
    /// <returns>Calibration outcome; unknown or unfinished runs cannot be reconciled.</returns>
    public async Task<SquadConsumptionReconciliationResult> ReconcileConsumptionAsync(
        SquadConsumptionReconciliation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.EvidenceReference);
        if (observation.ObservedCostUsd < 0 || observation.EvidenceReference.Length > 300 ||
            observation.EvidenceReference.Any(char.IsControl))
        {
            throw new ArgumentException("Billing reconciliation requires nonnegative USD and a bounded non-secret evidence reference.");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = _scribe.Load() ?? throw new InvalidOperationException("Initialize and complete a recorded run first.");
            ValidateState(state);
            _ = _scribe.PrepareEvidence(state);
            var calibration = state.Consumption?.Calibration;
            if (state.ConsumptionReconciliations?.SingleOrDefault(prior => prior.RunId == observation.RunId) is { } prior)
            {
                if (prior != observation)
                {
                    throw new InvalidOperationException("This run was already reconciled against different billing evidence.");
                }
                return new(SquadRunStatus.Completed, calibration?.Factor ?? 1, calibration?.Observations ?? 0,
                    "The identical recorded observation was already reconciled; no second calibration was applied.");
            }
            if (state.Checkpoint is not null || state.ActiveRun is not null ||
                !state.Runs.Any(run => run.RunId == observation.RunId) || state.Consumption is null ||
                !ConsumptionMath.CurrentRates(state.Consumption, DateOnly.FromDateTime(_options.TimeProvider.GetUtcNow().UtcDateTime)))
            {
                return new(SquadRunStatus.Blocked, calibration?.Factor ?? 1, calibration?.Observations ?? 0,
                    "Complete the recorded run and supply current exact rates before calibration.");
            }
            var charges = state.ModelCallCharges?.Where(charge => charge.RunId == observation.RunId).ToArray() ?? [];
            if (charges.Length == 0 || charges.Any(charge => charge.UncalibratedCostUsd is null ||
                charge.TariffSha256 != ConsumptionMath.TariffHash(state.Consumption)) ||
                charges.Sum(charge => charge.UncalibratedCostUsd ?? 0m) <= 0)
            {
                return new(SquadRunStatus.Blocked, calibration?.Factor ?? 1, calibration?.Observations ?? 0,
                    "Missing, unpriced, zero-denominator or different-tariff work cannot establish calibration; no approval was requested.");
            }
            var request = new SquadApprovalRequest(SquadApprovalKind.ConsumptionReconciliation,
                "Confirm the host-reported billing observation; calibrate once against uncalibrated recorded cost. " +
                "This does not authorize publishing private usage or changing historical model identities.",
                ArgumentsJson: JsonSerializer.Serialize(observation));
            if (_options.ApproveAsync is null)
            {
                return new(SquadRunStatus.ApprovalRequired, calibration?.Factor ?? 1, calibration?.Observations ?? 0, request.Description);
            }
            if (!await _options.ApproveAsync(request, cancellationToken).ConfigureAwait(false))
            {
                return new(SquadRunStatus.ApprovalDenied, calibration?.Factor ?? 1, calibration?.Observations ?? 0,
                    "Host denied consumption reconciliation; no calibration changed.");
            }
            return _scribe.ReconcileConsumption(state, observation, DateOnly.FromDateTime(_options.TimeProvider.GetUtcNow().UtcDateTime));
        }
        finally
        {
            _runLock.Release();
        }
    }

    private sealed partial class RunContext
    {
        private readonly SemaphoreSlim _costGate = new(1, 1);
        private readonly Dictionary<string, (RuntimeModelRoute Route, decimal MaxContext)> _costStarted = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeCostCalls = new(StringComparer.Ordinal);
        private RuntimeModelRoute? _coordinatorCostRoute;
        private decimal _coordinatorCostContext;
        private DateOnly Today => DateOnly.FromDateTime(runtime._options.TimeProvider.GetUtcNow().UtcDateTime);

        private static string UnitSlot(string stage, string role) => stage + ":" + role;

        private async ValueTask BeforeCostCallAsync(SquadRosterEntry? role, AgentCharter charter, string stage,
            RuntimeModelRoute route, DispatchCheckpoint? dispatch, long contextTokens)
        {
            if (dispatch is null)
            {
                _coordinatorCostRoute = route;
                _coordinatorCostContext = Math.Max(_coordinatorCostContext, contextTokens);
                if (runtime._options.FederationInvocation is { AdmitAsync: not null } federation)
                {
                    await AdmitFederationDemandAsync(federation, [ConsumptionMath.Demand("orchestration:coordinator",
                        "coordinator", "orchestration", charter.Name, PricingModels(route, charter),
                        state.Consumption, Today, minimumContext: contextTokens)], true).ConfigureAwait(false);
                }
                return;
            }
            if (_scribeHandoff is not null)
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
            }
            await _costGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_scribeHandoff is not null)
                {
                    await DrainPipeliningAsync().ConfigureAwait(false);
                }
                if (_costStarted.TryGetValue(dispatch.Id, out var prior) &&
                    prior.Route.ParameterModel == route.ParameterModel && ReferenceEquals(prior.Route.Client, route.Client) &&
                    (checkpoint.Cost?.CeilingUsd is null || contextTokens <= prior.MaxContext))
                {
                    return;
                }
                var slot = CostSlot(dispatch);
                if (runtime._options.FederationInvocation is { AdmitAsync: not null } federation)
                {
                    var demand = await BuildCostDemandAsync(role!, charter, stage, route, dispatch, contextTokens).ConfigureAwait(false);
                    await AdmitFederationDemandAsync(federation, demand, false).ConfigureAwait(false);
                }
                var admission = await AdmitCostAsync(role!, charter, stage, route, dispatch, [slot], contextTokens).ConfigureAwait(false);
                if (!admission.PermittedSlots.Contains(slot, StringComparer.Ordinal))
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "This cost round does not permit the requested exact dispatch slot.");
                }
                using (runtime._scribe.SyncRoot.EnterScope())
                {
                    checkpoint.Cost!.DispatchSlots[dispatch.Id] = slot;
                    var row = admission.Demand.SingleOrDefault(row => row.Slot == slot);
                    _costStarted[dispatch.Id] = (route, row is null ? decimal.MaxValue :
                        row.BaseContext + row.GrowthPerTurn * (row.InternalTurns - 1));
                    runtime._scribe.Save(state);
                }
            }
            finally
            {
                _costGate.Release();
            }
        }

        private string CostSlot(DispatchCheckpoint dispatch)
        {
            if (dispatch.ParentDispatchId is null)
            {
                return dispatch.UnitSlot ?? UnitSlot(dispatch.Stage, dispatch.Role);
            }
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                if (checkpoint.Cost!.DispatchSlots.TryGetValue(dispatch.Id, out var existing))
                {
                    return existing;
                }
                var used = checkpoint.Cost.DispatchSlots.Values.ToHashSet(StringComparer.Ordinal);
                return Enumerable.Range(1, runtime._options.MaxDispatches).Select(index => "worker:" +
                    index.ToString(CultureInfo.InvariantCulture)).FirstOrDefault(slot => !used.Contains(slot))
                    ?? throw _guard.Stop(SquadRunStatus.LimitReached, "No bounded worker cost slot remains.");
            }
        }

        private async ValueTask AdmitFederationDemandAsync(FederationInvocation federation,
            IReadOnlyList<SquadCostDemandRow> rows, bool coordinator)
        {
            try
            {
                await federation.AdmitAsync!(rows, coordinator, token).ConfigureAwait(false);
            }
            catch (RuntimeStopException exception)
            {
                throw _guard.Stop(exception.Status, exception.Message, exception.FailureClass);
            }
        }

        private async Task<SquadCostPreflight> AdmitCostAsync(SquadRosterEntry role, AgentCharter charter,
            string stage, RuntimeModelRoute route, DispatchCheckpoint? dispatch, IReadOnlyList<string> next, long minimumContext = 0)
        {
            _guard.Check(token);
            var cost = checkpoint.Cost ?? throw new InvalidDataException("Missing run-bound cost checkpoint.");
            if (cost.Approval is not null)
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
            }
            var demand = cost.CeilingUsd is null ? [] :
                await BuildCostDemandAsync(role, charter, stage, route, dispatch, minimumContext).ConfigureAwait(false);
            SquadCostPreflight round;
            long revision;
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var spend = CostSpend();
                var pricing = ConsumptionMath.PricingHash(state.Consumption);
                revision = cost.Revision;
                round = ConsumptionMath.Evaluate(runId, Guid.NewGuid().ToString("N"), cost.CeilingUsd,
                    spend.Spend, demand, next, pricing, spend.Hash,
                    complete: checkpoint.Routing is not null && runtime._scribe.VerifyRates(state),
                    calibrationEligible: ConsumptionMath.CurrentRates(state.Consumption, Today) &&
                        ConsumptionMath.EligibleCalibration(state.Consumption, Today),
                    spendKnown: spend.Known, pending: spend.Pending, approval: cost.Approval);
                if (cost.CeilingUsd is null)
                {
                    round = round with { Demand = [], PermittedSlots = next, Diagnostic = null };
                }
                if (round.Decision == "approved-over-ceiling" && OtherRootInFlight(dispatch))
                {
                    round = round with
                    {
                        Decision = "over-ceiling",
                        PermittedSlots = [],
                        Diagnostic = "An approved overshoot permits sequential units only; finish active unit handoffs before continuation."
                    };
                }
                runtime._scribe.CommitCostRound(state, revision, round);
            }
            if (round.Decision is "over-ceiling" or "cannot-confirm" && _pipelineSource is not null)
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
                return await AdmitCostAsync(role, charter, stage, route, dispatch, next, minimumContext).ConfigureAwait(false);
            }
            if (round.Decision == "cannot-confirm" || round.Decision == "over-ceiling" && round.Diagnostic is not null)
            {
                throw _guard.Stop(SquadRunStatus.InputRequired, round.Diagnostic!);
            }
            if (round.Decision != "over-ceiling")
            {
                return round;
            }
            if (OtherRootInFlight(dispatch))
            {
                throw _guard.Stop(SquadRunStatus.InputRequired,
                    "Cost admission needs sequential overshoot. No additional unit started; finish active handoffs before resuming.");
            }
            if (runtime._options.ApproveAsync is null && runtime._options.HumanApprovalChannel is null)
            {
                throw _guard.Stop(SquadRunStatus.ApprovalRequired,
                    "Cost overshoot needs explicit host approval of the recorded unchanged-demand sequential unit.");
            }
            var approved = await RequestApprovalAsync(new(SquadApprovalKind.CostOvershoot,
                "Stop or approve sequential unchanged-demand units under the same ceiling. " +
                "The conservative remaining reservation exceeds available USD; cannot-confirm and terminal at-ceiling states are never approvable.",
                role.Role, Evidence: Evidence, CostPreflight: round with
                {
                    Demand = round.Demand.Select(row => row with { PricingModels = row.PricingModels.ToArray() }).ToArray(),
                    PermittedSlots = round.PermittedSlots.ToArray(),
                }, DispatchId: dispatch?.Id, ActionId: "cost:" + HumanActions.Hash(next)), next, token).ConfigureAwait(false);
            _guard.Check(token);
            if (!approved)
            {
                throw _guard.Stop(SquadRunStatus.ApprovalDenied, "Host denied the recorded cost overshoot; no unit started.");
            }
            var reference = "cost-approval-" + Guid.NewGuid().ToString("N");
            var approval = new CostApproval(runId, cost.CeilingUsd!.Value, round.PricingSha256, round.Demand, round.RoundId, reference);
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var spend = CostSpend();
                var confirmed = ConsumptionMath.Evaluate(runId, Guid.NewGuid().ToString("N"), cost.CeilingUsd,
                    spend.Spend, demand, next, ConsumptionMath.PricingHash(state.Consumption), spend.Hash,
                    true, ConsumptionMath.EligibleCalibration(state.Consumption, Today), spend.Known, spend.Pending, approval);
                if (cost.Revision != revision + 1 || confirmed.Decision != "approved-over-ceiling" ||
                    confirmed.SpendSha256 != round.SpendSha256)
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired,
                        "Spend, pricing or demand changed while cost approval was pending; the stale response authorized no slot.");
                }
                runtime._scribe.CommitCostRound(state, cost.Revision, confirmed, approval);
                runtime._scribe.Notification(state, "CostOvershoot: approved via trusted host callback; reference " + reference + ".");
                return confirmed;
            }
        }

        private bool OtherRootInFlight(DispatchCheckpoint? dispatch)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var root = dispatch;
                while (root?.ParentDispatchId is { } parent)
                {
                    root = checkpoint.DispatchStack.SingleOrDefault(item => item.Id == parent);
                }
                return checkpoint.DispatchStack.Any(item => item.ParentDispatchId is null && item.Id != root?.Id &&
                    _costStarted.ContainsKey(item.Id) && _runningDispatches.Contains(item.Id) &&
                    !checkpoint.PendingEvidence.Any(evidence => evidence.DispatchId == item.Id));
            }
        }

        private (decimal Spend, decimal Pending, bool Known, string Hash) CostSpend()
        {
            if (state.FederationRun is not null)
            {
                return FederationExecution.Spend(runtime._files, state);
            }
            var pending = checkpoint.PendingEvidence.Select(evidence => evidence.DispatchId).ToHashSet(StringComparer.Ordinal);
            var charges = (state.ModelCallCharges ?? []).Where(charge => charge.RunId == runId &&
                (charge.DispatchId is null || !pending.Contains(charge.DispatchId)) &&
                (state.ModelCalls.Single(call => call.Id == charge.CallId).Status != "started" ||
                 !_activeCostCalls.Contains(charge.CallId))).ToArray();
            var reservations = pending.Select(id =>
            {
                if (!checkpoint.Cost!.DispatchSlots.TryGetValue(id, out var slot))
                {
                    return (Id: id, Cost: (decimal?)null);
                }
                var row = checkpoint.Cost.Rounds.LastOrDefault(round => round.PermittedSlots.Contains(slot, StringComparer.Ordinal))
                    ?.Demand.SingleOrDefault(row => row.Slot == slot);
                return (Id: id, Cost: row?.ProjectedCostUsd * ConsumptionMath.ReserveMultiplier);
            }).ToArray();
            return (charges.Sum(charge => charge.CostUsd ?? 0m), reservations.Sum(reservation => reservation.Cost ?? 0m),
                ConsumptionMath.UnobservedDispatches(runId, state) == 0 &&
                    charges.All(charge => charge.CostUsd is not null) && reservations.All(reservation => reservation.Cost is not null),
                ProjectFiles.Hash(JsonSerializer.Serialize(new { Charges = charges, Reservations = reservations })));
        }

        private async Task<SquadCostDemandRow[]> BuildCostDemandAsync(SquadRosterEntry currentRole,
            AgentCharter currentCharter, string currentStage, RuntimeModelRoute currentRoute, DispatchCheckpoint? dispatch,
            long minimumContext)
        {
            var routing = checkpoint.Routing ?? throw _guard.Stop(SquadRunStatus.InputRequired,
                "A complete confirmed mode/owner route is required before cost admission.");
            HashSet<string> completed;
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                completed = checkpoint.Evidence.Concat(checkpoint.PendingEvidence).Where(evidence => evidence.Depth == 1)
                    .Select(evidence => evidence.UnitSlot ?? UnitSlot(evidence.Stage, evidence.Role)).ToHashSet(StringComparer.Ordinal);
            }
            var units = new List<(string Slot, string Stage, string Role)>();
            var plan = Evidence.Concat(checkpoint.PendingEvidence).LastOrDefault(evidence => evidence.Stage == "plan" && evidence.Depth == 1);
            void Add(string stage, string role, string? slot = null)
            {
                slot ??= UnitSlot(stage, role);
                if (!completed.Contains(slot) && !units.Any(unit => unit.Slot == slot))
                {
                    units.Add((slot, stage, role));
                }
            }
            if (runtime._options.WatchInvocation is not null && checkpoint.Readiness?.IntakeComplete != true &&
                Readiness.Inputs.Count > 0)
            {
                Add("intake", "intake-validator");
                var remediator = RemediationRole();
                for (var cycle = Math.Max(1, Readiness.IntakeCycles); cycle <= 2; cycle++)
                {
                    Add("remediation", remediator, $"intake-remediation:{cycle}:{remediator}");
                    Add("validation", "intake-validator", $"intake-validation:{cycle}:intake-validator");
                }
            }
            if (checkpoint.FederationRepair is { } repair)
            {
                Add(repair.Stage, repair.Role);
            }
            else if (routing.Scope == SquadRunScope.Review)
            {
                Add("review", "tester", ClosingReviewSlot());
            }
            else
            {
                foreach (var role in DiscoveryRoles(checkpoint.Readiness?.DiscoveryDepth ?? "skip"))
                {
                    if (checkpoint.Readiness?.DiscoveryRecordId is null)
                    {
                        Add("discovery", role);
                    }
                }
                if ((checkpoint.Readiness?.Inputs.Count ?? routing.InputPaths.Count) > 0 ||
                    checkpoint.Readiness?.DiscoveryDepth is "quick" or "standard" or "deep")
                {
                    if (routing.Scope is SquadRunScope.Delivery or SquadRunScope.Plan &&
                        checkpoint.Readiness?.IntakeComplete != true)
                    {
                        Add("intake", "intake-validator");
                        var remediator = checkpoint.Readiness?.Inputs.Count > 0 ? RemediationRole() : "analyst";
                        for (var cycle = Math.Max(1, checkpoint.Readiness?.IntakeCycles ?? 0); cycle <= 2; cycle++)
                        {
                            Add("remediation", remediator, $"intake-remediation:{cycle}:{remediator}");
                            Add("validation", "intake-validator", $"intake-validation:{cycle}:intake-validator");
                        }
                        if (Readiness.Inputs.Any(input => Regex.IsMatch(runtime._files.Read(input.Path),
                            @"\b(user story|backlog item|work item)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromSeconds(1))) || checkpoint.Readiness?.DiscoveryDepth is "quick" or "standard" or "deep")
                        {
                            var alternateRemediator = remediator == "analyst" ? "product-owner" : "analyst";
                            for (var cycle = Math.Max(1, checkpoint.Readiness?.IntakeCycles ?? 0); cycle <= 2; cycle++)
                            {
                                Add("remediation", alternateRemediator, $"intake-remediation:{cycle}:{alternateRemediator}");
                            }
                        }
                    }
                }
                if (routing.Scope != SquadRunScope.Plan || routing.InputPaths.Count == 0 || runtime._options.WatchInvocation is not null)
                {
                    Add("research", "researcher");
                }
                if (routing.Scope != SquadRunScope.Research)
                {
                    Add("plan", "lead");
                    var lenses = CouncilLenses(request + "\n" + (plan is null ? "" : runtime._files.Read(plan.ArtifactPath)),
                        routing.ProducingRole, routing.IncludesRai, checkpoint.Production?.Plan.Units.Select(unit => unit.Role).ToArray(),
                        IsAutomaticWorkflow);
                    if (routing.RequiresCouncil || runtime._options.Mode == "autonomous" ||
                        IsAutomaticWorkflow && (checkpoint.Production is null || checkpoint.Production.Plan.Units.Count == 1) ||
                        lenses.Count(lens => lens.Needed) >= 2 ||
                        lenses.Any(lens => lens.Role == "rai" && lens.Needed) || runtime._options.Council is not null)
                    {
                        var members = checkpoint.Readiness?.CouncilConfirmed == true
                            ? checkpoint.Readiness.CouncilProposal!.Roles :
                            ReadinessContracts.CouncilRoles.Where(role =>
                                plan is null ? runtime.Roster.Resolve(role) is not null : lenses.Any(lens => lens.Needed && lens.Role == role))
                                .Concat(runtime._options.Council?.Roles ?? []).Distinct(StringComparer.Ordinal).ToArray();
                        if (members.Count == 0 && checkpoint.Readiness?.CouncilConfirmed != true)
                        {
                            members = ReadinessContracts.CouncilRoles;
                        }
                        foreach (var member in members)
                        {
                            Add("council", member);
                            if (UsesAutonomousValidation && checkpoint.Readiness?.CouncilProposal?.Roles.Count != 0 &&
                                checkpoint.Mode?.ValidationComplete != true)
                            {
                                for (var cycle = 1; cycle <= 2; cycle++)
                                {
                                    Add("council", member, ValidationSlot(cycle, member));
                                }
                            }
                        }
                    }
                    if (routing.Scope == SquadRunScope.Delivery)
                    {
                        if (checkpoint.Production is { } production)
                        {
                            foreach (var unit in production.Plan.Units)
                            {
                                Add("produce", unit.Role, ProducingSlot(unit));
                                if (UsesAutonomousValidation && checkpoint.Mode?.ValidationComplete != true)
                                {
                                    Add("produce", unit.Role, ProducingSlot(unit, 2));
                                }
                            }
                        }
                        else
                        {
                            var owners = completed.Contains(UnitSlot("plan", "lead")) ? [routing.ProducingRole] :
                                runtime.Roster.Members.Where(member => member.DeliverableRoot is not null &&
                                    member.Role is not ("researcher" or "lead" or "tester") && !RoleOutputContract.ReturnsOnly(member.Role))
                                    .Select(member => member.Role).Append(routing.ProducingRole).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (var owner in owners)
                            {
                                Add("produce", owner);
                                if (UsesAutonomousValidation && checkpoint.Mode?.ValidationComplete != true)
                                {
                                    Add("produce", owner, "autonomy:2:" + UnitSlot("produce", owner));
                                }
                            }
                        }
                        Add("review", "tester");
                    }
                }
            }
            var rows = new List<SquadCostDemandRow>();
            var workers = new List<SquadCostDemandRow>();
            if (runtime._options.Mode == "autopilot")
            {
                rows.Add(ConsumptionMath.Demand("final-validation", "final-validation", "scribe", "Squad Scribe",
                    [], state.Consumption, Today, native: true));
            }
            if (UsesAutonomousValidation && checkpoint.Mode?.CycleLimitAcknowledged != true)
            {
                rows.Add(ConsumptionMath.Demand("autonomous-outcome", "autonomous-outcome", "scribe", "Squad Scribe",
                    [], state.Consumption, Today, native: true));
            }
            if (checkpoint.Production is { } plannedProduction)
            {
                foreach (var unit in plannedProduction.Plan.Units)
                {
                    for (var iteration = ProducingIteration; iteration <= (UsesAutonomousValidation &&
                        checkpoint.Mode?.ValidationComplete != true ? 2 : ProducingIteration); iteration++)
                    {
                        foreach (var dependency in unit.Dependencies.Where(dependency =>
                            !(state.ProductionHandoffs ?? []).Any(handoff => handoff.RunId == runId &&
                                handoff.PlanDispatchId == plannedProduction.Plan.PlanDispatchId &&
                                (handoff.Iteration == 0 ? 1 : handoff.Iteration) == iteration &&
                                handoff.Producer == dependency && handoff.Consumer == unit.Id)))
                        {
                            var slot = iteration == 1 ? $"handoff:{dependency}:{unit.Id}" : $"handoff:{iteration}:{dependency}:{unit.Id}";
                            rows.Add(ConsumptionMath.Demand(slot, "scribe", "scribe", "Squad Scribe",
                                [], state.Consumption, Today, native: true));
                        }
                    }
                }
            }
            foreach (var unit in units)
            {
                var role = ResolveRole(unit.Role);
                var producingUnit = unit.Stage == "produce" ? checkpoint.Production?.Plan.Units.SingleOrDefault(candidate =>
                    ProductionPlans.Slot(checkpoint.Production.Plan, candidate) == unit.Slot ||
                        ProducingSlot(candidate, 2) == unit.Slot) : null;
                var charter = producingUnit?.Agent is { } agent
                    ? runtime.Artifacts.FindCharter(agent) ?? throw _guard.Stop(SquadRunStatus.Blocked,
                        $"Planned producing owner '{agent}' is no longer installed.")
                    : unit.Slot.StartsWith("autonomy-validation:", StringComparison.Ordinal) &&
                        Gate(Readiness.CouncilRecordId) is { } councilRecord &&
                        ReadinessContracts.Parse<SquadCouncilVerdict>(councilRecord.Payload).Findings
                            .SingleOrDefault(item => item.Role == unit.Role) is { } councilSeat ?
                        runtime.Artifacts.FindCharter(councilSeat.Agent) ??
                            throw _guard.Stop(SquadRunStatus.Blocked, "The original priced council agent is unavailable.")
                    : unit.Stage is "intake" or "validation" && checkpoint.Readiness?.Inputs.Count > 0
                    ? IntakeValidator().Charter : runtime.SelectAgent(role, request,
                        unit.Stage == "discovery" ? DiscoveryTask(unit.Role) :
                        unit.Stage == "council" && plan is not null ? CouncilTask(plan) :
                        "Remaining " + unit.Stage + " work.").Charter;
                var currentSlot = dispatch?.UnitSlot ?? UnitSlot(currentStage, currentRole.Role);
                var route = dispatch?.ParentDispatchId is null && unit.Slot == currentSlot
                    ? currentRoute : await ResolveModelAsync(role, charter, unit.Stage,
                        (long)(DispatchSize.ForStage(unit.Stage)?.Context ?? 0)).ConfigureAwait(false);
                if (unit.Slot == currentSlot && dispatch?.ParentDispatchId is null)
                {
                    charter = currentCharter;
                }
                rows.Add(ConsumptionMath.Demand(unit.Slot, unit.Stage, role.Role, charter.Name,
                    PricingModels(route, charter), state.Consumption, Today,
                    minimumContext: unit.Slot == currentSlot &&
                        dispatch?.ParentDispatchId is null ? minimumContext : 0));
                rows.Add(ConsumptionMath.Demand("scribe:" + unit.Slot, "scribe", "scribe", "Squad Scribe",
                    [], state.Consumption, Today, native: true));
                await ReserveWorkersAsync(role, charter, unit.Stage, workers, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1)
                    .ConfigureAwait(false);
            }
            if (workers.Count > 0)
            {
                var workerModels = workers.SelectMany(row => row.PricingModels).Distinct(StringComparer.Ordinal).ToArray();
                var worst = workers.OrderByDescending(row => row.ProjectedCostUsd ?? decimal.MaxValue).First();
                var used = checkpoint.Cost!.DispatchSlots.Where(pair =>
                    checkpoint.Evidence.Concat(checkpoint.PendingEvidence).Any(evidence => evidence.DispatchId == pair.Key))
                    .Select(pair => pair.Value).ToHashSet(StringComparer.Ordinal);
                for (var index = 1; index <= runtime._options.MaxDispatches; index++)
                {
                    var slot = "worker:" + index.ToString(CultureInfo.InvariantCulture);
                    if (!used.Contains(slot))
                    {
                        rows.Add(worst with
                        {
                            Slot = slot,
                            Role = "declared-worker",
                            Agent = "bounded installed child allowlist",
                            PricingModels = workerModels
                        });
                    }
                }
            }
            if (dispatch?.ParentDispatchId is not null)
            {
                var slot = CostSlot(dispatch);
                var index = rows.FindIndex(row => row.Slot == slot);
                var actual = ConsumptionMath.Demand(slot, currentStage, currentRole.Role, currentCharter.Name,
                    PricingModels(currentRoute, currentCharter), state.Consumption, Today, minimumContext: minimumContext);
                if (index < 0)
                {
                    rows.Add(actual);
                }
                else if (!rows[index].PricingModels.Contains(currentRoute.ParameterModel ?? "", StringComparer.Ordinal) ||
                    actual.ProjectedCostUsd > rows[index].ProjectedCostUsd)
                {
                    rows[index] = actual;
                }
            }
            var coordinator = _coordinatorCostRoute ?? await ResolveModelAsync(null, runtime._coordinator,
                "coordinator", 20_000).ConfigureAwait(false);
            rows.Add(ConsumptionMath.Demand("orchestration:coordinator", "coordinator", "orchestration",
                runtime._coordinator.Name, PricingModels(coordinator, runtime._coordinator), state.Consumption, Today,
                minimumContext: _coordinatorCostContext));
            return rows.ToArray();
        }

        private async Task ReserveWorkersAsync(SquadRosterEntry owner, AgentCharter parent, string stage,
            List<SquadCostDemandRow> rows, HashSet<string> visited, int depth)
        {
            if (depth >= runtime._options.MaxDepth || !visited.Add(parent.Name))
            {
                return;
            }
            foreach (var name in parent.DelegateAgents)
            {
                var child = runtime.Artifacts.FindCharter(name);
                if (child is null || child.DisableModelInvocation ||
                    runtime.Catalog.Roles.Any(role => role.Name != owner.Role &&
                        (role.PrimaryAgent == name || role.AlternateAgents.Contains(name, StringComparer.OrdinalIgnoreCase))))
                {
                    continue;
                }
                var route = await ResolveModelAsync(owner, child, stage,
                    (long)(DispatchSize.ForStage(stage)?.Context ?? 0)).ConfigureAwait(false);
                rows.Add(ConsumptionMath.Demand("declared:" + child.Name, stage, owner.Role, child.Name,
                    PricingModels(route, child), state.Consumption, Today));
                await ReserveWorkersAsync(owner, child, stage, rows, visited, depth + 1).ConfigureAwait(false);
            }
        }

        private static string[] PricingModels(RuntimeModelRoute route, AgentCharter charter) =>
            (route.PolicyApplied ? new[] { route.ParameterModel ?? "unknown" } :
                new[] { route.ParameterModel ?? "unknown" }.Concat(charter.ModelPreferences))
                .Distinct(StringComparer.Ordinal).ToArray();

        private void ValidateCostModelIdentity(RuntimeModelRoute route, string? observed)
        {
            if (checkpoint.Cost is { CeilingUsd: not null } cost && ConsumptionMath.KnownModel(observed) &&
                observed != route.ParameterModel)
            {
                const string diagnostic = "Cost identity changed: actual provider model differs from the admitted effective model. " +
                    "Its real attempt is recorded; no returned tool was executed. Resolve the concrete host binding and reprice through a new gate.";
                using (runtime._scribe.SyncRoot.EnterScope())
                {
                    var spend = CostSpend();
                    var round = ConsumptionMath.Evaluate(runId, Guid.NewGuid().ToString("N"), cost.CeilingUsd,
                        spend.Spend, cost.Rounds.LastOrDefault()?.Demand ?? [], [],
                        ConsumptionMath.PricingHash(state.Consumption), spend.Hash, false, false,
                        spend.Known, spend.Pending) with
                    { Diagnostic = diagnostic };
                    runtime._scribe.CommitCostRound(state, cost.Revision, round);
                }
                throw _guard.Stop(SquadRunStatus.InputRequired,
                    diagnostic);
            }
        }
    }
}
