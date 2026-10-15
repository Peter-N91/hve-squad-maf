using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private static readonly Action<ILogger, Exception?> LogHumanResponseDenied = LoggerMessage.Define(
        LogLevel.Warning, new EventId(9, "HumanResponseDenied"),
        "Human response was rejected; no action was authorized. Inspect the host submission diagnostic.");

    /// <summary>Reads notification-safe pending metadata without exposing private case payloads.</summary>
    /// <param name="cancellationToken">Cancellation before acquiring the root-exclusive reader.</param>
    /// <returns>Current pending/resolved challenges; historical consumed actions are excluded.</returns>
    public async Task<IReadOnlyList<SquadPendingHumanAction>> GetPendingHumanActionsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _scribe.Load(allowFederation: _isFederationControlPlane);
            ValidateState(state);
            if (state is null)
            {
                return [];
            }
            _scribe.VerifyIntegrity(state);
            return VisibleHumanActions(state.Checkpoint);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Reads one private pending payload for a trusted, access-controlled host UI. This API is
    /// never an agent tool or notification endpoint; the host must enforce case read authorization.
    /// </summary>
    /// <param name="actionId">The exact pending action identity.</param>
    /// <param name="cancellationToken">Cancellation before reading protected case storage.</param>
    /// <returns>The verified original question or approval, not a redacted outbound view.</returns>
    public async Task<SquadHumanActionDetails> ReadHumanActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = LoadHumanState();
            _scribe.VerifyIntegrity(state);
            var pending = FindHumanAction(state, actionId);
            var payload = _scribe.ReadHumanPayload(pending);
            return new(HumanActions.Copy(pending.Action), payload.Approval, payload.Question);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Authenticates and persists one exact response. It executes no model, project action or implicit resume.</summary>
    /// <param name="response">Untrusted binding, command and source reference; actor claims are not accepted here.</param>
    /// <param name="cancellationToken">Cancellation of channel verification and persistence.</param>
    /// <returns>Explicit acceptance or rejection with the same run's actual state.</returns>
    public async Task<SquadHumanSubmission> SubmitHumanResponseAsync(SquadHumanResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.Binding);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadHumanState();
            try
            {
                _ = _scribe.PrepareEvidence(state);
            }
            catch (RuntimeHistoryIntegrityException)
            {
                return new(false, response.Binding.Id, SquadRunStatus.Blocked,
                    "History or protected pending-payload integrity failed; nothing was authorized or overwritten.");
            }
            return await SubmitHumanResponseCoreAsync(state, response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Performs one bounded authenticated channel poll; missing/ignored responses leave gates pending.</summary>
    /// <param name="runId">The exact suspended run to inspect.</param>
    /// <param name="cancellationToken">Cancellation of provider reads.</param>
    /// <returns>Each accepted/rejected response and explicit transport/missing-response diagnostics.</returns>
    public async Task<SquadHumanPoll> PollHumanResponsesAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadHumanState();
            if (state.Checkpoint?.RunId != runId)
            {
                throw new InvalidDataException("No matching suspended run owns this poll.");
            }
            _ = _scribe.PrepareEvidence(state);
            var channel = _options.HumanApprovalChannel
                ?? throw new InvalidOperationException("Register an authenticated human approval channel before polling.");
            var submissions = new List<SquadHumanSubmission>();
            var diagnostics = new List<string>();
            foreach (var pending in (state.Checkpoint.HumanActions ?? []).Where(item => item.Action.Status == "pending").ToArray())
            {
                IReadOnlyList<SquadHumanResponse> responses;
                try
                {
                    responses = await channel.ReadResponsesAsync(HumanActions.Copy(pending.Action), cancellationToken).ConfigureAwait(false);
                }
                catch (SquadHumanTransportException ex)
                {
                    diagnostics.Add(ex.Message);
                    continue;
                }
                if (responses.Count > _options.MaxToolCalls)
                {
                    diagnostics.Add("Human channel returned more responses than the configured bounded poll permits.");
                    continue;
                }
                if (responses.Count == 0)
                {
                    diagnostics.Add("No recognized response for human action " + pending.Action.Binding.Id + "; it remains pending.");
                }
                foreach (var response in responses)
                {
                    var submission = await SubmitHumanResponseCoreAsync(state, response, cancellationToken).ConfigureAwait(false);
                    submissions.Add(submission);
                    if (submission.Accepted)
                    {
                        break;
                    }
                }
                if (state.Checkpoint is null)
                {
                    break;
                }
            }
            return new(submissions.ToArray(), diagnostics.ToArray());
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Explicitly reissues an expired unchanged challenge, or retries/reconciles its notification.</summary>
    /// <param name="actionId">The current pending or expired challenge, never a consumed decision.</param>
    /// <param name="cancellationToken">Cancellation of verification and publication.</param>
    /// <returns>The new/current safe metadata. Unknown publication outcomes never create a second issue.</returns>
    public async Task<SquadPendingHumanAction> RenewHumanActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = LoadHumanState();
            _ = _scribe.PrepareEvidence(state);
            var pending = FindHumanAction(state, actionId);
            if (pending.Decision is not null || pending.Action.Status is not ("pending" or "expired"))
            {
                throw new InvalidOperationException("A decided or superseded human action cannot be renewed.");
            }
            using var context = new RunContext(this, state, state.Checkpoint!, true, cancellationToken);
            context.VerifyHumanEvidence(pending);
            if (_options.TimeProvider.GetUtcNow() >= pending.Action.ExpiresAt || pending.Action.Status == "expired")
            {
                pending.Action = pending.Action with { Status = "expired" };
                pending = CreateHumanAction(state, _scribe.ReadHumanPayload(pending), pending.Scope,
                    pending.Action.Binding.Gate, pending.Action.Binding.Step, pending.Action.Binding.Role,
                    pending.Action.Binding.DispatchId, pending.Action.Binding.RoundId);
            }
            await PublishHumanActionAsync(state, pending, cancellationToken).ConfigureAwait(false);
            return HumanActions.Copy(pending.Action);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private RuntimeState LoadHumanState()
    {
        var state = _scribe.Load(allowFederation: _isFederationControlPlane)
            ?? throw new InvalidDataException("No initialized native state owns a human action.");
        ValidateState(state);
        if (_resources.Fingerprint() != ArtifactFingerprint)
        {
            throw new InvalidDataException("Released artifacts changed; the original human-action binding cannot be authorized.");
        }
        var checkpoint = state.Checkpoint ?? throw new InvalidDataException("No current native run owns a human action.");
        if (checkpoint.SquadRoot != _files.SquadRoot || checkpoint.Fingerprint != ArtifactFingerprint ||
            checkpoint.HostBinding != _hostBinding || checkpoint.Limits != Limits() ||
            !_isFederationControlPlane && !ResumeModelPolicyMatches(state, checkpoint))
        {
            throw new InvalidDataException("Human action host/release/root/execution configuration changed.");
        }
        return state;
    }

    private static HumanActionCheckpoint FindHumanAction(RuntimeState state, string id) =>
        state.Checkpoint?.HumanActions?.SingleOrDefault(item => item.Action.Binding.Id == id)
            ?? throw new InvalidDataException("No exact pending human action exists in this root.");

    private static SquadPendingHumanAction[] VisibleHumanActions(RunCheckpoint? checkpoint) =>
        (checkpoint?.HumanActions ?? []).Where(item => item.Action.Status is not ("consumed" or "superseded"))
            .Select(item => HumanActions.Copy(item.Action)).ToArray();

    private static string HumanEvidenceSha256(RuntimeState state)
    {
        var checkpoint = state.Checkpoint ?? throw new InvalidDataException("No current human-action checkpoint.");
        var evidence = HumanActions.Hash(new
        {
            checkpoint.RunId,
            checkpoint.Fingerprint,
            checkpoint.HostBinding,
            checkpoint.InputBindings,
            checkpoint.Routing,
            checkpoint.Readiness,
            Production = checkpoint.Production?.Plan,
            Evidence = checkpoint.Evidence.Concat(checkpoint.PendingEvidence).OrderBy(item => item.DispatchId, StringComparer.Ordinal).ToArray(),
            WrittenArtifacts = checkpoint.DispatchStack.SelectMany(dispatch => dispatch.WrittenArtifacts)
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            Gates = (state.GateRecords ?? []).Where(gate => gate.RunId == checkpoint.RunId).ToArray(),
            Handoffs = (state.ProductionHandoffs ?? []).Where(handoff => handoff.RunId == checkpoint.RunId).ToArray(),
            Effects = checkpoint.Effects.Where(effect => effect.Status == "completed" &&
                !(state.Trigger?.RunId == checkpoint.RunId && effect.Role == WatchContracts.TerminalRole &&
                    effect.ToolName == WatchContracts.TerminalTool))
                .Select(effect => new { effect.OperationId, effect.DispatchId, effect.ToolName, effect.ArgumentsSha256, effect.Receipt }).ToArray(),
            Ceiling = checkpoint.Cost?.CeilingUsd,
            Pricing = ConsumptionMath.PricingHash(state.Consumption),
            checkpoint.ModelRouting,
            checkpoint.HumanRevision,
            checkpoint.HumanChanges,
            state.Notify,
            Federation = state.FederationRun?.Plan,
            FederationRevision = state.FederationRun?.Revision,
        });
        return checkpoint.Mode is null ? evidence :
            HumanActions.Hash(new { evidence, checkpoint.Mode, state.OutputRevisions });
    }

    private HumanActionCheckpoint CreateHumanAction(RuntimeState state, HumanActionPayload payload, string scope,
        string gate, string step, string? role, string? dispatchId, string? roundId)
    {
        var channel = _options.HumanApprovalChannel
            ?? throw new InvalidOperationException("An authenticated human channel is not registered.");
        if (channel is Hosting.GitHubIssueApprovalChannel github &&
            (state.Notify?.Enabled != true || state.Notify.ApprovalChannel != "github-issue" ||
                !github.Repository.Equals(state.Notify.GitHubRepository, StringComparison.OrdinalIgnoreCase) ||
                !github.ApproverHandle.Equals(state.Notify.GitHubHandle, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The registered GitHub adapter does not match the confirmed non-secret notification opt-in.");
        }
        var checkpoint = state.Checkpoint ?? throw new InvalidDataException("No run owns this human action.");
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var path = _scribe.StoreHumanPayload(id, payload, "action");
        var root = _files.Resolve(_files.SquadRoot);
        var binding = new SquadHumanActionBinding(id, checkpoint.RunId,
            ProjectFiles.Hash(OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root),
            ArtifactFingerprint, _hostBinding, checkpoint.HumanRevision, gate, step,
            payload.Approval?.FederationGate is { } attribution ?
                attribution.Member + (role is null ? "" : ":" + role) : role, dispatchId, roundId,
            HumanActions.Hash(payload), HumanEvidenceSha256(state), _files.HashFile(path), _options.Mode,
            payload.Approval?.FederationGate?.RunId ?? _options.FederationInvocation?.FederationRunId,
            payload.Approval?.FederationGate?.Member ?? _options.FederationInvocation?.Member);
        var now = _options.TimeProvider.GetUtcNow();
        var pending = new HumanActionCheckpoint
        {
            Action = new(binding, payload.Question is not null, channel.Name, now, now + _options.HumanResponseTimeout, "pending"),
            PayloadPath = path,
            Scope = scope,
        };
        checkpoint.HumanActions ??= [];
        checkpoint.HumanActions.Add(pending);
        _scribe.RecordReportHumanGate(state, pending.Action);
        _scribe.Save(state);
        return pending;
    }

    private async ValueTask PublishHumanActionAsync(RuntimeState state, HumanActionCheckpoint pending, CancellationToken cancellationToken)
    {
        var channel = _options.HumanApprovalChannel
            ?? throw new InvalidOperationException("The pending action needs its registered authenticated channel.");
        if (pending.Action.DeliveryStatus != "sent")
        {
            SquadPendingHumanAction before;
            using (_scribe.SyncRoot.EnterScope())
            {
                before = HumanActions.Copy(pending.Action);
                pending.Action = pending.Action with { DeliveryStatus = "publishing", Diagnostic = null };
                _scribe.Save(state);
            }
            SquadHumanDelivery delivery;
            try
            {
                delivery = await channel.PublishAsync(before, cancellationToken).ConfigureAwait(false);
            }
            catch (SquadHumanTransportException ex)
            {
                delivery = new(ex.OutcomeUncertain ? "uncertain" : "failed", Diagnostic: ex.Message);
            }
            ValidateHumanDelivery(delivery);
            using (_scribe.SyncRoot.EnterScope())
            {
                pending.Action = pending.Action with
                {
                    DeliveryStatus = delivery.Status,
                    Reference = delivery.Reference,
                    Diagnostic = delivery.Diagnostic,
                };
                _scribe.Notification(state, $"Human action {pending.Action.Binding.Id}: {channel.Name} notification {delivery.Status}.");
                _scribe.RecordReportHumanGate(state, pending.Action);
                _scribe.Save(state);
            }
        }
        if (_options.HumanNotifier is { } notifier && pending.Action.OutboundStatus != "sent")
        {
            if (state.Notify?.Enabled != true || state.Notify.WebhookBinding != notifier.Binding)
            {
                throw new InvalidOperationException("The notifier lacks its exact confirmed non-secret remote-notification opt-in.");
            }
            SquadPendingHumanAction before;
            using (_scribe.SyncRoot.EnterScope())
            {
                pending.Action = pending.Action with { OutboundStatus = "publishing", OutboundDiagnostic = null };
                _scribe.Save(state);
                before = HumanActions.Copy(pending.Action);
            }
            SquadHumanDelivery delivery;
            try
            {
                delivery = await notifier.PublishAsync(before, cancellationToken).ConfigureAwait(false);
            }
            catch (SquadHumanTransportException ex)
            {
                delivery = new(ex.OutcomeUncertain ? "uncertain" : "failed", Diagnostic: ex.Message);
            }
            ValidateHumanDelivery(delivery);
            using (_scribe.SyncRoot.EnterScope())
            {
                pending.Action = pending.Action with { OutboundStatus = delivery.Status, OutboundDiagnostic = delivery.Diagnostic };
                _scribe.Notification(state, $"Human action {pending.Action.Binding.Id}: outbound-only notifier {delivery.Status}; no approval implied.");
                _scribe.RecordReportHumanGate(state, pending.Action);
                _scribe.Save(state);
            }
        }
        else if (_options.HumanNotifier is null && state.Notify is { Enabled: true, ApprovalChannel: "webhook" })
        {
            using (_scribe.SyncRoot.EnterScope())
            {
                pending.Action = pending.Action with
                {
                    OutboundStatus = "unavailable",
                    OutboundDiagnostic = "The confirmed webhook notifier is not registered; no outbound webhook was sent.",
                };
                _scribe.Save(state);
            }
        }
    }

    private static void ValidateHumanDelivery(SquadHumanDelivery delivery)
    {
        if (delivery.Status is not ("sent" or "failed" or "uncertain") ||
            delivery.Reference is { Length: > 256 } || delivery.Diagnostic is { Length: > 1024 })
        {
            throw new InvalidDataException("The registered human channel returned an invalid delivery status.");
        }
    }

    private async ValueTask<SquadHumanSubmission> SubmitHumanResponseCoreAsync(
        RuntimeState state, SquadHumanResponse response, CancellationToken cancellationToken)
    {
        var checkpoint = state.Checkpoint;
        var pending = checkpoint?.HumanActions?.SingleOrDefault(item => item.Action.Binding.Id == response.Binding.Id);
        SquadHumanSubmission Denied(string reason)
        {
            if (_options.LoggerFactory is { } factory)
            {
                LogHumanResponseDenied(factory.CreateLogger("HveSquad.AgentFramework.Runtime.Human"), null);
            }
            return new(false, response.Binding.Id, state.ActiveRun?.Status ?? SquadRunStatus.Blocked, reason);
        }
        if (pending is null || checkpoint is null || response.Binding != pending.Action.Binding ||
            pending.Action.Status != "pending" || pending.Decision is not null)
        {
            return Denied("Wrong run/root/action/evidence, stale challenge or replayed response; nothing was authorized.");
        }
        if (!Enum.IsDefined(response.Decision) || !HumanActions.IsReference(response.ResponseId) ||
            response.Input is { Length: > 0 } && response.Input.Length > _options.MaxArtifactCharacters ||
            response.Decision is SquadHumanDecision.Answer or SquadHumanDecision.Changes && string.IsNullOrWhiteSpace(response.Input) ||
            response.Decision is SquadHumanDecision.Approve or SquadHumanDecision.Stop && response.Input is not null ||
            response.Decision == SquadHumanDecision.Approve && pending.Action.IsQuestion ||
            response.Decision == SquadHumanDecision.Answer && !pending.Action.IsQuestion)
        {
            return Denied("Invalid decision shape; a question needs an answer and each action needs its exact recognized command.");
        }
        var now = _options.TimeProvider.GetUtcNow();
        if (now >= pending.Action.ExpiresAt || response.CreatedAt is { } responseTime &&
            (responseTime < pending.Action.CreatedAt || responseTime >= pending.Action.ExpiresAt || responseTime > now))
        {
            return Denied("The challenge or response timestamp is stale/expired; timeout never authorizes work.");
        }
        var channel = _options.HumanApprovalChannel;
        if (channel is null || channel.Name != pending.Action.Channel)
        {
            return Denied("The original authenticated approval channel is unavailable; no callback or webhook fallback was used.");
        }
        using var context = new RunContext(this, state, checkpoint, true, cancellationToken);
        try
        {
            context.VerifyHumanEvidence(pending);
        }
        catch (RuntimeHistoryIntegrityException)
        {
            return Denied("Bound input, output, pending payload or gate evidence changed; the old response is invalid.");
        }
        if (checkpoint.Effects.Any(effect => effect.Status == "in-flight") && response.Decision != SquadHumanDecision.Stop)
        {
            return Denied("An effect is uncertain. Reconcile it before any approval, answer or redirected work.");
        }
        SquadHumanAuthorization? authorization;
        try
        {
            authorization = await channel.AuthorizeAsync(HumanActions.Copy(pending.Action), response, cancellationToken).ConfigureAwait(false);
        }
        catch (SquadHumanTransportException ex)
        {
            return Denied(ex.Message);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (authorization is null || !HumanActions.IsReference(authorization.Provider) || !HumanActions.IsHash(authorization.SubjectSha256))
        {
            return Denied("The registered channel could not authenticate an authorized human actor and the exact response.");
        }
        if ((state.HumanDecisions ?? []).Any(decision => decision.Provider == authorization.Provider &&
            decision.ResponseId == response.ResponseId))
        {
            return Denied("This authenticated channel response reference was already consumed by another action.");
        }
        if (_options.TimeProvider.GetUtcNow() >= pending.Action.ExpiresAt)
        {
            return Denied("The challenge expired during authentication; no decision was accepted.");
        }
        context.VerifyHumanEvidence(pending);
        var inputPath = response.Input is null ? null : _scribe.StoreHumanPayload(response.Binding.Id, response.Input, "input");
        var receipt = new HumanDecisionReceipt(response.Binding, response.Decision, response.ResponseId,
            authorization.Provider, authorization.SubjectSha256, _options.TimeProvider.GetUtcNow(),
            inputPath, inputPath is null ? null : _files.HashFile(inputPath));
        _scribe.RecordHumanDecision(state, pending, receipt);
        if (response.Decision == SquadHumanDecision.Answer)
        {
            _scribe.RestoreHumanAnswer(state, pending);
        }
        if (response.Decision == SquadHumanDecision.Stop)
        {
            checkpoint.HumanStopRequested = true;
        }
        var status = response.Decision == SquadHumanDecision.Changes ? SquadRunStatus.ChangesRequested :
            response.Decision == SquadHumanDecision.Stop ? SquadRunStatus.Stopped :
            pending.Action.IsQuestion ? SquadRunStatus.InputRequired : SquadRunStatus.ApprovalRequired;
        state.ActiveRun = new(checkpoint.RunId, status,
            "Authenticated human decision recorded. Explicitly resume the same run; no model or project action executed.",
            checkpoint.Evidence.ToArray(), PendingHumanActions: VisibleHumanActions(checkpoint));
        _scribe.Save(state);
        SquadHumanDelivery resolution;
        try
        {
            resolution = await channel.ResolveAsync(HumanActions.Copy(pending.Action), receipt.Decision, cancellationToken).ConfigureAwait(false);
        }
        catch (SquadHumanTransportException ex)
        {
            resolution = new("failed", Diagnostic: ex.Message);
        }
        ValidateHumanDelivery(resolution);
        pending.ResolutionStatus = resolution.Status;
        _scribe.Notification(state, $"Human action {receipt.Binding.Id}: outcome notification {resolution.Status}.");
        _scribe.Save(state);
        return new(true, receipt.Binding.Id, status,
            resolution.Status == "sent" ? "Authenticated decision recorded; explicit same-run resume is required." :
                "Authenticated decision recorded, but outcome notification " + resolution.Status + ". No project action executed.");
    }

    private SquadRunResult? PrepareHumanContinuation(RuntimeState state, RunCheckpoint checkpoint, CancellationToken token)
    {
        if (checkpoint.HumanActions is not { Count: > 0 })
        {
            return null;
        }
        using var context = new RunContext(this, state, checkpoint, true, token);
        context.VerifyHumanEvidence();
        foreach (var pending in checkpoint.HumanActions.Where(item => item.Decision is not null))
        {
            _scribe.RestoreHumanAnswer(state, pending);
            checkpoint.HumanStopRequested |= pending.Decision!.Decision == SquadHumanDecision.Stop;
        }
        if (checkpoint.HumanStopRequested)
        {
            var uncertain = checkpoint.Effects.Any(effect => effect.Status == "in-flight");
            if (checkpoint.Mode is { } mode)
            {
                mode.Outcome = uncertain ? "stop requested: reconciliation required" : "stopped";
                context.RecordModeOutcome(mode.Outcome);
            }
            var result = new SquadRunResult(checkpoint.RunId,
                uncertain ? SquadRunStatus.ReconciliationRequired : SquadRunStatus.Stopped,
                uncertain ? "Stop requested. Reconcile uncertain effects; no further work or replay is permitted." :
                    "Run stopped by an authenticated human. Completed evidence and spend were preserved.",
                checkpoint.Evidence.ToArray(),
                Effects: checkpoint.Effects.Where(effect => effect.Status == "completed").Select(effect =>
                    new SquadExecutedEffect(effect.OperationId, effect.DispatchId, effect.Role, effect.Receipt!)).ToArray(),
                Consumption: ConsumptionMath.Summary(checkpoint.RunId, state.ModelCallCharges ?? [],
                    ConsumptionMath.UnobservedDispatches(checkpoint.RunId, state)),
                ModeOutcome: context.ModeResult());
            if (!uncertain)
            {
                state.Runs.Add(result);
                state.ActiveRun = null;
                state.Checkpoint = null;
            }
            else
            {
                state.ActiveRun = result;
            }
            _scribe.Save(state);
            return result;
        }
        var now = _options.TimeProvider.GetUtcNow();
        foreach (var change in checkpoint.HumanActions.Where(item => item.Action.Status == "changes").ToArray())
        {
            context.VerifyHumanEvidence(change);
            ApplyHumanChanges(state, checkpoint, change);
        }
        foreach (var pending in checkpoint.HumanActions.Where(item => item.Action.Status == "pending"))
        {
            if (pending.Action.Binding.EvidenceSha256 != HumanEvidenceSha256(state))
            {
                pending.Action = pending.Action with { Status = "superseded", Diagnostic = "Evidence changed; a fresh exact gate must be evaluated." };
            }
            else if (now >= pending.Action.ExpiresAt)
            {
                pending.Action = pending.Action with { Status = "expired", Diagnostic = "Explicitly renew this unchanged challenge; timeout is not approval." };
            }
        }
        var waiting = checkpoint.HumanActions.Where(item => item.Action.Status is "pending" or "expired").ToArray();
        if (waiting.Length > 0)
        {
            var status = waiting.Any(item => item.Action.IsQuestion) ? SquadRunStatus.InputRequired : SquadRunStatus.ApprovalRequired;
            var result = new SquadRunResult(checkpoint.RunId, status,
                "Human response is pending or expired. Poll/submit an authenticated response or explicitly renew; no model or project action executed.",
                checkpoint.Evidence.ToArray(), PendingHumanActions: VisibleHumanActions(checkpoint),
                ModeOutcome: context.ModeResult());
            state.ActiveRun = result;
            _scribe.Save(state);
            return result;
        }
        foreach (var pending in checkpoint.HumanActions.Where(item => item.Action.Status is "approve" or "answer" or "changes").ToArray())
        {
            if (pending.Action.Binding.EvidenceSha256 != HumanEvidenceSha256(state))
            {
                pending.Action = pending.Action with { Status = "superseded" };
                continue;
            }
            context.VerifyHumanEvidence(pending);
            var payload = _scribe.ReadHumanPayload(pending);
            if (pending.Decision?.Decision == SquadHumanDecision.Changes)
            {
                ApplyHumanChanges(state, checkpoint, pending);
            }
            else if (payload.Approval?.Kind == SquadApprovalKind.Routing)
            {
                checkpoint.Routing = payload.Approval.Routing
                    ?? throw new RuntimeHistoryIntegrityException("human-action: the approved routing proposal is missing.");
                pending.Action = pending.Action with { Status = "consumed" };
            }
            else if (payload.Approval?.Kind == SquadApprovalKind.CostOvershoot)
            {
                context.ApplyHumanCostApproval(pending, payload);
                pending.Action = pending.Action with { Status = "consumed" };
            }
        }
        _scribe.Save(state);
        return null;
    }

    private void ApplyHumanChanges(RuntimeState state, RunCheckpoint checkpoint, HumanActionCheckpoint pending)
    {
        var receipt = pending.Decision ?? throw new InvalidDataException("No authenticated change decision exists.");
        var input = new HumanChangeInput(receipt.Binding.Id, receipt.Binding.Step, receipt.Binding.DispatchId,
            receipt.InputPath ?? throw new InvalidDataException("Change input is missing."),
            receipt.InputSha256 ?? throw new InvalidDataException("Change input digest is missing."));
        _ = _scribe.ReadHumanInput(input.ActionId, input.InputPath, input.InputSha256);
        checkpoint.HumanChanges ??= [];
        checkpoint.HumanChanges.Add(input);
        checkpoint.HumanRevision++;
        checkpoint.ApprovedGates.Clear();
        checkpoint.Cost!.Approval = null;
        checkpoint.Cost.Revision++;
        var step = pending.Action.Binding.Step;
        var scopedProducer = pending.Action.Binding.DispatchId is { } dispatchId &&
            checkpoint.DispatchStack.Any(dispatch => dispatch.Id == dispatchId && dispatch.Stage == "produce");
        var scopedReview = pending.Action.Binding.DispatchId is { } reviewId &&
            checkpoint.DispatchStack.Any(dispatch => dispatch.Id == reviewId && dispatch.Stage == "review");
        if (checkpoint.Mode is { } mode && (step is "finaloutcome" or "autonomousoutcome" ||
            step == "riskgate" && mode.ChangeBlockedByCap))
        {
            var fanout = checkpoint.Production?.Plan.Units.Count > 1 && mode.Mode == "autopilot";
            if (mode.ProducingIteration == 2 || mode.ValidationRecords.Count != 1 && !fanout)
            {
                mode.ChangeBlockedByCap = true;
            }
            else
            {
                mode.HumanChangesPending = true;
                mode.ProducingChangeActionId = receipt.Binding.Id;
                mode.ProducingIteration = 2;
                mode.ValidationComplete = false;
                mode.FinalValidated = false;
                var reviews = checkpoint.Evidence.Where(item => item.Stage == "review").Select(item => item.DispatchId).ToHashSet(StringComparer.Ordinal);
                checkpoint.Evidence.RemoveAll(item => reviews.Contains(item.DispatchId));
                foreach (var id in reviews)
                {
                    checkpoint.SessionBindings.Remove("dispatch:" + id);
                }
            }
        }
        else if (!scopedProducer && !scopedReview && step != "cost")
        {
            var replan = step is "plan" or "implementation" or "council" or "counciloverride";
            var stages = replan ? new[] { "plan", "council", "produce", "review" } :
                new[] { "discovery", "intake", "validation", "remediation", "research", "plan", "council", "produce", "review" };
            var affected = checkpoint.Evidence.Concat(checkpoint.PendingEvidence)
                .Where(evidence => stages.Contains(evidence.Stage, StringComparer.Ordinal)).Select(evidence => evidence.DispatchId)
                .Concat(checkpoint.DispatchStack.Where(dispatch => stages.Contains(dispatch.Stage, StringComparer.Ordinal)).Select(dispatch => dispatch.Id))
                .ToHashSet(StringComparer.Ordinal);
            if (checkpoint.Effects.Any(effect => affected.Contains(effect.DispatchId)))
            {
                throw new RuntimeHistoryIntegrityException("human-action: reopening this stage would abandon actual effect identity; keep the producing dispatch and reconcile first.");
            }
            checkpoint.Evidence.RemoveAll(evidence => affected.Contains(evidence.DispatchId));
            checkpoint.PendingEvidence.RemoveAll(evidence => affected.Contains(evidence.DispatchId));
            checkpoint.DispatchStack.RemoveAll(dispatch => affected.Contains(dispatch.Id));
            foreach (var id in affected)
            {
                checkpoint.SessionBindings.Remove("dispatch:" + id);
            }
            checkpoint.Production = null;
            if (replan && checkpoint.Readiness is { } readiness)
            {
                readiness.CouncilProposal = null;
                readiness.CouncilSelection = null;
                readiness.CouncilConfirmed = false;
                readiness.CouncilRecordId = null;
            }
            else
            {
                checkpoint.HumanPriorIntakeCycles = Math.Max(checkpoint.HumanPriorIntakeCycles, checkpoint.Readiness?.IntakeCycles ?? 0);
                checkpoint.HumanReadinessRevision++;
                checkpoint.Readiness = null;
                checkpoint.Routing = null;
            }
        }
        foreach (var other in checkpoint.HumanActions ?? [])
        {
            other.Action = other.Action with { Status = other == pending ? "consumed" : "superseded" };
        }
        _scribe.Notification(state, "Authenticated changes reopened the affected work; private input remains in protected case storage.");
    }

    private sealed partial class RunContext
    {
        private readonly HashSet<string> _usedHumanAnswers = new(StringComparer.Ordinal);

        private string EffectActionId(string dispatchId)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                return "effect:" + dispatchId + ":" + checkpoint.Effects.Count(item => item.DispatchId == dispatchId)
                    .ToString(CultureInfo.InvariantCulture);
            }
        }

        internal void VerifyHumanEvidence(HumanActionCheckpoint? pending = null)
        {
            VerifyEvidence();
            if (pending is null)
            {
                return;
            }
            var payload = runtime._scribe.ReadHumanPayload(pending);
            if (runtime._isFederationControlPlane && payload.Approval?.FederationGate is { } attribution)
            {
                FederationExecution.VerifyGateSource(runtime._files, state, attribution, payload.Approval.Evidence ?? []);
            }
            if (pending.Action.Binding.EvidenceSha256 != HumanEvidenceSha256(state))
            {
                throw new RuntimeHistoryIntegrityException("human-action: exact gate evidence changed.");
            }
            if (payload.Approval?.SkillScript is { } script &&
                pending.Action.Status is not ("consumed" or "superseded"))
            {
                try
                {
                    SkillScriptContracts.Verify(runtime.Artifacts, runtime.Provenance, script.Binding);
                    if (runtime._resources.Fingerprint() != runtime.ArtifactFingerprint ||
                        script.Inputs.Any(input => runtime._files.HashFile(input.Path) != input.Sha256))
                    {
                        throw new RuntimeHistoryIntegrityException("human-action: registered script resource or original input scope changed.");
                    }
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
                {
                    throw new RuntimeHistoryIntegrityException("human-action: registered script resource or original input scope is missing or changed.");
                }
            }
            if (payload.Approval?.CostPreflight is { } round)
            {
                var current = checkpoint.Cost?.Rounds.LastOrDefault();
                var spend = CostSpend();
                if (current?.RoundId != round.RoundId || HumanActions.Serialize(current) != HumanActions.Serialize(round) ||
                    round.Decision != "over-ceiling" || round.Confidence != "medium" || round.Diagnostic is not null ||
                    checkpoint.Cost?.CeilingUsd != round.CeilingUsd || !spend.Known ||
                    round.SpendSha256 != spend.Hash || round.PricingSha256 != ConsumptionMath.PricingHash(state.Consumption) ||
                    spend.Spend >= round.CeilingUsd || payload.CostSlots is not { Count: 1 })
                {
                    throw new RuntimeHistoryIntegrityException("human-action: financial round/spend/demand/pricing changed or is not approvable.");
                }
            }
        }

        internal void ApplyHumanCostApproval(HumanActionCheckpoint pending, HumanActionPayload payload)
        {
            VerifyHumanEvidence(pending);
            var round = payload.Approval!.CostPreflight!;
            var cost = checkpoint.Cost!;
            var approval = new CostApproval(runId, cost.CeilingUsd!.Value, round.PricingSha256,
                round.Demand, round.RoundId, "human:" + pending.Action.Binding.Id);
            var spend = CostSpend();
            var confirmed = ConsumptionMath.Evaluate(runId, Guid.NewGuid().ToString("N"), cost.CeilingUsd,
                spend.Spend, round.Demand, payload.CostSlots!, ConsumptionMath.PricingHash(state.Consumption), spend.Hash,
                true, ConsumptionMath.EligibleCalibration(state.Consumption, Today), spend.Known, spend.Pending, approval);
            if (confirmed.Decision != "approved-over-ceiling" || confirmed.SpendSha256 != round.SpendSha256)
            {
                throw new RuntimeHistoryIntegrityException("human-action: the authenticated financial response cannot admit this changed demand.");
            }
            runtime._scribe.CommitCostRound(state, cost.Revision, confirmed, approval);
        }

        private async ValueTask<HumanDecisionReceipt> RequestHumanDecisionAsync(
            HumanActionPayload payload, string scope, string gate, string step, string? role,
            string? dispatchId, string? roundId, CancellationToken cancellationToken)
        {
            _guard.Check(token);
            VerifyEvidence();
            HumanActionCheckpoint pending;
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var hash = HumanActions.Hash(payload);
                var evidence = HumanEvidenceSha256(state);
                pending = (checkpoint.HumanActions ?? []).LastOrDefault(item => item.Scope == scope &&
                    item.Action.Binding.ActionSha256 == hash && item.Action.Binding.EvidenceSha256 == evidence &&
                    item.Action.Binding.Revision == checkpoint.HumanRevision &&
                    item.Action.Status != "superseded" &&
                    (payload.Question is null || !_usedHumanAnswers.Contains(item.Action.Binding.Id))) ??
                    runtime.CreateHumanAction(state, payload, scope, gate, step, role, dispatchId, roundId);
                if (pending.Decision is { } decision && pending.Action.Status != "expired")
                {
                    pending.Action = pending.Action with { Status = "consumed" };
                    _usedHumanAnswers.Add(pending.Action.Binding.Id);
                    runtime._scribe.Save(state);
                    return decision;
                }
            }
            await runtime.PublishHumanActionAsync(state, pending, cancellationToken).ConfigureAwait(false);
            throw _guard.Stop(payload.Question is null ? SquadRunStatus.ApprovalRequired : SquadRunStatus.InputRequired,
                "Authenticated human response required for " + gate + " action " + pending.Action.Binding.Id +
                ". The protected pending payload was retained; no response or notification acknowledgement authorizes continuation.");
        }

        private string HumanChangeContext()
        {
            var changes = (checkpoint.HumanChanges ?? []).Select(input => new
            {
                input.ActionId,
                input.Step,
                input.DispatchId,
                Input = runtime._scribe.ReadHumanInput(input.ActionId, input.InputPath, input.InputSha256),
            }).ToArray();
            var federation = runtime._options.FederationInvocation?.Revision is { } revision ?
                "\nAuthenticated federation change request (descriptive input, never permission):\n" +
                FederationExecution.HumanRevisionInput(runtime._files, revision) : "";
            return federation + (changes.Length == 0 ? "" :
                "\nAuthenticated human change requests (descriptive input, never tool permission or executable commands):\n" +
                JsonSerializer.Serialize(changes));
        }
    }
}
