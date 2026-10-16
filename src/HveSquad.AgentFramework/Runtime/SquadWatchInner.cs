using System.Diagnostics;
using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    internal async Task<SquadWatchRunResult> CompleteWatchTerminalAsync(SquadWatchTrigger trigger,
        SquadWatchEvent source, SquadRunResult result, ISquadWatchTerminalConnector? connector, CancellationToken token)
    {
        await _runLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = _scribe.Load() ?? throw new InvalidDataException("The original Watch member state is missing.");
            ValidateState(state);
            _scribe.VerifyNativeMember(state);
            WatchContracts.VerifyInput(_files, trigger);
            if (state.Trigger != trigger || result.RunId != trigger.RunId)
            {
                throw new InvalidDataException("The terminal action is not bound to this exact original event/inner run.");
            }
            var actual = state.ActiveRun ?? state.Runs.SingleOrDefault(run => run.RunId == trigger.RunId);
            if (actual is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual), JsonSerializer.SerializeToElement(result)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: terminal work needs the actual original native outcome, not a substituted result.");
            }
            if (actual.WatchTerminal is { } delivered && state.Checkpoint is null)
            {
                WatchContracts.ReadTerminalEvidence(_files, _scribe, state, delivered);
                return new(SquadWatchStatus.Delivered, "The original verified terminal resource is retained; no model or effect was replayed.",
                    trigger, actual, delivered);
            }
            var checkpoint = state.Checkpoint ?? throw new InvalidDataException("The original Watch checkpoint is unavailable.");
            if (checkpoint.RunId != trigger.RunId || checkpoint.HostBinding != _hostBinding)
            {
                throw new InvalidDataException("The terminal action requires the unchanged original host/run binding.");
            }
            if (actual.FailureClass == SquadEvidenceFailureClass.HistoryIntegrity ||
                checkpoint.Effects.Any(effect => effect.Status == "in-flight"))
            {
                return new(SquadWatchStatus.ReconciliationRequired,
                    "Original native evidence or an uncertain effect blocks terminal execution; no new provider action was attempted.",
                    trigger, actual);
            }
            foreach (var evidence in actual.Evidence)
            {
                _scribe.Verify(evidence, state);
            }
            if (connector is null)
            {
                return new(WatchStatus(actual, prepared: true),
                    "The actual event-scoped outcome is prepared. Register an independently authorized terminal connector; no remote link is claimed.",
                    trigger, actual);
            }
            var previousStep = checkpoint.WatchTerminal;
            var previousEffect = previousStep is null ? null : checkpoint.Effects.SingleOrDefault(effect =>
                effect.OperationId == previousStep.OperationId);
            SquadWatchTerminalReceipt? previous = null;
            if (previousEffect?.Status == "completed")
            {
                previous = _scribe.ReadEffectResult(previousEffect).Deserialize<SquadWatchTerminalReceipt>()
                    ?? throw new InvalidDataException("The actual terminal result is missing.");
                WatchContracts.VerifyTerminal(previousStep!.Request, previousStep.ConnectorBinding, previous);
            }
            if (previousStep is not null && previousStep.ConnectorBinding != connector.Binding)
            {
                throw new InvalidDataException("Restore the original terminal connector binding before reconciliation or continuation.");
            }
            var kind = trigger.TerminalKind;
            if (kind == SquadWatchTerminalKind.SourceComment)
            {
                kind = actual.Scope == SquadRunScope.Delivery ? SquadWatchTerminalKind.DraftPullRequest :
                    source.Number is null ? SquadWatchTerminalKind.Issue : SquadWatchTerminalKind.SourceComment;
            }
            var artifacts = actual.Evidence.SelectMany(evidence => evidence.Artifacts ?? [])
                .Where(artifact => !_files.IsProtectedStatePath(artifact.Path)).DistinctBy(item => item.Path).ToArray();
            var outputs = actual.Evidence.SelectMany(evidence => evidence.ExecutedHostTools ?? [])
                .Where(tool => tool.Status == SquadToolOutputStatus.Succeeded)
                .SelectMany(tool => tool.Outputs ?? []).DistinctBy(item => item.Path).ToArray();
            var findings = FederationExecution.Successful(actual) ? Array.Empty<string>() :
                new[] { "Native outcome: " + actual.Status }.Concat(actual.Evidence.Where(evidence =>
                    evidence.Risk == "High" || evidence.Verdict == "Stop" || evidence.BlockingIssues > 0)
                    .Select(evidence => evidence.Role + "/" + evidence.Stage + ": " + evidence.Verdict + "; risk " + evidence.Risk))
                    .Distinct(StringComparer.Ordinal).ToArray();
            var request = new SquadWatchTerminalRequest(Guid.NewGuid().ToString("N"), trigger.RunId, _files.SquadRoot,
                source.Repository.ToLowerInvariant(), trigger.Ref, trigger.EventId, source.Number, source.CommitSha,
                kind, actual.Status, artifacts, outputs, findings, previous);
            if (previous is not null && WatchContracts.TerminalEvidenceKey(request) == WatchContracts.TerminalEvidenceKey(previousStep!.Request))
            {
                return FinishWatchTerminal(state, trigger, actual, previous);
            }
            if (previousStep is not null && previousEffect is null &&
                WatchContracts.TerminalEvidenceKey(request) == WatchContracts.TerminalEvidenceKey(previousStep.Request))
            {
                request = previousStep.Request;
            }
            else
            {
                checkpoint.WatchTerminal = new() { Request = request, ConnectorBinding = connector.Binding, OperationId = request.OperationId };
                _scribe.RecordWatchTerminalPreparation(state, checkpoint.WatchTerminal);
            }
            if (!await connector.AuthorizeAsync(request, token).ConfigureAwait(false))
            {
                return new(WatchStatus(actual, prepared: true),
                    "The terminal capability did not authorize this exact provider action. Trigger authorization alone grants no publication permission.",
                    trigger, actual, previous);
            }
            _scribe.VerifyNativeMember(state);
            WatchContracts.VerifyInput(_files, trigger);
            var remaining = _options.RunTimeout - checkpoint.ActiveExecutionTime;
            if (remaining <= TimeSpan.Zero)
            {
                return new(SquadWatchStatus.Blocked, "The original run's active-execution deadline is exhausted.", trigger, actual, previous);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(remaining);
            var guard = new RuntimeGuard(_options, checkpoint.Budget, budget =>
            {
                checkpoint.Budget = budget;
                _scribe.Save(state);
            });
            guard.Tool(deadline.Token);
            var payload = JsonSerializer.SerializeToElement(request);
            var hash = ProjectFiles.Hash(JsonSerializer.Serialize(payload));
            var effect = new EffectCheckpoint
            {
                OperationId = request.OperationId,
                DispatchId = "watch-terminal:" + trigger.RunId,
                Role = WatchContracts.TerminalRole,
                ToolName = WatchContracts.TerminalTool,
                ArgumentsSha256 = hash,
                PayloadPath = _scribe.StorePayload(request.OperationId, new ProtectedEffectPayload(hash, payload), "arguments"),
                Effect = SquadToolEffect.External,
            };
            checkpoint.Effects.Add(effect);
            _scribe.Save(state);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var trace = _options.EnableOpenTelemetry ? new ActivitySource(_options.OpenTelemetrySourceName) : null;
                using var activity = trace?.StartActivity("create.watch.terminal", ActivityKind.Client);
                var receipt = await connector.ExecuteAsync(request, deadline.Token).ConfigureAwait(false);
                WatchContracts.VerifyTerminal(request, connector.Binding, receipt);
                effect.Receipt = AttestOutput(_options.Tools.Single(tool => tool.Function.Name == WatchContracts.TerminalTool),
                    hash, new([], JsonSerializer.Serialize(receipt)));
                effect.ResultPath = _scribe.StorePayload(effect.OperationId, receipt, "result");
                effect.ResultSha256 = _files.HashFile(effect.ResultPath);
                effect.Status = "completed";
                _scribe.RecordEffect(state, trigger.RunId, effect);
                return FinishWatchTerminal(state, trigger, actual, receipt);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                SquadHumanTransportException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return new(SquadWatchStatus.ReconciliationRequired,
                    "The original provider action has no verified terminal receipt. Reconcile its exact operation; no retry or drafted link is permitted.",
                    trigger, actual, previous);
            }
            finally
            {
                checkpoint.ActiveExecutionTime += Stopwatch.GetElapsedTime(started);
                _scribe.Save(state);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private SquadWatchRunResult FinishWatchTerminal(RuntimeState state, SquadWatchTrigger trigger,
        SquadRunResult run, SquadWatchTerminalReceipt receipt)
    {
        var effect = state.Checkpoint!.Effects.Single(item => item.OperationId == receipt.OperationId);
        SquadModeResult? mode = run.ModeOutcome;
        if (state.Checkpoint.Mode is not null && FederationExecution.Successful(run))
        {
            using var context = new RunContext(this, state, state.Checkpoint, true, CancellationToken.None);
            context.RecordModeOutcome("provider-attested draft surface delivered; final human review remains outstanding");
            mode = context.ModeResult();
        }
        var updated = run with
        {
            WatchTerminal = receipt,
            Effects = state.Checkpoint.Effects.Where(item => item.Status == "completed")
                .Select(item => new SquadExecutedEffect(item.OperationId, item.DispatchId, item.Role, item.Receipt!)).ToArray(),
            ModeOutcome = mode,
        };
        _ = _scribe.ReadEffectResult(effect);
        _scribe.RecordWatchTerminalDelivery(state, receipt);
        if (FederationExecution.Successful(run) && !(state.Checkpoint.HumanActions ?? [])
            .Any(action => action.Action.Status is not ("consumed" or "superseded")))
        {
            state.Runs.Add(updated);
            state.ActiveRun = null;
            state.Checkpoint = null;
        }
        else
        {
            state.ActiveRun = updated;
        }
        _scribe.Save(state);
        return new(WatchStatus(updated, prepared: false),
            FederationExecution.Successful(updated)
                ? "The connector verified the actual routed terminal resource. Human review is still required; no merge, push or deployment was authorized."
                : "The connector verified the draft/escalation surface, but the original native finding or human gate still blocks work.",
            trigger, updated, receipt);
    }

    private static SquadWatchStatus WatchStatus(SquadRunResult run, bool prepared) =>
        FederationExecution.Successful(run) ? prepared ? SquadWatchStatus.Prepared : SquadWatchStatus.Delivered :
            run.Status is SquadRunStatus.ApprovalRequired or SquadRunStatus.InputRequired or SquadRunStatus.ChangesRequested
                ? SquadWatchStatus.Pending : run.Status == SquadRunStatus.ReconciliationRequired
                    ? SquadWatchStatus.ReconciliationRequired : SquadWatchStatus.Blocked;
}

internal static partial class WatchContracts
{
    internal static string TerminalEvidenceKey(SquadWatchTerminalRequest request) => HumanActions.Hash(new
    {
        request.Kind,
        request.Outcome,
        request.Artifacts,
        request.ProjectOutputs,
        request.BlockingFindings,
    });

    internal static void ReadTerminalEvidence(ProjectFiles files, RuntimeScribe scribe, RuntimeState state,
        SquadWatchTerminalReceipt receipt)
    {
        var operation = state.NativeOperations.SingleOrDefault(item => item.ExecutedEffect?.OperationId == receipt.OperationId);
        if (operation?.ExecutedEffect is not { Role: TerminalRole, Receipt.Name: TerminalTool } original)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: a terminal resource has no actual original native effect receipt.");
        }
        var effect = new EffectCheckpoint
        {
            OperationId = original.OperationId,
            DispatchId = original.DispatchId,
            Role = original.Role,
            ToolName = original.Receipt.Name,
            ArgumentsSha256 = original.Receipt.ArgumentsSha256,
            PayloadPath = files.SquadRoot + "/payloads/" + original.OperationId + "-arguments.json",
            Effect = SquadToolEffect.External,
        };
        var request = scribe.ReadPayload(effect).Arguments.Deserialize<SquadWatchTerminalRequest>()
            ?? throw new InvalidDataException("The original provider action arguments are missing.");
        VerifyTerminal(request, receipt.ConnectorBinding, receipt);
        if (original.Receipt.Receipt != JsonSerializer.Serialize(receipt))
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: the reported terminal differs from its actual provider effect.");
        }
    }
}

internal sealed partial class RuntimeScribe
{
    internal void RecordWatchTerminalPreparation(RuntimeState state, WatchTerminalCheckpoint terminal)
    {
        RecordNativeOperation(state, "WatchTerminalPrepared", state.Checkpoint!.RunId, null,
            watchRecord: JsonSerializer.SerializeToElement(terminal));
        Save(state);
    }

    internal void RecordWatchTerminalDelivery(RuntimeState state, SquadWatchTerminalReceipt receipt)
    {
        if (!state.NativeOperations.Any(item => item.Operation == "WatchTerminalDelivered" &&
            item.RunId == state.Checkpoint!.RunId && item.WatchRecord is { } value &&
            value.TryGetProperty("OperationId", out var id) && id.GetString() == receipt.OperationId))
        {
            RecordNativeOperation(state, "WatchTerminalDelivered", state.Checkpoint!.RunId, null,
                watchRecord: JsonSerializer.SerializeToElement(receipt));
            Decision(state, "Actual provider terminal observed; human acceptance remains outstanding. " +
                HumanActions.Serialize(new { receipt.OperationId, receipt.Kind, receipt.ResourceId, receipt.ProviderObservationSha256 }));
        }
    }
}
