using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

internal sealed class RunCheckpoint
{
    public required string RunId { get; init; }
    public required string Request { get; init; }
    public required string SquadRoot { get; init; }
    public required string Fingerprint { get; init; }
    public required string HostBinding { get; init; }
    public required RunLimits Limits { get; init; }
    public SquadRoutingProposal? Routing { get; set; }
    public List<SquadDispatchEvidence> Evidence { get; set; } = [];
    public List<SquadArtifactEvidence> InputBindings { get; set; } = [];
    public List<string> ApprovedGates { get; set; } = [];
    public RunBudget Budget { get; set; } = new();
    public TimeSpan ActiveExecutionTime { get; set; }
    public List<DispatchCheckpoint> DispatchStack { get; set; } = [];
    public List<SquadDispatchEvidence> PendingEvidence { get; set; } = [];
    public int NextDispatchOrdinal { get; set; }
    public string? CoordinatorSessionKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadModelRoutingConfiguration? ModelRouting { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelRoutingInputSha256 { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ModelAvailability { get; set; }
    public Dictionary<string, string> SessionBindings { get; set; } = new(StringComparer.Ordinal);
    public bool PipeliningDisabled { get; set; }
    public List<EffectCheckpoint> Effects { get; set; } = [];
    public List<string> RecordedDecisions { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CostRunCheckpoint? Cost { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReadinessCheckpoint? Readiness { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProductionCheckpoint? Production { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModeCheckpoint? Mode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HumanActionCheckpoint>? HumanActions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HumanChangeInput>? HumanChanges { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int HumanRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int HumanReadinessRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int HumanPriorIntakeCycles { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HumanStopRequested { get; set; }
    public string Status { get; set; } = "not-started";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FederationArtifactRepair? FederationRepair { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WatchTerminalCheckpoint? WatchTerminal { get; set; }
}

internal sealed record RunLimits(
    int Dispatches, int Depth, int ModelCalls, int ToolCalls,
    int ArtifactCharacters, int ArtifactsPerDispatch, TimeSpan Timeout, int ConcurrentDispatches = 4);

internal sealed class RunBudget
{
    public int Dispatches { get; set; }
    public int ModelCalls { get; set; }
    public int ToolCalls { get; set; }
}

internal sealed record DispatchCheckpoint(
    string Id, string Role, string Agent, string Stage, int Depth, string Task,
    string? ParentDispatchId = null, string? OrderKey = null, string? SessionKey = null,
    DateTimeOffset? RecordedAt = null)
{
    public IReadOnlyList<DispatchArtifactAllocation> Allocations { get; set; } = [];
    public IReadOnlyList<SquadArtifactEvidence> WrittenArtifacts { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentSelectionRationale { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitSlot { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TopicId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadDiscoveryFindings? PendingDiscoveryFindings { get; set; }
}

internal sealed class EffectCheckpoint
{
    public required string OperationId { get; init; }
    public required string DispatchId { get; init; }
    public required string Role { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsSha256 { get; init; }
    public required string PayloadPath { get; init; }
    public SquadToolEffect Effect { get; init; }
    public IReadOnlyList<string>? ProjectWritePaths { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SquadSkillExecution? SkillScript { get; init; }
    public string Status { get; set; } = "in-flight";
    public SquadToolExecution? Receipt { get; set; }
    public string? ResultPath { get; set; }
    public string? ResultSha256 { get; set; }
}

internal sealed record ProtectedEffectPayload(string ArgumentsSha256, JsonElement Arguments);

internal sealed record AppendOnlyFileEvidence(long Length, string Sha256);
internal sealed record DispatchArtifactAllocation(string Name, string Kind, string Path);

internal sealed record PendingHistoryAppend(
    string Path, AppendOnlyFileEvidence Before, string Content, string AfterSha256);

internal sealed record NativeOperationEvidence(
    string Id, string Operation, string? RunId, string? DispatchId, DateTimeOffset RecordedAt,
    string ExecutionKind = "deterministic-native", int ModelInvocations = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ModelCallObservation>? ObservedDispatchCalls = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadExecutedEffect? ExecutedEffect = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ModelCallObservation? ObservedAttempt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ModelCallCharge? ObservedCharge = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadGateRecord? GateRecord = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadProductionHandoff? ProductionHandoff = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    HumanDecisionReceipt? HumanDecision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    OutputRevisionSnapshot? OutputRevision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? WatchRecord = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ReportHumanGate? HumanGate = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SquadRoleMemoryEntry? RoleMemory = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    LearningOperationRecord? LearningRecord = null);

internal sealed record ModelCallObservation(
    string Id, string RunId, string? DispatchId, string Agent, string? Role, string Stage,
    int CallOrdinal, string Status, string? RequestedModel, string? ObservedModel,
    long? InputTokens = null, long? OutputTokens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? EffectiveModel = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RouteRationale = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? AssignmentClass = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Floor = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? CachedInputTokens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? CacheWriteTokens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? EstimatedContextTokens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? RecordedAt = null);

internal sealed record CoordinatorInvocationEvidence(
    string Id, string RunId, string Agent, string Status, IReadOnlyList<ModelCallObservation> Calls);
