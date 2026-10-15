using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private static SquadCouncilLens[] CouncilLenses(string text, string? owner, bool rai,
        IReadOnlyList<string>? producingOwners = null, bool implementationValidation = false)
    {
        var rules = new (string Lens, string Role, string Pattern, string[] Owners)[]
        {
            ("Architecture", "architect", @"\b(architect\w*|infrastructure|topology|component\w*|integration\w*|system design|landing.zone|bicep|terraform)\b",
                ["architect", "azure-architect", "aws-architect", "iac-author"]),
            ("Security", "security", @"\b(secur\w*|auth|authentication|authorization|identity|secret\w*|credential\w*|encrypt\w*|network exposure|regulated data|sensitive data|threat\w*)\b",
                ["security", "privacy", "supply-chain", "vuln-manager"]),
            ("Cost", "cost-manager", @"\b(cost\w*|budget\w*|pricing|finops|billable|sku\w*|resize\w*|provision\w*)\b",
                ["cost-manager"]),
            ("Product-fit", "product-owner", @"\b(product\w*|user.facing|acceptance criteria|roadmap|user experience|customer\w*|feature\w*|scope change|onboarding)\b",
                ["product-owner", "analyst", "designer"]),
            ("RAI", "rai", @"\b(ai|ml|llm\w*|rai|responsible.ai|machine learning|training data|model selection|agent autonomy)\b",
                ["rai", "prompt-engineer"]),
        };
        return rules.Select(rule =>
        {
            var match = Regex.Match(text, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            var roleOwned = rule.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase) ||
                (producingOwners ?? []).Any(producingOwner => rule.Owners.Contains(producingOwner, StringComparer.OrdinalIgnoreCase)) ||
                implementationValidation && rule.Role == "architect" &&
                    (owner == "developer" || producingOwners?.Contains("developer", StringComparer.Ordinal) == true);
            var needed = match.Success || roleOwned || rule.Role == "rai" && rai;
            return new SquadCouncilLens(rule.Lens, rule.Role, needed,
                match.Success ? "Current scope cue: " + match.Value :
                roleOwned ? "The confirmed focus or verified plan's producing owners work in this lens." :
                rule.Role == "rai" && rai ? "Explicit responsible-AI coverage." :
                "No current scope cue or owning-role responsibility for this lens.");
        }).ToArray();
    }

    private sealed record DiscoveryGateOutcome(string Depth, string OptIn,
        IReadOnlyList<SquadArtifactEvidence> Inputs, SquadDiscoveryFindings? Findings,
        IReadOnlyList<SquadDispatchEvidence> Evidence, string Handoff,
        IReadOnlyList<SquadDiscoveryFindings>? PendingFindings = null);

    private sealed record IntakeGateOutcome(string Owner, SquadRunScope Scope,
        IReadOnlyList<SquadArtifactEvidence> Inputs, SquadDispatchEvidence Evidence,
        SquadIntakeFindings Findings, string ReportedVerdict, string Verdict, int Cycles,
        bool PermitsDownstreamDispatch, string? ReusedFrom = null);

    private sealed partial class RunContext
    {
        private ReadinessCheckpoint Readiness => checkpoint.Readiness
            ?? throw new InvalidDataException("Missing run-bound readiness checkpoint.");
        private string ReadinessRunKey => runId + (checkpoint.HumanRevision == 0 ? "" :
            "-human-revision-" + checkpoint.HumanRevision.ToString(CultureInfo.InvariantCulture));

        private string RequestedDiscovery()
        {
            var match = Regex.Match(request, @"(?<![\p{L}\p{N}])discovery\s*=\s*(?<depth>\S+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var value = runtime._options.Discovery ?? (match.Success ? match.Groups["depth"].Value : null);
            if (value is not (null or "quick" or "standard" or "deep" or "skip"))
            {
                throw _guard.Stop(SquadRunStatus.InputRequired, "Discovery must be quick, standard, deep or skip.");
            }
            return value ?? "";
        }

        private void PrepareReadiness(SquadRoutingProposal routing)
        {
            var discovery = RequestedDiscovery();
            var scopeHash = ProjectFiles.Hash(JsonSerializer.Serialize(new
            {
                routing.Request,
                routing.ProducingRole,
                routing.Scope,
                Inputs = checkpoint.InputBindings
                    .Where(input => routing.InputPaths.Contains(input.Path, StringComparer.Ordinal)).ToArray(),
                runtime._options.TopicId,
            }));
            if (checkpoint.HumanReadinessRevision > 0)
            {
                scopeHash = ProjectFiles.Hash(scopeHash + ":" + checkpoint.HumanReadinessRevision.ToString(CultureInfo.InvariantCulture));
            }
            var topic = runtime._options.TopicId ?? "topic-" + ProjectFiles.Hash(request)[..16].ToLowerInvariant();
            if (checkpoint.Readiness is { } saved)
            {
                if (saved.ScopeSha256 != scopeHash || saved.DiscoveryInput != discovery || saved.HumanPresent != runtime._options.HumanPresent ||
                    saved.TopicId != topic ||
                    saved.CouncilInputSha256 != (runtime._options.Council is null ? null :
                        ProjectFiles.Hash(JsonSerializer.Serialize(runtime._options.Council))))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "The resumed discovery/council configuration changed; previous gates cannot authorize it.");
                }
                return;
            }
            checkpoint.Readiness = new()
            {
                TopicId = topic,
                ScopeSha256 = scopeHash,
                DiscoveryInput = discovery,
                HumanPresent = runtime._options.HumanPresent,
                Inputs = checkpoint.InputBindings.ToList(),
                RecordedAt = runtime._options.TimeProvider.GetUtcNow(),
                CouncilInputSha256 = runtime._options.Council is null ? null :
                    ProjectFiles.Hash(JsonSerializer.Serialize(runtime._options.Council)),
                IntakeCycles = checkpoint.HumanPriorIntakeCycles,
            };
            runtime._scribe.Save(state);
        }

        private SquadGateRecord? Gate(string? id) => id is null ? null :
            state.GateRecords?.SingleOrDefault(record => record.Id == id)
                ?? throw new RuntimeHistoryIntegrityException("history-integrity: a referenced readiness verdict is missing.");

        private static bool SameInputs(IReadOnlyList<SquadArtifactEvidence> first, List<SquadArtifactEvidence> second) =>
            first.Count == second.Count && first.All(input => second.Any(other =>
                other.Path.Equals(input.Path, ProjectFiles.PathComparison) && other.Sha256 == input.Sha256));

        private void BindInput(SquadArtifactEvidence input)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                runtime._scribe.VerifyArtifact(input);
                var binding = input with { Sha256 = runtime._files.HashFile(input.Path) };
                if (!Readiness.Inputs.Any(existing => existing.Path.Equals(input.Path, ProjectFiles.PathComparison)))
                {
                    Readiness.Inputs.Add(binding);
                    checkpoint.InputBindings.Add(binding);
                }
                runtime._scribe.Save(state);
            }
        }

        private static string[] DiscoveryRoles(string depth) => depth switch
        {
            "quick" => ["analyst"],
            "standard" => ["designer", "analyst"],
            "deep" => ["designer", "challenger", "experimenter", "analyst"],
            _ => [],
        };

        private bool DiscoveryEligible(SquadRoutingProposal routing) =>
            runtime._options.Profile is "product" or "full" &&
            runtime.Roster.Resolve("analyst") is not null && runtime.Roster.Resolve("intake-validator") is not null &&
            Readiness.Inputs.Count == 0 && routing.Scope is SquadRunScope.Delivery or SquadRunScope.Plan &&
            Regex.IsMatch(request, @"\b(goal|outcome|improve|reduce|increase|we need|we want|better|explore|opportunit\w*)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) &&
            !Regex.IsMatch(request, @"\b(fix|pin|rename|retry|upgrade|patch|remove)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private async Task RunDiscoveryAsync(SquadRoutingProposal routing)
        {
            if (Readiness.DiscoveryRecordId is not null)
            {
                _ = Gate(Readiness.DiscoveryRecordId);
                return;
            }
            if (!Readiness.HumanPresent)
            {
                DecisionOnce($"## Discovery Unavailable {runId}\nDiscovery was not offered because this run is unattended." +
                    (Readiness.DiscoveryInput.Length == 0 ? "" : " The explicit discovery input was ignored."));
                Readiness.DiscoveryDepth = "skip";
                runtime._scribe.Save(state);
                return;
            }
            if (Readiness.DiscoveryInput == "skip")
            {
                Readiness.DiscoveryDepth = "skip";
                Readiness.DiscoveryOptIn = "explicit-input";
                RecordDiscovery([], null, [], "skipped");
                return;
            }
            if (routing.Scope is SquadRunScope.Research or SquadRunScope.Review || routing.InputPaths.Count > 0)
            {
                if (Readiness.DiscoveryInput.Length > 0 && Readiness.DiscoveryInput != "skip")
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired,
                        "Discovery requires a planning/delivery goal with no existing inputs; intake owns supplied requirement artifacts.");
                }
                return;
            }

            var prior = (state.GateRecords ?? []).Where(record => record.Kind == "Discovery Verdict" &&
                record.TopicId == Readiness.TopicId && record.ScopeSha256 == Readiness.ScopeSha256)
                .Select(record => (Record: record, Outcome: ReadinessContracts.Parse<DiscoveryGateOutcome>(record.Payload)))
                .LastOrDefault(item => item.Outcome.Handoff is "pending" or "intake" or "skipped");
            var priorValid = prior.Record is not null && prior.Outcome.Inputs.All(input =>
                File.Exists(runtime._files.Resolve(input.Path)) && runtime._files.HashFile(input.Path) == input.Sha256);
            if (Readiness.DiscoveryInput.Length == 0 && priorValid)
            {
                foreach (var evidence in prior.Outcome.Evidence)
                {
                    runtime._scribe.Verify(evidence);
                }
                Readiness.DiscoveryDepth = prior.Outcome.Depth;
                Readiness.DiscoveryOptIn = prior.Outcome.OptIn;
                Readiness.DiscoveryRecordId = prior.Record!.Id;
                foreach (var input in prior.Outcome.Inputs)
                {
                    if (!Readiness.Inputs.Any(existing => existing.Path.Equals(input.Path, ProjectFiles.PathComparison)))
                    {
                        Readiness.Inputs.Add(input);
                        checkpoint.InputBindings.Add(input);
                    }
                }
                runtime._scribe.Save(state);
                return;
            }
            if (Readiness.DiscoveryDepth is null)
            {
                if (Readiness.DiscoveryInput.Length > 0 || prior.Record is not null && !priorValid)
                {
                    Readiness.DiscoveryDepth = Readiness.DiscoveryInput.Length > 0 ? Readiness.DiscoveryInput : prior.Outcome.Depth;
                    Readiness.DiscoveryOptIn = Readiness.DiscoveryInput.Length > 0 ? "explicit-input" : prior.Outcome.OptIn;
                    if (!priorValid && prior.Record is not null)
                    {
                        DecisionOnce($"## Discovery Revalidation {runId}\nPrior brief inputs changed; retain the confirmed depth without repeating the offer.");
                    }
                }
                else if (DiscoveryEligible(routing))
                {
                    Readiness.DiscoveryDepth = await AskQuestionAsync(new("discovery",
                        "Choose discovery depth before any substantive work: quick (analyst brief), standard (framing and brief), " +
                        "deep (framing, objections, experiment and brief), or skip (use the settled request).",
                        ["quick", "standard", "deep", "skip"]), reuseConfirmed: true).ConfigureAwait(false);
                    if (Readiness.DiscoveryDepth is not ("quick" or "standard" or "deep" or "skip"))
                    {
                        Readiness.DiscoveryDepth = null;
                        throw _guard.Stop(SquadRunStatus.InputRequired, "Choose quick, standard, deep or skip; no discovery role was dispatched.");
                    }
                    Readiness.DiscoveryOptIn = Readiness.DiscoveryDepth == "skip" ? "offer-declined" : "offer-accepted";
                }
                else
                {
                    return;
                }
                runtime._scribe.Save(state);
            }
            if (Readiness.DiscoveryDepth == "skip")
            {
                RecordDiscovery([], null, [], "skipped");
                return;
            }

            var roles = DiscoveryRoles(Readiness.DiscoveryDepth);
            var missing = roles.Append("intake-validator").Where(role => runtime.Roster.Resolve(role) is null).ToArray();
            if (missing.Length > 0)
            {
                await ExtendGateRosterAsync(missing,
                    $"Discovery depth '{Readiness.DiscoveryDepth}' requires these exact roles: {string.Join(", ", missing)}. " +
                    "Declining does not silently reduce the depth; choose a shallower depth or skip on a new turn.").ConfigureAwait(false);
            }
            foreach (var role in roles)
            {
                PrecheckGateRole(role, DiscoveryTask(role));
            }
            try
            {
                foreach (var role in roles)
                {
                    await DispatchAsync(role, "discovery", DiscoveryTask(role), 1,
                        unitSlot: UnitSlot("discovery", role)).ConfigureAwait(false);
                }
                VerifyEvidence();
                var evidence = Evidence.Where(item => item.Stage == "discovery" && item.Depth == 1).ToArray();
                var analyst = evidence.Single(item => item.Role == "analyst");
                var findings = ReadinessContracts.Parse<SquadDiscoveryFindings>(analyst.StructuredReturn!.Payload);
                await ApproveAsync(new(SquadApprovalKind.Discovery,
                    "Confirm this exact discovery brief, chosen direction and all discarded reasons against the recorded human answers. " +
                    "An unrelated answer does not authorize an invented direction. This approves only the brief as input, not implementation.\n" +
                    JsonSerializer.Serialize(new { Brief = analyst.Artifacts, Findings = findings }),
                    "analyst")).ConfigureAwait(false);
                VerifyEvidence();
                foreach (var input in analyst.Artifacts ?? [])
                {
                    BindInput(input);
                }
                RecordDiscovery(evidence, findings, Readiness.Inputs.ToArray(), "pending");
            }
            catch (RuntimeStopException ex) when (ex.Status is SquadRunStatus.InputRequired or SquadRunStatus.Blocked)
            {
                var evidence = Evidence.Where(item => item.Stage == "discovery").ToArray();
                var id = ReadinessRunKey + "-discovery-incomplete-" + state.ConfirmedAnswers.Count.ToString(CultureInfo.InvariantCulture);
                var outcome = new DiscoveryGateOutcome(Readiness.DiscoveryDepth, Readiness.DiscoveryOptIn!, [],
                    null, evidence, ex.Status == SquadRunStatus.InputRequired ? "unanswered" : "abandoned",
                    checkpoint.DispatchStack.Where(dispatch => dispatch.PendingDiscoveryFindings is not null)
                        .Select(dispatch => dispatch.PendingDiscoveryFindings!).ToArray());
                runtime._scribe.RecordGate(state, id, "Discovery Verdict", Readiness.TopicId, Readiness.ScopeSha256,
                    outcome, RuntimeScribe.RenderDiscovery(outcome.Depth, outcome.OptIn, evidence, null, null, outcome.Handoff));
                throw;
            }
        }

        private string DiscoveryTask(string role) =>
            "Discovery pass only, at confirmed depth " + Readiness.DiscoveryDepth +
            ". Interview the human with ask_host, exactly one question per provider response. Never choose a material direction for them. " +
            (role == "designer" ? "Facilitated design-thinking session. Return framing and all chosen/discarded solution themes and reasons; write no artifact. " :
                role == "challenger" ? "Return severity-graded objections to the human-confirmed framing; write no artifact. " :
                role == "experimenter" ? "Return the riskiest assumption and cheapest proposed experiment; write no artifact. " :
                "Run the Discover pass, not a full PRD. Write the single brief with Problem, Why now, In scope, Out of scope, Success measure, " +
                "Options considered (including every discarded reason), Chosen direction (exact confirmed human answer), Assumptions and Open questions. ") +
            "Return a SquadDiscoveryFindings payload. Prior discovery findings:\n" +
            JsonSerializer.Serialize(Evidence.Where(item => item.Stage == "discovery" &&
                Array.IndexOf(DiscoveryRoles(Readiness.DiscoveryDepth!), item.Role) <
                Array.IndexOf(DiscoveryRoles(Readiness.DiscoveryDepth!), role)));

        private void RecordDiscovery(IReadOnlyList<SquadDispatchEvidence> evidence, SquadDiscoveryFindings? findings,
            SquadArtifactEvidence[] inputs, string handoff)
        {
            var outcome = new DiscoveryGateOutcome(Readiness.DiscoveryDepth!, Readiness.DiscoveryOptIn!, inputs, findings, evidence, handoff);
            var record = runtime._scribe.RecordGate(state, ReadinessRunKey + "-discovery", "Discovery Verdict",
                Readiness.TopicId, Readiness.ScopeSha256, outcome,
                RuntimeScribe.RenderDiscovery(outcome.Depth, outcome.OptIn, evidence, findings, inputs.Length == 0 ? null : inputs[0].Path, handoff));
            Readiness.DiscoveryRecordId = record.Id;
            runtime._scribe.Save(state);
        }

        private (SquadRosterEntry Entry, AgentCharter Charter, string Rationale) IntakeValidator()
        {
            var entry = ResolveRole("intake-validator");
            var baseInputs = Readiness.Inputs.Where(input => !state.DispatchRecords.Any(item =>
                item.Stage == "remediation" && (item.Artifacts ?? []).Any(artifact => artifact.Path == input.Path))).ToArray();
            var allBusiness = baseInputs.Length > 0 && baseInputs.All(input =>
                Regex.IsMatch(input.Path + "\n" + runtime._files.Read(input.Path),
                    @"\b(BRD|business requirements)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            var task = allBusiness ? "BRD input; business requirements completeness and clarity." : "PRD input; requirement completeness and clarity.";
            var selected = runtime.SelectAgent(entry, "", task);
            var authors = state.DispatchRecords.Where(item => (item.Artifacts ?? []).Any(artifact =>
                Readiness.Inputs.Any(input => input.Path.Equals(artifact.Path, ProjectFiles.PathComparison)))).Select(item => item.Agent);
            if (authors.Contains(selected.Charter.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "Intake requires a distinct validator, not the author of any scoped input.");
            }
            return (entry, selected.Charter, selected.Rationale);
        }

        private string RemediationRole() => Readiness.Inputs.Any(input => Regex.IsMatch(
            runtime._files.Read(input.Path), @"\b(user story|backlog item|work item)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) ? "product-owner" : "analyst";

        private async Task RunIntakeAsync(SquadRoutingProposal routing)
        {
            if (Readiness.Inputs.Count == 0 ||
                runtime._options.WatchInvocation is null && routing.Scope is SquadRunScope.Research or SquadRunScope.Review ||
                Readiness.IntakeComplete)
            {
                return;
            }
            if (runtime.Roster.Resolve("intake-validator") is null)
            {
                if (runtime._options.WatchInvocation is not null)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, runtime.MissingRoleDiagnostic("intake-validator"));
                }
                await ExtendGateRosterAsync(["intake-validator"],
                    "These requirement inputs need an independent intake-validator. Confirm this exact extension; no self-review or skipped gate is permitted.")
                    .ConfigureAwait(false);
            }
            var validator = IntakeValidator();
            IntakeGateOutcome? current = Gate(Readiness.IntakeRecordId) is { } saved
                ? ReadinessContracts.Parse<IntakeGateOutcome>(saved.Payload) : null;
            if (current is null)
            {
                var reusable = (state.GateRecords ?? []).Where(record => record.Kind == "Intake Readiness Verdict" &&
                    checkpoint.HumanReadinessRevision == 0)
                    .Select(record => (Record: record, Outcome: ReadinessContracts.Parse<IntakeGateOutcome>(record.Payload)))
                    .LastOrDefault(item => item.Outcome.PermitsDownstreamDispatch &&
                        item.Outcome.Owner == routing.ProducingRole && item.Outcome.Scope == routing.Scope &&
                        item.Outcome.Evidence.Agent == validator.Charter.Name && SameInputs(item.Outcome.Inputs, Readiness.Inputs));
                if (reusable.Record is not null)
                {
                    runtime._scribe.Verify(reusable.Outcome.Evidence);
                    current = reusable.Outcome with { Cycles = 0, ReusedFrom = reusable.Record.DecisionRef };
                    RecordIntake(current);
                    return;
                }
                current = await ValidateIntakeAsync(routing, validator, 0, null).ConfigureAwait(false);
            }
            while (!current.PermitsDownstreamDispatch)
            {
                if (current.Cycles < Readiness.IntakeCycles)
                {
                    current = await ValidateIntakeAsync(routing, validator, Readiness.IntakeCycles, current).ConfigureAwait(false);
                    continue;
                }
                var blocking = current.Findings.Issues.Where(issue => issue.Blocking).ToArray();
                if (blocking.Length == 0 || blocking.Any(issue => issue.RequiresHuman) ||
                    current.Findings.ClarifyingQuestions.Count > 0 || Readiness.IntakeCycles >= 2)
                {
                    var questions = current.Findings.ClarifyingQuestions.Concat(blocking.Where(issue => issue.RequiresHuman)
                        .Select(issue => issue.Description)).Distinct(StringComparer.Ordinal).ToArray();
                    using (runtime._scribe.SyncRoot.EnterScope())
                    {
                        foreach (var question in questions)
                        {
                            var dispatchId = runtime._options.HumanApprovalChannel is null ? null : current.Evidence.DispatchId;
                            if (!state.OpenQuestions.Any(item => item.Role == "intake-validator" && item.Question == question &&
                                item.DispatchId == dispatchId))
                            {
                                state.OpenQuestions.Add(new("intake-validator", question, DispatchId: dispatchId));
                            }
                        }
                        runtime._scribe.Save(state);
                    }
                    if (runtime._options.HumanApprovalChannel is not null && questions.Length > 0 &&
                        Readiness.IntakeCycles < 2 && current.Cycles < 2)
                    {
                        foreach (var question in questions)
                        {
                            _ = await AskQuestionAsync(new("intake-validator", question, DispatchId: current.Evidence.DispatchId),
                                reuseConfirmed: true).ConfigureAwait(false);
                        }
                        Readiness.IntakeCycles = current.Cycles + 1;
                        runtime._scribe.Save(state);
                        current = await ValidateIntakeAsync(routing, validator, Readiness.IntakeCycles, current).ConfigureAwait(false);
                        continue;
                    }
                    throw _guard.Stop(SquadRunStatus.Blocked, "Intake is Not-Ready. Resolve the recorded blocking/human questions; " +
                        "no assumptions or further remediation may bypass them.\n" + string.Join("\n", questions) +
                        "\nDecision: " + Gate(Readiness.IntakeRecordId)!.DecisionRef);
                }
                var cycle = Readiness.IntakeCycles + 1;
                var role = RemediationRole();
                if (runtime.Roster.Resolve(role) is null)
                {
                    await ExtendGateRosterAsync([role],
                        "This independent intake needs the actual input-shape owner '" + role +
                        "' to draft the missing content. Confirm the exact extension; no coordinator substitute is permitted.")
                        .ConfigureAwait(false);
                }
                _ = ResolveRole(role);
                var draft = await DispatchAsync(role, "remediation",
                    "Draft supplemental corrected input for the following stable blocking gaps. Do not edit the original input or infer human decisions. " +
                    "The human must confirm this draft before it becomes an input. Return SquadIntakeRemediation and write the actual draft.\n" +
                    JsonSerializer.Serialize(new { Cycle = cycle, Inputs = Readiness.Inputs, Findings = current.Findings }), 1,
                    unitSlot: $"intake-remediation:{cycle}:{role}").ConfigureAwait(false);
                await ApproveAsync(new(SquadApprovalKind.IntakeRemediation,
                    "Confirm this exact supplemental input and addressed gap identities. Original bytes remain immutable; " +
                    "this approves only the draft as an intake input, not implementation or external actions.\n" +
                    JsonSerializer.Serialize(new { Cycle = cycle, Draft = draft.Artifacts, draft.StructuredReturn }),
                    role)).ConfigureAwait(false);
                VerifyEvidence();
                using (runtime._scribe.SyncRoot.EnterScope())
                {
                    foreach (var input in draft.Artifacts ?? [])
                    {
                        runtime._scribe.VerifyArtifact(input);
                        if (!Readiness.Inputs.Any(existing => existing.Path.Equals(input.Path, ProjectFiles.PathComparison)))
                        {
                            var binding = input with { Sha256 = runtime._files.HashFile(input.Path) };
                            Readiness.Inputs.Add(binding);
                            checkpoint.InputBindings.Add(binding);
                        }
                    }
                    Readiness.IntakeCycles = cycle;
                    runtime._scribe.Save(state);
                }
            }
        }

        private async Task<IntakeGateOutcome> ValidateIntakeAsync(SquadRoutingProposal routing,
            (SquadRosterEntry Entry, AgentCharter Charter, string Rationale) validator, int cycle, IntakeGateOutcome? previous)
        {
            validator = IntakeValidator();
            var stage = cycle == 0 ? "intake" : "validation";
            var slot = cycle == 0 ? UnitSlot(stage, "intake-validator") : $"intake-validation:{cycle}:intake-validator";
            var evidence = await DispatchAgentAsync(validator.Entry, validator.Charter, stage,
                "Independently assess all five intake dimensions against the exact inputs and approved supplements. " +
                "Use stable issue identities across cycles; attribute every gap to exact input hashes. " +
                "Every non-blocking gap needs an explicit recorded assumption. Human-dependent gaps remain blocking. " +
                "Return SquadIntakeFindings. No edits, no self-review, no guessed answers.\n" +
                JsonSerializer.Serialize(new { Inputs = Readiness.Inputs, Previous = previous?.Findings, IntendedWork = routing.ProducingRole }),
                1, agentSelectionRationale: validator.Rationale, unitSlot: slot).ConfigureAwait(false);
            var findings = ReadinessContracts.Parse<SquadIntakeFindings>(evidence.StructuredReturn!.Payload);
            var verdict = evidence.BlockingIssues > 0 || findings.ClarifyingQuestions.Count > 0 ? "Not-Ready" : evidence.Verdict;
            if (evidence.Risk == "High")
            {
                verdict = "Not-Ready";
            }
            var permits = verdict is "Ready" or "Ready-With-Gaps" && evidence.BlockingIssues == 0;
            var current = new IntakeGateOutcome(routing.ProducingRole, routing.Scope, Readiness.Inputs.ToArray(),
                evidence, findings, evidence.Verdict, verdict, cycle, permits);
            RecordIntake(current);
            if (previous is not null && !permits)
            {
                var before = previous.Findings.Issues.Where(issue => issue.Blocking).Select(issue => issue.Id).ToHashSet(StringComparer.Ordinal);
                var after = findings.Issues.Where(issue => issue.Blocking).Select(issue => issue.Id).ToHashSet(StringComparer.Ordinal);
                if (!after.IsProperSubsetOf(before))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "Intake blocking gaps did not strictly shrink with stable identities. " +
                        "No further remediation is permitted. Decision: " + Gate(Readiness.IntakeRecordId)!.DecisionRef);
                }
            }
            return current;
        }

        private void RecordIntake(IntakeGateOutcome outcome)
        {
            var record = runtime._scribe.RecordGate(state, ReadinessRunKey + "-intake-" + outcome.Cycles.ToString(CultureInfo.InvariantCulture),
                "Intake Readiness Verdict", Readiness.TopicId, Readiness.ScopeSha256, outcome,
                RuntimeScribe.RenderIntake(outcome.Inputs, outcome.Evidence, outcome.Findings,
                    outcome.Verdict, outcome.Cycles, outcome.PermitsDownstreamDispatch));
            Readiness.IntakeRecordId = record.Id;
            Readiness.IntakeComplete = outcome.PermitsDownstreamDispatch;
            runtime._scribe.Save(state);
        }

        private async Task<SquadCouncilVerdict?> RunCouncilAsync(SquadRoutingProposal routing, SquadDispatchEvidence plan)
        {
            var source = request + "\n" + runtime._files.Read(plan.ArtifactPath);
            var lenses = CouncilLenses(source, routing.ProducingRole, routing.IncludesRai,
                checkpoint.Production?.Plan.Units.Select(unit => unit.Role).ToArray(), IsAutomaticWorkflow);
            var required = routing.RequiresCouncil || runtime._options.Mode == "autonomous" ||
                IsAutomaticWorkflow && checkpoint.Production?.Plan.Units.Count == 1 ||
                lenses.Count(lens => lens.Needed) >= 2 || lenses.Any(lens => lens.Role == "rai" && lens.Needed);
            if (!required && runtime._options.Council is null)
            {
                return null;
            }
            var scope = ProjectFiles.Hash(JsonSerializer.Serialize(new
            {
                Readiness.ScopeSha256,
                Inputs = Readiness.Inputs,
                Plan = CouncilPlanScope(plan),
                Research = Evidence.Where(item => item.Stage == "research" && item.Depth == 1)
                    .Select(item => item.ArtifactSha256).ToArray(),
            }));
            if (Readiness.CouncilRecordId is not null)
            {
                var saved = Gate(Readiness.CouncilRecordId)!;
                var savedVerdict = ReadinessContracts.Parse<SquadCouncilVerdict>(saved.Payload) with { DecisionRef = saved.DecisionRef };
                if (saved.ScopeSha256 != scope)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "The recorded council verdict scope changed; no old waiver or condition can authorize it.");
                }
                if (savedVerdict.Verdict == "Stop" && savedVerdict.OverrideReason is null)
                {
                    return await ConfirmCouncilOverrideAsync(savedVerdict).ConfigureAwait(false);
                }
                if (IsAutomaticWorkflow && MandatoryCouncilRisk(savedVerdict) is { } savedRisk)
                {
                    await EscalateModeRiskAsync(savedRisk, savedVerdict.Findings, savedVerdict).ConfigureAwait(false);
                }
                return savedVerdict;
            }
            var automatic = new SquadCouncilProposal(Readiness.TopicId, scope, lenses,
                lenses.Where(lens => lens.Needed).Select(lens => lens.Role).ToArray(),
                lenses.Where(lens => lens.Needed && runtime.Roster.Resolve(lens.Role) is null).Select(lens => lens.Role).ToArray(), []);
            if (Readiness.CouncilSelection is null && runtime._options.Council is null && Readiness.HumanPresent)
            {
                var waiver = (state.GateRecords ?? []).LastOrDefault(record => record.Kind == "Council Waiver" &&
                    record.TopicId == Readiness.TopicId && record.ScopeSha256 == scope);
                if (waiver is not null)
                {
                    var prior = ReadinessContracts.Parse<SquadCouncilVerdict>(waiver.Payload);
                    Readiness.CouncilSelection = new() { Roles = [], WaiverReason = prior.Proposal.Reason };
                    Readiness.CouncilProposal = prior.Proposal;
                    Readiness.CouncilConfirmed = true;
                    runtime._scribe.Save(state);
                }
            }
            if (Readiness.CouncilSelection is null)
            {
                var selection = runtime._options.Council is { } configured ? ReadinessContracts.Copy(configured) :
                    !IsAutomaticWorkflow && Readiness.HumanPresent && runtime._options.SelectCouncilAsync is { } select ? await select(automatic, token).ConfigureAwait(false) : new()
                    {
                        Roles = automatic.Roles.ToArray(),
                    };
                if (selection is null)
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "Choose or explicitly waive the task-fit council; no seat was dispatched.");
                }
                if (selection.Roles.Count == 0 && selection.WaiverReason is null && automatic.Roles.Count == 0)
                {
                    if (!Readiness.HumanPresent)
                    {
                        throw _guard.Stop(SquadRunStatus.ApprovalRequired,
                            "An unattended explicit council has no domain cue. Confirm its exact lens roles through the host; no question was asked.");
                    }
                    var role = await AskQuestionAsync(new("council",
                        "An explicit council was requested without a domain cue. Choose its relevant canonical lens role.",
                        ReadinessContracts.CouncilRoles), reuseConfirmed: true).ConfigureAwait(false);
                    selection.Roles = [role];
                }
                ReadinessContracts.ValidateCouncil(selection);
                var roles = selection.Roles.Count == 0 && selection.WaiverReason is null
                    ? automatic.Roles : selection.Roles.ToArray();
                var waived = automatic.Roles.Except(roles, StringComparer.Ordinal).ToArray();
                if (waived.Length > 0 && selection.WaiverReason is null)
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "Omitting a needed lens requires an explicit human waiver reason.");
                }
                var selectedLenses = lenses.Select(lens => roles.Contains(lens.Role, StringComparer.Ordinal)
                    ? lens with { Needed = true, Reason = lens.Needed ? lens.Reason : "Explicit human-proposed lens." }
                    : waived.Contains(lens.Role, StringComparer.Ordinal) ? lens with { Reason = "Waived: " + selection.WaiverReason } : lens).ToArray();
                Readiness.CouncilSelection = ReadinessContracts.Copy(selection);
                Readiness.CouncilProposal = new(Readiness.TopicId, scope, selectedLenses, roles,
                    roles.Where(role => runtime.Roster.Resolve(role) is null).ToArray(), waived, selection.WaiverReason);
                runtime._scribe.Save(state);
            }
            var proposal = Readiness.CouncilProposal!;
            if (proposal.ScopeSha256 != scope)
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "The plan or input scope changed; a prior council choice cannot be reused.");
            }
            if (runtime._options.WatchInvocation is not null &&
                (proposal.MissingRoles.Count > 0 || proposal.WaivedRoles.Count > 0 || proposal.Roles.Count == 0))
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "Watch cannot add or waive council roles. Add the named missing roles through a separate confirmed roster change: " +
                    string.Join(", ", proposal.MissingRoles));
            }
            if (!Readiness.HumanPresent && (proposal.MissingRoles.Count > 0 && runtime._options.HumanApprovalChannel is null ||
                proposal.WaivedRoles.Count > 0 || proposal.Roles.Count == 0))
            {
                throw _guard.Stop(SquadRunStatus.ApprovalRequired, "Unattended work cannot add or waive council lenses; confirm the named missing roles.");
            }
            if (!Readiness.CouncilConfirmed)
            {
                try
                {
                    if (proposal.MissingRoles.Count > 0)
                    {
                        await ExtendGateRosterAsync(proposal.MissingRoles,
                            "Confirm the task-fit council extension and exact retained roster. A decline waives only the council, not another gate.",
                            proposal).ConfigureAwait(false);
                    }
                    if (!IsAutomaticWorkflow && runtime._options.WatchInvocation is null ||
                        proposal.WaivedRoles.Count > 0 || proposal.Roles.Count == 0)
                    {
                        await ApproveAsync(new(SquadApprovalKind.Council,
                            "Confirm these task-fit parallel seats, missing/omitted lenses and any explicit scoped waiver. " +
                            "A decline waives only this council; risk, cost and consequential-action gates remain mandatory.",
                            Council: proposal)).ConfigureAwait(false);
                    }
                }
                catch (RuntimeStopException ex) when (ex.Status == SquadRunStatus.ApprovalDenied && Readiness.HumanPresent)
                {
                    _guard.ClearConfirmedCouncilDecline();
                    proposal = proposal with
                    {
                        Roles = [],
                        WaivedRoles = proposal.Lenses.Where(lens => lens.Needed).Select(lens => lens.Role).ToArray(),
                        Reason = "Council declined through the trusted human callback.",
                    };
                    Readiness.CouncilProposal = proposal;
                }
                Readiness.CouncilConfirmed = true;
                runtime._scribe.Save(state);
            }
            if (proposal.Roles.Count == 0)
            {
                return RecordCouncil(proposal, plan, [], "Waived");
            }
            foreach (var member in proposal.Roles)
            {
                var charter = PrecheckGateRole(member, CouncilTask(plan));
                if (charter.Name.Equals(plan.Agent, StringComparison.OrdinalIgnoreCase))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "An independent council seat cannot review its own authored plan. No partial council was dispatched.");
                }
            }
            if (proposal.Roles.Select(member => PrecheckGateRole(member, CouncilTask(plan)).Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != proposal.Roles.Count)
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "Council independence requires distinct concrete top-level seat agents; one agent cannot synthesize multiple lens findings.");
            }
            var findings = await DispatchCouncilAsync(proposal.Roles.ToList(), CouncilTask(plan)).ConfigureAwait(false);
            VerifyEvidence();
            var verdict = SynthesizeCouncil(findings);
            var result = RecordCouncil(proposal, plan, findings, verdict);
            if (IsAutomaticWorkflow && MandatoryCouncilRisk(result) is { } risk)
            {
                await EscalateModeRiskAsync(risk, findings, result).ConfigureAwait(false);
            }
            if (verdict == "Stop")
            {
                result = await ConfirmCouncilOverrideAsync(result).ConfigureAwait(false);
            }
            return result;
        }

        private string CouncilPlanScope(SquadDispatchEvidence plan)
        {
            var text = runtime._files.Read(plan.ArtifactPath);
            var paths = Evidence.SelectMany(item => item.Artifacts ?? [])
                .OrderByDescending(artifact => artifact.Path.Length);
            foreach (var artifact in paths)
            {
                text = text.Replace(artifact.Path, "<verified-" + artifact.Name + ">",
                    StringComparison.Ordinal);
            }
            return ProjectFiles.Hash(text);
        }

        private SquadArtifactEvidence[] DiscoverySources() => Evidence.Where(item => item.Stage == "discovery" && item.Role == "designer")
            .Select(item => new SquadArtifactEvidence("framing", item.ArtifactPath, runtime._files.HashFile(item.ArtifactPath))).ToArray();

        private async Task<SquadCouncilVerdict> ConfirmCouncilOverrideAsync(SquadCouncilVerdict result)
        {
            if (IsAutomaticWorkflow)
            {
                await EscalateModeRiskAsync(MandatoryCouncilRisk(result) ?? "The council stopped this exact work.",
                    result.Findings, result).ConfigureAwait(false);
            }
            var reason = Readiness.CouncilSelection!.OverrideReason;
            if (reason is null || result.Findings.Any(item => item.Risk == "High" ||
                ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Issues.Any(issue => issue.RequiresHuman)))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "Council verdict Stop: resolve the recorded blockers and mandatory risk gate. " +
                    "A council waiver cannot waive risk, cost or impactful actions. Decision: " + result.DecisionRef);
            }

            await ApproveAsync(new(SquadApprovalKind.CouncilOverride,
                "Confirm this exact low-risk council Stop override and all outstanding conditions. " +
                "It does not authorize cost overshoot or impactful actions. Reason: " + reason,
                Council: result.Proposal, CouncilVerdict: result)).ConfigureAwait(false);
            result = result with { OverrideReason = reason };
            var record = runtime._scribe.RecordGate(state, ReadinessRunKey + "-council-override", "Council Override",
                Readiness.TopicId, result.Proposal.ScopeSha256, result,
                "* Original Verdict: Stop\n* Decision: explicitly overridden by user\n* Reason: " + reason);
            Readiness.CouncilRecordId = record.Id;
            runtime._scribe.Save(state);
            return result with { DecisionRef = record.DecisionRef };
        }

        private AgentCharter PrecheckGateRole(string role, string task)
        {
            var entry = ResolveRole(role);
            var charter = runtime.SelectAgent(entry, request, task).Charter;
            ValidateRoot(entry);
            _ = runtime._scribe.AgentHistoryPath(charter.Name);
            if (!runtime._coordinator.DelegateAgents.Contains(charter.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    $"Coordinator does not declare required gate agent '{charter.Name}'. No partial gate was dispatched.");
            }
            return charter;
        }

        private string CouncilTask(SquadDispatchEvidence plan) =>
            "Independently cross-check this exact plan and its verified inputs from your owned lens. Return SquadCouncilFindings " +
            "with attributed stable issues, checkable stable conditions, dependencies, exact source hashes and suggested follow-ups. " +
            "Approve, Conditional, Concern or Block; risk Low, Medium or High. Do not invent another lens's opinion.\n" +
            JsonSerializer.Serialize(new { Plan = plan.Artifacts, Inputs = Readiness.Inputs, Council = Readiness.CouncilProposal });

        private SquadCouncilVerdict RecordCouncil(SquadCouncilProposal proposal, SquadDispatchEvidence plan,
            IReadOnlyList<SquadDispatchEvidence> findings, string verdict)
        {
            var conditions = findings.SelectMany(item =>
                ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Conditions
                    .Select(condition => new SquadCouncilCondition(item.Role, condition))).ToArray();
            var id = ReadinessRunKey + (verdict == "Waived" ? "-council-waiver" : "-council");
            var result = new SquadCouncilVerdict(verdict, "", proposal, findings, conditions);
            var record = runtime._scribe.RecordGate(state, id, verdict == "Waived" ? "Council Waiver" : "Council Verdict",
                Readiness.TopicId, proposal.ScopeSha256, result,
                verdict == "Waived" ? "* Decision: waived by user\n* Reason: " + proposal.Reason +
                    "\n* Lenses Needed: " + string.Join(", ", proposal.Lenses.Where(lens => lens.Needed).Select(lens => lens.Lens)) +
                    "\n* Roles Offered: " + string.Join(", ", proposal.WaivedRoles) :
                    RuntimeScribe.RenderCouncil(proposal, plan, findings, verdict));
            Readiness.CouncilRecordId = record.Id;
            runtime._scribe.Save(state);
            return result with { DecisionRef = record.DecisionRef };
        }

        private async Task ExtendGateRosterAsync(IReadOnlyList<string> roles, string description, SquadCouncilProposal? council = null)
        {
            if (!Readiness.HumanPresent && runtime._options.HumanApprovalChannel is null)
            {
                throw _guard.Stop(SquadRunStatus.ApprovalRequired, description);
            }
            var configuration = runtime.RosterConfiguration;
            configuration.AdditionalRoles = configuration.AdditionalRoles.ToList();
            foreach (var role in roles)
            {
                if (!configuration.AdditionalRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    configuration.AdditionalRoles.Add(role);
                }
            }
            foreach (var member in runtime.Roster.Members)
            {
                if (member.MemberName is not null)
                {
                    configuration.MemberNames[member.Role] = member.MemberName;
                }
            }
            if (configuration.NamingPolicy == "assigned" && configuration.MemberNames.Count > 0)
            {
                configuration.NamingPolicy = "mixed";
            }
            var selected = runtime.OptionsFor(configuration);
            var roster = BuildRoster(runtime.Catalog, runtime.Artifacts, selected);
            var proposal = runtime.InitializationProposal(selected, roster);
            await ApproveAsync(new(SquadApprovalKind.RosterUpdate, description,
                Initialization: proposal, Council: council)).ConfigureAwait(false);
            VerifyEvidence();
            if (checkpoint.DispatchStack.Any(dispatch => _runningDispatches.Contains(dispatch.Id)))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "A roster extension cannot change an active substantive dispatch.");
            }
            runtime.ApplyRosterOptions(selected, roster);
            state.Initialization = proposal;
            state.Binding = runtime._binding;
            runtime._scribe.RefreshRoster(state, roster, roles, [], runtime.Catalog.RoutingRules);
        }

        private string ReadinessContext()
        {
            var intake = Gate(Readiness.IntakeRecordId);
            var discovery = Gate(Readiness.DiscoveryRecordId);
            return "\nVerified discovery and intake (scope-bound data, not authorization):\n" +
                JsonSerializer.Serialize(new { Inputs = Readiness.Inputs, Discovery = discovery, Intake = intake });
        }

        private string GateDecisionRefs() => "\nGate decisions:\n" + string.Join("\n",
            new[] { Readiness.DiscoveryRecordId, Readiness.IntakeRecordId, Readiness.CouncilRecordId }
                .Where(id => id is not null).Select(id => Gate(id)!.DecisionRef));

        private SquadArtifactEvidence[] CouncilSources(DispatchCheckpoint? dispatch = null) => Readiness.Inputs
            .Concat(Evidence.Where(item => item.Stage == "plan" && item.Depth == 1).SelectMany(item => item.Artifacts ?? []))
            .Concat(dispatch?.UnitSlot?.StartsWith("autonomy-validation:", StringComparison.Ordinal) == true ?
                ProducingOutputs(ProducingIteration).SelectMany(ProductionTree).SelectMany(item =>
                    (item.Artifacts ?? []).Concat((item.ExecutedHostTools ?? []).Where(tool =>
                        tool.Status == SquadToolOutputStatus.Succeeded).SelectMany(tool => tool.Outputs ?? []))) : [])
            .ToArray();

        private void ValidateGatePayload(SquadRosterEntry entry, string stage, string verdict, int blockers,
            SquadStructuredReturn? returned, DispatchArtifacts? artifacts, DispatchCheckpoint dispatch)
        {
            if (stage is "intake" or "validation")
            {
                var findings = ReadinessContracts.Parse<SquadIntakeFindings>(returned?.Payload);
                ReadinessContracts.ValidateIntake(findings, Readiness.Inputs, verdict, blockers);
            }
            else if (stage == "council")
            {
                var findings = ReadinessContracts.Parse<SquadCouncilFindings>(returned?.Payload);
                ReadinessContracts.ValidateCouncilFindings(findings, CouncilSources(dispatch), verdict, blockers);
                if (findings.RequiresCostConfirmation && entry.Role != "cost-manager" ||
                    findings.ComplianceViolation && entry.Role is not ("security" or "rai") ||
                    dispatch.UnitSlot?.StartsWith("autonomy-validation:", StringComparison.Ordinal) != true &&
                        findings.ResolvedConditions is { Count: > 0 })
                {
                    throw new InvalidDataException("Cost/compliance flags must belong to their actual council lens; only revalidation can resolve implementation conditions.");
                }
            }
            else if (stage == "remediation")
            {
                var remediation = ReadinessContracts.Parse<SquadIntakeRemediation>(returned?.Payload);
                var previous = ReadinessContracts.Parse<IntakeGateOutcome>(Gate(Readiness.IntakeRecordId)!.Payload);
                var blocking = previous.Findings.Issues.Where(issue => issue.Blocking).Select(issue => issue.Id).ToHashSet(StringComparer.Ordinal);
                if (remediation.CorrectedIssueIds is null || remediation.CorrectedIssueIds.Count == 0 ||
                    remediation.CorrectedIssueIds.Distinct(StringComparer.Ordinal).Count() != remediation.CorrectedIssueIds.Count ||
                    remediation.CorrectedIssueIds.Any(id => !blocking.Contains(id)))
                {
                    throw new InvalidDataException("The remediation draft must identify actual stable outstanding blocking gaps.");
                }
                ReadinessContracts.Sources(remediation.Inputs, Readiness.Inputs);
                if (artifacts is null)
                {
                    throw new InvalidDataException("Intake remediation requires an actual supplemental draft, not a return alone.");
                }
            }
            else if (stage == "discovery")
            {
                var findings = ReadinessContracts.Parse<SquadDiscoveryFindings>(returned?.Payload);
                ReadinessContracts.Texts(findings.Assumptions, "discovery assumption");
                if (findings.OpenQuestions is null || findings.OpenQuestions.Count > 64 || findings.Options is null ||
                    findings.Options.Count > 32 || findings.Objections is null)
                {
                    throw new InvalidDataException("Discovery requires explicit bounded options, objections and open questions.");
                }
                foreach (var question in findings.OpenQuestions)
                {
                    ReadinessContracts.Text(question.Question, "discovery question");
                }
                if (findings.OpenQuestions.Any(question => question.Blocking))
                {
                    using (runtime._scribe.SyncRoot.EnterScope())
                    {
                        dispatch.PendingDiscoveryFindings = findings;
                        foreach (var question in findings.OpenQuestions.Where(question => question.Blocking))
                        {
                            if (!state.OpenQuestions.Any(item => item.Role == entry.Role && item.Question == question.Question))
                            {
                                state.OpenQuestions.Add(new(entry.Role, question.Question));
                            }
                        }
                        runtime._scribe.Save(state);
                    }
                    throw _guard.Stop(SquadRunStatus.InputRequired,
                        "Discovery has unanswered blocking human questions; resume to answer them individually, never guess the direction:\n" +
                        string.Join("\n", findings.OpenQuestions.Where(question => question.Blocking).Select(question => question.Question)));
                }
                foreach (var option in findings.Options)
                {
                    ReadinessContracts.Id(option.Id);
                    ReadinessContracts.Text(option.Description, "discovery option");
                    ReadinessContracts.Text(option.Reason, "option reason");
                    if (option.Outcome is not ("chosen" or "discarded"))
                    {
                        throw new InvalidDataException("Discovery option outcome must be chosen or discarded.");
                    }
                }
                if (findings.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != findings.Options.Count)
                {
                    throw new InvalidDataException("Discovery option identities must be unique.");
                }
                if (entry.Role is "designer" or "analyst")
                {
                    ReadinessContracts.Text(findings.Problem, "human problem framing");
                    ReadinessContracts.Text(findings.SuccessMeasure, "success measure");
                    if (findings.Options.Count(option => option.Outcome == "chosen") != 1 ||
                        findings.DirectionDecision is not { } decision || decision.RunId != runId ||
                        decision.Role is not ("analyst" or "designer") ||
                        !state.ConfirmedAnswers.Contains(decision))
                    {
                        throw new InvalidDataException("The chosen discovery direction must cite an actual confirmed human answer in this run.");
                    }
                    if (entry.Role == "analyst")
                    {
                        ReadinessContracts.Text(findings.WhyNow, "why now");
                        ReadinessContracts.Text(findings.InScope, "in scope");
                        ReadinessContracts.Text(findings.OutOfScope, "out of scope");
                        var brief = runtime._files.Read(artifacts?.PrimaryPath
                            ?? throw new InvalidDataException("Only the analyst must write the actual discovery brief."));
                        foreach (var section in new[] { "Problem", "Why now", "In scope", "Out of scope", "Success measure",
                            "Options considered", "Chosen direction", "Assumptions", "Open questions" })
                        {
                            if (!Regex.IsMatch(brief, @"(?im)^#{1,6}\s+" + Regex.Escape(section) + @"\s*$",
                                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                            {
                                throw new InvalidDataException("The analyst brief is missing its required " + section + " section.");
                            }
                        }
                        if (!brief.Contains(decision.Answer, StringComparison.Ordinal) ||
                            !brief.Contains(findings.Problem!, StringComparison.Ordinal) ||
                            !brief.Contains(findings.SuccessMeasure!, StringComparison.Ordinal) ||
                            !brief.Contains(findings.WhyNow!, StringComparison.Ordinal) ||
                            !brief.Contains(findings.InScope!, StringComparison.Ordinal) ||
                            !brief.Contains(findings.OutOfScope!, StringComparison.Ordinal) ||
                            findings.Assumptions.Any(assumption => !brief.Contains(assumption, StringComparison.Ordinal)) ||
                            findings.OpenQuestions.Any(question => !brief.Contains(question.Question, StringComparison.Ordinal)) ||
                            findings.Options.Any(option => !brief.Contains(option.Description, StringComparison.Ordinal) ||
                                !brief.Contains(option.Reason, StringComparison.Ordinal)))
                        {
                            throw new InvalidDataException("The brief must preserve the actual human choice and every explored/discarded reason.");
                        }
                        var previous = Evidence.Where(item => item.Stage == "discovery" && item.Role == "designer")
                            .Select(item => ReadinessContracts.Parse<SquadDiscoveryFindings>(item.StructuredReturn!.Payload)).LastOrDefault();
                        if (previous is not null && previous.Options.Any(option => !findings.Options.Contains(option)))
                        {
                            throw new InvalidDataException("The analyst brief cannot discard previously explored options or their reasons.");
                        }
                    }
                }
                if (entry.Role == "experimenter")
                {
                    ReadinessContracts.Text(findings.RiskiestAssumption, "riskiest assumption");
                    ReadinessContracts.Text(findings.ProposedTest, "proposed test");
                }
                foreach (var objection in findings.Objections)
                {
                    ReadinessContracts.Id(objection.Id);
                    ReadinessContracts.Text(objection.Description, "discovery objection");
                    if (objection.Dimension is not ("Low" or "Medium" or "High"))
                    {
                        throw new InvalidDataException("Discovery objections require Low, Medium or High severity.");
                    }
                    if (entry.Role == "challenger")
                    {
                        ReadinessContracts.ValidateIssues(findings.Objections, DiscoverySources(), blockers);
                    }
                    dispatch.PendingDiscoveryFindings = null;
                }
            }
        }
    }
}
