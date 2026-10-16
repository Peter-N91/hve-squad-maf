using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private sealed partial class RunContext
    {
        private sealed record DispatchWork(
            SquadRosterEntry Entry, AgentCharter Charter, string Task, string? Rationale, string? UnitSlot = null);

        private SquadProductionPlan PrepareProduction(SquadDispatchEvidence plan)
        {
            VerifyEvidence();
            var parsed = ProductionPlans.Parse(runtime._files.Read(plan.ArtifactPath), plan, checkpoint.Routing!.ProducingRole,
                !runtime._resources.RequiresSeparatePlanDetails, runtime._options.MaxDispatches,
                Readiness.Inputs.Select(input => input.Path).Concat(Evidence
                    .Where(item => item.Stage == "research" && item.Depth == 1).Select(item => item.ArtifactPath)).ToArray());
            if (parsed.Units.Any(unit => unit.Role == "backlog-executor") && !HasFinalizedBacklogInput())
            {
                if (!parsed.ExplicitDeliverables)
                {
                    var executor = parsed.Units.Single();
                    parsed = parsed with
                    {
                        Units =
                        [
                            new("backlog-handoff", "product-owner", [], [], [],
                                "Prepare the finalized backlog handoff from the verified research and approved lead plan. " +
                                "Include exact work items, acceptance criteria, dependencies and destination. Do not mutate a tracker."),
                            executor with { Dependencies = ["backlog-handoff"] },
                        ],
                    };
                }
                else if (parsed.Units.Where(unit => unit.Role == "backlog-executor").Any(unit =>
                    !unit.Dependencies.Any(dependency => parsed.Units.Any(source =>
                        source.Id == dependency && source.Role == "product-owner"))))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        "A backlog-executor unit requires a verified finalized product-owner handoff input or an explicit " +
                        "product-owner producing dependency. Replan that prerequisite; no tracker effect was authorized.");
                }
            }
            var owners = new Dictionary<string, string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var protectedInputs = checkpoint.InputBindings.Select(input => input.Path)
                .Concat(Readiness.Inputs.Select(input => input.Path))
                .Concat(Evidence.Where(item => item.Stage is "research" or "plan").SelectMany(item =>
                    new[] { item.ArtifactPath }.Concat((item.Artifacts ?? []).Select(artifact => artifact.Path)))).ToArray();
            var units = new List<SquadProducingUnit>();
            foreach (var unit in parsed.Units)
            {
                var entry = ResolveRole(unit.Role);
                if (entry.Role is "researcher" or "lead" or "tester" or "scribe" or "intake-validator" ||
                    entry.DeliverableRoot is null && RoleOutputContract.RequiredEffect(entry.Role) is null)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        $"Deliverable '{unit.Id}' has no substantive producing owner; no developer substitution was made.");
                }
                ValidateRoot(entry);
                var selected = runtime.SelectAgent(entry, request, unit.PlanContext);
                if (selected.Charter.DisableModelInvocation ||
                    !runtime._coordinator.DelegateAgents.Contains(selected.Charter.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        $"Producing owner '{selected.Charter.Name}' is not an installed permitted coordinator child.");
                }
                _ = runtime._scribe.AgentHistoryPath(selected.Charter.Name);
                var outputs = new List<string>();
                foreach (var output in unit.ProjectOutputs)
                {
                    if (output.Length > 1024 || output.Any(char.IsControl) || output.EndsWith('/') || output.EndsWith('\\'))
                    {
                        throw _guard.Stop(SquadRunStatus.Blocked, "A planned project output requires an exact contained file path.");
                    }
                    var resolved = runtime._files.Resolve(output);
                    var normalized = string.Join("/", output.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
                    if (Directory.Exists(resolved) || runtime._files.IsProtectedStatePath(normalized) ||
                        ProjectFiles.IsWithin(normalized, ".git") ||
                        protectedInputs.Any(path => runtime._files.Resolve(path).Equals(resolved, ProjectFiles.PathComparison)) ||
                        runtime._files.IsMemberRoot && !ProjectFiles.IsWithin(normalized, runtime._files.SquadRoot) &&
                            !ProjectFiles.IsWithin(normalized, "docs") && !ProjectFiles.IsWithin(normalized, "outputs"))
                    {
                        throw _guard.Stop(SquadRunStatus.Blocked, "A planned output escaped its scope or would replace protected evidence.");
                    }
                    if (owners.Keys.Any(path => path.Equals(resolved, ProjectFiles.PathComparison) ||
                        path.StartsWith(resolved + Path.DirectorySeparatorChar, ProjectFiles.PathComparison) ||
                        resolved.StartsWith(path + Path.DirectorySeparatorChar, ProjectFiles.PathComparison)))
                    {
                        throw _guard.Stop(SquadRunStatus.Blocked,
                            "Planned producing outputs collide; resolve exact ownership before any producer is dispatched.");
                    }
                    owners.Add(resolved, unit.Id);
                    outputs.Add(normalized);
                }
                if (outputs.Count > runtime._options.MaxArtifactsPerDispatch ||
                    parsed.ExplicitDeliverables && RoleOutputContract.RequiredEffect(entry.Role) == SquadToolEffect.ProjectWrite &&
                        outputs.Count == 0)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        $"Deliverable '{unit.Id}' needs bounded explicit project outputs for its required write effect.");
                }
                units.Add(unit with
                {
                    ProjectOutputs = outputs.ToArray(),
                    Agent = selected.Charter.Name,
                    AgentSelectionRationale = selected.Rationale,
                });
            }
            var proposal = parsed with { Units = units.ToArray() };
            if (checkpoint.Production is { } prior)
            {
                if (JsonSerializer.Serialize(prior.Plan) != JsonSerializer.Serialize(proposal))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        "The resumed plan-derived owners, outputs or dependencies changed; prior approval cannot authorize them.");
                }
                return prior.Plan;
            }
            checkpoint.Production = new() { Plan = proposal };
            runtime._scribe.Save(state);
            return proposal;
        }

        private bool HasFinalizedBacklogInput()
        {
            // Native Markdown receipts bind decoded text; input and federation authority bind original file bytes.
            bool Matches(string path, string hash, bool textReceipt) => checkpoint.InputBindings.Any(input =>
                runtime._files.Resolve(input.Path).Equals(runtime._files.Resolve(path), ProjectFiles.PathComparison) &&
                input.Sha256 == runtime._files.HashFile(input.Path) &&
                (textReceipt ? ProjectFiles.Hash(runtime._files.Read(input.Path)) : input.Sha256) == hash);
            if (runtime._options.FederationInvocation?.Inputs.Any(input =>
                input.ProducerRole == "product-owner" && input.ProducerStage == "produce" && input.DispatchId is not null &&
                Matches(input.Path, input.Sha256, textReceipt: false)) == true)
            {
                return true;
            }
            foreach (var run in state.Runs.Where(run => run.Status == SquadRunStatus.Completed))
            {
                var reviewed = run.Evidence.Any(evidence => evidence.Stage == "review" && evidence.Role == "tester" &&
                    evidence.Depth == 1 && evidence.Verdict == "Completed" && evidence.BlockingIssues == 0 && evidence.Risk != "High");
                if (!reviewed)
                {
                    continue;
                }
                foreach (var evidence in run.Evidence.Where(evidence => evidence.Stage == "produce" &&
                    evidence.Role == "product-owner" && evidence.Depth == 1 && evidence.Verdict == "Completed" &&
                    evidence.BlockingIssues == 0 && evidence.Risk != "High" &&
                    state.ModelCalls.Any(call => call.DispatchId == evidence.DispatchId && call.RunId == run.RunId)))
                {
                    var artifacts = evidence.Artifacts ?? [new("primary", evidence.ArtifactPath, evidence.ArtifactSha256)];
                    if (artifacts.Any(artifact => Matches(artifact.Path, artifact.Sha256, textReceipt: true)))
                    {
                        runtime._scribe.Verify(evidence, state);
                        foreach (var review in run.Evidence.Where(item => item.Stage == "review" && item.Depth == 1))
                        {
                            if (!state.ModelCalls.Any(call => call.DispatchId == review.DispatchId && call.RunId == run.RunId))
                            {
                                throw new RuntimeHistoryIntegrityException("history-integrity: the finalized backlog handoff review belongs to another run.");
                            }
                            runtime._scribe.Verify(review, state);
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        private async Task<SquadDispatchEvidence[]> RunProductionAsync(
            SquadDispatchEvidence plan, SquadCouncilVerdict? council)
        {
            var production = checkpoint.Production?.Plan ?? PrepareProduction(plan);
            if (ProducingIteration == 2 && checkpoint.Mode?.ValidationRecords.FirstOrDefault() is { } previous)
            {
                council = ReadinessContracts.Parse<AutonomousValidationOutcome>(Gate(previous)!.Payload).Verdict;
            }
            if (runtime._options.Mode == "autonomous" && council?.Verdict == "Waived")
            {
                RecordModeOutcome("council waived: loop ended");
                throw _guard.Stop(SquadRunStatus.Blocked, "The confirmed council waiver ends this autonomous turn before implementation.");
            }
            VerifyProductionGates(plan, council);
            var reviewer = ResolveRole("tester");
            var reviewAgent = runtime.SelectAgent(reviewer, request, "Independently review the plan's actual produced outputs.").Charter;
            ValidateRoot(reviewer);
            if (reviewAgent.DisableModelInvocation ||
                !runtime._coordinator.DelegateAgents.Contains(reviewAgent.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "Producing work requires an installed independent closing reviewer.");
            }
            if (!checkpoint.Production!.Confirmed)
            {
                await ApproveAsync(new(SquadApprovalKind.Implementation,
                    "Approve this exact plan-derived producing set, concrete owners, dependencies, project outputs and council conditions. " +
                    "Each consequential registered function still requires its own approval; no role work is performed inline.",
                    production.Units.Count == 1 ? production.Units[0].Role : null,
                    CouncilVerdict: council, ProductionPlan: ProductionPlans.Copy(production))).ConfigureAwait(false);
                VerifyProductionGates(plan, council);
                checkpoint.Production.Confirmed = true;
                runtime._scribe.Save(state);
            }
            var completed = new Dictionary<string, SquadDispatchEvidence>(StringComparer.Ordinal);
            foreach (var unit in production.Units)
            {
                if (CompletedProduction(unit) is { } prior)
                {
                    ValidateProducedDispatch(prior, unit);
                    completed.Add(unit.Id, prior);
                }
            }
            while (completed.Count < production.Units.Count)
            {
                VerifyProductionGates(plan, council);
                var eligible = production.Units.Where(unit => !completed.ContainsKey(unit.Id) &&
                    unit.Dependencies.All(completed.ContainsKey)).ToArray();
                if (eligible.Length == 0)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "No resolved dependency-ready producing owner remains.");
                }
                var work = new List<DispatchWork>();
                foreach (var unit in eligible)
                {
                    var handoffs = unit.Dependencies.Select(dependency =>
                    {
                        var source = completed[dependency];
                        ValidateProducedDispatch(source, production.Units.Single(candidate => candidate.Id == dependency));
                        return runtime._scribe.RecordProductionHandoff(state, production, dependency, unit.Id,
                            ProductionTree(source), runtime._options.TimeProvider.GetUtcNow(),
                            ProducingIteration == 1 ? 0 : ProducingIteration);
                    }).ToArray();
                    var entry = ResolveRole(unit.Role);
                    var charter = runtime.Artifacts.FindCharter(unit.Agent!)
                        ?? throw _guard.Stop(SquadRunStatus.Blocked, $"Producing owner '{unit.Agent}' is no longer installed.");
                    work.Add(new(entry, charter, ProductionTask(unit, plan, council, handoffs),
                        unit.AgentSelectionRationale,
                        production.ExplicitDeliverables || ProducingIteration == 2 ? ProducingSlot(unit) : null));
                }
                var outputs = await DispatchBatchAsync("produce", work).ConfigureAwait(false);
                VerifyProductionGates(plan, council);
                for (var index = 0; index < eligible.Length; index++)
                {
                    ValidateProducedDispatch(outputs[index], eligible[index]);
                    completed.Add(eligible[index].Id, outputs[index]);
                }
            }
            return production.Units.Select(unit => completed[unit.Id]).ToArray();
        }

        private void VerifyProductionGates(SquadDispatchEvidence plan, SquadCouncilVerdict? council)
        {
            VerifyEvidence();
            var research = Evidence.Where(item => item.Stage == "research" && item.Depth == 1).ToArray();
            if (research.Length != 1 || research[0].Verdict != "Completed" || research[0].BlockingIssues > 0 ||
                research[0].Risk == "High" || plan.Stage != "plan" || plan.Role != "lead" ||
                plan.Verdict != "Completed" || plan.BlockingIssues > 0 || plan.Risk == "High" ||
                checkpoint.Production?.Plan.PlanDispatchId != plan.DispatchId ||
                checkpoint.Production.Plan.PlanSha256 != plan.ArtifactSha256 ||
                !Readiness.IntakeComplete && Readiness.Inputs.Count > 0)
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "Every producing owner requires verified research, the current lead plan and current input readiness.");
            }
            runtime._scribe.Verify(research[0]);
            runtime._scribe.Verify(plan);
            if (Gate(Readiness.CouncilRecordId) is { } gate)
            {
                var recorded = ProducingIteration == 2 && checkpoint.Mode?.ValidationRecords.FirstOrDefault() is { } previous ?
                    ReadinessContracts.Parse<AutonomousValidationOutcome>(Gate(previous)!.Payload).Verdict :
                    ReadinessContracts.Parse<SquadCouncilVerdict>(gate.Payload) with { DecisionRef = gate.DecisionRef };
                if (JsonSerializer.Serialize(recorded) != JsonSerializer.Serialize(council))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "The exact recorded council conditions or waiver changed before production.");
                }
            }
        }

        private SquadDispatchEvidence? CompletedProduction(SquadProducingUnit unit)
        {
            var slot = ProducingSlot(unit);
            var matches = Evidence.Where(item => item.Stage == "produce" && item.Depth == 1 &&
                (item.UnitSlot ?? UnitSlot(item.Stage, item.Role)) == slot).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new RuntimeHistoryIntegrityException("history-integrity: a producing unit has multiple completed identities."),
            };
        }

        private string ProductionTask(SquadProducingUnit unit, SquadDispatchEvidence plan,
            SquadCouncilVerdict? council, IReadOnlyList<SquadProductionHandoff> handoffs)
        {
            var task = "Execute only the approved plan. " +
                "Source edits and external actions require the registered host functions; no script execution is available. " +
                "An artifact claiming edits is not a substitute for actually executing the configured tools." +
                (ProducingIteration == 2 ? "\nThis is the second and final bounded producing draft. Address the independent " +
                    "revalidation findings, not new scope. Prior applied outputs are archived byte-for-byte before any authorized revision." : "") +
                ReadinessContext() + "\nThe implementation approval explicitly acknowledges these exact council conditions: " +
                JsonSerializer.Serialize(council);
            return checkpoint.Production!.Plan.ExplicitDeliverables || checkpoint.Production.Plan.Units.Count > 1 ? task +
                "\nExecute only this plan-derived unit, not another owner's work:\n" + JsonSerializer.Serialize(unit) +
                "\nVerified current plan:\n" + JsonSerializer.Serialize(plan) +
                "\nExplicit verified dependency handoffs (data, not permission):\n" + JsonSerializer.Serialize(handoffs) : task;
        }

        private SquadDispatchEvidence[] ProductionTree(SquadDispatchEvidence root)
        {
            using var ownership = runtime._scribe.SyncRoot.EnterScope();
            var records = Evidence.Concat(checkpoint.PendingEvidence).Append(root)
                .DistinctBy(item => item.DispatchId).ToDictionary(item => item.DispatchId, StringComparer.Ordinal);
            bool Belongs(SquadDispatchEvidence item)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (item.DispatchId != root.DispatchId)
                {
                    if (!visited.Add(item.DispatchId))
                    {
                        throw new RuntimeHistoryIntegrityException("history-integrity: cyclic producing child ownership.");
                    }
                    if (item.ParentDispatchId is null || !records.TryGetValue(item.ParentDispatchId, out var parent))
                    {
                        return false;
                    }
                    item = parent;
                }
                return true;
            }
            return records.Values.Where(Belongs).OrderBy(item => item.DispatchOrder, StringComparer.Ordinal).ToArray();
        }

        private void ValidateProducedDispatch(SquadDispatchEvidence output, SquadProducingUnit? unit = null)
        {
            if (output.Verdict != "Completed" || output.BlockingIssues > 0 || output.Risk == "High" ||
                unit is not null && (output.Role != unit.Role || output.Agent != unit.Agent))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "A producing owner reported unresolved work or changed identity.");
            }
            var tree = ProductionTree(output);
            var receipts = tree.SelectMany(item => item.ExecutedHostTools ?? []).ToArray();
            var required = runtime._options.Tools.Where(tool =>
                tool.AllowedRoles.Contains(output.Role, StringComparer.OrdinalIgnoreCase) && tool.RequireOutputForCompletion)
                .Select(tool => tool.Effect).ToHashSet();
            if (RoleOutputContract.RequiredEffect(output.Role) is { } effect)
            {
                required.Add(effect);
            }
            foreach (var requiredEffect in required)
            {
                if (!receipts.Any(receipt => RoleOutputContract.Proves(output.Role, requiredEffect, receipt)))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        $"'{output.Role}' has no approved {requiredEffect} output receipt for this producing unit. " +
                        (output.Role == "presenter" ? "An actual host-produced .pptx is required; Markdown is not a rendered deck." :
                            "Another unit's receipt, a report, failure or dry-run cannot prove its work."));
                }
            }
            foreach (var path in unit?.ProjectOutputs ?? [])
            {
                if (!receipts.Where(receipt => receipt.Effect == SquadToolEffect.ProjectWrite &&
                    receipt.Status == SquadToolOutputStatus.Succeeded).SelectMany(receipt => receipt.Outputs ?? [])
                    .Any(artifact => runtime._files.Resolve(artifact.Path).Equals(runtime._files.Resolve(path), ProjectFiles.PathComparison)))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, $"Planned output '{path}' has no applied receipt from its actual owner.");
                }
            }
            runtime._scribe.VerifyOutputs(output, state);
        }

        private async Task<SquadDispatchEvidence[]> DispatchBatchAsync(string stage, IReadOnlyList<DispatchWork> work)
        {
            if (checkpoint.Cost?.CeilingUsd is not null)
            {
                var first = work[0];
                var route = await ResolveModelAsync(first.Entry, first.Charter, stage, 50_000).ConfigureAwait(false);
                var admission = await AdmitCostAsync(first.Entry, first.Charter, stage, route, null,
                    work.Select(item => item.UnitSlot ?? UnitSlot(stage, item.Entry.Role)).ToArray()).ConfigureAwait(false);
                if (admission.Decision == "approved-over-ceiling")
                {
                    var sequential = new List<SquadDispatchEvidence>();
                    foreach (var item in work)
                    {
                        sequential.Add(await DispatchAgentAsync(item.Entry, item.Charter, stage, item.Task, 1,
                            agentSelectionRationale: item.Rationale, unitSlot: item.UnitSlot).ConfigureAwait(false));
                    }
                    return sequential.ToArray();
                }
            }
            var dispatches = work.Select(item => DispatchAgentAsync(item.Entry, item.Charter, stage, item.Task, 1,
                deferCommit: true, agentSelectionRationale: item.Rationale, unitSlot: item.UnitSlot)).ToArray();
            try
            {
                return await Task.WhenAll(dispatches).ConfigureAwait(false);
            }
            finally
            {
                await DrainPipeliningAsync().ConfigureAwait(false);
                using (runtime._scribe.SyncRoot.EnterScope())
                {
                    var roots = checkpoint.DispatchStack.Where(dispatch => dispatch.ParentDispatchId is null &&
                        dispatch.Stage == stage && work.Any(item => dispatch.Role == item.Entry.Role &&
                            dispatch.UnitSlot == item.UnitSlot && dispatch.Task == item.Task))
                        .Select(dispatch => dispatch.Id).ToHashSet(StringComparer.Ordinal);
                    CommitPending(roots);
                }
            }
        }

        private SquadProducingUnit? ProductionUnit(DispatchCheckpoint? dispatch)
        {
            if (dispatch?.Stage != "produce" || checkpoint.Production is null)
            {
                return null;
            }
            var id = dispatch.Id;
            var role = dispatch.Role;
            var slot = dispatch.UnitSlot;
            var parent = dispatch.ParentDispatchId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (parent is not null)
            {
                if (!visited.Add(id))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: cyclic producing dispatch scope.");
                }
                id = parent;
                var pending = checkpoint.DispatchStack.SingleOrDefault(item => item.Id == id);
                var recorded = pending is null ? _evidence.Concat(checkpoint.PendingEvidence).SingleOrDefault(item => item.DispatchId == id) : null;
                role = pending?.Role ?? recorded?.Role ?? throw new RuntimeHistoryIntegrityException("history-integrity: missing producing parent.");
                slot = pending?.UnitSlot ?? recorded?.UnitSlot;
                parent = pending?.ParentDispatchId ?? recorded?.ParentDispatchId;
            }
            return checkpoint.Production.Plan.Units.SingleOrDefault(unit =>
                (ProductionPlans.Slot(checkpoint.Production.Plan, unit) == (slot ?? UnitSlot("produce", role)) ||
                    ProducingSlot(unit, 2) == slot));
        }

        private bool ProductionReadAllowed(string path, DispatchCheckpoint? dispatch)
        {
            using var ownership = runtime._scribe.SyncRoot.EnterScope();
            var resolved = runtime._files.Resolve(path);
            var unit = ProductionUnit(dispatch);
            if (unit is null)
            {
                return true;
            }
            var allowed = (state.ProductionHandoffs ?? []).Where(handoff =>
                handoff.RunId == runId && handoff.Consumer == unit.Id &&
                (handoff.Iteration == 0 ? 1 : handoff.Iteration) == ProducingIteration)
                .Select(handoff => handoff.Producer).ToHashSet(StringComparer.Ordinal);
            foreach (var owner in checkpoint.Production!.Plan.Units.Where(candidate => candidate.Id != unit.Id && !allowed.Contains(candidate.Id)))
            {
                if (owner.ProjectOutputs.Any(output =>
                    runtime._files.Resolve(output).Equals(resolved, ProjectFiles.PathComparison)))
                {
                    return false;
                }
                var slot = ProductionPlans.Slot(checkpoint.Production.Plan, owner);
                var revisions = _evidence.Concat(checkpoint.PendingEvidence).Where(item =>
                    item.Stage == "produce" && item.Depth == 1 &&
                    ((item.UnitSlot ?? UnitSlot(item.Stage, item.Role)) == slot || item.UnitSlot == ProducingSlot(owner, 2)));
                if (revisions.SelectMany(ProductionTree).Any(item =>
                    new[] { item.ArtifactPath, item.HistoryPath }.Concat((item.Artifacts ?? []).Select(artifact => artifact.Path))
                        .Concat((item.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? []).Select(artifact => artifact.Path))
                        .Any(output => runtime._files.Resolve(output).Equals(resolved, ProjectFiles.PathComparison))))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
