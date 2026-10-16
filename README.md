# HVE Squad for Microsoft Agent Framework

<img src="docs/assets/logo.svg" width="80" height="80" alt="HVE Squad MAF: connected framework nodes around a coordinator">

Embed [HVE Squad](https://github.com/Peter-N91/hve-squad) in your own .NET AI
application so end users can do evidence-backed business work: prepare an RFP
response, compare customer solution options, or draft a reviewed knowledge article.
Built on native Microsoft Agent Framework, this is a library intended for NuGet
consumption, not a Copilot development plugin or an application-building assistant.
Version **0.1.0-preview.1 is unpublished**; use source or a local feed today.

It runs real MAF agents using released HVE charters, skills, and instructions.
The supported app-facing workflow is evidence-gated work with research, planning,
plan-derived producing owners, human gates, and review. Existing `technical-writer`, `analyst`,
and `product-owner` specialists are not newly shipped sales, insurance, finance, or
support expert personas. Approved domain evidence grounds them; it does not
automatically confer domain expertise.

Start with the [RFP response workbench](docs/use-cases.html#rfp), including a
[complete `BidDraftService.DraftAsync` method](docs/use-cases.html#rfp-code).
For publication rather than local consumption, see the
[maintainer publishing procedure](docs/maintaining.html#publishing).
The library does not call the HVE Squad MCP service, fork Agent Framework, or
redistribute upstream charters in its package.

## Documentation site

The static GitHub Pages site is ready in `docs/`:
[overview](docs/index.html), [getting started](docs/getting-started.html),
[runtime reference](docs/usage.html), [hosting](docs/hosting.html),
[use cases](docs/use-cases.html), [federation](docs/federation.html), and
[contributing and maintaining](docs/maintaining.html).

**No separate squad server is required.** This is a .NET library distributed as a
NuGet package, not a Copilot plugin. It executes inside the consumer's MAF application.
That application supplies its model client, authentication, document ingestion and
rights checks, isolated case storage, human approval UI, job lifecycle, and any
retrieval or external tools. GitHub Pages hosts documentation only, never the agent
runtime. Optional service hosting and future NuGet publication are explained in the
[maintainer guide](docs/maintaining.html#publishing), not presented as shipped infrastructure.

To publish the site after reviewing and pushing these changes, set the repository's
**Settings > Pages > Source** to **GitHub Actions** and run **Deploy documentation**
from `main`. The actual workflow deployment output supplies the URL; adding these
files alone does not make the site live.

## Supported behavior

| Capability | Native implementation |
| --- | --- |
| Simple setup | `SquadRuntime.CreateAsync`, or `AddHveSquad` with configuration and the application's existing `IChatClient` |
| Release alignment | Defaults to the pinned official `v0.18.1` baseline, resolved to its verified exact commit before installation; `latest` is explicit candidate evaluation |
| Roles and profiles | Canonical profiles/packs or confirmed custom roles; provided/assigned/mixed/skipped names; read-only Init choices and confirmed roster refresh/removal preserve outputs, root overrides and historical spend |
| Concrete routing | Canonical intent rules prefer a specific contained phrase over its generic keyword; conflicting outcomes require a human owner choice. Primaries remain the default unless a catalog Selection Cue justifies an allowed alternate |
| Model policy | Persisted off/ranked/manual routing uses actual host/client bindings, assignment fit and consequence floors; bounded manual menus, explicit stale fallback/refusal and requested/effective/observed identity evidence |
| Governed delivery | Research, structurally verified single plan for `v0.18.1` (linked phase details for legacy resources), conditional intake/council, actual plan-derived producing owners, then review |
| Producing fan-out | Verified deliverable ownership and task/phase dependencies; independent units are parallel-eligible, dependent units receive exact Scribe-recorded handoffs, and every unit needs its own output/effect proof |
| Discovery and intake | Explicit quick/standard/deep/skip discovery or a once-per-topic eligible human offer; analyst-owned brief, independent typed intake, hash-bound reuse and at most two strictly shrinking supplemental-draft revalidations |
| Task-fit councils | Relevant independent seats, recorded omissions and confirmed persistent extensions; attributed issues/conditions, most-restrictive synthesis and scoped human waivers that cannot bypass risk, model-spend or consequential-action gates |
| Focused requests | Research-only, plan-only and review-only runs have distinct completion statuses |
| Skills | Native `AgentSkillsProvider` loads canonical names and references on demand, including APM-renamed directories; floor and host-selected instructions are injected without eagerly loading unrelated Squad/mode bodies |
| Registered scripts | Exact host-registered canonical resource/release/executor bindings use MAF `run_skill_script`, dispatch permissions, verified input/output scopes and existing effect/restart gates; the bounded process reference requires an explicit executable |
| Human interaction | Existing callbacks plus opt-in authenticated durable questions/approvals; exact actor/root/run/action/evidence binding, in-chat/GitHub issue adapters and outbound-only webhook notification |
| Evidence | Verifies outputs, append-only history bytes/order, protected session bindings and digest-sealed checkpoints before advancing; valid agent names retain their literal history filenames |
| Return-only roles | Intake and other applicable roles return typed findings, persisted as Scribe receipts rather than fake agent-written deliverables |
| Host tools | Explicit function registrations bind role permissions, effects and successful output evidence; trusted `ProjectWritePaths` prevents a second owner from writing an already-claimed output |
| Concurrency | Independent council seats, producing units and their declared workers execute with bounded provider concurrency; sessions/tools are dispatch-local and Scribe commits are serialized in dispatch order |
| Execution modes | Interactive stage gates remain the default; autonomous adds at most two independent revalidations, and autopilot sequences full delivery with mandatory specific-risk and final-human gates |
| Persistence | Native v2 release/root-bound checkpoints, MAF sessions, exact input/effect bindings and cumulative limits; explicit same-run resume skips completed stages |
| Federation lifecycle | Confirmed named Init, byte-preserving native-v2 Promotion and additive Expansion; verified registry/meta-routing, inherited contacts/naming, registered member roots and durable shared-output ownership |
| Federation execution | Confirmed dependency scripts, actual producer-first MAF runs, enumerated/hash-verified read-only handoffs, bounded stage-only recovery and aggregate autopilot with attributed gates and one consolidated final acceptance |
| Watch events | Authenticated label/command/committed-workflow opt-ins, structural event-member bootstrap, actual original inner execution/resume, inert payload controls, remote gates and provider-attested draft/review/comment/issue delivery |
| Read-only helpers | `SquadReports`, or `CreateReports` on the runtime/federation/DI factory, exports authorized native projections and optional local case facts; real Markdown/self-contained HTML, explicit PDF/Word capabilities/fallback and seven-section period-grounded governance |
| Consumer-local memory | `SquadRoleMemory` uses bounded role notes inside one native case, with sole-writer Scribe receipts, restart recovery, original-byte preservation and separately authorized optional model-context disclosure |
| Human-only learning | `SquadLearning` requires actual human initiation, explicit source rights, sanitized confirmation and next-free SL/TL rows; every fork, branch, atomic commit/push and PR has its own authenticated approval and verified provider receipt, or returns prepared-only commands |

**This is not full Copilot-host parity.** The runtime supports bounded interactive,
autonomous and autopilot single-squad operation with plan-derived producing ownership.
Federation lifecycle, producer-first dependencies and aggregate autopilot are
implemented, including opt-in native Watch triggers, read-only reports and
consumer-local memory/human-only learning. Reviewed candidate-upgrade tooling is
available; complete package/platform/conformance evidence remains unfinished.
Skill-script execution is available
only through explicit trusted host registration. Unknown
modes fail explicitly rather than silently skipping their contracts. The library
provides no unrestricted shell, deployment, tracker, web-search or model credentials.
Those capabilities need explicit host integration.

`Discovery` accepts `quick`, `standard`, `deep`, or `skip`; null permits the eligible
offer only for `product`/`full` goals without input artifacts. `TopicId` supplies a
stable scope identity. `HumanPresent = false` disables interviews, even with an
explicit depth. Quick dispatches the analyst; standard adds designer framing;
deep adds challenger objections and an experimenter before the analyst's brief.
Questions use `AskAsync` one at a time; an unanswered material choice stops.
Only the analyst writes the brief, including all discarded directions and reasons.
The separate `Discovery` approval confirms that exact brief against the real human
answers before it can become a grounding input.

Intake uses `SquadIntakeFindings` with five assessed dimensions, stable attributed
issues, exact input hashes, human questions and explicit non-blocking assumptions.
Ready labels cannot hide blockers. Unchanged inputs and validator identity may
reuse a prior ready verdict. Remediation produces an immutable supplemental draft
under the input-shape owner, confirmed by `IntakeRemediation` before independent
revalidation. Originals remain unchanged; human-only, nonshrinking and unresolved
cycle-two gaps stop rather than become assumptions.

`Council` can propose exact canonical seats and a `WaiverReason`;
`SelectCouncilAsync` provides an optional host selection UI. `Council` approval
confirms the task-fit roles and omissions; a trusted human decline records a scoped
waiver, not general permission. Missing seats require confirmed `RosterUpdate`.
`SquadCouncilFindings` carries actual issues, dependencies and source-bound stable
conditions into `Implementation` approval and the real producer's assignment.
`CouncilOverride` requires an explicit reason and separate approval; high-risk
or human-dependent blockers remain non-overridable. Gate responses expose exact
decision references. Optional durable channels preserve these exact gate facts; a
notification or model statement cannot approve them.

The canonical release identity and all 279 numbered assertion dispositions (including 15 letter-suffixed subassertions) live in
[the compatibility manifest](compatibility/hve-squad-release.json), which is embedded
in the package and exposed through `SupportedSquadRelease.Current`. Its conformance
status is separate from its release identity: `verified` covers mechanical native
conformance, not live-model quality or product deployment readiness. An official baseline tag that moves to another commit is rejected before
acquisition. Explicit latest/candidate inspection never promotes the manifest or
replaces release-bound existing state.

The [individual conformance catalogue](compatibility/hve-squad-conformance.json)
names each assertion's native adaptation, exact executable methods and minimum
case counts. The catalogue alone is not passing evidence. The compiled
`verify-conformance` command requires successful full-suite TRX, all actual
mode/profile observations and an isolated package/repack proof using the same
native DLL as the tests. Copilot-specific state formats, plugin hooks and UI
character limits are not silently presented as identical native formats.

`SquadRoot` can isolate named members under `.copilot-tracking/squad/members/`.
`SquadFederation` supplies the confirmed lifecycle and execution controller. Approved
`DeliverableRoots` overrides are included in initialization, and member file tools
cannot discover sibling work without exact host-approved input paths.
`ResumeAsync(runId)` continues a suspended native v2 run; submitting a new request
never silently restarts it. An uncertain consequential call returns
`ReconciliationRequired` until a trusted registration's `ReconcileAsync` proves
its outcome. Completed effects use verified protected results rather than replay.
Native v1 needs `AllowNativeV1Migration` and a separate `StateMigration` approval;
its original state and consumption/rate bytes are backed up and append-only
evidence is retained. The current consumption ledger remains a replace-derived
view of preserved and newly observed work.
An interrupted v1 run remains blocked rather than being guessed into a v2 resume.
Credentials remain host-owned. Authorized private case facts and payloads belong
in protected case storage, not public notifications or repository artifacts.

`MaxConcurrentDispatches` defaults to 4 simultaneous provider calls. Set it to 1
for a host client that requires sequential calls; nested workers do not hold a
parent's provider slot. Independent plan-owned producing units use this same bound.
`ReconcileEvidenceAsync()` verifies history and performs at most one deterministic
correction pass. Ledger-only defects produce explicit diagnostics without disabling
parallel dispatch; changed history/checkpoint/session evidence blocks dependent work.
Known partial Scribe appends can be completed from their exact protected byte binding,
without rewriting prior bytes or repeating a model invocation. Native Scribe operations
and actual coordinator calls are recorded separately; unknown model tokens/costs are
not converted into zero. `SquadRunResult.Effects` retains verified completed host calls
even when their producing dispatch did not finish.

`GetInitializationChoices()` offers the actual role menu, profiles, packs,
single-squad/federation choice, four naming policies and optional notification
contacts without writing state. A host `SelectInitializationAsync` UI can change
the proposal; `ApproveAsync` still confirms it before creation. Explicit settings
can be confirmed together without repeating interviews. Custom `Roles` always
include Scribe; omitting a methodology role disables that leg and causes a later
required dispatch to block. `Profile = "auto"` uses bounded read-only discovery;
an explicit profile remains authoritative.
`UpdateRosterAsync(configuration)` requires its own `RosterUpdate` approval.
Pack removal keeps roles also contributed by the profile/another pack, retains
prior outputs/history, and marks removed-member observations rather than losing spend.
`RosterConfiguration` returns a defensive configuration copy for another host instance.
Non-secret notification choices are captured in protected state. Capturing a channel
does not claim delivery: the registered adapter reports actual sent/failed/uncertain
status, separately from an authenticated human response.

`ModelRouting` is optional: null inherits a confirmed policy, and a new squad
defaults to `off`. Off preserves the legacy `ModelSelector`/provider behavior,
without deriving runnable models from a charter preference or catalog tier.
For `ranked` or `manual`, supply `AvailableModelsAsync` with the exact IDs and
caller-owned clients your host can actually run. Ranking reads the installed
dated catalog; 90 days is current, over 90 days uses a logged host static-tier
fallback. Manual questions offer at most eight evaluated candidates and validate
any other typed available ID. Advertised but uncatalogued picks are manual-only
and unevaluated; pricing requires an exact host tariff, not a catalog guess.
`GetModelRoutingChoicesAsync()` is state-read-only.
`UpdateModelRoutingAsync(configuration)` requires `ModelRouting` approval and
refuses recasting a suspended run. `AskAsync` questions may include `Choices`;
numeric answers and exact typed IDs work in the sample.
Specific pre-execution `SquadModelUnavailableException` rejections can narrow
availability and rerank; manual picks require another human choice. Ordinary
provider failures, and rejections after actual tool execution, are not retried.
Model mismatch diagnostics remain visible; a known below-floor observed model
blocks before its tool requests execute. Unknown identities never become a
fabricated mismatch. Model ranking's `Blended` price is not billed spend.

### Plan-derived producing work

The lead can declare multiple deliverables in its verified plan. The following is
an excerpt, not a substitute for the complete RPI plan and its task context:

```markdown
## Deliverables

| Id | Role | Tasks | Depends On | Project Outputs |
| --- | --- | --- | --- | --- |
| guide | technical-writer | P01-T01 | None | None |
| analysis | analyst | P01-T02 | None | None |
| summary | product-owner | P01-T03 | guide, analysis | None |
```

Each unique lower-kebab-case ID names a real producing unit. `Tasks` references
actual current-plan task IDs; their task and phase dependencies also order units.
Legacy two-file plans may use `None` for task IDs and declare unit dependencies
explicitly. An unowned prerequisite, unresolved role, dependency cycle or declared
output collision stops before production. There is no generic developer fallback.
A plan without this table retains the human-confirmed single focus owner, except
the released backlog prerequisite: without an original verified product-owner
handoff, backlog execution proposes `product-owner` first and `backlog-executor`
as its dependent unit. Both actual owners appear in implementation approval.
An explicit multi-owner plan must declare that dependency or supply a verified
reviewed handoff. A file merely claiming to be finalized is insufficient.
Native and federation handoffs retain their original producer provenance; none
grants tracker-write permission.
Autonomous validation still requires a genuinely independent council agent for a
producing lens. Use an installed canonical alternate author when appropriate;
without independent coverage the runtime stops rather than self-approving.

`Implementation` approval exposes the exact `SquadProductionPlan`: concrete
installed agents, task context, dependency edges and project outputs. Independent
owners are parallel-eligible; dependent owners start only after their prerequisite
outputs and literal agent histories are verified and the Scribe records an exact
`SquadProductionHandoff`. Nested workers remain bound to their parent's allowlist
and producing unit; they cannot impersonate another roster role. Review closes
the whole producing set after every required unit is verified.

The derived consumption ledger retains individual attempts and adds separate
`Attribution` and `Usage & Cost` tables in confirmed roster order. Only roles
with actual provider work appear. Orchestration distinguishes measured model
calls from zero-model native writes; unknown counters/costs remain unknown.
Estimated USD/0.01 credit equivalents are not billed provider credits.

`Project Outputs` lists exact comma-separated files written by registered host
functions, not automatically allocated Markdown artifacts. A write-producing unit
must declare its required files. Each function needs its own consequential
approval unless the host explicitly classifies it as safe for a noninteractive mode,
and a trusted `ProjectWritePaths` projection within that unit's approved
files. Separate units cannot supersede each other's outputs or borrow receipts,
even when they use the same role. Completed units survive same-run resume without
re-running their effects; changed source evidence or handoffs fail closed.

`SquadRunResult.ProductionPlan` and `ProductionHandoffs` expose actual plan/edge
evidence. `SquadToolOutput.Status` defaults to `Succeeded` for compatible existing
hosts. Report `Failed` or `DryRun` for a non-applied result: the actual returned
call remains visible in `Effects`, but carries no applied output evidence and
cannot make delivery `Completed`. A trustworthy host must attest real outcomes;
the runtime cannot sandbox a dishonest arbitrary executor.

### Bounded modes, not blanket permission

Set `SquadRuntimeOptions.Mode` to `interactive` (default), `autonomous`, or
`autopilot` in trusted host configuration. The live sample accepts `--mode` and
JSON `mode`; request text cannot select or change a mode. A suspended run keeps
its original mode, tool classifications, limits, release and root bindings.

Autonomous delivery selects task-fit council seats, implements only a permitting
verdict, and independently revalidates the actual output. There are at most two
revalidations. Stable issue divergence, Stop, high risk, human-required facts,
confirm-tier cost changes and compliance violations stop for a specific `RiskGate`.
Conditions cannot disappear: validators retain them or supply `ResolvedConditions`
with exact produced evidence and the check performed. Cycle two always reaches
an `AutonomousOutcome` human gate, even when converged. An acknowledgement of an
unresolved result does not make delivery complete or authorize a third cycle.

Autopilot requires a producing outcome and runs applicable discovery/intake,
research, the lead plan, task-fit council, real producing waves, bounded single-build validation
and independent review. Routine stage confirmations flow automatically, but
initialization, discovery direction, roster/model management, cost and specific
impactful actions keep their own authority. The exact final output always needs
`FinalOutcome` validation through a callback or authenticated durable channel.
Approval of that outcome is not deployment, push, merge or release permission.

Host-only `SquadToolRegistration.Impact` defaults to `Unspecified`, which remains
gated. `Safe` permits only bounded local writes with both `ProjectWritePaths` and
`OutputEvidence`, or explicitly safe external retrieval. External mutations never
become safe. `Action` classifies deployment, push, merge, migration, deletion,
secret rotation, compliance, cost confirmation or another irreversible operation;
each still needs a specific gate even if marked `Safe`. Interactive behavior keeps
its existing per-call approvals. Hosts must truthfully classify and constrain tools.

Only a second independently requested draft of the same producing unit, or the
same-plan fan-out rework explicitly requested by an authenticated final response, may revise
its prior applied output. The Scribe first archives and verifies the exact original
bytes with an immutable validation or authenticated-change receipt. Earlier effects/history remain
intact; unrelated units still cannot overwrite them. Final `/changes` after one
validation can reopen that second draft, with fresh cap and final decisions.
Authenticated final fan-out changes reopen only the unchanged planned units and
their dependency handoffs. Rework after the second producing iteration stops at
a new human risk gate, never resets a validation counter or replays earlier effects.

Autopilot overlaps safe research-to-plan and plan-to-council dispatches with one
deterministic Scribe handoff, when `MaxConcurrentDispatches > 1`. Every consuming
intake/council, cost, risk, effect, revalidation, review and final-outcome barrier
drains and verifies it. Pending spend reserves the admitted unrounded projection
times `3.0` once until the actual handoff replaces it. Approved overshoot stays
sequential. Ledger-only warnings do not disable pipelining; a pipelined
history-integrity failure latches it off and blocks consuming work.
`SquadRunResult.ModeOutcome` reports actual cycles, convergence, final validation
and verified overlaps; native mode summaries retain actual per-stage dispatch refs.

### Model consumption and cost admission

Supply `Consumption` with a verified `ObservedOn` date, non-secret `Source`,
estimator revision `2`, and exact model-ID tariffs in USD per million tokens.
Each `SquadModelRate.Standard` requires `Input`, `Cached`, `CacheWrite`, and
`Output`. Confirm a flat tariff with `FlatRateConfirmed = true`, or provide a
positive `LongContextThresholdTokens` and all four `LongContext` rates.
Do not apply Copilot prices to a different provider by default.

Actual attempts, including coordinator calls, failed/rejected calls and retired
members, retain their execution-time tariff and calibration provenance.
Provider-reported identity wins over the effective passed ID; a requested alias
or tier never becomes a price identity. Missing rates or historical snapshots
stay unknown. Optional host-only `TokenUsageSelector` supplies an exact token
split; otherwise measured totals with unknown cache use a conservative bound,
and missing telemetry uses a clearly labeled class-floor estimate per attempt.
Native Scribe operations invoke no model and have no model tokens.

`CostCeiling` accepts a finite positive USD decimal string or the literal `unset`.
Omission inherits only on the same run's `ResumeAsync`; a new run inherits no
ceiling unless the host explicitly supplies one. `SquadRunConfiguration` provides
per-invocation settings without changing the existing overloads.
Before dispatch, admission reserves the complete current mode boundary,
conservative pre-plan owners, declared workers, maximum intake remediation,
both permitted autonomous drafts/revalidations and final validation.
Eligible calibration applies once; unrounded rows are summed, admission reserves
`3.0` times that sum, and only displayed/persisted totals round to four decimals.

Calibration needs positive reconciled observations, a real reconciliation date
and the exact `ObservedOn|2` basis. Run a baseline without a ceiling, obtain its
authorized host billing delta, then call `ReconcileConsumptionAsync`. It asks
for `ConsumptionReconciliation` approval, rejects unknown/different-tariff
denominators, updates a clamped running mean once per run and keeps historical
charges unchanged. Reusing the same host tariff template preserves that
reconciliation across runtime instances; changed tariffs invalidate old calibration.

```csharp
var baseline = await squad.RunAsync("Research a bounded calibration case.");
await squad.ReconcileConsumptionAsync(new SquadConsumptionReconciliation(
    baseline.RunId, observedModelCostUsd, "host-billing-receipt"), cancellationToken);
var delivery = await squad.RunAsync("Deliver the bounded case.",
    new SquadRunConfiguration("10.00"), cancellationToken);
```

`observedModelCostUsd` must come from the host's actual billing observation,
not an invented example value. Configure the exact client/model and tariff
before creating `squad`. Uncalibrated baselines remain labeled estimates.
`SquadRunResult.Consumption` reports real attempt counts, unknowns and priced
estimates; `CostPreflight` exposes the durable decision and exact permitted slots.
These are model-spend estimates, not provider invoices or Azure workload budgets.

Confident admission equality permits work. `cannot-confirm` and spend at/above
the ceiling permit no later substantive unit and cannot be approved. A
`CostOvershoot` approval permits sequential unchanged-demand units only;
repricing, expanded context/demand, changed identity or changed ceiling requires
a new gate. In-flight units finish their verified handoff even if they cross
the ceiling. Pending unrecorded handoffs reserve their admitted unrounded cost
times `3.0`, then recorded spend replaces that reservation exactly once.
Unexpected or partial native snapshots fail compare-and-swap without overwriting
the preserved bytes. Producing fan-out uses this same admission authority, with exact
post-plan unit slots and sequential units for approved overshoot. Modes and safe
Scribe pipelining use the same authority; aggregate federation autopilot reuses its
exact inner demand and original spend. Watch and root transitions remain sequential.
Federation initialization uses no models; promotion preserves recorded
inner spend and honest unknowns without adding a second consumption ledger. Durable cost approval uses
the same exact round, pricing, spend, remaining-demand and sequential-unit authority.

For an RFP job, the result is a cited Markdown draft with gaps and review evidence,
not a Word/PDF export, a pricing engine, compliance proof, or a submitted bid.
`Completed` does not authorize commercial, legal, or security commitments.
Delivery/plan requests with `InputPaths` require intake; the released intake seat
is the PRD Quality Reviewer, not a generic business-document validator. Evaluate
that fit and surface blockers rather than assuming arbitrary RFPs will pass.
Commodity ticket routing and simple summaries may need only plain MAF; long-running
business agents still need product-specific authorization, data isolation, model-quality
evaluation and lifecycle integration beyond this preview.

The native Scribe is a deterministic persistence service, not a model agent. It owns
the state directory and writes evidence only for actual specialist dispatches. Its
versioned state format is separate from Copilot's, so existing Copilot squad state
cannot be adopted or overwritten implicitly.

### Federation lifecycle and producer-first orchestration

Use the existing DI factory's `CreateFederationAsync()` or
`SquadFederation.CreateAsync(client, options, source)`. Creation and
`GetSnapshot()` are read-only observations. The registry wins detection over a
top-level roster; a stray roster is reported for explicit reconciliation.

`InitializeAsync(members)` proposes and confirms required unique lower-kebab-case
names, actual rosters, routes and inherited contacts/naming before writing. On an
existing federation it expands additively; an existing single squad requires
`PromoteAsync(name, selectedTrackingDirectories)`. `GetPromotionCandidates()`
enumerates every actual tracking directory except the native squad root, including
ambiguous earlier workflows for an explicit keep/move choice.

Promotion requires quiescent verified native v2 state. Every source is copied and
verified before its individually known original is removed. Original mutable
state/roster bytes are archived; append-only history, decision and consumption
bytes, model pricing, authenticated receipts and output revisions are retained.
The current root/configuration index is rebased, while sealed exact historical
file mappings verify old receipts without granting an agent new access.
`ResumeLifecycleAsync(operationId)` continues only the same confirmed image after
an interruption. Changed inputs, competing writers and collisions block; there
is no auto-suffix, silent overwrite or Copilot-state importer.

`CreateMemberRuntimeAsync(name, readOnlyInputs)` opens the same MAF inner engine
with its original release, limits, host capabilities and mandatory gates. Members
cannot enumerate siblings or write outside their own roots, except owned
repository-wide `docs`/`outputs` paths. Those paths are claimed durably before
writing; a second member cannot borrow them. Actual `team.md` deliverable cells
are read at dispatch and retained on confirmed refresh/model updates.

`RunAsync(request, configuration)` selects an explicit target or the most-specific
meta-route; conflicting disjoint outcomes reach a human rather than a keyword-length
guess. `Members`, member-specific `Requests` and typed `Dependencies` produce an
exact confirmed execution script. Producers finish before consumers; explicitly
independent members may execute concurrently within the host bound.

The coordinator reads current roster cells, enumerates actual producer files and
verifies their native dispatch receipts before recording immutable read-only
handoffs. State/history is not a deliverable. An unrun producer runs first in the
same turn; a missing/stale artifact from a completed producer repairs only its
named producing stage. One bounded attempt per dependency is allowed, never an
automatic second loop or consumer re-derivation. An owner gap requires confirmed
expansion, a user-supplied artifact or a visible attributed `Ready-With-Gaps`
assumption. Consumer tools still cannot enumerate or write a sibling root.

Untargeted host `Mode = "autopilot"` runs ordinary inner pipelines with one complete
inner/meta financial authority and one consolidated final human outcome. The root
tariff/calibration prices demand once; actual attempts retain their execution-time
charges and no root consumption ledger is invented. A configured aggregate ceiling
uses sequential member barriers, including every approved overshoot; independent
ungated members can overlap. Deterministic meta slots have no fabricated model spend.
Risk/impact gates carry the actual member/run attribution, and acknowledgement
cannot clear a Stop or authorize release. Explicit `Target` keeps the inner final
gate and independent ceiling instead.

`ResumeAsync(metaRunId)` preserves the same script and inner run identities.
Federation `GetPendingHumanActionsAsync`, `ReadHumanActionAsync`, submit/poll/renew,
`ReconcileMemberAsync` and `ReconcileEvidenceAsync` reuse the native authenticated
gate/effect/integrity services. Completed aggregate inner runs wait at their own
verified barrier until consolidated acceptance or stop. An authenticated final
change uses `ReviseMemberAsync(metaRunId, new(member, stage))` to confirm the real
owner's produce/review stage, preserve original bytes and retain the two-cycle cap.
`ReconcileConsumptionAsync` calibrates only a complete accepted aggregate run,
once against its original uncalibrated costs, without repricing inner history.

Multi-repository execution remains outside this capability. The
[federation guide](docs/federation.html) covers the working API and remaining
product/host boundary. An MCP service is not required.

### Authorized event-scoped Watch

`SquadFederation.HandleWatchEventAsync(event, configuration)` authenticates the
explicit repository signal before creating state or dispatching a model.
`ISquadWatchEventAuthority` re-reads or authenticates structural provenance;
`ISquadWatchTerminalConnector` separately authorizes and verifies a bounded provider
action. A label is not publication permission. `RegisterWatch(configuration)` restores
the same policy after restart for the ordinary pending/read/submit/poll/renew APIs.

The released map is implemented: issue label to autopilot/draft PR, same-repository
PR label to independent review, authorized `/squad` comment to routed work,
committed schedule/manual workflow to configured work, and filtered push to an
independent validation council. Event prose cannot change mode, profile, roots,
model/routing cells, ceiling or permissions. Original input remains byte-bound in
the authorized event member; attempted control assignments are recorded as ignored.
Unattended discovery and Scribe overlap are disabled. Independent intake still runs.
Missing intake/council roles block and name the required confirmed roster change.

Names come only from issue/PR numbers, workflow IDs, UTC sweep dates and normalized
branch/commit metadata. Authorized bootstrap reuses the exact existing lifecycle:
fresh Init, quiescent native-v2 Promotion, additive Expansion and proven orphan
re-registration. Human-owned/unknown trees are never overwritten. Only a proven
unstarted seed image may repair a missing tree; lost started work must be restored,
never reconstructed with invented spend or replayed effects. Event trees are retained.

`Prepared` means actual local work lacks a separately authorized terminal capability;
it never contains a fabricated URL. `Delivered` requires an actual re-read provider
postcondition and is not human validation or release permission. Pending risk/cost
gates remain pending even when a draft/escalation has been posted. Uncertain effects
require `ReconcileWatchAsync(trigger.WatchRunId, configuration)` and explicit
continuation. Process exit, a dry run and model prose cannot prove delivery.

The working GitHub adapters verify label actors/permissions, comments, fork origin,
reviewed workflow blobs and actual draft/review/comment/issue postconditions.
The draft connector verifies an already host-prepared non-protected
`squad-watch/` branch and the actual published output bytes; it cannot push, merge
or deploy. Local source editing uses explicit host projections. Draft publication
and consequential operations remain separate authorized host steps.

The [native runner](samples/HveSquad.AgentFramework.Sample/WatchEventRunner.cs),
[trusted settings reference](samples/HveSquad.AgentFramework.Sample/native-watch.settings.example.json)
and [workflow reference](samples/HveSquad.AgentFramework.Sample/native-watch.workflow.yml)
require deliberate setup, exact committed workflow identity, durable isolated case
storage, authenticated `GITHUB_EVENT_NAME`/`GITHUB_RUN_ID`, host-owned `GITHUB_TOKEN`
and actual model settings. The reference is not an installed workflow or an always-on
service; no Copilot CLI is required. Fork-origin and multi-repository Watch are excluded.
Do not print raw event/settings files, credentials or protected state in CI.

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --watch-event <platform-event.json> --watch-settings <trusted-watch-settings.json> `
  --project <durable-isolated-case>
```

### Consumer-local memory and human-only learning

Memory and learning are explicit native helper APIs, not extra model stages.
`CreateMemory` and `CreateLearning` on the runtime or DI factory need no new model
client or artifact acquisition. The case must already contain verified native v2
state. Federation helpers require one explicit member; they never scan hub/sibling
memory or global IDE memory.

```csharp
var memoryConfiguration = new SquadMemoryConfiguration
{
    Binding = "case-memory-policy-v1",
    AuthorizeAsync = authorizeMemoryAsync,
    SecretValues = hostSecretValues,
};
var memory = factory.CreateMemory(memoryConfiguration);
await memory.RememberAsync(new SquadMemoryNote(
    "researcher", "When cache reuse recurs",
    "Require independent invalidation proof before reusing cached inputs.",
    "All consumers with cached inputs", "A reviewed local convention"),
    cancellationToken);
```

`authorizeMemoryAsync` checks exact case/role rights. Notes live under that native
root's `memory/squad-<role>.md`, not `/memories/repo` or a global store. The Scribe
alone appends notes and original receipts; a quiescent case is required. Reads and
learning discovery do not repair or mutate state. Existing notes are not evicted
when the configured limit is reached. Native promotion preserves original note
bytes and resolves their sealed historical paths inside the new member.
Set `SquadRuntimeOptions.Memory` in host code only when its `model-context` access
check permits sharing the owning role's notes with the host-selected model.
Memory is descriptive, untrusted case data and cannot grant tools or permissions.

`SquadLearningConfiguration` requires a separate actual-human initiation authorizer,
per-source learning disclosure, an authenticated `HumanApprovalChannel` and
`ConfirmAsync` showing the redacted draft/checklist and reading a real response.
`InChatApprovalChannel.AuthorizeLearningInvocationAsync` denies by default unless
the host registers `authorizeLearningInvocation` that proves this exact human
event, not merely a human-owned session. Report permission, Watch opt-in,
`HumanPresent`, ordinary callbacks and prepared-object hashes are not learning
consent. The raw agent factory refuses `Squad Learn`; it is not a callable model
agent.

Discovery offers approved durable notes or explicitly marked local conventions.
Preparation sanitizes secrets, names, absolute paths and URLs locally, generalizes
environment details, and requires human confirmation of broad applicability.
Populate host-known `SecretValues` and `SensitiveNames`; conservative filtering is
not a substitute for that review. Unestablished/refused sanitization stops before
target access. Original/live memory and deployed playbooks remain untouched.

Choose `upstream` with the pinned package repository and an explicit same-name
fork, or `tenant` with an exact host-registered `TenantRepository`. The source table
must have the released six columns and valid rows. IDs are `SL-`/`TL-` followed by
the next number after the highest existing valid ID; gaps are not reused. The UTC
date and every original license notice are preserved in the exact candidate diff.
Without a connector, `PrepareAsync` needs an actual host-provided target snapshot
and returns `prepared` with commands, not publication.

`GitHubLearningConnector` is a working narrowly scoped reference. Its caller-owned
authenticated `HttpClient`, independent resource authorizer and explicit repository
pair are separate from human approval. It verifies public-upstream/private-tenant
visibility, fork lineage, branch identity, exact sole-file commit content and PR
head. The commit-and-push uses GitHub GraphQL `createCommitOnBranch` with
`expectedHeadOid`, so a changed branch cannot be overwritten. Fork, branch,
commit/push and PR each require their own approval. It never merges, deploys or
edits a local installed playbook. Interrupted/unverified actions remain
`reconciliation-required`; `ReconcileAsync` only re-reads provider evidence and
never replays them. Human PR review remains the final poisoning/leakage/drift gate.
Live provider setup and tenant rights remain host-owned; no remote publication was
performed to demonstrate these APIs.

See the matching [English memory/learning reference](docs/usage.html#learning) and
[French reference](docs/fr/usage.html#learning) for the authentication and preparation
flow.

## Prerequisites

- .NET 10 SDK.
  Package verification is validated with SDK 10.0.402 and requires fixed NuGet ZIP
  timestamps. These archive dates are not release dates or native evidence clocks.
- Git and [APM CLI](https://github.com/microsoft/apm) on PATH. APM 0.18.0 is used in compatibility CI.
- Network access to GitHub and APM dependencies when resolving a release. The
  default path acquires the complete matched dependency tree, not just a source clone.
- A dedicated writable case directory for approved text inputs, native state, and
  outputs. An individual business case needs no Git repository or source checkout;
  Git/APM are used for artifact acquisition.
- For model execution, an application-provided `Microsoft.Extensions.AI.IChatClient`.
  The sample uses the official OpenAI .NET adapter.

The package is not published to NuGet. Use a project reference, or build a local
package; the [getting-started guide](docs/getting-started.html#package) covers local
consumption and labels the future public-feed command separately:

```powershell
dotnet build HveSquad.AgentFramework.slnx
dotnet pack src\HveSquad.AgentFramework --configuration Release --output artifacts
```

To verify real consumption rather than a source-reference build:

```powershell
pwsh -NoProfile -File .\scripts\Test-PackageConsumer.ps1
```

The [package-only consumer](samples/HveSquad.PackageConsumer) restores the actual
`.nupkg` outside this checkout, using source mapping and isolated dependencies.
It compares the loaded DLL to the package and executes actual scripted MAF research,
authenticated same-run resume, a federation member, four local document formats,
Scribe memory and human-only discovery. Helpers resolve neither a model nor a source.
The package includes its [consumer README](src/HveSquad.AgentFramework/README.md),
MIT license and exact embedded/packed release manifest, not upstream charters or case
state. No model credentials, publication or remote mutation are used.

## Use in an existing MAF application

The worked example puts a **Draft response for bid review** button in a .NET sales
portal. The host authorizes the case and normalizes an RFP plus approved capability
evidence into known text inputs, then calls the library inside an application job:

```text
Sales user + approved inputs
    -> authorized .NET case job
    -> SquadRuntime: intake / research / approved outline / draft / review
    -> run status + draft evidence
    -> portal UI for bid-manager review (no external send)
```

The [complete class and result record](docs/use-cases.html#rfp-code) accept a
server-selected absolute `caseDirectory`, an existing `IChatClient`, human approval
and question callbacks, and cancellation. It pins `v0.18.1`, selects `full` for
`technical-writer` and the intake seats, supplies two known `InputPaths`, and
registers no custom tools. Native case reads and scoped Markdown/state writes
remain available. `InputPaths` is not a read allowlist; isolate the available data
in the case directory.

`BidDraftResult` retains the `SquadRunResult` and returns a draft-relative path only
on `Completed`, selected from the producing owner's exact evidence—not the latest
file or a user-supplied path. The caller maps other outcomes and exceptions to its
job UI and revalidates canonical path containment and user access before serving
output. The runtime does not ingest cloud Office documents or send results itself.

`ProjectPath` means this case work directory, not necessarily a development project.
Use separate authorized case directories and a contained `SquadRoot` for unrelated
cases. The application queue enforces one active run per case. Dispose
each runtime after use; the host retains ownership of its model client.

Human callbacks remain supported. Initialization and independent management operations
still require their explicit host callback: durable operation never auto-seeds a roster.
For run gates, registering `HumanApprovalChannel` opts into authenticated suspension
instead of waiting inside a callback. No channel or callback means no approval.

### Durable human decisions

`InChatApprovalChannel` takes the host's actual authenticated `ClaimsPrincipal`
resolver and case/action authorization check. Never construct that principal from
submitted JSON, model text or a caller-supplied account name. The console reference
uses its operating-system process account for a local operator; it is not tenant
authentication for an IndustryIQ web application.

`GitHubIssueApprovalChannel` opens one exact approval issue, assigns the confirmed
human and accepts `/approve`, `/answer: <answer>`, `/changes: <note>`, `/stop` or
attributed `squad/approved`, `squad/changes` and `squad/stop` label events. It re-reads
the actual provider source and verifies the configured human account or current
write/maintain/admin collaborator. Unrelated prose, bot actors, stale events and
blanket `/approve-all` commands are not authorization.

The host owns the authenticated `HttpClient` and must disable automatic redirects.
Provision Issues write, account/collaborator-read permissions and the
`squad-approval`/`squad/changes` labels deliberately. Credentials may rotate without
changing the stable adapter binding. A changed repository/account binding cannot
resume an old run.

`WebhookSquadNotifier` resolves its secret HTTPS endpoint from a host-owned function
at send time. Its configured `WebhookBinding` is opaque, non-secret and explicitly
opted in. It sends redacted metadata only and has no inbound approval API. Suppress
or redact private webhook URLs in the host's HTTP instrumentation; never log an
endpoint, query string, token or authorization header.

```csharp
var pending = await squad.GetPendingHumanActionsAsync(cancellationToken);
// Render private details only after the application checks case read access.
var details = await squad.ReadHumanActionAsync(actionId, cancellationToken);
var response = SquadHumanResponse.FromCommand(
    details.Action, humanCommand, authenticatedResponseId);
var submitted = await squad.SubmitHumanResponseAsync(response, cancellationToken);
if (submitted.Accepted)
{
    var continued = await squad.ResumeAsync(details.Action.Binding.RunId, cancellationToken);
}
```

Here `actionId`, `humanCommand` and the response reference come from the application's
authorized UI. `SubmitHumanResponseAsync` verifies the registered channel; a claimed
actor name is never accepted. GitHub hosts use `PollHumanResponsesAsync(runId)` to
read and authenticate actual comments/label events instead of constructing responses
from event webhook text. Neither submission nor polling runs a model or project effect.

`SquadRunResult.PendingHumanActions` and `GetPendingHumanActionsAsync()` expose only
opaque binding/expiry/delivery metadata. `ReadHumanActionAsync()` is a host-only
protected-payload API, not an agent tool or public endpoint. Pending questions,
raw arguments and human change input remain in protected case storage with exact
byte digests; notifications do not disclose them.

Responses bind the actual root, release, host registration, run, gate, revision,
dispatch, financial round, action arguments and verified evidence. Changed payloads,
stale/foreign/replayed sources or unauthorized actors cannot continue work.
`HumanResponseTimeout` defaults to one day, with a maximum of 30 days; equality at
expiry rejects the response. `RenewHumanActionAsync(actionId)` explicitly reissues an
expired unchanged challenge or reconciles its notification. An uncertain issue
creation is looked up, never blindly recreated.

`/changes` preserves original bytes/history/spend, records private descriptive input
and reopens the affected plan/readiness work. A producing tool change preserves its
exact dispatch and prior effect identities while requiring a fresh action approval.
Revised readiness/council records get distinct identities; old verdicts are never
rewritten. Returned intake questions require real authenticated answers and independent
revalidation within the existing two-cycle bound, not an automatic readiness override.
`/stop` freezes further work; explicit same-run resume records the stopped outcome.
Uncertain effects still require `ReconcileAsync` and are never automatically replayed.
An interrupted answer/stop projection is reconstructed only from its immutable
authenticated receipt, never inferred from a model session or local approval file.
Pending time is separate from cumulative active execution and never resets run limits.

### Configuration and dependency injection

```json
{
  "HveSquad": {
    "ProjectPath": "C:\\app-data\\bid-cases\\demo",
    "Profile": "full",
    "Version": "v0.18.1",
    "Mode": "interactive",
    "Packs": [],
    "InputPaths": ["inputs\\rfp.txt", "inputs\\approved-capabilities.txt"],
    "MaxDispatches": 32,
    "MaxModelCalls": 64
  }
}
```

```csharp
using HveSquad.AgentFramework.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<IChatClient>(chatClient);
services.AddHveSquad(
    configuration.GetSection("HveSquad"),
    configureHost: options =>
    {
        options.ApproveAsync = ApproveInYourApplicationAsync;
        options.AskAsync = AskInYourApplicationAsync;
    });

// Resolve from the application's service provider:
var factory = serviceProvider.GetRequiredService<SquadRuntimeFactory>();
using var squad = await factory.CreateAsync(cancellationToken);
var result = await squad.RunAsync(request, cancellationToken);
```

This configuration describes one provisioned demo case, not one directory shared
by every application user. `ApproveInYourApplicationAsync` and
`AskInYourApplicationAsync` are the application's callbacks, with the signatures in
the [complete job method](docs/use-cases.html#rfp-code). Registration is lazy:
network access and artifact acquisition occur in `CreateAsync`, not in a DI constructor.
Each factory call creates an independently owned runtime. Configuration binds only
known data settings; delegates, model selectors, tools and logging are host-only.

### Supplying real capabilities

Advisory Markdown drafts need no source editor, CRM writer, or external sender.
Agents can read contained case files and write scoped methodology artifacts using
native tools. Your host owns ingestion, approved retrieval, and delivery.
If another application feature genuinely needs to change source or an external
system, it requires a trusted host function; a report is not evidence of that effect.
For example, an optional source-editing integration can register:

```csharp
options.Tools.Add(new SquadToolRegistration(
    function,                    // An AIFunction implemented by the host.
    "edit",                      // Permission corresponding to the charter.
    ["developer"],               // Only these roster roles can call it.
    SquadToolEffect.ProjectWrite,
    OutputEvidence: result => ExtractSuccessfulOutputs(result)));
```

`ExtractSuccessfulOutputs` must return a `SquadToolOutput` based on the function's
actual successful result, never on model claims. Its `ProjectPaths` are verified
project-relative output files; external operations require a meaningful receipt.
Failed or dry-run operations must not attest an applied effect. For a host-specific
producing role, `RequireOutputForCompletion: true` can require the configured effect.

The sample's `ProjectFileWriter` demonstrates a contained source writer and output
attestation, not a capability needed by the RFP example. Every consequential call requires a separate host approval containing
the actual arguments. Hosts are responsible for honest effect classification,
resource isolation and not mutating the runtime's shared state through custom tools.

Host output files are hashed as raw bytes, including binary artifacts, with a
64 MiB per-file limit. Repeated approved edits in one dispatch retain all receipts
and verify the last observed contents. Overlapping output ownership across distinct
dispatches fails closed rather than treating an earlier artifact as unchanged.
A presenter needs a host-backed `.pptx` output; a Markdown report does not count as
a rendered deck. The runtime verifies file evidence, not the internal validity of
an Office package, and supplies no renderer itself.

Use `ModelSelector` for per-role clients and explicit model IDs, and
`EnableOpenTelemetry` for tracing. Provider-reported token counts are recorded;
unknown counts, model identities and costs remain unknown rather than invented.

### Registering an exact released script

Script resources are data until trusted application code registers a real executor.
Acquire the chosen pinned artifacts, resolve the canonical skill with
`SquadSkillScriptBinding.Create(roots, skillName, resourcePath, executorBinding)`,
and supply it through `SquadToolRegistration.SkillScript`. The binding includes
actual release provenance, raw entry-point/script digests and an opaque executor
revision. Explicit directory artifacts remain non-official; the binding does not
certify arbitrary local content as a released capability.

Supply the existing charter permission, role allowlist, `Effect`, `Impact`, `Action`
and `OutputEvidence`, plus a trusted `ProjectReadPaths` projection (explicitly empty
when there are no file inputs). Writes also require `ProjectWritePaths`. Before
execution the runtime verifies original input bytes, the exact acquired artifacts,
plan-unit output ownership and current authority. Changed inputs invalidate a pending
approval. Changed executor/scopes require a new binding and cannot resume the old run.
The host updates `ExecutorBinding` when its implementation, command builder,
projections or resource isolation changes.

MAF `load_skill` advertises only the registered script paths and their parameter
schemas. `run_skill_script` uses `skillName`, `scriptName` and an `arguments` object;
APM folder names, script text and model-chosen aliases never grant permission.
The original registered function name is not a second script entry point.
Unregistered scripts stay unavailable, including at coordinator scope. Native
Consumption/Scribe results and `GetModelRoutingChoicesAsync` replace Squad's
Copilot-schema state scripts; no generic executor can read private native payloads,
rewrite writer-owned state or use a member's unapproved sibling inputs.

The [bounded process reference](src/HveSquad.AgentFramework/Hosting/SquadProcessExecutor.cs)
uses a fixed absolute executable and separate `ArgumentList` values, closes stdin,
enforces a timeout of at most ten minutes and bounds each captured output stream.
Cancellation/timeout terminates the actual process tree. Environment is an explicit
host allowlist (only Windows `SystemRoot` is retained by default), not inherited
model or connector credentials. Nonzero exits produce `Failed`; missing output,
timeout, stream overflow or changed partial output fails explicitly. Successful
declared local files are hashed as exact bytes; stdout can be published to one
declared contained output only after a successful exit.

`SquadToolOutput.ReceiptKind` distinguishes a `HostObservation` from `Process`.
A process exit/stream digest is not external retrieval, deployment, tracker or other
provider/resource proof. Such integrations must verify the actual postcondition
and supply connector-specific reconciliation. Interrupted or partially applied
effects are never replayed automatically. Original bytes and effect identities
remain preserved when a bounded same-unit second draft is authorized.

Each call owns and disposes its process; the runtime does not dispose host functions,
executors, model clients or an optionally supplied `ActivitySource`. The optional
`run.skill_script` span has no paths, arguments, stdout/stderr, environment values
or other private attributes. The host configures OpenTelemetry resource attributes
and exporter/redaction policy. Protected process output is local case data, not
notification, report or CI content.

For a credential-free real MAF/process demonstration, create a new isolated directory
and select a trusted PowerShell 7.4+ executable:

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --skill-executor-demo "C:\Program Files\PowerShell\7\pwsh.exe" `
  --project C:\app-data\squad-executor-demo --version v0.18.1
```

The [reference host](samples/HveSquad.AgentFramework.Sample/SkillExecutorDemo.cs)
registers only `pr-reference/scripts/read-diff.ps1`, reads one original synthetic
input and produces verified research. It never overwrites an existing demo input
or enables unrestricted shell, build/test, credentials or state-script execution.
Its explicit process environment includes `PATH` (and Windows `PATHEXT`) for the
released reader's local Git lookup. Optional `HVE_SQUAD_RELEASE_GITHUB_TOKEN`
authenticates metadata acquisition only and is not inherited by the executable.
The general live host remains a bounded source writer; arbitrary process authority
is not deserialized from CLI/JSON/DI configuration.

This reference is not an OS sandbox. A trusted script can otherwise access resources
outside its declarations or detach processes. The application must supply operating
system/container isolation, tenant rights and a reviewed command builder where needed.
Scripted acceptance is mechanical evidence, not paid-model quality or IndustryIQ readiness.

## Staying aligned with HVE Squad releases

`Version = "latest"` resolves `/repos/Peter-N91/hve-squad/releases/latest`.
The source validates that it is a published stable semantic-version release,
resolves the **tag** to its commit SHA, and installs that SHA through APM.
It never uses the release's `target_commitish` field, a branch head, or `main`.

The supported default is **v0.18.1**, commit
`4c1f140f12f17abd3f200baa3afcb6d4c84bb6da`, with HVE Core
`18f5ac75a1556e49f1e90647a4816d17499e1d7f`. The legacy **v0.16.2** release remains a
separate regression target. The [canonical manifest](compatibility/hve-squad-release.json)
tracks conformance independently of identity; upstream roles and rules are not copied into duplicate prompts.

- A new runtime using `latest` checks for the current published stable release.
  A running runtime retains its resolved release and rejects artifact changes.
- Cache inventories cover deployed agents, instructions and complete skill resources.
  Missing or changed files invalidate the cache and trigger reacquisition.
- Authentication/rate-limit/network failures are reported; there is no silent
  fallback to `main`, a prerelease or another version.
- For reproducible/offline operation, resolve/install a release in advance and pass
  a `ProjectArtifactSource` for that installed directory. Explicit directory sources
  are host-managed overrides, not proof of published-release provenance.
- Existing native state is bound to the release, artifact fingerprint and roster.
  A new release cannot silently resume an old conversation. Keep the original
  version for that project, use a fresh project, or supply an explicit host migration.
  Automatic cross-release migration is not implemented. Explicit native-v1 migration
  requires separate approval and preserves original bytes; it is not a Copilot importer.

The [release compatibility workflow](.github/workflows/release-compatibility.yml) exercises
the exact current and legacy pins. [Reviewed release alignment](.github/workflows/release-alignment.yml)
detects future official stable releases on schedule/manual dispatch and opens one draft candidate
proposal without replacing the production pin. Evaluation and privileged metadata-only mutation are
separate; actual source/dependency/resource/role/mode/schema/assertion drift is reported.

Both [candidate-head checks](.github/workflows/release-candidate.yml) must execute on Windows/Linux
and complete successfully. Unknown/unverified contracts, absent/skipped categories, wrong-head green
runs and missing write permission or workflow approval block promotion. Bot-created PRs may require
a repository writer to select **Approve workflows to run**. Semantic observations are advisory:
missing baselines are `unbaselined` and absent answer judging is `not judged`, not a fabricated score.
Human review/merge is required; no automation merges, publishes or deploys.
See the [release-sync maintainer reference](tools/HveSquad.ReleaseSync/README.md) for commands,
repository settings, drift maintenance and explicit prepared/pending boundaries.
**Automatic acquisition is not a guarantee of automatic behavioral compatibility.**

## Runnable sample

Credential-free release inspection:

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --latest
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --release v0.18.1
```

With no arguments the inspector searches for an installed project; explicit
artifact-directory arguments remain supported.

For a live run, configure `OPENAI_API_KEY` and `OPENAI_MODEL` securely in the host
environment. Optional `OPENAI_ENDPOINT` supports an OpenAI-compatible endpoint,
including a correctly configured Azure OpenAI v1 endpoint. No credentials are
stored in the repository.

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --run "Research the question in inputs\rfp.txt using inputs\approved-capabilities.txt. Cite source identifiers and report gaps. Stop after research; do not change inputs or send anything externally." `
  --project C:\app-data\bid-cases\demo --version v0.18.1 --profile full
```

First provision the [fictional approved text inputs](docs/getting-started.html#sample);
an empty directory cannot supply product evidence. This focused request produces
research evidence, not a completed response draft.

Alternatively, `--config <run.json>` accepts basic request/project/version/profile
and the documented data-only roster, mode, model, cost and durable-gate settings.
Start from [the native run configuration](samples/HveSquad.AgentFramework.Sample/native-run.settings.example.json).
The console waits for explicit `yes` on approvals and supports Ctrl+C.
In addition to native case reads and methodology artifacts, it exposes a contained
`write_project_file` source tool for the developer role. That is an optional sample
capability, not the library's business proposition. It has no shell/build/test
executor or external research connector and does not imply unavailable operations ran.

For a process-restart demonstration without model credentials, create an isolated
demo directory and run these as two separate processes:

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --approval-demo start --project C:\app-data\squad-approval-demo
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --approval-demo resume --project C:\app-data\squad-approval-demo --reply /approve
```

The first process confirms only the explicitly invoked local demo roster and saves
an actual pending routing gate. The second authenticates the local process operator,
resumes that same run and writes real scripted MAF research evidence. This proves
suspension/continuation mechanics, not live-model quality or IndustryIQ readiness.

Live runs opt in with `--durable-approvals true` or JSON `durableApprovals`.
JSON `notifications` chooses the non-secret GitHub settings or webhook binding;
`humanResponseTimeout` is a positive TimeSpan. The sample reads `GITHUB_TOKEN` or
`SQUAD_WEBHOOK_URL` only from the host environment. In-chat resume can use
`--reply "/answer: text"` or an interactive command; one command-line reply cannot
approve a batch. GitHub resume polls the actual issue, not a command-line approval.

The same compiled sample now exposes the model-independent public helper factories:

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --help
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --federation-demo research --project C:\app-data\empty-federation-demo
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --report governance --project C:\app-data\squad-approval-demo --period 7d
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --report document --project C:\app-data\squad-approval-demo --request "Current roster and evidence" --format docx
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --memory read --project C:\app-data\squad-approval-demo --role researcher
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --learn discover --project C:\app-data\squad-approval-demo
```

The federation demo requires an existing empty isolated directory and explicitly
synthetic local confirmations. Helpers require an existing native case and exact
console disclosure rights, not model credentials or release acquisition.
[The helper CLI reference](docs/usage.html#helper-cli) includes local note JSON,
prepared-only learning, explicit GitHub connector registration and read-only
reconciliation. No connector means `prepared`, not a PR. Register the same connector
during preparation and execution; a new capability cannot be bolted onto an old
prepared authority. Every actual mutation has its own authenticated approval and
resource-rights check. The local process-account console is not multi-tenant
authentication; never send private helper output to public CI logs.

Helper fallback or unresolved reconciliation returns exit code 3, stopped/failure 1,
invalid arguments 2, cancellation 130 and verified/prepared outcomes 0. JSON cannot
install adapters, executors, renderers, principals, source or memory/learning rights.

## Development and compatibility checks

```powershell
$env:HVE_SQUAD_TEST_POWERSHELL = (Get-Command pwsh).Source
$env:HVE_SQUAD_RELEASE_VERSION = "v0.18.1"
$env:HVE_SQUAD_RELEASE_COMMIT = "4c1f140f12f17abd3f200baa3afcb6d4c84bb6da"
$env:HVE_SQUAD_REQUIRE_RELEASE_TESTS = "true"
dotnet test tests\HveSquad.AgentFramework.Tests --configuration Release -- xUnit.MaxParallelThreads=4

# Preserve the exact supported legacy acquisition as a separate regression target.
$env:HVE_SQUAD_RELEASE_VERSION = "v0.16.2"
$env:HVE_SQUAD_RELEASE_COMMIT = "a941195c36dfb6181453f51ec4638b085ca3b3ad"
dotnet test tests\HveSquad.AgentFramework.Tests --configuration Release --no-build `
  --filter Category=ReleaseCompatibility -- xUnit.MaxParallelThreads=4
```

Keep version/commit selectors paired and unset conflicting selectors when using
`HVE_SQUAD_RELEASE_ROOT` for a host-managed installation containing `.github` and
`.agents`. Use the read-only release-sync `inspect` command for future stable
candidate evaluation. Mandatory categories must execute, never silently return.
These checks require no paid model and do not establish live-model quality,
full individual assertion conformance, Linux execution or IndustryIQ readiness.

### Security and repository automation

The workflows adapt HVE Squad's security approach to this C# library and static site:

| Workflow | Purpose |
| --- | --- |
| `codeql.yml` | C# manual-build analysis and JavaScript analysis |
| `checkov.yml` | Blocking Actions/secrets checks and IaC/container checks when relevant files exist |
| `zizmor.yml` | Blocking offline GitHub Actions analysis |
| `dependency-review.yml` | High/critical newly introduced dependency advisories block pull requests |
| `dependency-audit.yml` | Direct/transitive NuGet audit; high/critical advisories and incomplete feeds block |
| `scorecard.yml` | Supply-chain assessment reports, no public Scorecard API publication or badge |
| `release-compatibility.yml` | Reuse the full Windows/Linux acceptance matrix on `main`, daily and manual runs |
| [release-alignment.yml](.github/workflows/release-alignment.yml) | Read-only stable candidate evaluation and separate idempotent draft proposals; no default promotion |
| [release-candidate.yml](.github/workflows/release-candidate.yml) | Exact-head full native/current/legacy tests, actual package consumer, individual assertion evidence and reviewed promotion gates |
| `docs.yml` | Least-privilege static Pages deployment from `main` |

Dependabot proposes weekly NuGet and action updates. Repository dependency graph,
code scanning, secret scanning/push protection and branch rules must still be enabled
where available; workflow files cannot configure them. Set the Actions variable
`SECURITY_SARIF_UPLOAD=true` to opt into trusted-main Checkov/Zizmor code-scanning
uploads. Their report artifacts are retained regardless. See
[workflow activation and maintenance](docs/maintaining.html#workflows) for permissions,
feature availability, severity policy and publishing boundaries.

Configuration and dependency-injection packages are grouped so library, sample and
test projects update together rather than introducing NuGet downgrades. Release
acceptance runs once per pull-request update through the candidate workflow;
`main`, scheduled and manual compatibility runs reuse that same matrix.
Its GitHub release metadata requests use the job's read-only token, passed
explicitly by the test host; the library never discovers credentials automatically.
Local release tests remain anonymous unless the host explicitly provides
`HVE_SQUAD_RELEASE_GITHUB_TOKEN`. Never store a token in source or test fixtures.

The lower-level `HveSquadBuilder`, `SquadAgentFactory` and `SquadWorkflowBuilder`
remain available for artifact inspection and custom composition. They are not the
governed runtime entry point; use `SquadRuntime` when you need its enforced gates.

## License

MIT. See [LICENSE](LICENSE). Upstream artifacts are acquired at installation time,
not redistributed in this package, and retain their own licenses. This project is
not affiliated with or endorsed by Microsoft.
