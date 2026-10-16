using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>An explicit producer/consumer artifact requirement, never authority inferred from artifact prose.</summary>
public sealed record SquadFederationDependency(string? Producer, string Consumer, string Role,
    string Stage = "produce", string ArtifactName = "primary", string? ExpectedSha256 = null)
{
    /// <summary>Stable identity used for an exact input override or recorded missing-input diagnostic.</summary>
    public string Id => HumanActions.Hash(new { Producer, Consumer, Role, Stage, ArtifactName, ExpectedSha256 })[..24];
}

/// <summary>A user-proposed existing artifact or a visible gap assumption; the federation plan must confirm it.</summary>
public sealed record SquadFederationInputOverride(string DependencyId, string? Path = null, string? Assumption = null);

/// <summary>Trusted per-turn selection. Mode, capabilities and mandatory floors remain the host's options.</summary>
public sealed class SquadFederationRunConfiguration
{
    /// <summary>A single explicit target bypasses meta-routing and keeps ordinary inner autopilot/final validation.</summary>
    public string? Target { get; set; }
    /// <summary>Explicit independent outcomes, or empty for most-specific meta-routing and human ambiguity resolution.</summary>
    public IList<string> Members { get; set; } = [];
    /// <summary>Optional member-specific descriptive work; it cannot set runtime controls or permissions.</summary>
    public IDictionary<string, string> Requests { get; set; } = new Dictionary<string, string>();
    /// <summary>Complete declared dependency edges. Producers are added before their consumers, never run in the same wave.</summary>
    public IList<SquadFederationDependency> Dependencies { get; set; } = [];
    /// <summary>Exact user input/Ready-With-Gaps proposals, confirmed at the meta-plan gate.</summary>
    public IList<SquadFederationInputOverride> InputOverrides { get; set; } = [];
    /// <summary>USD ceiling or unset; omission inherits only inside the same suspended meta-run.</summary>
    public string? CostCeiling { get; set; }
}

/// <summary>Read-only routing observations. Conflicting disjoint outcomes are not ranked by keyword length.</summary>
public sealed record SquadFederationRoutingProposal(IReadOnlyList<string> Members,
    IReadOnlyList<string> MatchedPatterns, bool NeedsHumanChoice, string Diagnostic);

/// <summary>One confirmed dependency-ordered member invocation; reuse and narrow repair are explicit.</summary>
public sealed record SquadFederationPlannedMember(string Name, string InnerRunId, string Request,
    IReadOnlyList<string> DependsOn, bool ParallelEligible, string Work,
    string? RepairRole = null, string? RepairStage = null);

/// <summary>Complete local execution script. It does not replace any member's plan or grant tool authority.</summary>
public sealed record SquadFederationExecutionPlan(string RunId, string Request, string Mode,
    bool AggregateAutopilot, string? Target, string Discovery,
    IReadOnlyList<SquadFederationPlannedMember> Members, IReadOnlyList<SquadFederationDependency> Dependencies,
    IReadOnlyList<SquadFederationInputOverride> InputOverrides);

/// <summary>Coordinator-enumerated actual artifact bytes and their original producer dispatch.</summary>
/// <param name="ProducerRole">Original verified owner, never inferred from artifact prose.</param>
/// <param name="ProducerStage">Original verified stage; a council opinion is not a finalized producing handoff.</param>
public sealed record SquadFederationHandoffArtifact(string Name, string Path, string Sha256, long Bytes,
    string? Producer, string? DispatchId, string? DeliverableRoot,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ProducerRole = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ProducerStage = null);

/// <summary>An explicitly accepted non-blocking gap, attributed to the actual confirming host/human decision.</summary>
public sealed record SquadFederationGapAssumption(string DependencyId, string Consumer, string Description,
    string ChosenBy, string ApprovalReference);

/// <summary>Immutable read-only input transfer; state, history and pending private payloads are not deliverables.</summary>
public sealed record SquadFederationHandoff(string Id, string RunId, string? Producer, string Consumer,
    IReadOnlyList<SquadFederationHandoffArtifact> Artifacts, IReadOnlyList<SquadFederationGapAssumption> Assumptions,
    string ApprovalReference, DateTimeOffset RecordedAt);

/// <summary>Exact inner owner of a lifted gate. Final acceptance still never authorizes release.</summary>
public sealed record SquadFederationGateContext(string RunId, string Member, string InnerRunId);
/// <summary>An explicit owning-member change selection; it requires an authenticated consolidated changes receipt.</summary>
public sealed record SquadFederationRevision(string Member, string Stage = "produce");

/// <summary>Actual inner execution or explicitly reused prior artifact; summaries are not proof of dispatch.</summary>
public sealed record SquadFederationMemberOutcome(string Member, string InnerRunId, bool Reused,
    SquadRunResult? Result);

/// <summary>Durable meta-run outcome. Completed is separate from consolidated human acceptance and deployment.</summary>
public sealed record SquadFederationRunResult(string RunId, SquadRunStatus Status, string Diagnostic,
    SquadFederationExecutionPlan? Plan, IReadOnlyList<SquadFederationMemberOutcome> Members,
    IReadOnlyList<SquadFederationHandoff> Handoffs, SquadFederationCost? Cost,
    bool FinalValidated = false, SquadCostPreflight? CostPreflight = null,
    IReadOnlyList<SquadPendingHumanAction>? PendingHumanActions = null);

public sealed partial class SquadFederation
{
    private static readonly string[] RequiredInnerStages = ["research", "plan", "produce", "review"];
    private static readonly Action<ILogger, Exception?> LogExecutionBlocked = LoggerMessage.Define(
        LogLevel.Warning, new EventId(8, "FederationExecutionBlocked"),
        "Federation execution stopped at an evidence or human barrier. Exact inner work is retained; no dependency was re-derived.");
    private readonly SemaphoreSlim _metaGate = new(1, 1);

    /// <summary>Reads current verified meta-routing without making model calls or changing case state.</summary>
    /// <param name="request">Original human request, not ingested artifact instructions.</param>
    /// <param name="target">Optional exact single-member selector.</param>
    /// <returns>Literal most-specific outcomes, or a clear human-choice diagnostic.</returns>
    public SquadFederationRoutingProposal GetRoutingProposal(string request, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var state = LoadFederation();
        return RoutingProposal(state, request, target);
    }

    /// <summary>Runs actual dependency-ordered inner engines, with explicit verified read-only inputs.</summary>
    /// <param name="request">Original work request. It cannot grant mode, model, tool or release permissions.</param>
    /// <param name="configuration">Optional trusted selection, dependency and input proposals.</param>
    /// <param name="cancellationToken">Cancellation of the bounded meta-run and active member work.</param>
    /// <returns>The actual persisted outcome, including any exact pending gate or required recovery.</returns>
    public Task<SquadFederationRunResult> RunAsync(string request,
        SquadFederationRunConfiguration? configuration = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return ExecuteFederationAsync(request, null, configuration, cancellationToken);
    }

    /// <summary>Continues the same release/host/plan-bound meta-run, never restarts completed member effects.</summary>
    /// <param name="runId">Exact suspended meta-run identity.</param>
    /// <param name="configuration">Only an unconfirmed plan may change selection; a ceiling may be explicitly replaced or unset.</param>
    /// <param name="cancellationToken">Cancellation of remaining execution.</param>
    /// <returns>Actual same-run continuation with all completed evidence and spend preserved.</returns>
    public Task<SquadFederationRunResult> ResumeAsync(string runId,
        SquadFederationRunConfiguration? configuration = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
        {
            throw new ArgumentException("A meta-run ID must be an exact native GUID.", nameof(runId));
        }
        return ExecuteFederationAsync(null, runId, configuration, cancellationToken);
    }

    /// <summary>Confirms the named member/stage for an authenticated final change, then resumes that exact inner run.</summary>
    /// <param name="runId">Exact suspended meta-run whose human requested changes.</param>
    /// <param name="revision">The real owning member and produce/review stage, never a cross-root edit.</param>
    /// <param name="cancellationToken">Cancellation of confirmation and same-run continuation.</param>
    /// <returns>The bounded revised outcome; a used validator cap cannot be reset.</returns>
    public async Task<SquadFederationRunResult> ReviseMemberAsync(string runId, SquadFederationRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        FederationPersistence.ValidateName(revision.Member);
        if (revision.Stage is not ("produce" or "review"))
        {
            throw new ArgumentException("Select the owning produce or review stage; effects and validator caps cannot be discarded.", nameof(revision));
        }
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadFederation();
            var meta = state.FederationRun;
            if (meta?.RunId != runId || meta.Plan?.AggregateAutopilot != true)
            {
                throw new InvalidDataException("Only this exact suspended aggregate autopilot outcome may be revised.");
            }
            var step = meta.Steps.SingleOrDefault(step => step.Member == revision.Member && !step.Reuse && step.Repair is null)
                ?? throw new InvalidDataException("Choose an actual owning inner pipeline, not another member's artifact.");
            var origin = (state.HumanDecisions ?? []).LastOrDefault(receipt => receipt.Binding.RunId == runId &&
                receipt.Decision == SquadHumanDecision.Changes && receipt.Binding.Gate == "finaloutcome")
                ?? throw new InvalidDataException("No authenticated final changes receipt authorizes this proposal.");
            var id = runId + "-changes-" + origin.Binding.Id + "-" + revision.Member;
            if (!await _template.RequestFederationApprovalAsync(state, new(SquadApprovalKind.FederationPlan,
                "Confirm the owning member/stage for the recorded authenticated changes. Producer edits return to their producer; " +
                "no member can change another's artifacts and no third validation is permitted.\n" + HumanActions.Serialize(revision),
                ActionId: id), cancellationToken).ConfigureAwait(false))
            {
                return Result(state, SquadRunStatus.ApprovalDenied, "The scoped revision was declined; completed work remains unchanged.");
            }
            var record = new FederationHumanRevision(runId, revision.Member, step.InnerRunId, revision.Stage, origin, id);
            if (meta.Revisions.All(prior => prior.ApprovalRecordId != id))
            {
                meta.Revisions.Add(record);
                meta.Revision++;
                meta.FinalValidated = false;
                step.Result = null;
                step.OutcomeRecordId = null;
                state.Checkpoint!.Cost!.Approval = null;
                _scribe.RecordFederationTransition(state, id, "Federation Human Changes", record);
            }
        }
        catch (RuntimeStopException exception)
        {
            var state = LoadFederation();
            return Result(state, exception.Status, exception.Message);
        }
        finally
        {
            _lifecycleLock.Release();
        }
        return await ResumeAsync(runId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads notification-safe hub challenges; private payloads require the separate host-only API.</summary>
    /// <param name="cancellationToken">Cancellation of the bounded state read.</param>
    /// <returns>Current federation-level attributed pending actions.</returns>
    public async Task<IReadOnlyList<SquadPendingHumanAction>> GetPendingHumanActionsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = LoadFederation();
            var actions = (await _template.GetPendingHumanActionsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (state.FederationRun is { Plan: not null } meta)
            {
                foreach (var step in meta.Steps.Where(step => step.Started && !step.Reuse))
                {
                    var (_, _, member) = FederationExecution.ReadMember(_files, step.Member, ArtifactFingerprint);
                    if (member.Checkpoint?.RunId != step.InnerRunId)
                    {
                        continue;
                    }
                    using var runtime = await ExecutionMemberAsync(meta, step, cancellationToken).ConfigureAwait(false);
                    actions.AddRange(await runtime.GetPendingHumanActionsAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            foreach (var watch in state.WatchRuns ?? [])
            {
                var (_, _, member) = FederationExecution.ReadMember(_files, watch.Trigger.Member, ArtifactFingerprint);
                if (member.Checkpoint?.RunId != watch.Trigger.RunId)
                {
                    continue;
                }
                using var runtime = await CreateWatchRuntimeAsync(watch, _registeredWatch ??
                    throw new InvalidDataException("Register the original trusted Watch policy before reading its pending human gates."),
                    cancellationToken).ConfigureAwait(false);
                actions.AddRange(await runtime.GetPendingHumanActionsAsync(cancellationToken).ConfigureAwait(false));
            }
            return actions.ToArray();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Reads one exact protected federation gate for an authorized host UI, never an agent tool.</summary>
    /// <param name="actionId">Exact pending action ID.</param>
    /// <param name="cancellationToken">Cancellation of the bounded state read.</param>
    /// <returns>The verified original payload and safe metadata.</returns>
    public Task<SquadHumanActionDetails> ReadHumanActionAsync(string actionId, CancellationToken cancellationToken = default) =>
        WithHumanOwnerAsync(null, actionId, runtime => runtime.ReadHumanActionAsync(actionId, cancellationToken), cancellationToken);

    /// <summary>Authenticates a hub response without running models, member tools or an implicit resume.</summary>
    /// <param name="response">Untrusted exact response; the registered channel authenticates the actual actor.</param>
    /// <param name="cancellationToken">Cancellation of authentication and persistence.</param>
    /// <returns>Explicit accepted/rejected submission evidence.</returns>
    public Task<SquadHumanSubmission> SubmitHumanResponseAsync(SquadHumanResponse response, CancellationToken cancellationToken = default) =>
        WithHumanOwnerAsync(response.Binding.RunId, null,
            runtime => runtime.SubmitHumanResponseAsync(response, cancellationToken), cancellationToken);

    /// <summary>Polls the registered channel once for this meta-run's attributed actions.</summary>
    /// <param name="runId">Exact suspended meta-run.</param>
    /// <param name="cancellationToken">Cancellation of provider reads.</param>
    /// <returns>Actual accepted/rejected/missing response diagnostics.</returns>
    public async Task<SquadHumanPoll> PollHumanResponsesAsync(string runId, CancellationToken cancellationToken = default)
    {
        var submissions = new List<SquadHumanSubmission>();
        var diagnostics = new List<string>();
        var pending = await GetPendingHumanActionsAsync(cancellationToken).ConfigureAwait(false);
        var owners = pending.Where(action => action.Status == "pending").Select(action => action.Binding.RunId)
            .Append(runId).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var owner in owners)
        {
            var poll = await WithHumanOwnerAsync(owner, null,
                runtime => runtime.PollHumanResponsesAsync(owner, cancellationToken), cancellationToken).ConfigureAwait(false);
            submissions.AddRange(poll.Submissions);
            diagnostics.AddRange(poll.Diagnostics);
        }
        return new(submissions, diagnostics);
    }

    /// <summary>Explicitly renews an expired unchanged hub gate; expiry never becomes consent.</summary>
    /// <param name="actionId">Current pending/expired action identity.</param>
    /// <param name="cancellationToken">Cancellation of verification/publication.</param>
    /// <returns>Current exact notification-safe challenge.</returns>
    public Task<SquadPendingHumanAction> RenewHumanActionAsync(string actionId, CancellationToken cancellationToken = default) =>
        WithHumanOwnerAsync(null, actionId, runtime => runtime.RenewHumanActionAsync(actionId, cancellationToken), cancellationToken);

    /// <summary>Recovers one exact interrupted hub append/receipt; altered original bytes remain blocked.</summary>
    /// <param name="cancellationToken">Cancellation before root-exclusive reconciliation.</param>
    /// <returns>Actual integrity/repair evidence without model or project effects.</returns>
    public Task<SquadEvidenceReconciliation> ReconcileEvidenceAsync(CancellationToken cancellationToken = default) =>
        _template.ReconcileEvidenceAsync(cancellationToken);

    /// <summary>Reconciles only the actual owning member's uncertain effect through its original trusted connector.</summary>
    /// <param name="runId">The exact suspended meta-run.</param>
    /// <param name="member">Registered member owning the interrupted inner run.</param>
    /// <param name="cancellationToken">Cancellation of connector reconciliation.</param>
    /// <returns>The actual inner disposition; a separate meta resume is still required.</returns>
    public async Task<SquadRunResult> ReconcileMemberAsync(string runId, string member, CancellationToken cancellationToken = default)
    {
        var state = LoadFederation();
        var step = state.FederationRun?.RunId == runId
            ? state.FederationRun.Steps.SingleOrDefault(step => step.Member == member)
            : null;
        if (step is null)
        {
            throw new InvalidDataException("No exact meta-run/member owns this reconciliation.");
        }
        return await WithHumanOwnerAsync(step.InnerRunId, null,
            runtime => runtime.ReconcileAsync(step.InnerRunId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles one complete accepted aggregate run against actual host billing without repricing inner history.</summary>
    /// <param name="observation">Nonnegative actual billing and bounded non-secret evidence reference.</param>
    /// <param name="cancellationToken">Cancellation of exact host confirmation.</param>
    /// <returns>One-time root calibration; incomplete, unknown or mixed-tariff work is explicitly blocked.</returns>
    public async Task<SquadConsumptionReconciliationResult> ReconcileConsumptionAsync(
        SquadConsumptionReconciliation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Guid.TryParseExact(observation.RunId, "N", out _) || observation.ObservedCostUsd < 0 ||
            string.IsNullOrWhiteSpace(observation.EvidenceReference) || observation.EvidenceReference.Length > 300 ||
            observation.EvidenceReference.Any(char.IsControl))
        {
            throw new ArgumentException("Aggregate billing needs an exact run ID, nonnegative USD and a bounded non-secret reference.", nameof(observation));
        }
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadFederation();
            var calibration = state.Consumption?.Calibration;
            if ((state.ConsumptionReconciliations ?? []).SingleOrDefault(prior => prior.RunId == observation.RunId) is { } prior)
            {
                if (prior != observation)
                {
                    throw new InvalidDataException("The aggregate run was already reconciled against different billing evidence.");
                }
                return new(SquadRunStatus.Completed, calibration!.Factor, calibration.Observations,
                    "The identical aggregate observation was already recorded; no second calibration was applied.");
            }
            var result = (state.FederationRuns ?? []).SingleOrDefault(result => result.RunId == observation.RunId &&
                result.Status == SquadRunStatus.Completed && result.FinalValidated && result.Plan?.AggregateAutopilot == true);
            if (state.FederationRun is not null || result is null || state.Consumption is null ||
                !ConsumptionMath.CurrentRates(state.Consumption, Today()) || !_scribe.VerifyRates(state))
            {
                return new(SquadRunStatus.Blocked, calibration?.Factor ?? 1m, calibration?.Observations ?? 0,
                    "Only a complete accepted aggregate run with current exact root rates may establish calibration.");
            }
            var charges = new List<ModelCallCharge>();
            foreach (var member in result.Members.Where(member => !member.Reused))
            {
                var (_, _, inner) = FederationExecution.ReadMember(_files, member.Member, ArtifactFingerprint);
                if (inner.Checkpoint?.RunId == member.InnerRunId ||
                    inner.Runs.All(run => run.RunId != member.InnerRunId) ||
                    ConsumptionMath.UnobservedDispatches(member.InnerRunId, inner) > 0)
                {
                    throw new InvalidDataException("An incomplete inner run cannot supply aggregate calibration.");
                }
                var observed = inner.ModelCalls.Where(call => call.RunId == member.InnerRunId).ToArray();
                var priced = (inner.ModelCallCharges ?? []).Where(charge => charge.RunId == member.InnerRunId).ToArray();
                if (observed.Any(call => priced.All(charge => charge.CallId != call.Id)))
                {
                    throw new InvalidDataException("An unpriced provider attempt cannot supply aggregate calibration.");
                }
                charges.AddRange(priced);
            }
            var raw = charges.Sum(charge => charge.UncalibratedCostUsd ?? 0m);
            if (raw <= 0 || charges.Any(charge => charge.UncalibratedCostUsd is null ||
                charge.TariffSha256 != ConsumptionMath.TariffHash(state.Consumption)) ||
                charges.Select(charge => charge.CallId).Distinct(StringComparer.Ordinal).Count() != charges.Count)
            {
                return new(SquadRunStatus.Blocked, calibration?.Factor ?? 1m, calibration?.Observations ?? 0,
                    "Unknown, zero-denominator, duplicated or mixed-tariff work is non-approvable aggregate calibration.");
            }
            var approval = new SquadApprovalRequest(SquadApprovalKind.ConsumptionReconciliation,
                "Confirm the complete host-observed aggregate billing; calibrate once against original uncalibrated attempts. " +
                "No inner charge/history or release permission changes.", ArgumentsJson: HumanActions.Serialize(observation));
            if (_options.ApproveAsync is null)
            {
                return new(SquadRunStatus.ApprovalRequired, calibration?.Factor ?? 1m, calibration?.Observations ?? 0, approval.Description);
            }
            if (!await _options.ApproveAsync(approval, cancellationToken).ConfigureAwait(false))
            {
                return new(SquadRunStatus.ApprovalDenied, calibration?.Factor ?? 1m, calibration?.Observations ?? 0,
                    "The host declined aggregate reconciliation; no calibration changed.");
            }
            return _scribe.ApplyConsumptionReconciliation(state, observation, raw, Today());
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<T> WithHumanOwnerAsync<T>(string? runId, string? actionId,
        Func<SquadRuntime, Task<T>> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = LoadFederation();
            _scribe.VerifyFederationRun(state);
            if (runId is not null && runId == state.Checkpoint?.RunId ||
                actionId is not null && (state.Checkpoint?.HumanActions ?? []).Any(pending => pending.Action.Binding.Id == actionId))
            {
                return await action(_template).ConfigureAwait(false);
            }
            foreach (var watch in state.WatchRuns ?? [])
            {
                var (_, _, member) = FederationExecution.ReadMember(_files, watch.Trigger.Member, ArtifactFingerprint);
                if (member.Checkpoint?.RunId == watch.Trigger.RunId &&
                    (runId == watch.Trigger.RunId || actionId is not null &&
                        (member.Checkpoint.HumanActions ?? []).Any(pending => pending.Action.Binding.Id == actionId)))
                {
                    using var runtime = await CreateWatchRuntimeAsync(watch, _registeredWatch ??
                        throw new InvalidDataException("Register the exact trusted Watch policy before submitting its original human response."),
                        token).ConfigureAwait(false);
                    return await action(runtime).ConfigureAwait(false);
                }
            }
            var meta = state.FederationRun;
            foreach (var step in meta?.Steps.Where(step => step.Started && !step.Reuse) ?? [])
            {
                var (_, _, member) = FederationExecution.ReadMember(_files, step.Member, ArtifactFingerprint);
                if (runId == step.InnerRunId || actionId is not null &&
                    (member.Checkpoint?.HumanActions ?? []).Any(pending => pending.Action.Binding.Id == actionId))
                {
                    using var runtime = await ExecutionMemberAsync(meta!, step, token).ConfigureAwait(false);
                    return await action(runtime).ConfigureAwait(false);
                }
            }
            throw new InvalidDataException("The exact action/run has no current federation owner; no fallback authorized it.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static SquadFederationRoutingProposal RoutingProposal(RuntimeState state, string request, string? target)
    {
        var federation = state.Federation!;
        if (target is not null)
        {
            FederationPersistence.ValidateName(target);
            return federation.Members.Any(member => member.Entry.Name == target)
                ? new([target], [], false, "The explicit target overrides meta-routing; inner autopilot remains forward-only.")
                : new([], [], true, "The explicit target is not registered. Confirm expansion or choose an existing member.");
        }
        var routes = federation.Routes.Where(route => federation.Members.Single(member =>
            member.Entry.Name == route.Member).Entry.Owner != "watch-mode").ToArray();
        var matches = SquadRuntime.RoutingPatterns.Match(request, routes, route => route.Pattern.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var members = matches.Select(match => match.Owner.Member).Distinct(StringComparer.Ordinal).ToArray();
        if (members.Length == 0)
        {
            members = routes.Where(route => route.Pattern == "*").Select(route => route.Member)
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        return new(members, matches.Select(match => match.Pattern).Distinct(StringComparer.Ordinal).ToArray(),
            members.Length != 1, members.Length == 1 ? "Most-specific outcome owner selected." :
            members.Length == 0 ? "No confident pattern matched; choose a member or confirm federation expansion." :
            "Disjoint outcomes conflict. Choose an owner or explicitly select the independent outcomes; no keyword-length guess was made.");
    }

    private async Task<SquadFederationRunResult> ExecuteFederationAsync(string? request, string? resumeId,
        SquadFederationRunConfiguration? configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RunTimeout);
        var token = timeout.Token;
        var started = Stopwatch.GetTimestamp();
        var id = resumeId ?? Guid.NewGuid().ToString("N");
        using var span = _trace?.StartActivity(resumeId is null ? "run.federation" : "resume.federation", ActivityKind.Internal);
        try
        {
            _template.VerifyAcquiredArtifacts();
            var initial = LoadFederation();
            if (initial.FederationRun is not null && resumeId is null)
            {
                return Result(initial, SquadRunStatus.Blocked, "A prior meta-run is active. Resume its exact ID; do not restart its members.");
            }
            using var executionLock = new FileStream(_files.Resolve(ProjectFiles.StateRoot + "/federation.run.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using (var diskLock = _scribe.AcquireLock())
            {
                var state = LoadFederation();
                if (resumeId is not null)
                {
                    if (state.FederationRun?.RunId != resumeId || state.Checkpoint?.HostBinding != _template.HostBinding ||
                        state.Checkpoint.Limits != _template.CreateFederationCheckpoint(id, "").Limits)
                    {
                        throw new InvalidDataException("No exact host/release-bound federation checkpoint owns this continuation.");
                    }
                    _scribe.VerifyFederationRun(state);
                    request = state.Checkpoint.Request;
                    if (configuration is not null)
                    {
                        ValidateRunConfiguration(configuration);
                        if (state.FederationRun.PlanConfirmed &&
                            HumanActions.Hash(ConfigurationIdentity(configuration)) !=
                            HumanActions.Hash(ConfigurationIdentity(state.FederationRun.Configuration)))
                        {
                            throw new InvalidDataException("A confirmed selected set/dependency script cannot change on resume.");
                        }
                        if (!state.FederationRun.PlanConfirmed &&
                            HumanActions.Hash(ConfigurationIdentity(configuration)) !=
                            HumanActions.Hash(ConfigurationIdentity(state.FederationRun.Configuration)))
                        {
                            state.FederationRun.Configuration = CopyConfiguration(configuration);
                            state.FederationRun.Plan = null;
                            state.FederationRun.Steps.Clear();
                        }
                    }
                }
                else
                {
                    configuration = CopyConfiguration(configuration ?? new());
                    ValidateRunConfiguration(configuration);
                    state.Checkpoint = _template.CreateFederationCheckpoint(id, request!);
                    state.FederationRun = new()
                    {
                        RunId = id,
                        Configuration = configuration,
                        RegistrySha256 = state.Federation!.RegistrySha256,
                        RoutingSha256 = state.Federation.RoutingSha256,
                    };
                    state.UserTurns.Add(request!);
                }
                var ceiling = configuration?.CostCeiling ?? (resumeId is null ? _options.CostCeiling : null);
                if (ceiling is not null)
                {
                    state.Checkpoint!.Cost!.CeilingUsd = ConsumptionMath.ParseCeiling(ceiling);
                    state.Checkpoint.Cost.Approval = null;
                    state.Checkpoint.Cost.Revision++;
                }
                _scribe.Save(state);
                _template.VerifyFederationHumanActions(state, token);
                if (state.Checkpoint!.HumanStopRequested)
                {
                    return Finish(state, SquadRunStatus.Stopped, "The authenticated human stopped the meta-run. Completed member work was retained.");
                }
                var remaining = _options.RunTimeout - state.Checkpoint.ActiveExecutionTime;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result(state, SquadRunStatus.LimitReached, "This meta-run exhausted its cumulative active-execution timeout.");
                }
                timeout.CancelAfter(remaining);
            }

            await PrepareFederationPlanAsync(request!, token).ConfigureAwait(false);
            while (true)
            {
                var state = LoadFederation();
                var meta = state.FederationRun!;
                _scribe.VerifyFederationRun(state);
                var remaining = meta.Steps.Where(step => step.Result is null && !step.Reuse).ToArray();
                if (remaining.Length == 0)
                {
                    break;
                }
                var done = meta.Steps.Where(step => step.Reuse || step.Result is { } result && FederationExecution.Successful(result))
                    .Select(step => step.Member).ToHashSet(StringComparer.Ordinal);
                var eligible = meta.Plan!.Members.Where(member => remaining.Any(step => step.Member == member.Name) &&
                    member.DependsOn.All(done.Contains)).ToArray();
                if (eligible.Length == 0)
                {
                    throw new InvalidDataException("A dependency cycle or unresolved producer stops the meta-run; no consumer was dispatched.");
                }
                var wave = eligible[0].ParallelEligible && state.Checkpoint!.Cost!.CeilingUsd is null
                    ? eligible.Where(member => member.ParallelEligible).Take(_options.MaxConcurrentDispatches).ToArray()
                    : [eligible[0]];
                foreach (var member in wave)
                {
                    await PrepareMemberInputsAsync(member.Name, token).ConfigureAwait(false);
                }
                await PrepareAggregateDemandAsync(token).ConfigureAwait(false);
                await AdmitFederationAsync(wave.Select(member => "member:" + member.Name).ToArray(), token).ConfigureAwait(false);
                var results = await Task.WhenAll(wave.Select(member => ExecuteMemberAsync(member.Name, token))).ConfigureAwait(false);
                using (var diskLock = _scribe.AcquireLock())
                {
                    state = LoadFederation();
                    meta = state.FederationRun!;
                    for (var index = 0; index < wave.Length; index++)
                    {
                        var step = meta.Steps.Single(step => step.Member == wave[index].Name);
                        var result = results[index];
                        step.Result = result;
                        if (FederationExecution.Successful(result))
                        {
                            VerifyMemberOutcome(state, step);
                            step.OutcomeRecordId = id + "-outcome-" + step.Member + "-revision-" + meta.Revision.ToString(CultureInfo.InvariantCulture);
                            _scribe.RecordFederationTransition(state, step.OutcomeRecordId, "Federation Member Outcome", result, step.Member);
                        }
                    }
                    state.FederationCurrentRun = FederationExecution.Cost(_files, state);
                    _scribe.Save(state);
                    var failed = results.FirstOrDefault(result => !FederationExecution.Successful(result));
                    if (failed is not null)
                    {
                        foreach (var step in meta.Steps.Where(step => step.Result is { } result && !FederationExecution.Successful(result)))
                        {
                            step.Result = null;
                        }
                        _scribe.Save(state);
                        return Result(state, failed.Status, "The owning member stopped: " + failed.ResponseText);
                    }
                }
            }

            using (var diskLock = _scribe.AcquireLock())
            {
                var state = LoadFederation();
                foreach (var step in state.FederationRun!.Steps.Where(step => !step.Reuse))
                {
                    VerifyMemberOutcome(state, step);
                }
                FederationExecution.VerifyInputs(_files, state.FederationRun.Handoffs.SelectMany(handoff => handoff.Artifacts));
                state.FederationCurrentRun = FederationExecution.Cost(_files, state);
                _scribe.Save(state);
                if (state.FederationRun.Plan!.AggregateAutopilot)
                {
                    var outcome = Result(state, SquadRunStatus.ApprovalRequired,
                        "Validate the actual complete federation outcome and each exact council/review reference. This is not deployment, push, merge or future effect permission.");
                    var approved = await _template.RequestFederationApprovalAsync(state, new(SquadApprovalKind.FinalOutcome,
                        outcome.Diagnostic + "\n" + HumanActions.Serialize(new
                        {
                            outcome.Members,
                            outcome.Handoffs,
                            outcome.Cost,
                            Plan = state.FederationRun.Plan,
                            state.FederationRun.Revision,
                        }),
                        FederationPlan: state.FederationRun.Plan, ActionId: id + ":final:" +
                            state.FederationRun.Revision.ToString(CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
                    if (!approved)
                    {
                        return Finish(state, SquadRunStatus.ApprovalDenied, "The consolidated outcome was declined; no release was authorized.");
                    }
                    state.FederationRun.FinalValidated = true;
                    _scribe.RecordFederationTransition(state, id + "-final", "Federation Final Outcome",
                        new { Validated = true, Outcomes = outcome.Members, Cost = outcome.Cost });
                }
                return Finish(state, SquadRunStatus.Completed,
                    "Actual selected member work and read-only dependency handoffs completed. No deployment or release is claimed.");
            }
        }
        catch (RuntimeStopException exception)
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadFederation();
            return exception.Status is SquadRunStatus.Stopped or SquadRunStatus.ApprovalDenied
                ? Finish(state, exception.Status, exception.Message) : Result(state, exception.Status, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            if (_logger is not null)
            {
                LogExecutionBlocked(_logger, null);
            }
            var state = LoadFederation();
            return Result(state, SquadRunStatus.Blocked, exception.Message);
        }
        finally
        {
            try
            {
                using var diskLock = _scribe.AcquireLock();
                var state = LoadFederation();
                if (state.Checkpoint?.RunId == id)
                {
                    state.Checkpoint.ActiveExecutionTime += Stopwatch.GetElapsedTime(started);
                    _scribe.Save(state);
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
    }

    private async Task PrepareFederationPlanAsync(string request, CancellationToken token)
    {
        using var diskLock = _scribe.AcquireLock();
        var state = LoadFederation();
        var meta = state.FederationRun!;
        if (meta.PlanConfirmed)
        {
            return;
        }
        if (meta.Plan is not null)
        {
            await ConfirmFederationPlanAsync(state, token).ConfigureAwait(false);
            return;
        }
        var configuration = meta.Configuration;
        var route = RoutingProposal(state, request, configuration.Target);
        var selected = configuration.Target is not null ? route.Members.ToArray() :
            configuration.Members.Count > 0 ? configuration.Members.ToArray() : route.Members.ToArray();
        if (configuration.Target is not null && selected.Length == 0)
        {
            throw new RuntimeStopException(SquadRunStatus.InputRequired, route.Diagnostic);
        }
        if (configuration.Target is null && configuration.Members.Count == 0 && route.NeedsHumanChoice)
        {
            var choices = selected.Length > 0 ? selected : state.Federation!.Members
                .Where(member => member.Entry.Owner != "watch-mode").Select(member => member.Entry.Name).ToArray();
            var answer = await _template.AskFederationQuestionAsync(state, new("federation-routing",
                route.Diagnostic + " Choose one exact owner, or explicitly provide a confirmed multi-member selection through the host.",
                choices), token).ConfigureAwait(false);
            if (!choices.Contains(answer, StringComparer.Ordinal))
            {
                throw new RuntimeStopException(SquadRunStatus.InputRequired, "Choose one exact registered owner; no outcome was guessed.");
            }
            selected = [answer];
        }
        if (selected.Length == 0 || selected.Any(name => state.Federation!.Members.All(member => member.Entry.Name != name)))
        {
            throw new RuntimeStopException(SquadRunStatus.InputRequired, "An outcome owner is missing. Confirm federation expansion or choose a registered member.");
        }
        if (configuration.Target is not null && configuration.Dependencies.Count > 0)
        {
            throw new InvalidDataException("An explicit single target is forward-only. Supply read-only inputs directly, or select an untargeted dependency plan.");
        }
        foreach (var dependency in configuration.Dependencies)
        {
            if (!selected.Contains(dependency.Consumer, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Every dependency consumer must be in the selected outcome set.");
            }
            if (dependency.Producer is not null && state.Federation!.Members.All(member => member.Entry.Name != dependency.Producer))
            {
                throw new RuntimeStopException(SquadRunStatus.InputRequired,
                    "Unknown producer. Offer confirmed expansion or an exact user-supplied input; never assign its missing work to the consumer.");
            }
            if (dependency.Producer is null && configuration.InputOverrides.All(item => item.DependencyId != dependency.Id))
            {
                throw new RuntimeStopException(SquadRunStatus.InputRequired,
                    "No sub-squad owns dependency " + dependency.Id + ". Confirm expansion, supply an existing artifact, or explicitly accept a visible Ready-With-Gaps assumption.");
            }
        }
        var members = selected.Concat(configuration.Dependencies.Where(dependency => dependency.Producer is not null)
            .Select(dependency => dependency.Producer!)).Distinct(StringComparer.Ordinal).ToArray();
        var order = DependencyOrder(members, configuration.Dependencies);
        if (meta.Discovery is null)
        {
            var eligible = members.Any(name => state.Federation!.Members.Single(member => member.Entry.Name == name).Entry.Profile is "product" or "full");
            meta.Discovery = !eligible || !_options.HumanPresent ? "skip" : _options.Discovery;
            if (meta.Discovery is null)
            {
                meta.Discovery = await _template.AskFederationQuestionAsync(state, new("federation-discovery",
                    "Choose one discovery depth for every eligible product/full member. Sessions remain member-local.",
                    ["skip", "quick", "standard", "deep"]), token).ConfigureAwait(false);
            }
            if (meta.Discovery is not ("skip" or "quick" or "standard" or "deep"))
            {
                throw new RuntimeStopException(SquadRunStatus.InputRequired, "Choose an exact discovery depth; no repeated member offer was made.");
            }
            _scribe.Save(state);
        }
        var steps = new List<FederationMemberStep>();
        var script = new List<SquadFederationPlannedMember>();
        foreach (var name in order)
        {
            var dependencies = configuration.Dependencies.Where(dependency => dependency.Producer == name).ToArray();
            var step = new FederationMemberStep
            {
                Member = name,
                InnerRunId = Guid.NewGuid().ToString("N"),
                Request = configuration.Requests.TryGetValue(name, out var specific) ? specific : request,
            };
            if (!selected.Contains(name, StringComparer.Ordinal))
            {
                var missing = dependencies.Where(dependency => FederationExecution.FindArtifacts(_files, ArtifactFingerprint, dependency).Length == 0).ToArray();
                step.Reuse = missing.Length == 0;
                if (missing.Length > 0)
                {
                    var (_, _, producer) = FederationExecution.ReadMember(_files, name, ArtifactFingerprint);
                    if (producer.Runs.Any(FederationExecution.Successful))
                    {
                        if (missing.Select(dependency => (dependency.Role, dependency.Stage, dependency.ArtifactName)).Distinct().Count() != 1)
                        {
                            throw new RuntimeStopException(SquadRunStatus.InputRequired, "Several different producer stages need repair; explicitly scope one bounded repair rather than rerun the whole completed squad.");
                        }
                        var dependency = missing[0];
                        step.Repair = new(dependency.Role, dependency.Stage, dependency.ArtifactName,
                            "Dependency " + dependency.Id + " is absent, partial or stale at its current roster root.");
                    }
                }
            }
            steps.Add(step);
            var before = configuration.Dependencies.Where(dependency => dependency.Consumer == name && dependency.Producer is not null)
                .Select(dependency => dependency.Producer!).Distinct(StringComparer.Ordinal).ToArray();
            var parallel = before.Length == 0 && configuration.Dependencies.All(dependency => dependency.Producer != name) &&
                state.Federation!.Routes.Where(route => route.Member == name).All(route => route.ParallelEligible);
            script.Add(new(name, step.InnerRunId, step.Request, before, parallel,
                step.Reuse ? "reuse verified artifact" : step.Repair is null ? "run inner pipeline" : "repair producer stage",
                step.Repair?.Role, step.Repair?.Stage));
        }
        meta.Plan = new(meta.RunId, request, _options.Mode, _options.Mode == "autopilot" && configuration.Target is null,
            configuration.Target, meta.Discovery, script, configuration.Dependencies.ToArray(), configuration.InputOverrides.ToArray());
        meta.Steps = steps;
        _scribe.Save(state);
        await ConfirmFederationPlanAsync(state, token).ConfigureAwait(false);
    }

    private async Task ConfirmFederationPlanAsync(RuntimeState state, CancellationToken token)
    {
        var meta = state.FederationRun!;
        var configuration = meta.Configuration;
        if (!await _template.RequestFederationApprovalAsync(state, new(SquadApprovalKind.FederationPlan,
            "Confirm this dependency-first execution script. Producers complete their artifact gates before consumers; " +
            "only explicitly independent outcomes are parallel-eligible. Missing/partial inputs never become consumer re-derivation.\n" +
            HumanActions.Serialize(meta.Plan), FederationPlan: meta.Plan, ActionId: meta.RunId + ":plan"), token).ConfigureAwait(false))
        {
            throw new RuntimeStopException(SquadRunStatus.ApprovalDenied, "The federation execution script was declined.");
        }
        meta.PlanConfirmed = true;
        meta.Status = "running";
        foreach (var input in configuration.InputOverrides)
        {
            if (input.Assumption is null)
            {
                continue;
            }
            var dependency = configuration.Dependencies.Single(dependency => dependency.Id == input.DependencyId);
            var actor = (state.HumanDecisions ?? []).LastOrDefault(receipt => receipt.Binding.RunId == meta.RunId);
            meta.Assumptions.Add(new(dependency.Id, dependency.Consumer, input.Assumption,
                actor is null ? "confirming trusted host user" : actor.Provider + ":" + actor.SubjectSha256,
                "federation-plan:" + meta.RunId));
        }
        _scribe.RecordFederationTransition(state, meta.RunId + "-plan", "Federation Execution Plan",
            meta.Plan ?? throw new InvalidDataException("The exact confirmed execution script is missing."));
    }

    private async Task PrepareMemberInputsAsync(string name, CancellationToken token)
    {
        using var diskLock = _scribe.AcquireLock();
        var state = LoadFederation();
        var meta = state.FederationRun!;
        var step = meta.Steps.Single(step => step.Member == name);
        foreach (var dependency in meta.Plan!.Dependencies.Where(dependency => dependency.Consumer == name))
        {
            var id = meta.RunId + "-handoff-" + dependency.Id;
            var prior = meta.Handoffs.SingleOrDefault(handoff => handoff.Id == id);
            if (prior is not null)
            {
                FederationExecution.VerifyInputs(_files, prior.Artifacts);
                continue;
            }
            var inputOverride = meta.Plan.InputOverrides.SingleOrDefault(item => item.DependencyId == dependency.Id);
            var artifacts = inputOverride?.Path is { } path
                ? UserArtifact(path) : inputOverride?.Assumption is not null ? [] :
                FederationExecution.FindArtifacts(_files, ArtifactFingerprint, dependency);
            var assumptions = meta.Assumptions.Where(assumption => assumption.DependencyId == dependency.Id).ToArray();
            if (artifacts.Length == 0 && assumptions.Length == 0)
            {
                throw new RuntimeStopException(SquadRunStatus.InputRequired,
                    "The producer's bounded run/stage repair still did not supply dependency " + dependency.Id +
                    ". The consumer was not invoked. Confirm expansion, a user artifact or an explicit gap; no second consecutive producer loop is permitted.");
            }
            var record = new SquadFederationHandoff(id, meta.RunId, dependency.Producer, name, artifacts, assumptions,
                "federation-plan:" + meta.RunId, _options.TimeProvider.GetUtcNow());
            if (_options.Mode == "interactive" &&
                !await _template.RequestFederationApprovalAsync(state, new(SquadApprovalKind.FederationHandoff,
                    "Approve only these actual enumerated/hash-verified producer artifacts as read-only consumer input.\n" +
                    HumanActions.Serialize(record), ActionId: id, FederationPlan: meta.Plan), token).ConfigureAwait(false))
            {
                throw new RuntimeStopException(SquadRunStatus.ApprovalDenied, "The exact read-only artifact handoff was declined.");
            }
            FederationExecution.VerifyInputs(_files, artifacts);
            meta.Handoffs.Add(record);
            step.Inputs.AddRange(artifacts);
            _scribe.RecordFederationTransition(state, id, "Federation Handoff", record);
        }
    }

    private SquadFederationHandoffArtifact[] UserArtifact(string path)
    {
        path = path.Replace('\\', '/');
        if (_files.IsProtectedStatePath(path))
        {
            throw new InvalidDataException("Private state/history/payloads cannot substitute for a dependency deliverable.");
        }
        var bytes = new FileInfo(_files.Resolve(path)).Length;
        return [new("user-input", path, _files.HashFile(path), bytes, null, null, null)];
    }

    private async Task<SquadRunResult> ExecuteMemberAsync(string name, CancellationToken token)
    {
        FederationMemberStep step;
        FederationRunCheckpoint meta;
        await _metaGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadFederation();
            meta = state.FederationRun!;
            step = meta.Steps.Single(step => step.Member == name);
            FederationExecution.VerifyInputs(_files, step.Inputs);
            step.Started = true;
            _scribe.Save(state);
        }
        finally
        {
            _metaGate.Release();
        }
        using var runtime = await ExecutionMemberAsync(meta, step, token).ConfigureAwait(false);
        var (_, _, member) = FederationExecution.ReadMember(_files, name, ArtifactFingerprint);
        var revision = meta.Revisions.LastOrDefault(revision => revision.Member == name && revision.InnerRunId == step.InnerRunId);
        var completed = member.Runs.SingleOrDefault(result => result.RunId == step.InnerRunId) ??
            (member.Checkpoint?.Status == "federation-awaiting-validation" && member.ActiveRun?.RunId == step.InnerRunId
                ? member.ActiveRun : null);
        if (revision is not null && (member.GateRecords ?? []).All(record => record.Id != revision.ApprovalRecordId))
        {
            completed = null;
        }
        if (completed is not null)
        {
            return completed;
        }
        if (member.Checkpoint is not null && member.Checkpoint.RunId != step.InnerRunId)
        {
            throw new InvalidDataException("Another member run owns this root; the meta-run cannot adopt or replay it.");
        }
        var result = member.Checkpoint is null
            ? await runtime.RunAsync(step.Request, token).ConfigureAwait(false)
            : await runtime.ResumeAsync(step.InnerRunId, token).ConfigureAwait(false);
        return result;
    }

    private async Task<SquadRuntime> ExecutionMemberAsync(FederationRunCheckpoint meta, FederationMemberStep step,
        CancellationToken token, bool forecast = false)
    {
        var reader = new RuntimeScribe(_files);
        var state = reader.Load(allowFederation: true) ?? throw new InvalidDataException("The federation state is missing.");
        reader.VerifyFederation(state);
        var member = state.Federation!.Members.Single(member => member.Entry.Name == step.Member);
        var (_, _, current) = FederationExecution.ReadMember(_files, step.Member, ArtifactFingerprint);
        var options = RestoreMemberOptions(current, member);
        options.InputPaths = _options.InputPaths.Concat(step.Inputs.Select(input => input.Path))
            .Distinct(StringComparer.Ordinal).ToArray();
        options.Discovery = member.Entry.Profile is "product" or "full" ? meta.Discovery : "skip";
        if (meta.Plan!.AggregateAutopilot)
        {
            options.CostCeiling = "unset";
            options.Consumption = state.Consumption is null ? null : ConsumptionMath.Copy(state.Consumption);
        }
        else
        {
            options.CostCeiling = meta.Configuration.CostCeiling ?? _options.CostCeiling;
        }
        if (!forecast)
        {
            options.FederationInvocation = new(meta.RunId, step.Member, step.InnerRunId, meta.Plan.AggregateAutopilot,
                step.Inputs.ToArray(), meta.Assumptions.Where(assumption => assumption.Consumer == step.Member).ToArray(),
                step.Repair, (approval, cancellation) => LiftApprovalAsync(step.Member, step.InnerRunId, approval, cancellation),
                meta.Plan.AggregateAutopilot && state.Checkpoint!.Cost!.CeilingUsd is not null
                    ? (rows, coordinator, cancellation) => UpdateAggregateDemandAsync(step.Member, step.InnerRunId, rows, coordinator, cancellation) : null,
                meta.Revisions.LastOrDefault(revision => revision.Member == step.Member && revision.InnerRunId == step.InnerRunId));
        }
        return await SquadRuntime.CreateAsync(_client, options, _source, token).ConfigureAwait(false);
    }

    private async ValueTask<bool> LiftApprovalAsync(string member, string innerRunId, SquadApprovalRequest approval,
        CancellationToken token)
    {
        await _metaGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadFederation();
            var attribution = new SquadFederationGateContext(state.FederationRun!.RunId, member, innerRunId);
            FederationExecution.VerifyGateSource(_files, state, attribution, approval.Evidence ?? []);
            return await _template.RequestFederationApprovalAsync(state, approval with
            {
                Description = "Sub-squad " + member + ": " + approval.Description,
                FederationGate = attribution,
                ActionId = member + ":" + (approval.ActionId ?? HumanActions.Hash(approval)),
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _metaGate.Release();
        }
    }

    private async Task PrepareAggregateDemandAsync(CancellationToken token)
    {
        var state = LoadFederation();
        if (state.FederationRun!.Plan!.AggregateAutopilot != true)
        {
            return;
        }
        using (var diskLock = _scribe.AcquireLock())
        {
            state = LoadFederation();
            _scribe.ConfigureConsumption(state, _options.Consumption, DateOnly.FromDateTime(_options.TimeProvider.GetUtcNow().UtcDateTime));
        }
        var meta = state.FederationRun!;
        if (state.Checkpoint!.Cost!.CeilingUsd is null)
        {
            return;
        }
        var demand = new Dictionary<string, SquadCostDemandRow[]>();
        foreach (var step in meta.Steps.Where(step => step.Result is null && !step.Reuse))
        {
            using var runtime = await ExecutionMemberAsync(meta, step, token, forecast: true).ConfigureAwait(false);
            var rows = await runtime.ForecastFederationDemandAsync(step.Request, step.InnerRunId,
                state.Consumption, step.Repair, token).ConfigureAwait(false);
            demand.Add(step.Member, rows.ToArray());
        }
        using var writer = _scribe.AcquireLock();
        state = LoadFederation();
        foreach (var (name, rows) in demand)
        {
            state.FederationRun!.Steps.Single(step => step.Member == name).Demand = rows.ToList();
        }
        _scribe.Save(state);
    }

    private async ValueTask UpdateAggregateDemandAsync(string name, string innerRunId,
        IReadOnlyList<SquadCostDemandRow> rows, bool coordinator, CancellationToken token)
    {
        await _metaGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using (var diskLock = _scribe.AcquireLock())
            {
                var state = LoadFederation();
                var step = state.FederationRun!.Steps.Single(step => step.Member == name && step.InnerRunId == innerRunId);
                if (coordinator)
                {
                    step.Demand.RemoveAll(row => row.Slot == "orchestration:coordinator");
                    step.Demand.AddRange(rows);
                }
                else
                {
                    step.Demand = rows.ToList();
                }
                _scribe.Save(state);
            }
            await AdmitFederationAsync(["member:" + name], token).ConfigureAwait(false);
        }
        finally
        {
            _metaGate.Release();
        }
    }

    private async Task AdmitFederationAsync(IReadOnlyList<string> next, CancellationToken token)
    {
        using var diskLock = _scribe.AcquireLock();
        var state = LoadFederation();
        var meta = state.FederationRun!;
        if (!meta.Plan!.AggregateAutopilot)
        {
            return;
        }
        var cost = state.Checkpoint!.Cost!;
        var rows = meta.Steps.Where(step => step.Result is null && !step.Reuse).SelectMany(step =>
            step.Demand.Select(row => row with
            {
                Slot = step.Member + ":" + row.Slot,
                ReservationId = step.InnerRunId + ":" + (row.ReservationId ?? row.Slot)
            })
                .Append(ConsumptionMath.Demand("member:" + step.Member, "scribe", "scribe", "Native inner admission",
                    [], state.Consumption, Today(), native: true))).ToList();
        rows.Add(ConsumptionMath.Demand("meta:coordinator", "coordinator", "orchestration", "Native federation routing",
            [], state.Consumption, Today(), native: true));
        rows.Add(ConsumptionMath.Demand("meta:scribe", "scribe", "scribe", "Squad Scribe",
            [], state.Consumption, Today(), native: true));
        rows.Add(ConsumptionMath.Demand("meta:final", "final-validation", "scribe", "Consolidated human outcome",
            [], state.Consumption, Today(), native: true));
        var spend = FederationExecution.Spend(_files, state);
        var pricing = ConsumptionMath.PricingHash(state.Consumption);
        var round = ConsumptionMath.Evaluate(meta.RunId, Guid.NewGuid().ToString("N"), cost.CeilingUsd,
            spend.Spend, rows, next, pricing, spend.Hash,
            complete: _scribe.VerifyRates(state) && meta.PlanConfirmed &&
                meta.Steps.Where(step => !step.Reuse && step.Result is null).All(step => cost.CeilingUsd is null || step.Demand.Count > 0),
            calibrationEligible: ConsumptionMath.CurrentRates(state.Consumption, Today()) &&
                ConsumptionMath.EligibleCalibration(state.Consumption, Today()), spendKnown: spend.Known, approval: cost.Approval);
        if (round.Decision == "over-ceiling" && round.Diagnostic is null &&
            cost.Rounds.LastOrDefault() is { Decision: "over-ceiling" } prior &&
            prior.SpendSha256 == round.SpendSha256 && prior.PricingSha256 == round.PricingSha256 &&
            HumanActions.Hash(prior.Demand) == HumanActions.Hash(round.Demand))
        {
            round = prior;
        }
        else
        {
            _scribe.CommitCostRound(state, cost.Revision, round);
        }
        if (round.Decision == "cannot-confirm" || round.Decision == "over-ceiling" && round.Diagnostic is not null)
        {
            throw new RuntimeStopException(SquadRunStatus.InputRequired, round.Diagnostic!);
        }
        if (round.Decision != "over-ceiling")
        {
            return;
        }
        if (!await _template.RequestFederationApprovalAsync(state, new(SquadApprovalKind.CostOvershoot,
            "Stop or approve sequential unchanged-demand inner/meta slots under this exact aggregate ceiling. " +
            "Unknown rates/calibration/spend and at-ceiling states cannot be approved.", CostPreflight: round,
            ActionId: "federation-cost:" + HumanActions.Hash(next)), token, next.Take(1).ToArray()).ConfigureAwait(false))
        {
            throw new RuntimeStopException(SquadRunStatus.ApprovalDenied, "Aggregate financial overshoot declined before member work.");
        }
        var fresh = FederationExecution.Spend(_files, state);
        var approval = new CostApproval(meta.RunId, cost.CeilingUsd!.Value, round.PricingSha256, round.Demand,
            round.RoundId, "federation-cost:" + round.RoundId);
        var admitted = ConsumptionMath.Evaluate(meta.RunId, Guid.NewGuid().ToString("N"), cost.CeilingUsd, fresh.Spend,
            rows, next.Take(1).ToArray(), pricing, fresh.Hash, true,
            ConsumptionMath.EligibleCalibration(state.Consumption, Today()), fresh.Known, approval: approval);
        if (admitted.Decision != "approved-over-ceiling" || fresh.Hash != spend.Hash)
        {
            throw new RuntimeStopException(SquadRunStatus.InputRequired, "Aggregate spend/demand changed during approval; the stale response admitted no member.");
        }
        _scribe.CommitCostRound(state, cost.Revision, admitted, approval);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_options.TimeProvider.GetUtcNow().UtcDateTime);

    private void VerifyMemberOutcome(RuntimeState state, FederationMemberStep step)
    {
        var (_, scribe, member) = FederationExecution.ReadMember(_files, step.Member, ArtifactFingerprint, _options.TimeProvider);
        var actual = member.Runs.SingleOrDefault(run => run.RunId == step.InnerRunId) ??
            (member.Checkpoint?.Status == "federation-awaiting-validation" && member.ActiveRun?.RunId == step.InnerRunId
                ? member.ActiveRun : null);
        if (actual is null || HumanActions.Serialize(actual) != HumanActions.Serialize(step.Result) ||
            !FederationExecution.Successful(actual) ||
            member.Checkpoint?.RunId == step.InnerRunId && member.Checkpoint.Status != "federation-awaiting-validation")
        {
            throw new RuntimeHistoryIntegrityException("A meta-stage cannot advance without the completed actual inner run and fresh Scribe barrier.");
        }
        foreach (var evidence in actual.Evidence)
        {
            scribe.Verify(evidence, member);
        }
        if (state.FederationRun!.Plan!.AggregateAutopilot && step.Repair is null &&
            (actual.Status != SquadRunStatus.Completed || actual.Scope != SquadRunScope.Delivery ||
             actual.ModeOutcome?.Mode != "autopilot" ||
             RequiredInnerStages.Any(stage => actual.Evidence.All(evidence => evidence.Stage != stage))))
        {
            throw new RuntimeHistoryIntegrityException("Aggregate autopilot requires the actual unchanged inner producing and independent Review stages.");
        }
    }

    private SquadFederationRunResult Result(RuntimeState state, SquadRunStatus status, string diagnostic)
    {
        var meta = state.FederationRun;
        return new(meta?.RunId ?? "", status, diagnostic, meta?.Plan,
            meta?.Steps.Select(step => new SquadFederationMemberOutcome(step.Member, step.InnerRunId, step.Reuse, step.Result)).ToArray() ?? [],
            meta?.Handoffs.ToArray() ?? [], meta is null ? state.FederationCurrentRun : FederationExecution.Cost(_files, state),
            meta?.FinalValidated ?? false, state.Checkpoint?.Cost?.Rounds.LastOrDefault(),
            (state.Checkpoint?.HumanActions ?? []).Where(action => action.Action.Status is not ("consumed" or "superseded"))
                .Select(action => HumanActions.Copy(action.Action)).ToArray());
    }

    private SquadFederationRunResult Finish(RuntimeState state, SquadRunStatus status, string diagnostic)
    {
        var result = Result(state, status, diagnostic);
        foreach (var step in state.FederationRun!.Steps.Where(step => step.Started && !step.Reuse))
        {
            var (_, scribe, member) = FederationExecution.ReadMember(_files, step.Member, ArtifactFingerprint, _options.TimeProvider);
            if (member.Checkpoint?.Effects.Any(effect => effect.Status == "in-flight") == true)
            {
                return Result(state, SquadRunStatus.ReconciliationRequired,
                    "An owning member effect is uncertain. Reconcile its exact connector disposition; the meta-run and original evidence remain active.");
            }
            using var memberLock = scribe.AcquireLock();
            if (member.Checkpoint?.RunId == step.InnerRunId && member.Checkpoint.Status == "federation-awaiting-validation")
            {
                scribe.FinalizeFederationMember(member, step.InnerRunId, result.RunId,
                    result.RunId + "-terminal", status != SquadRunStatus.Completed);
            }
        }
        state.FederationRuns ??= [];
        state.FederationRuns.Add(result);
        state.FederationCurrentRun = result.Cost;
        _scribe.RecordFederationTransition(state, result.RunId + "-terminal", "Federation Run Outcome", result);
        state.FederationRun = null;
        state.Checkpoint = null;
        _scribe.Save(state);
        return result;
    }

    private static string[] DependencyOrder(string[] members, IEnumerable<SquadFederationDependency> dependencies)
    {
        var edges = dependencies.Where(dependency => dependency.Producer is not null).ToArray();
        var ordered = new List<string>();
        while (ordered.Count < members.Length)
        {
            var next = members.Where(member => !ordered.Contains(member, StringComparer.Ordinal) &&
                edges.Where(edge => edge.Consumer == member).All(edge => ordered.Contains(edge.Producer!, StringComparer.Ordinal))).ToArray();
            if (next.Length == 0)
            {
                throw new InvalidDataException("Cyclic federation dependencies require a human resolution; no member work started.");
            }
            ordered.AddRange(next);
        }
        return ordered.ToArray();
    }

    private static SquadFederationRunConfiguration CopyConfiguration(SquadFederationRunConfiguration value) =>
        FederationPersistence.Copy(value);

    private static object ConfigurationIdentity(SquadFederationRunConfiguration value) => new
    {
        value.Target,
        value.Members,
        value.Requests,
        value.Dependencies,
        value.InputOverrides,
    };

    private static void ValidateRunConfiguration(SquadFederationRunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration.Members);
        ArgumentNullException.ThrowIfNull(configuration.Requests);
        ArgumentNullException.ThrowIfNull(configuration.Dependencies);
        ArgumentNullException.ThrowIfNull(configuration.InputOverrides);
        _ = ConsumptionMath.ParseCeiling(configuration.CostCeiling);
        if (configuration.Target is not null)
        {
            FederationPersistence.ValidateName(configuration.Target);
        }
        foreach (var name in configuration.Members.Concat(configuration.Requests.Keys)
            .Concat(configuration.Dependencies.Select(dependency => dependency.Consumer))
            .Concat(configuration.Dependencies.Where(dependency => dependency.Producer is not null).Select(dependency => dependency.Producer!)))
        {
            FederationPersistence.ValidateName(name);
        }
        if (configuration.Members.Count > 64 || configuration.Members.Distinct(StringComparer.Ordinal).Count() != configuration.Members.Count ||
            configuration.Dependencies.Count > 256 || configuration.Dependencies.Select(dependency => dependency.Id).Distinct(StringComparer.Ordinal).Count() != configuration.Dependencies.Count ||
            configuration.Dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.Role) ||
                dependency.Stage is not ("research" or "plan" or "produce" or "review") ||
                dependency.ArtifactName != "primary" || dependency.Producer == dependency.Consumer ||
                dependency.ExpectedSha256 is not null && !HumanActions.IsHash(dependency.ExpectedSha256)) ||
            configuration.InputOverrides.Count > 256 || configuration.InputOverrides.Select(input => input.DependencyId).Distinct(StringComparer.Ordinal).Count() != configuration.InputOverrides.Count ||
            configuration.InputOverrides.Any(input => configuration.Dependencies.All(dependency => dependency.Id != input.DependencyId) ||
                (input.Path is null) == (input.Assumption is null) ||
                input.Assumption is { } assumption && (string.IsNullOrWhiteSpace(assumption) || assumption.Length > 4000)) ||
            configuration.Requests.Values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Federation selection needs bounded unique members/edges, primary artifacts and one exact input or explicit assumption per override.", nameof(configuration));
        }
    }
}
