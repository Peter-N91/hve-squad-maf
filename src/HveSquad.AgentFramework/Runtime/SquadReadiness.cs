using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>A stable, attributed gap. Input content cannot grant permission to resolve it.</summary>
/// <param name="Id">A stable lower-kebab-case identity, retained across revalidation.</param>
/// <param name="Dimension">The affected intake dimension or council lens.</param>
/// <param name="Description">The actual finding, not just an issue count.</param>
/// <param name="Blocking">Whether proceeding would require a material guess.</param>
/// <param name="RequiresHuman">Whether a human decision or unavailable external fact is needed.</param>
/// <param name="Sources">Exact verified artifacts supporting the finding.</param>
/// <param name="DependsOn">Other issue identities on which this finding depends.</param>
public sealed record SquadGateIssue(string Id, string Dimension, string Description, bool Blocking,
    bool RequiresHuman, IReadOnlyList<SquadArtifactEvidence> Sources, IReadOnlyList<string> DependsOn);

/// <summary>A checkable council condition with stable identity and verified source attribution.</summary>
/// <param name="Id">An identity local to the reporting role.</param>
/// <param name="Description">What the implementer must acknowledge and satisfy or explicitly accept.</param>
/// <param name="Sources">Verified evidence on which the condition rests.</param>
/// <param name="DependsOn">Conditions from the same role which must precede it.</param>
public sealed record SquadGateCondition(string Id, string Description,
    IReadOnlyList<SquadArtifactEvidence> Sources, IReadOnlyList<string> DependsOn);

/// <summary>An explicit downstream assumption covering one non-blocking intake issue.</summary>
/// <param name="IssueId">The corresponding non-blocking issue.</param>
/// <param name="Description">The assumption carried into research and planning.</param>
public sealed record SquadIntakeAssumption(string IssueId, string Description);

/// <summary>Independent intake findings. All five dimensions must be assessed.</summary>
/// <param name="Dimensions">Completeness, Clarity, Testability, Consistency and Scope Boundaries.</param>
/// <param name="Issues">Actual attributed gaps; an empty collection means none were found.</param>
/// <param name="ClarifyingQuestions">Unanswered human questions, never silently replaced by assumptions.</param>
/// <param name="Assumptions">Explicit assumptions for every non-blocking gap.</param>
public sealed record SquadIntakeFindings(IReadOnlyList<string> Dimensions, IReadOnlyList<SquadGateIssue> Issues,
    IReadOnlyList<string> ClarifyingQuestions, IReadOnlyList<SquadIntakeAssumption> Assumptions);

/// <summary>The remediation author's proposed, human-confirmed supplemental input.</summary>
/// <param name="CorrectedIssueIds">Blocking identities addressed by the actual draft.</param>
/// <param name="Inputs">Original input artifacts to which the draft applies.</param>
public sealed record SquadIntakeRemediation(IReadOnlyList<string> CorrectedIssueIds,
    IReadOnlyList<SquadArtifactEvidence> Inputs);

/// <summary>A council role's independent findings, including domain-specific returned data.</summary>
/// <param name="Issues">Attributed issues, with stable identity and actual source evidence.</param>
/// <param name="Conditions">Checkable conditions, not a count or a verdict alone.</param>
/// <param name="FollowUps">Suggested follow-up work, which is not silently added to scope.</param>
/// <param name="DomainData">Optional actual domain results, such as a cost estimate with honest unknowns.</param>
/// <param name="ResolvedConditions">Independent revalidation checks backed by actual producing evidence.</param>
/// <param name="RequiresCostConfirmation">The cost-manager's explicit confirm-tier recommendation.</param>
/// <param name="ComplianceViolation">A security or RAI violation which always needs human escalation.</param>
public sealed record SquadCouncilFindings(IReadOnlyList<SquadGateIssue> Issues,
    IReadOnlyList<SquadGateCondition> Conditions, IReadOnlyList<string> FollowUps, JsonElement? DomainData = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SquadConditionResolution>? ResolvedConditions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool RequiresCostConfirmation = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ComplianceViolation = false);

/// <summary>An independent validator's check of a prior condition against actual producing evidence.</summary>
/// <param name="ConditionId">The reporting role's exact prior condition identity.</param>
/// <param name="Summary">What was actually checked.</param>
/// <param name="Sources">Exact produced artifact or successful host-output evidence.</param>
public sealed record SquadConditionResolution(string ConditionId, string Summary,
    IReadOnlyList<SquadArtifactEvidence> Sources);

/// <summary>A discovery direction and the recorded reason for choosing or discarding it.</summary>
/// <param name="Id">The stable direction identity.</param>
/// <param name="Description">The explored direction.</param>
/// <param name="Outcome">Chosen or discarded.</param>
/// <param name="Reason">The actual reason, including for discarded directions.</param>
public sealed record SquadDiscoveryOption(string Id, string Description, string Outcome, string Reason);

/// <summary>A research unknown, distinguished from an unanswered material human choice.</summary>
/// <param name="Question">The open question.</param>
/// <param name="Blocking">Whether the direction cannot be agreed without its answer.</param>
public sealed record SquadDiscoveryQuestion(string Question, bool Blocking);

/// <summary>Discovery-only results. Only the analyst also writes the brief.</summary>
/// <param name="Problem">The user's problem framing.</param>
/// <param name="SuccessMeasure">How the outcome will be measured.</param>
/// <param name="Options">Every explored direction, including discarded options and their reasons.</param>
/// <param name="DirectionDecision">A verbatim, actually recorded human answer settling the direction.</param>
/// <param name="WhyNow">The analyst's trigger for the work.</param>
/// <param name="InScope">The agreed included work.</param>
/// <param name="OutOfScope">The agreed exclusions.</param>
/// <param name="Assumptions">Unverified assumptions, explicitly labeled.</param>
/// <param name="OpenQuestions">Research unknowns or blocking unanswered choices.</param>
/// <param name="Objections">Severity-graded challenger objections.</param>
/// <param name="RiskiestAssumption">The experimenter's riskiest assumption.</param>
/// <param name="ProposedTest">The cheapest proposed test of that assumption.</param>
public sealed record SquadDiscoveryFindings(string? Problem, string? SuccessMeasure,
    IReadOnlyList<SquadDiscoveryOption> Options, SquadConfirmedAnswer? DirectionDecision,
    string? WhyNow, string? InScope, string? OutOfScope, IReadOnlyList<string> Assumptions,
    IReadOnlyList<SquadDiscoveryQuestion> OpenQuestions, IReadOnlyList<SquadGateIssue> Objections,
    string? RiskiestAssumption = null, string? ProposedTest = null);

/// <summary>A considered council lens, including why it was needed or omitted.</summary>
/// <param name="Lens">The released lens name.</param>
/// <param name="Role">Its canonical role.</param>
/// <param name="Needed">Whether the current work calls for this lens.</param>
/// <param name="Reason">The cue or explicit human selection, or the reason for omission.</param>
public sealed record SquadCouncilLens(string Lens, string Role, bool Needed, string Reason);

/// <summary>A data-only council proposal; only a trusted host confirmation authorizes it.</summary>
/// <param name="TopicId">The current topic.</param>
/// <param name="ScopeSha256">The exact request, owner, inputs and plan binding.</param>
/// <param name="Lenses">Every considered released lens, with inclusion or omission reasons.</param>
/// <param name="Roles">The proposed actual seats.</param>
/// <param name="MissingRoles">Needed seats absent from the confirmed roster.</param>
/// <param name="WaivedRoles">Needed lenses explicitly proposed for waiver.</param>
/// <param name="Reason">A bounded waiver or selection rationale.</param>
public sealed record SquadCouncilProposal(string TopicId, string ScopeSha256,
    IReadOnlyList<SquadCouncilLens> Lenses, IReadOnlyList<string> Roles,
    IReadOnlyList<string> MissingRoles, IReadOnlyList<string> WaivedRoles, string? Reason = null);

/// <summary>Explicit council adjustments. Empty roles normally mean task-fit selection.</summary>
public sealed class SquadCouncilConfiguration
{
    /// <summary>Explicit canonical seats. Empty plus a waiver reason means a full waiver.</summary>
    public IList<string> Roles { get; set; } = [];
    /// <summary>Human rationale required when needed lenses are omitted or the council is waived.</summary>
    public string? WaiverReason { get; set; }
    /// <summary>Explicit request to override a low-risk Stop, still requiring its own approval.</summary>
    public string? OverrideReason { get; set; }

    /// <summary>Rejects unknown seats and missing or unbounded waiver/override rationale.</summary>
    /// <exception cref="InvalidDataException">The configuration is not an explicit valid council proposal.</exception>
    public void Validate() => ReadinessContracts.ValidateCouncil(this);
}

/// <summary>A role-qualified condition carried to the implementation approval and assignment.</summary>
/// <param name="Role">The actual independent reporting seat.</param>
/// <param name="Condition">Its exact condition.</param>
public sealed record SquadCouncilCondition(string Role, SquadGateCondition Condition);

/// <summary>The most restrictive recorded council outcome, not an authorization for other gates.</summary>
/// <param name="Verdict">Go, Go-With-Conditions, Stop or Waived.</param>
/// <param name="DecisionRef">The exact durable decision heading.</param>
/// <param name="Proposal">The confirmed current council coverage and scope.</param>
/// <param name="Findings">Only the actually dispatched seats.</param>
/// <param name="Conditions">Every attributed condition acknowledged by the implementer.</param>
/// <param name="OverrideReason">A separately confirmed override, if any.</param>
public sealed record SquadCouncilVerdict(string Verdict, string DecisionRef, SquadCouncilProposal Proposal,
    IReadOnlyList<SquadDispatchEvidence> Findings, IReadOnlyList<SquadCouncilCondition> Conditions,
    string? OverrideReason = null);

internal sealed record SquadGateRecord(string Id, string Kind, string RunId, string TopicId,
    string ScopeSha256, string DecisionRef, JsonElement Payload, DateTimeOffset RecordedAt);

internal sealed class ReadinessCheckpoint
{
    public required string TopicId { get; init; }
    public required string ScopeSha256 { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string DiscoveryInput { get; init; } = "";
    public bool HumanPresent { get; init; }
    public string? DiscoveryDepth { get; set; }
    public string? DiscoveryOptIn { get; set; }
    public string? DiscoveryRecordId { get; set; }
    public List<SquadArtifactEvidence> Inputs { get; set; } = [];
    public string? IntakeRecordId { get; set; }
    public int IntakeCycles { get; set; }
    public bool IntakeComplete { get; set; }
    public SquadCouncilProposal? CouncilProposal { get; set; }
    public SquadCouncilConfiguration? CouncilSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CouncilInputSha256 { get; init; }
    public bool CouncilConfirmed { get; set; }
    public string? CouncilRecordId { get; set; }
}

internal static partial class ReadinessContracts
{
    internal static readonly string[] IntakeDimensions =
        ["Completeness", "Clarity", "Testability", "Consistency", "Scope Boundaries"];
    internal static readonly string[] CouncilRoles =
        ["architect", "security", "cost-manager", "product-owner", "rai"];
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static T Parse<T>(JsonElement? payload) where T : class
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value)
        {
            throw new InvalidDataException($"A complete typed {typeof(T).Name} payload is required.");
        }
        return value.Deserialize<T>(PayloadOptions)
            ?? throw new InvalidDataException($"Missing {typeof(T).Name} findings.");
    }

    internal static void ValidateConfiguration(SquadRuntimeOptions options)
    {
        if (options.Discovery is not (null or "quick" or "standard" or "deep" or "skip"))
        {
            throw new ArgumentException("Discovery must be quick, standard, deep or skip.", nameof(options));
        }
        if (options.TopicId is { } topic && !Identity().IsMatch(topic))
        {
            throw new ArgumentException("TopicId must be a bounded lower-kebab-case identifier.", nameof(options));
        }
        if (options.Council is { } council)
        {
            ValidateCouncil(council);
        }
    }

    internal static SquadCouncilConfiguration Copy(SquadCouncilConfiguration source) => new()
    {
        Roles = source.Roles.ToArray(),
        WaiverReason = source.WaiverReason,
        OverrideReason = source.OverrideReason,
    };

    internal static void ValidateCouncil(SquadCouncilConfiguration council)
    {
        if (council.Roles is null || council.Roles.Any(role => !CouncilRoles.Contains(role, StringComparer.Ordinal)) ||
            council.Roles.Distinct(StringComparer.Ordinal).Count() != council.Roles.Count)
        {
            throw new InvalidDataException("Council seats must be unique exact canonical lens roles.");
        }
        if (council.WaiverReason is not null)
        {
            Text(council.WaiverReason, "council waiver reason");
        }
        if (council.OverrideReason is not null)
        {
            Text(council.OverrideReason, "council override reason");
        }
    }

    internal static void ValidateIssues(IReadOnlyList<SquadGateIssue>? issues,
        IReadOnlyList<SquadArtifactEvidence> inputs, int blockingIssues)
    {
        if (issues is null || issues.Count > 64 || issues.Count(issue => issue.Blocking) != blockingIssues)
        {
            throw new InvalidDataException("Issue details must match the actual blocking issue count and bounded finding set.");
        }
        foreach (var issue in issues)
        {
            Id(issue.Id);
            Text(issue.Dimension, "issue dimension");
            Text(issue.Description, "issue description");
            Sources(issue.Sources, inputs);
            if (issue.RequiresHuman && !issue.Blocking)
            {
                throw new InvalidDataException("An unanswered material human choice cannot be a non-blocking assumption.");
            }
        }
        Dependencies(issues.Select(issue => (issue.Id, issue.DependsOn)).ToArray());
    }

    internal static void ValidateIntake(SquadIntakeFindings findings,
        IReadOnlyList<SquadArtifactEvidence> inputs, string verdict, int blockers)
    {
        ValidateIssues(findings.Issues, inputs, blockers);
        if (findings.Dimensions is null || findings.Dimensions.Count != IntakeDimensions.Length ||
            !IntakeDimensions.All(dimension => findings.Dimensions.Contains(dimension, StringComparer.Ordinal)) ||
            findings.Issues.Any(issue => !IntakeDimensions.Contains(issue.Dimension, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Intake must assess each of the five released dimensions exactly once.");
        }
        Texts(findings.ClarifyingQuestions, "clarifying question");
        if (findings.Assumptions is null || findings.Assumptions.Count > 64 ||
            findings.Assumptions.Select(item => item.IssueId).Distinct(StringComparer.Ordinal).Count() != findings.Assumptions.Count ||
            findings.Assumptions.Any(item => !findings.Issues.Any(issue => issue.Id == item.IssueId && !issue.Blocking)) ||
            findings.Issues.Where(issue => !issue.Blocking).Any(issue => !findings.Assumptions.Any(item => item.IssueId == issue.Id)))
        {
            throw new InvalidDataException("Every non-blocking gap requires one explicit assumption; blocking gaps cannot be assumptions.");
        }
        foreach (var assumption in findings.Assumptions)
        {
            Text(assumption.Description, "assumption");
        }
        if (verdict == "Ready-With-Gaps" && findings.Issues.Count == 0)
        {
            throw new InvalidDataException("Readiness cannot hide blocking gaps, unanswered questions or missing gap details.");
        }
    }

    internal static void ValidateCouncilFindings(SquadCouncilFindings findings,
        IReadOnlyList<SquadArtifactEvidence> inputs, string verdict, int blockers)
    {
        ValidateIssues(findings.Issues, inputs, blockers);
        if (findings.Conditions is null || findings.Conditions.Count > 64)
        {
            throw new InvalidDataException("Council conditions must be an explicit bounded collection.");
        }
        foreach (var condition in findings.Conditions)
        {
            Id(condition.Id);
            Text(condition.Description, "council condition");
            Sources(condition.Sources, inputs);
        }
        Dependencies(findings.Conditions.Select(condition => (condition.Id, condition.DependsOn)).ToArray());
        Texts(findings.FollowUps, "council follow-up");
        if (verdict == "Conditional" && findings.Conditions.Count == 0 ||
            verdict == "Concern" && findings.Issues.Count == 0 && findings.Conditions.Count == 0 && findings.FollowUps.Count == 0)
        {
            throw new InvalidDataException("Conditional or Concern must include actual conditions, issues or follow-ups.");
        }
    }

    internal static void Sources(IReadOnlyList<SquadArtifactEvidence>? sources, IReadOnlyList<SquadArtifactEvidence> inputs)
    {
        if (sources is null || sources.Count == 0 || sources.Count > 16 ||
            sources.Any(source => !inputs.Any(input =>
                input.Path.Equals(source.Path, ProjectFiles.PathComparison) && input.Sha256 == source.Sha256)))
        {
            throw new InvalidDataException("A finding requires exact verified source paths and hashes from this gate's input set.");
        }
    }

    internal static void Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"A bounded nonempty {field} is required.");
        }
    }

    internal static void Texts(IReadOnlyList<string>? values, string field)
    {
        if (values is null || values.Count > 64)
        {
            throw new InvalidDataException($"An explicit bounded {field} collection is required.");
        }
        foreach (var value in values)
        {
            Text(value, field);
        }
    }

    internal static void Id(string? id)
    {
        if (id is null || !Identity().IsMatch(id))
        {
            throw new InvalidDataException("Finding identities must be stable bounded lower-kebab-case identifiers.");
        }
    }

    private static void Dependencies((string Id, IReadOnlyList<string> Dependencies)[] entries)
    {
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length ||
            entries.Any(entry => entry.Dependencies is null || entry.Dependencies.Count > 64 ||
                entry.Dependencies.Distinct(StringComparer.Ordinal).Count() != entry.Dependencies.Count ||
                entry.Dependencies.Any(dependency => dependency == entry.Id || !entries.Any(other => other.Id == dependency))))
        {
            throw new InvalidDataException("Finding dependencies require unique known identities and no self-dependencies.");
        }
        var remaining = entries.ToDictionary(entry => entry.Id, entry => entry.Dependencies.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidDataException("Finding dependencies are cyclic.");
            }
            foreach (var id in ready)
            {
                remaining.Remove(id);
            }
            foreach (var dependencies in remaining.Values)
            {
                dependencies.ExceptWith(ready);
            }
        }
    }

    [GeneratedRegex("^(?=.{1,64}$)[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identity();
}
