using HveSquad.AgentFramework.Artifacts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Host configuration for the interactive, native single-squad delivery runtime.</summary>
public sealed class SquadRuntimeOptions
{
    public string ProjectPath { get; set; } = Environment.CurrentDirectory;
    public string Profile { get; set; } = "default";
    public IList<string> Packs { get; set; } = [];
    public string Version { get; set; } = "latest";
    public string? NamingPolicy { get; set; }
    public IDictionary<string, string> MemberNames { get; set; } = new Dictionary<string, string>();
    public string? ApprovalChannel { get; set; }

    /// <summary>Only interactive single-squad operation is implemented. Other modes fail closed.</summary>
    public string Mode { get; set; } = "interactive";
    /// <summary>Explicit input artifacts force the intake gate; the coordinator can discover more.</summary>
    public IList<string> InputPaths { get; set; } = [];
    public bool RequireCouncil { get; set; }
    public bool IncludeRaiInCouncil { get; set; }
    public int MaxDispatches { get; set; } = 32;
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

    /// <summary>
    /// Host-owned functions. Registrations declare their real effects and charter permission;
    /// prompts or function arguments cannot grant permission. The host must enforce its own
    /// resource constraints and must never let a registration mutate shared squad state.
    /// </summary>
    public IList<SquadToolRegistration> Tools { get; set; } = [];

    /// <summary>Null role denotes the user-invoked coordinator; clients remain caller-owned.</summary>
    public Func<SquadRosterEntry?, AgentCharter, SquadModelSelection?>? ModelSelector { get; set; }
    public Func<SquadRosterEntry, SquadSkill, bool>? SkillFilter { get; set; }
    public Func<SquadRosterEntry?, SquadInstruction, bool>? InstructionFilter { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public bool EnableOpenTelemetry { get; set; }
    public string OpenTelemetrySourceName { get; set; } = "HveSquad.AgentFramework.Runtime";
}

public sealed record SquadModelSelection(IChatClient ChatClient, string? ModelId = null);

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

/// <summary>
/// Registers a real host capability. OutputEvidence attests successful outputs from the returned
/// result; required source/effect roles cannot complete on a call receipt alone.
/// RequireOutputForCompletion adds an effect requirement for other host-specific producing roles.
/// Merely exposing an optional tool never requires an otherwise unrequested external action.
/// </summary>
public sealed record SquadToolRegistration(
    AIFunction Function,
    string CharterPermission,
    IReadOnlyList<string> AllowedRoles,
    SquadToolEffect Effect,
    Func<object?, SquadToolOutput>? OutputEvidence = null,
    bool RequireOutputForCompletion = false);

/// <summary>
/// Trusted host attestation extracted from an actual function result, never model arguments.
/// ProjectPaths are existing project-relative outputs; Receipt describes successful external
/// execution or observed external data. A failed/dry-run operation must not attest an applied effect.
/// </summary>
public sealed record SquadToolOutput(IReadOnlyList<string> ProjectPaths, string? Receipt = null);

public enum SquadApprovalKind
{
    Initialization,
    Routing,
    Plan,
    Implementation,
    ConsequentialTool,
}

public sealed record SquadInitializationProposal(
    string Profile,
    IReadOnlyList<string> Packs,
    string NamingPolicy,
    IReadOnlyDictionary<string, string> MemberNames,
    string ApprovalChannel,
    IReadOnlyList<string> Roles,
    string Scope = "single-squad");

public sealed record SquadApprovalRequest(
    SquadApprovalKind Kind,
    string Description,
    string? Role = null,
    string? ToolName = null,
    string? ArgumentsJson = null,
    SquadInitializationProposal? Initialization = null,
    IReadOnlyList<SquadDispatchEvidence>? Evidence = null,
    SquadRoutingProposal? Routing = null);

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
    SquadRunScope Scope = SquadRunScope.Delivery);

public sealed record SquadQuestion(string Role, string Question);
public sealed record SquadConfirmedAnswer(string RunId, string Role, string Question, string Answer);
public enum SquadRunScope { Delivery, Research, Plan, Review }

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
}

public enum SquadEvidenceKind { Artifact, StructuredReturn }
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
    SquadStructuredReturn? StructuredReturn = null);

/// <summary>A receipt recorded only after the trusted registered function actually returns.</summary>
public sealed record SquadToolExecution(
    string Name, SquadToolEffect Effect, string ArgumentsSha256,
    IReadOnlyList<SquadArtifactEvidence>? Outputs = null, string? Receipt = null);

/// <summary>
/// Completed means that the native delivery cycle produced verified research, plan, output and
/// review evidence with no reported blockers; it does not mean deployed, released or host-validated.
/// </summary>
public sealed record SquadRunResult(
    string RunId,
    SquadRunStatus Status,
    string ResponseText,
    IReadOnlyList<SquadDispatchEvidence> Evidence,
    SquadRunScope Scope = SquadRunScope.Delivery);
