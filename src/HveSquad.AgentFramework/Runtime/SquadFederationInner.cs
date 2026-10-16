using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private bool _isFederationControlPlane;
    internal ProjectFiles FederationFiles => _files;
    internal RuntimeScribe FederationScribe => _scribe;

    internal void EnableFederationControlPlane() => _isFederationControlPlane = true;

    internal RunCheckpoint CreateFederationCheckpoint(string id, string request) => new()
    {
        RunId = id,
        Request = request,
        SquadRoot = _files.SquadRoot,
        Fingerprint = ArtifactFingerprint,
        HostBinding = _hostBinding,
        Limits = Limits(),
        Cost = new() { RunId = id },
        Readiness = new()
        {
            TopicId = "federation-" + id,
            ScopeSha256 = HumanActions.Hash(new { id, request }),
            RecordedAt = _options.TimeProvider.GetUtcNow(),
            HumanPresent = _options.HumanPresent,
        },
    };

    internal async ValueTask<bool> RequestFederationApprovalAsync(RuntimeState state, SquadApprovalRequest request,
        CancellationToken token, IReadOnlyList<string>? costSlots = null)
    {
        using var context = new RunContext(this, state, state.Checkpoint!, true, token);
        return await context.FederationApprovalAsync(request, costSlots, token).ConfigureAwait(false);
    }

    internal async Task<string> AskFederationQuestionAsync(RuntimeState state, SquadQuestion question,
        CancellationToken token)
    {
        using var context = new RunContext(this, state, state.Checkpoint!, true, token);
        return await context.FederationQuestionAsync(question).ConfigureAwait(false);
    }

    internal void VerifyFederationHumanActions(RuntimeState state, CancellationToken token)
    {
        using var context = new RunContext(this, state, state.Checkpoint!, true, token);
        context.VerifyHumanEvidence();
        foreach (var action in state.Checkpoint!.HumanActions ?? [])
        {
            _scribe.RestoreHumanAnswer(state, action);
            if (action.Decision?.Decision == SquadHumanDecision.Stop)
            {
                state.Checkpoint.HumanStopRequested = true;
            }
        }
    }

    internal async Task<IReadOnlyList<SquadCostDemandRow>> ForecastFederationDemandAsync(string request,
        string innerRunId, SquadConsumptionConfiguration? pricing, FederationArtifactRepair? repair,
        CancellationToken token)
    {
        var original = _scribe.Load() ?? throw new InvalidDataException("Initialize the registered member before aggregate forecasting.");
        _scribe.VerifyNativeMember(original);
        ReadEffectiveRosterRoots();
        var state = FederationPersistence.Copy(original);
        var checkpoint = state.Checkpoint?.RunId == innerRunId ? state.Checkpoint :
            CreateFederationCheckpoint(innerRunId, request);
        if (original.Checkpoint is not null && original.Checkpoint.RunId != innerRunId)
        {
            throw new InvalidDataException("Another member run is active; it cannot become this meta-run's cost demand.");
        }
        state.Checkpoint = checkpoint;
        state.Consumption = pricing is null ? null : ConsumptionMath.Copy(pricing);
        if (_options.ModelRouting is { } desired &&
            state.ModelRouting is { } current && !SamePolicy(desired, current))
        {
            throw new InvalidDataException("Confirm the member's changed model policy before aggregate forecasting.");
        }
        state.ModelRouting ??= _options.ModelRouting is null ? null : CopyModelRouting(_options.ModelRouting);
        var owner = repair?.Role ?? Roster.Members.FirstOrDefault(member => member.DeliverableRoot is not null &&
            member.Role is not ("researcher" or "lead" or "tester" or "scribe" or "intake-validator"))?.Role
            ?? throw new InvalidDataException("A selected autopilot member has no actual producing owner.");
        checkpoint.Routing ??= new(request, owner, _options.InputPaths.ToArray(), true, _options.IncludeRaiInCouncil,
            repair is null ? SquadRunScope.Delivery : SquadRunScope.ArtifactRepair);
        checkpoint.FederationRepair = repair;
        checkpoint.Readiness ??= new()
        {
            TopicId = "federation-" + innerRunId,
            ScopeSha256 = HumanActions.Hash(request),
            RecordedAt = _options.TimeProvider.GetUtcNow(),
            HumanPresent = _options.HumanPresent,
        };
        checkpoint.Readiness.DiscoveryDepth ??= _options.Discovery ?? "skip";
        checkpoint.Readiness.Inputs = _options.InputPaths.Select(path => new SquadArtifactEvidence("input", path, _files.HashFile(path))).ToList();
        using var context = new RunContext(this, state, checkpoint, checkpoint.Evidence.Count > 0, token) { _costPreview = true };
        return await context.ForecastDemandAsync(owner).ConfigureAwait(false);
    }

    private sealed partial class RunContext
    {
        internal bool _costPreview;

        internal ValueTask<bool> FederationApprovalAsync(SquadApprovalRequest request,
            IReadOnlyList<string>? costSlots, CancellationToken cancellationToken) =>
            RequestApprovalAsync(request, costSlots, cancellationToken);

        internal Task<string> FederationQuestionAsync(SquadQuestion question) =>
            AskQuestionAsync(question, reuseConfirmed: true);

        private void ApplyFederationRevision()
        {
            if (runtime._options.FederationInvocation?.Revision is not { } revision ||
                (state.GateRecords ?? []).Any(record => record.Id == revision.ApprovalRecordId))
            {
                return;
            }
            FederationExecution.VerifyHumanRevision(runtime._files, runtime.ArtifactFingerprint, revision);
            if (revision.InnerRunId != runId || checkpoint.Mode is not { } mode ||
                revision.Stage is not ("produce" or "review") ||
                revision.Stage == "produce" && (mode.ProducingIteration == 2 || mode.ValidationRecords.Count > 1))
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "The scoped federation change cannot reset an inner validator cap or reopen another run.");
            }
            runtime._scribe.RecordGate(state, revision.ApprovalRecordId, "Federation Human Changes",
                Readiness.TopicId, Readiness.ScopeSha256, revision, "* Authenticated original hub decision; exact member/stage scope.");
            checkpoint.HumanRevision++;
            checkpoint.ApprovedGates.Clear();
            checkpoint.Cost!.Approval = null;
            checkpoint.Cost.Revision++;
            mode.FinalValidated = false;
            if (revision.Stage == "produce")
            {
                mode.HumanChangesPending = true;
                mode.ProducingChangeActionId = revision.Origin.Binding.Id;
                mode.ProducingIteration = 2;
                mode.ValidationComplete = false;
            }
            var reviews = checkpoint.Evidence.Where(item => item.Stage == "review").Select(item => item.DispatchId).ToHashSet(StringComparer.Ordinal);
            checkpoint.Evidence.RemoveAll(item => reviews.Contains(item.DispatchId));
            _evidence.RemoveAll(item => reviews.Contains(item.DispatchId));
            foreach (var id in reviews)
            {
                checkpoint.SessionBindings.Remove("dispatch:" + id);
            }
            runtime._scribe.Save(state);
        }

        internal async Task<SquadCostDemandRow[]> ForecastDemandAsync(string owner)
        {
            Scope = checkpoint.Routing!.Scope;
            var role = ResolveRole(owner);
            var charter = runtime.Catalog.ResolveAgent(role);
            var stage = checkpoint.FederationRepair?.Stage ?? "produce";
            var route = await ResolveModelAsync(role, charter, stage, 60_000).ConfigureAwait(false);
            return await BuildCostDemandAsync(role, charter, stage, route, null, 0).ConfigureAwait(false);
        }

        private async Task<SquadRunResult> RunFederationArtifactRepairAsync()
        {
            var repair = checkpoint.FederationRepair!;
            if (repair.Stage is not ("research" or "plan" or "produce" or "review") ||
                repair.ArtifactName != "primary" || checkpoint.Effects.Any(effect => effect.Status == "in-flight"))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "Only a registered primary producing stage can be repaired; reconcile uncertain effects first.");
            }
            Scope = SquadRunScope.ArtifactRepair;
            var role = ResolveRole(repair.Role);
            ValidateRoot(role);
            checkpoint.Routing ??= new(request, role.Role, runtime._options.InputPaths.ToArray(), false, false, Scope,
                RouteRationale: "Only the named producer stage is repaired; no consumer re-derivation.");
            checkpoint.InputBindings = runtime._options.InputPaths.Select(path =>
                new SquadArtifactEvidence("input", path, runtime._files.HashFile(path))).ToList();
            PrepareReadiness(checkpoint.Routing);
            if (runtime._options.Mode == "interactive")
            {
                await ApproveAsync(new(SquadApprovalKind.Implementation,
                    "Repair only the producer's named stage. Original stages, output/effect receipts and spend remain recorded.",
                    role.Role)).ConfigureAwait(false);
            }
            var task = "Repair only this producer-owned stage and its primary artifact. " +
                "Do not re-run other stages or re-derive another member's requirements. " +
                "The federation will verify the new artifact before continuing the same consumer turn.\n" +
                JsonSerializer.Serialize(repair) + "\nExplicit read-only inputs:\n" +
                string.Join("\n", runtime._options.InputPaths);
            var evidence = await DispatchAsync(role.Role, repair.Stage, task, 1).ConfigureAwait(false);
            VerifyEvidence();
            if (evidence.BlockingIssues > 0 || evidence.Verdict != "Completed" || evidence.Risk == "High")
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "The producer stage repair did not clear its actual artifact gate.");
            }
            if (repair.Stage == "produce")
            {
                ValidateProducedDispatch(evidence);
            }
            return new(runId, SquadRunStatus.ArtifactCompleted,
                "Only the named producer artifact stage completed; no full delivery or release is claimed.",
                Evidence.ToArray(), Scope);
        }
    }
}
