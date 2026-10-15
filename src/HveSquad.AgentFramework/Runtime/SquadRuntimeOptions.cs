using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Host configuration for the bounded native single-squad delivery runtime.</summary>
public sealed class SquadRuntimeOptions
{
    public string ProjectPath { get; set; } = Environment.CurrentDirectory;
    /// <summary>Contained native writer root; member roots use .copilot-tracking/squad/members/name.</summary>
    public string SquadRoot { get; set; } = ".copilot-tracking/squad";
    /// <summary>Opt-in native v1 migration still requires a separate host approval before writing.</summary>
    public bool AllowNativeV1Migration { get; set; }
    /// <summary>Approved per-role root overrides; surviving roots are never silently reset.</summary>
    public IDictionary<string, string> DeliverableRoots { get; set; } = new Dictionary<string, string>();
    public string Profile { get; set; } = "default";
    public IList<string> Packs { get; set; } = [];
    /// <summary>Explicit custom roles; an empty list uses the selected profile.</summary>
    public IList<string> Roles { get; set; } = [];
    /// <summary>Explicit role additions, including a confirmed council extension.</summary>
    public IList<string> AdditionalRoles { get; set; } = [];
    /// <summary>The confirmed topology choice; federation creation uses the federation controller.</summary>
    public string InitializationScope { get; set; } = "single-squad";
    /// <summary>Non-secret notification/contact choices; credentials and webhook URLs stay host-owned.</summary>
    public SquadNotificationConfiguration Notifications { get; set; } = new();
    public string Version { get; set; } = SupportedSquadRelease.Current.Tag;
    public string? NamingPolicy { get; set; }
    public IDictionary<string, string> MemberNames { get; set; } = new Dictionary<string, string>();
    public string? ApprovalChannel { get; set; }

    /// <summary>Interactive (default), autonomous validation, or the full autopilot delivery pipeline.</summary>
    public string Mode { get; set; } = "interactive";
    /// <summary>Explicit input artifacts force the intake gate; the coordinator can discover more.</summary>
    public IList<string> InputPaths { get; set; } = [];
    public bool RequireCouncil { get; set; }
    public bool IncludeRaiInCouncil { get; set; }
    /// <summary>Explicit quick, standard, deep or skip. Null allows the once-per-topic eligible offer.</summary>
    public string? Discovery { get; set; }
    /// <summary>Optional stable topic identity; changed scope invalidates prior discovery and waivers.</summary>
    public string? TopicId { get; set; }
    /// <summary>False prohibits discovery interviews, including an explicit discovery hint.</summary>
    public bool HumanPresent { get; set; } = true;
    /// <summary>Explicit task-fit council adjustments; all choices still require human confirmation.</summary>
    public SquadCouncilConfiguration? Council { get; set; }
    /// <summary>Explicit USD ceiling or "unset"; omission inherits only within the same resumed run.</summary>
    public string? CostCeiling { get; set; }
    /// <summary>Provider-specific verified rates and calibration; null retains the squad's recorded tariff.</summary>
    public SquadConsumptionConfiguration? Consumption { get; set; }
    /// <summary>Optional trusted normalization of the provider's actual input/cache/output counters.</summary>
    public Func<ChatResponse, SquadTokenUsage?>? TokenUsageSelector { get; set; }
    public int MaxDispatches { get; set; } = 32;
    /// <summary>Maximum simultaneous provider calls; nested workers do not hold their parent's slot.</summary>
    public int MaxConcurrentDispatches { get; set; } = 4;
    public int MaxDepth { get; set; } = 4;
    public int MaxModelCalls { get; set; } = 64;
    public int MaxToolCalls { get; set; } = 128;
    public int MaxArtifactCharacters { get; set; } = 200_000;
    public int MaxArtifactsPerDispatch { get; set; } = 16;
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A trusted host channel, never a model-writable approval file. Initialization proposes the
    /// profile, names and channel together; true confirms that complete proposal.
    /// </summary>
    public Func<SquadApprovalRequest, CancellationToken, ValueTask<bool>>? ApproveAsync { get; set; }
    public Func<SquadQuestion, CancellationToken, ValueTask<string?>>? AskAsync { get; set; }
    /// <summary>Opt-in authenticated durable human gates. Initialization still needs its explicit local callback.</summary>
    public ISquadHumanApprovalChannel? HumanApprovalChannel { get; set; }
    /// <summary>Optional outbound-only notifier; acknowledgement cannot answer a question or approve an action.</summary>
    public ISquadHumanNotifier? HumanNotifier { get; set; }
    /// <summary>Wall-clock lifetime of a durable challenge; pending time does not reset the run's active-execution budget.</summary>
    public TimeSpan HumanResponseTimeout { get; set; } = TimeSpan.FromDays(1);
    /// <summary>Optional native Init UI. Selection is a proposal; ApproveAsync must still confirm it before any files are written.</summary>
    public Func<SquadInitializationChoices, CancellationToken, ValueTask<SquadRosterConfiguration?>>? SelectInitializationAsync { get; set; }
    /// <summary>Optional native task-fit council UI. A selection is a proposal, never approval.</summary>
    public Func<SquadCouncilProposal, CancellationToken, ValueTask<SquadCouncilConfiguration?>>? SelectCouncilAsync { get; set; }

    /// <summary>
    /// Host-owned functions. Registrations declare their real effects and charter permission;
    /// prompts or function arguments cannot grant permission. The host must enforce its own
    /// resource constraints and must never let a registration mutate shared squad state.
    /// </summary>
    public IList<SquadToolRegistration> Tools { get; set; } = [];

    /// <summary>Null role denotes the user-invoked coordinator; clients remain caller-owned.</summary>
    public Func<SquadRosterEntry?, AgentCharter, SquadModelSelection?>? ModelSelector { get; set; }
    /// <summary>Null inherits persisted policy; a new squad defaults to off. Changes require confirmation.</summary>
    public SquadModelRoutingConfiguration? ModelRouting { get; set; }
    /// <summary>Actual current model/client bindings, never the declared catalog or an assumed provider capability.</summary>
    public Func<CancellationToken, ValueTask<IReadOnlyList<SquadAvailableModel>>>? AvailableModelsAsync { get; set; }
    /// <summary>Trusted interpretation of a canonical Selection Cue; it cannot widen the roster or child allowlist.</summary>
    public Func<SquadAgentSelectionContext, SquadAgentSelection?>? AgentSelector { get; set; }
    /// <summary>Trusted clock for freshness, gates, new provider/dispatch/Scribe receipts and helper periods; historical evidence is never rewritten.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    /// <summary>Opt-in code-only consumer-local memory and explicit per-role model-context disclosure rights.</summary>
    public SquadMemoryConfiguration? Memory { get; set; }
    public Func<SquadRosterEntry, SquadSkill, bool>? SkillFilter { get; set; }
    public Func<SquadRosterEntry?, SquadInstruction, bool>? InstructionFilter { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public bool EnableOpenTelemetry { get; set; }
    public string OpenTelemetrySourceName { get; set; } = "HveSquad.AgentFramework.Runtime";
    internal FederationInvocation? FederationInvocation { get; set; }
    internal WatchInvocation? WatchInvocation { get; set; }
}

public sealed record SquadModelSelection(IChatClient ChatClient, string? ModelId = null, string? CapabilityClass = null);

/// <summary>Persisted model policy. Only an exact host-available manual ID can become a pick.</summary>
public sealed class SquadModelRoutingConfiguration
{
    /// <summary>Off omits routing overrides; ranked and manual have replace-owned Model cells.</summary>
    public string Mode { get; set; } = "off";
    /// <summary>Per-role manual choices; ranked readouts are calculated, never taken from this input.</summary>
    public IDictionary<string, string> Models { get; set; } = new Dictionary<string, string>();
}

/// <summary>An actual host-advertised ID and its caller-owned execution client.</summary>
/// <param name="ModelId">The exact available identifier.</param>
/// <param name="ChatClient">The client this host can actually use for that ID.</param>
/// <param name="CapabilityClass">Optional host-declared floor evidence, especially during stale fallback.</param>
/// <param name="ContextWindowTokens">Optional confirmed standard input limit.</param>
/// <param name="LongContextWindowTokens">Optional confirmed long-context input limit for this binding.</param>
public sealed record SquadAvailableModel(string ModelId, IChatClient ChatClient, string? CapabilityClass = null,
    long? ContextWindowTokens = null, long? LongContextWindowTokens = null);

/// <summary>A bounded manual-menu candidate; unknown catalog facts remain nullable.</summary>
public sealed record SquadModelCandidate(string ModelId, string DisplayName, int? Fit,
    decimal? BlendedUsdPerMillion, bool Unevaluated = false);

/// <summary>Read-only role suggestions and validation; a menu is not permission to dispatch.</summary>
public sealed record SquadRoleModelChoice(string Role, string AssignmentClass, string? Floor,
    string? SuggestedModel, IReadOnlyList<SquadModelCandidate> Candidates, string CellStatus, string? CurrentModel = null);

/// <summary>Actual host-available choices. Each role menu contains at most eight ranked candidates.</summary>
public sealed record SquadModelRoutingChoices(string Mode, IReadOnlyList<SquadRoleModelChoice> Roles,
    IReadOnlyList<string> AvailableModels, IReadOnlyList<string> Diagnostics);

/// <summary>The actual confirmed or declined model-policy update.</summary>
public sealed record SquadModelRoutingUpdateResult(SquadRunStatus Status, SquadModelRoutingConfiguration Configuration,
    string Diagnostic);

/// <summary>A trusted host reports that this provider rejected a model before executing that call.</summary>
/// <remarks>Only this specific pre-execution rejection is eligible for reranking. Ordinary provider failures are never retried.</remarks>
public sealed class SquadModelUnavailableException : Exception
{
    /// <summary>Creates an explicit pre-execution rejection with the host's reported available IDs.</summary>
    /// <param name="modelId">The exact rejected request.</param>
    /// <param name="availableModels">The current reported set; it is still intersected with actual client bindings.</param>
    public SquadModelUnavailableException(string modelId, IReadOnlyList<string> availableModels)
        : base("The host rejected the requested model before executing this provider call.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(availableModels);
        ModelId = modelId;
        AvailableModels = availableModels.ToArray();
    }

    /// <summary>The exact rejected model ID.</summary>
    public string ModelId { get; }
    /// <summary>Reported availability, not a grant to use an unregistered client or lower a floor.</summary>
    public IReadOnlyList<string> AvailableModels { get; }
}

/// <summary>Canonical role/cue and actual assignment supplied to a trusted native selector.</summary>
public sealed record SquadAgentSelectionContext(SquadRosterEntry Role, string Request, string Assignment);
/// <summary>A concrete alternate justified by a cited clause of the role's installed Selection Cue.</summary>
public sealed record SquadAgentSelection(string Agent, string Cue, string Rationale);

/// <summary>Routing identity is separate from the closed role-consumption payload.</summary>
public sealed record SquadDispatchModelIdentity(string? RequestedModel, string? EffectiveModel,
    string? ObservedModel, string AssignmentClass, string? Floor, string Rationale, bool Unevaluated = false);

public enum SquadToolEffect
{
    /// <summary>Non-consequential local reads; the trusted registration must be accurate.</summary>
    ReadOnly,
    ProjectWrite,
    /// <summary>External side effects, restricted to the approved production stage.</summary>
    External,
    /// <summary>External retrieval, such as web research. Requires host approval at any stage.</summary>
    ExternalRead,
}

/// <summary>Trusted host classification; omission never makes a consequential tool safe.</summary>
public enum SquadToolImpact
{
    /// <summary>Unknown impact requires a specific human gate.</summary>
    Unspecified,
    /// <summary>Bounded, reversible local work or retrieval with host output/receipt attestation.</summary>
    Safe,
    /// <summary>A specific impactful action, regardless of execution mode.</summary>
    Impactful,
}

/// <summary>Mandatory action boundaries which no mode or safe-impact declaration can waive.</summary>
public enum SquadToolAction
{
    /// <summary>No special action classification; impact and effect still apply.</summary>
    General,
    Deployment,
    Push,
    Merge,
    Migration,
    Deletion,
    SecretRotation,
    Compliance,
    CostConfirmation,
    Irreversible,
}

/// <summary>
/// Registers a real host capability. OutputEvidence attests successful outputs from the returned
/// result; required source/effect roles cannot complete on a call receipt alone.
/// RequireOutputForCompletion adds an effect requirement for other host-specific producing roles.
/// Merely exposing an optional tool never requires an otherwise unrequested external action.
/// ProjectWritePaths is a trusted, pre-execution path projection. Independent writers require
/// it; model arguments alone cannot establish output ownership.
/// Impact is a host-only classification, not a permission supplied by input text. Unknown impact
/// and mandatory Action boundaries retain exact human gates in every mode. Safe writes require
/// bounded paths and output attestation; safe retrieval needs a receipt extractor. A dishonest
/// executor cannot be sandboxed by this library.
/// SkillScript opts this function into canonical run_skill_script rather than exposing a
/// guessed shell. ProjectReadPaths is mandatory for scripts, including an explicit empty scope.
/// The host owns functions/executors and changes ExecutorBinding when their implementation,
/// input/output projections, command arguments or resource isolation change.
/// </summary>
public sealed record SquadToolRegistration(
    AIFunction Function,
    string CharterPermission,
    IReadOnlyList<string> AllowedRoles,
    SquadToolEffect Effect,
    Func<object?, SquadToolOutput>? OutputEvidence = null,
    bool RequireOutputForCompletion = false,
    Func<SquadEffectReconciliationRequest, CancellationToken, ValueTask<SquadEffectReconciliation>>? ReconcileAsync = null,
    Func<AIFunctionArguments, IReadOnlyList<string>>? ProjectWritePaths = null,
    SquadToolImpact Impact = SquadToolImpact.Unspecified,
    SquadToolAction Action = SquadToolAction.General,
    SquadSkillScriptBinding? SkillScript = null,
    Func<AIFunctionArguments, IReadOnlyList<string>>? ProjectReadPaths = null);

/// <summary>
/// Trusted host attestation extracted from an actual function result, never model arguments.
/// ProjectPaths are existing project-relative outputs; Receipt describes successful external
/// execution or observed external data. A failed/dry-run operation must not attest an applied effect.
/// </summary>
public sealed record SquadToolOutput(IReadOnlyList<string> ProjectPaths, string? Receipt = null,
    SquadToolOutputStatus Status = SquadToolOutputStatus.Succeeded,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    SquadToolReceiptKind ReceiptKind = SquadToolReceiptKind.HostObservation);

/// <summary>Distinguishes a verified host/resource observation from process success alone.</summary>
public enum SquadToolReceiptKind
{
    /// <summary>The trusted integration attests the operation's actual resource/output postcondition.</summary>
    HostObservation,
    /// <summary>Only process exit, output streams and declared local files were observed; not an external resource receipt.</summary>
    Process,
}

/// <summary>Actual host result; a returned function does not necessarily apply its intended effect.</summary>
public enum SquadToolOutputStatus
{
    /// <summary>The trusted function performed the attested work.</summary>
    Succeeded,
    /// <summary>The function failed; it cannot prove the intended work.</summary>
    Failed,
    /// <summary>The function produced a preview or simulation, not an applied effect.</summary>
    DryRun,
}

/// <summary>An uncertain effect's immutable identity; arguments stay in protected case storage.</summary>
public sealed record SquadEffectReconciliationRequest(
    string RunId, string SquadRoot, string OperationId, string DispatchId,
    string Role, string ToolName, string ArgumentsSha256, System.Text.Json.JsonElement Arguments,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadSkillExecution? SkillScript = null);

/// <summary>What a trusted host can actually establish about an interrupted effect.</summary>
public enum SquadEffectDisposition
{
    /// <summary>Actual successful execution is proven by outputs/receipt and a returned result.</summary>
    Executed,
    /// <summary>The host proves the operation did not execute; fresh approval is still required.</summary>
    NotExecuted,
    /// <summary>The outcome remains uncertain; neither replay nor later work is permitted.</summary>
    CannotConfirm,
}

/// <summary>Host reconciliation evidence, not a model claim or authorization to replay.</summary>
public sealed record SquadEffectReconciliation(
    SquadEffectDisposition Disposition,
    SquadToolOutput? Output = null,
    System.Text.Json.JsonElement? Result = null,
    string? Diagnostic = null);

public enum SquadApprovalKind
{
    Initialization,
    Routing,
    Plan,
    Implementation,
    ConsequentialTool,
    StateMigration,
    RosterUpdate,
    ModelRouting,
    CostOvershoot,
    ConsumptionReconciliation,
    Council,
    CouncilOverride,
    IntakeRemediation,
    Discovery,
    RiskGate,
    AutonomousOutcome,
    FinalOutcome,
    FederationInitialization,
    FederationPromotion,
    FederationExpansion,
    FederationPlan,
    FederationHandoff,
}

public sealed record SquadInitializationProposal(
    string Profile,
    IReadOnlyList<string> Packs,
    string NamingPolicy,
    IReadOnlyDictionary<string, string> MemberNames,
    string ApprovalChannel,
    IReadOnlyList<string> Roles,
    string Scope = "single-squad",
    IReadOnlyDictionary<string, string>? DeliverableRoots = null,
    SquadNotificationConfiguration? Notifications = null,
    string? NotificationQuestion = null,
    IReadOnlyList<string>? MissingMethodologyRoles = null,
    IReadOnlyList<SquadRosterEntry>? Roster = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadModelRoutingConfiguration? ModelRouting = null);

/// <summary>Captured notification choices, never a secret transport endpoint.</summary>
public sealed class SquadNotificationConfiguration
{
    /// <summary>In-chat, GitHub issue approval, or outbound-only webhook notification.</summary>
    public string ApprovalChannel { get; set; } = "in-chat";
    /// <summary>Explicit remote-notification opt-in; false is confirmed at Init, not silently inferred.</summary>
    public bool Enabled { get; set; }
    /// <summary>Optional courtesy contact; never an approval credential.</summary>
    public string? Email { get; set; }
    /// <summary>Authorized assignee/mention handle for a future registered GitHub adapter.</summary>
    public string? GitHubHandle { get; set; }
    /// <summary>Owner/repository containing approval issues; never a URL or secret.</summary>
    public string? GitHubRepository { get; set; }
    /// <summary>Opaque host-owned notifier registration key, not a webhook URL.</summary>
    public string? WebhookBinding { get; set; }
}

/// <summary>A proposed profile/custom roster plus explicit additions, naming and contact choices.</summary>
public sealed class SquadRosterConfiguration
{
    /// <summary>The canonical base profile.</summary>
    public string Profile { get; set; } = "default";
    /// <summary>Additive canonical pack identifiers.</summary>
    public IList<string> Packs { get; set; } = [];
    /// <summary>Custom role subset, or empty to use the profile; Scribe is always included.</summary>
    public IList<string> Roles { get; set; } = [];
    /// <summary>Explicit extra canonical roles; never automatically installed or substituted.</summary>
    public IList<string> AdditionalRoles { get; set; } = [];
    /// <summary>Skip, provided, assigned, or mixed.</summary>
    public string? NamingPolicy { get; set; }
    /// <summary>Role-to-name choices retained for surviving roles on refresh.</summary>
    public IDictionary<string, string> MemberNames { get; set; } = new Dictionary<string, string>();
    /// <summary>Explicit root overrides retained for surviving roles on refresh.</summary>
    public IDictionary<string, string> DeliverableRoots { get; set; } = new Dictionary<string, string>();
    /// <summary>Single-squad or federation; member roots retain their native topology.</summary>
    public string Scope { get; set; } = "single-squad";
    /// <summary>Non-secret optional contact and channel choices.</summary>
    public SquadNotificationConfiguration? Notifications { get; set; }
}

/// <summary>A canonical role menu entry and its actual installed-resource status.</summary>
/// <param name="Role">The exact cast role.</param>
/// <param name="Description">The installed charter's work description.</param>
/// <param name="Selectable">Whether the primary is installed and dispatchable.</param>
/// <param name="Diagnostic">Explicit missing-resource/prerequisite guidance.</param>
public sealed record SquadRoleChoice(SquadRole Role, string Description, bool Selectable, string? Diagnostic);

/// <summary>Read-only Init choices. Nothing in this record authorizes creating state.</summary>
/// <param name="Proposed">Current explicit or discovery-based selection.</param>
/// <param name="Profiles">Canonical profiles and their cues.</param>
/// <param name="Packs">Canonical additive packs and their cues.</param>
/// <param name="Roles">Selectable custom roles with descriptions and exact missing-resource guidance.</param>
/// <param name="Topologies">Single-squad and federation choices.</param>
/// <param name="NamingPolicies">Provided, assigned, mixed and skipped naming.</param>
/// <param name="NotificationChannels">In-chat, GitHub issue and outbound-only webhook choices.</param>
/// <param name="Discovery">Read-only project observations, not permission or model instructions.</param>
/// <param name="SuggestedCouncilRoles">Relevant missing council extensions for explicit confirmation.</param>
public sealed record SquadInitializationChoices(
    SquadRosterConfiguration Proposed, IReadOnlyList<SquadProfile> Profiles, IReadOnlyList<SquadPack> Packs,
    IReadOnlyList<SquadRoleChoice> Roles, IReadOnlyList<string> Topologies, IReadOnlyList<string> NamingPolicies,
    IReadOnlyList<string> NotificationChannels, IReadOnlyList<string> Discovery,
    IReadOnlyList<string> SuggestedCouncilRoles);

/// <summary>A confirmed or declined roster change; prior outputs/history remain intact.</summary>
/// <param name="Status">Actual approval/change status.</param>
/// <param name="Proposal">The exact presented configuration.</param>
/// <param name="AddedRoles">New confirmed roles.</param>
/// <param name="RemovedRoles">Removed roles whose historical spend is retained.</param>
/// <param name="Diagnostic">Explicit outcome or blocked prerequisite.</param>
public sealed record SquadRosterUpdateResult(
    SquadRunStatus Status, SquadInitializationProposal Proposal, IReadOnlyList<string> AddedRoles,
    IReadOnlyList<string> RemovedRoles, string Diagnostic);

public sealed record SquadApprovalRequest(
    SquadApprovalKind Kind,
    string Description,
    string? Role = null,
    string? ToolName = null,
    string? ArgumentsJson = null,
    SquadInitializationProposal? Initialization = null,
    IReadOnlyList<SquadDispatchEvidence>? Evidence = null,
    SquadRoutingProposal? Routing = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadModelRoutingConfiguration? ModelRouting = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadCostPreflight? CostPreflight = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadCouncilProposal? Council = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadCouncilVerdict? CouncilVerdict = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadProductionPlan? ProductionPlan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DispatchId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ActionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ProjectWritePaths = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadSkillExecution? SkillScript = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadFederationProposal? Federation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadFederationExecutionPlan? FederationPlan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadFederationGateContext? FederationGate = null);

/// <summary>
/// Classification is a proposal, not an authorization. The human confirms input completeness and
/// required council coverage; RequireCouncil and InputPaths are trusted host overrides.
/// </summary>
public sealed record SquadRoutingProposal(
    string Request,
    string ProducingRole,
    IReadOnlyList<string> InputPaths,
    bool RequiresCouncil,
    bool IncludesRai,
    SquadRunScope Scope = SquadRunScope.Delivery,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? MatchedPatterns = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RouteRationale = null);

public sealed record SquadQuestion(string Role, string Question,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Choices = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DispatchId = null);
public sealed record SquadConfirmedAnswer(string RunId, string Role, string Question, string Answer,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DispatchId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? HumanActionId = null);
public enum SquadRunScope { Delivery, Research, Plan, Review, ArtifactRepair }

public enum SquadRunStatus
{
    Completed,
    ApprovalRequired,
    ApprovalDenied,
    InputRequired,
    Blocked,
    Unsupported,
    LimitReached,
    Failed,
    ResearchCompleted,
    PlanCompleted,
    ReviewCompleted,
    ReconciliationRequired,
    ChangesRequested,
    Stopped,
    ArtifactCompleted,
}

public enum SquadEvidenceKind { Artifact, StructuredReturn }
/// <summary>Ledger defects warn; history or checkpoint integrity defects block dependent work.</summary>
public enum SquadEvidenceFailureClass
{
    /// <summary>No evidence-integrity defect was observed.</summary>
    None,
    /// <summary>Only the derived consumption ledger needs correction; evidence barriers remain valid.</summary>
    LedgerOnly,
    /// <summary>Protected history/checkpoint evidence cannot be verified; dependent work must stop.</summary>
    HistoryIntegrity,
}

/// <summary>The result of one bounded, deterministic evidence-reconciliation pass.</summary>
/// <param name="FailureClass">The most restrictive observed defect.</param>
/// <param name="CorrectivePasses">Zero or one corrective pass; never a model Scribe loop.</param>
/// <param name="HistoryVerified">Whether append-only history and checkpoint evidence are valid.</param>
/// <param name="LedgerVerified">Whether the derived ledger agrees with recorded work.</param>
/// <param name="Diagnostics">Explicit correction/warning/failure diagnostics.</param>
public sealed record SquadEvidenceReconciliation(
    SquadEvidenceFailureClass FailureClass, int CorrectivePasses, bool HistoryVerified,
    bool LedgerVerified, IReadOnlyList<string> Diagnostics);

public sealed record SquadArtifactEvidence(string Name, string Path, string Sha256);
public sealed record SquadStructuredReturn(string Contract, System.Text.Json.JsonElement Payload);

/// <summary>
/// Evidence from an actual dispatch. ArtifactPath points to the primary artifact or, for
/// StructuredReturn, the deterministic Scribe's receipt, not an agent-authored deliverable.
/// </summary>
public sealed record SquadDispatchEvidence(
    string DispatchId,
    string Stage,
    string Role,
    string Agent,
    string ArtifactPath,
    string ArtifactSha256,
    string HistoryPath,
    string Summary,
    string Verdict,
    string Risk,
    int BlockingIssues,
    string Model,
    string ModelSource,
    string? ModelTier,
    long? InputTokens,
    long? OutputTokens,
    IReadOnlyList<SquadToolExecution>? ExecutedHostTools = null,
    SquadEvidenceKind EvidenceKind = SquadEvidenceKind.Artifact,
    IReadOnlyList<SquadArtifactEvidence>? Artifacts = null,
    SquadStructuredReturn? StructuredReturn = null,
    int Depth = 1,
    string? AssignmentSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ParentDispatchId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DispatchOrder = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadDispatchModelIdentity? ModelRouting = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? AgentSelectionRationale = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? UnitSlot = null);

/// <summary>A receipt recorded only after the trusted registered function actually returns.</summary>
public sealed record SquadToolExecution(
    string Name, SquadToolEffect Effect, string ArgumentsSha256,
    IReadOnlyList<SquadArtifactEvidence>? Outputs = null, string? Receipt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    SquadToolOutputStatus Status = SquadToolOutputStatus.Succeeded,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    SquadToolReceiptKind ReceiptKind = SquadToolReceiptKind.HostObservation,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadSkillExecution? SkillScript = null);

/// <summary>An actual completed consequential call, even if its owning dispatch did not finish.</summary>
/// <param name="OperationId">The durable operation identity.</param>
/// <param name="DispatchId">The real invoking dispatch.</param>
/// <param name="Role">The authorized roster owner.</param>
/// <param name="Receipt">The verified host result, never a model assertion.</param>
public sealed record SquadExecutedEffect(
    string OperationId, string DispatchId, string Role, SquadToolExecution Receipt);

/// <summary>
/// Completed means that the native delivery cycle produced verified research, plan, output and
/// review evidence with no reported blockers; it does not mean deployed, released or host-validated.
/// </summary>
public sealed record SquadRunResult(
    string RunId,
    SquadRunStatus Status,
    string ResponseText,
    IReadOnlyList<SquadDispatchEvidence> Evidence,
    SquadRunScope Scope = SquadRunScope.Delivery,
    SquadEvidenceFailureClass FailureClass = SquadEvidenceFailureClass.None,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<SquadExecutedEffect>? Effects = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadConsumptionSummary? Consumption = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadCostPreflight? CostPreflight = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadProductionPlan? ProductionPlan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SquadProductionHandoff>? ProductionHandoffs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SquadPendingHumanAction>? PendingHumanActions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadModeResult? ModeOutcome = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadWatchTerminalReceipt? WatchTerminal = null);
