using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sources;

/// <summary>The native disposition of a released assertion, not a claim that it has passed.</summary>
public enum SquadAssertionDisposition
{
    /// <summary>Equivalent observable behavior implemented by the native runtime.</summary>
    NativeEquivalent,
    /// <summary>A documented native host or packaging equivalent.</summary>
    HostAdaptation,
    /// <summary>Deterministic observation comparison with optional answer judging.</summary>
    SemanticComparison,
}

/// <summary>One assertion's native interpretation and implementation/verification ownership.</summary>
public sealed record SquadReleaseAssertion(
    string Id,
    SquadAssertionDisposition Disposition,
    string Adaptation,
    string VerificationOwner,
    IReadOnlyList<string> ImplementationTasks);

/// <summary>
/// The package's canonical official-release identity and conformance inventory.
/// Presence in this inventory does not establish passed conformance.
/// </summary>
public sealed partial class SupportedSquadRelease
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly Lazy<SupportedSquadRelease> Manifest = new(Load);
    private readonly HashSet<string> _returnsOnlyRoles;
    private readonly Dictionary<string, SquadToolEffect> _roleEffects;

    private SupportedSquadRelease(ManifestDocument document, IReadOnlyList<SquadReleaseAssertion> assertions)
    {
        Repository = document.Repository;
        Tag = document.Tag;
        CommitSha = document.CommitSha;
        HveCoreRepository = document.HveCoreRepository;
        HveCoreCommitSha = document.HveCoreCommitSha;
        BehaviorContractPath = document.BehaviorContractPath;
        ConformanceStatus = document.ConformanceStatus;
        ArtifactContract = document.ArtifactContract;
        LegacyAdvisoryRelease = document.LegacyAdvisoryRelease;
        Assertions = assertions;
        _returnsOnlyRoles = new HashSet<string>(document.RoleEffects.ReturnsOnly, StringComparer.OrdinalIgnoreCase);
        if (_returnsOnlyRoles.Count == 0 || _returnsOnlyRoles.Count != document.RoleEffects.ReturnsOnly.Count ||
            _returnsOnlyRoles.Any(string.IsNullOrWhiteSpace) || document.RoleEffects.ProjectWrite.Count == 0 ||
            document.RoleEffects.External.Count == 0 || document.RoleEffects.ExternalRead.Count == 0)
        {
            throw new InvalidDataException("The supported-release manifest has an incomplete or duplicate role-effect policy.");
        }

        _roleEffects = new Dictionary<string, SquadToolEffect>(StringComparer.OrdinalIgnoreCase);
        AddEffects(document.RoleEffects.ProjectWrite, SquadToolEffect.ProjectWrite);
        AddEffects(document.RoleEffects.External, SquadToolEffect.External);
        AddEffects(document.RoleEffects.ExternalRead, SquadToolEffect.ExternalRead);
    }

    /// <summary>Gets the immutable manifest embedded from the repository's compatibility file.</summary>
    public static SupportedSquadRelease Current => Manifest.Value;

    /// <summary>Gets the official Squad repository.</summary>
    public string Repository { get; }
    /// <summary>Gets the production baseline's stable tag.</summary>
    public string Tag { get; }
    /// <summary>Gets the immutable Squad commit.</summary>
    public string CommitSha { get; }
    /// <summary>Gets the pinned HVE Core dependency repository.</summary>
    public string HveCoreRepository { get; }
    /// <summary>Gets the immutable transitive HVE Core commit.</summary>
    public string HveCoreCommitSha { get; }
    /// <summary>Gets the released behavior-document path at the pinned commit.</summary>
    public string BehaviorContractPath { get; }
    /// <summary>Gets the declared conformance evidence status, independently of release identity.</summary>
    public string ConformanceStatus { get; }
    /// <summary>Gets the native artifact-contract identifier.</summary>
    public string ArtifactContract { get; }
    /// <summary>Gets the older advisory release retained for regression testing.</summary>
    public string LegacyAdvisoryRelease { get; }
    /// <summary>Gets every distinct numbered assertion with explicit native ownership.</summary>
    public IReadOnlyList<SquadReleaseAssertion> Assertions { get; }

    /// <summary>Checks exact identity; a matching tag at a different commit is not supported.</summary>
    /// <param name="release">Provenance to check; null denotes non-official/local artifacts.</param>
    /// <returns>Whether both the tag and commit match the canonical baseline.</returns>
    public bool Matches(SquadRelease? release) =>
        release is not null && Tag.Equals(release.Tag, StringComparison.Ordinal) &&
        CommitSha.Equals(release.CommitSha, StringComparison.OrdinalIgnoreCase);

    internal bool ReturnsOnly(string role) => _returnsOnlyRoles.Contains(role);

    internal SquadToolEffect? RequiredEffect(string role) =>
        _roleEffects.TryGetValue(role, out var effect) ? effect : null;

    private void AddEffects(IEnumerable<string> roles, SquadToolEffect effect)
    {
        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role) || !_roleEffects.TryAdd(role, effect))
            {
                throw new InvalidDataException($"Invalid or duplicate manifest effect contract for role '{role}'.");
            }
        }
    }

    private static SupportedSquadRelease Load()
    {
        using var stream = typeof(SupportedSquadRelease).Assembly.GetManifestResourceStream(
            "HveSquad.AgentFramework.hve-squad-release.json")
            ?? throw new InvalidDataException("The package is missing its canonical supported-release manifest.");
        var document = JsonSerializer.Deserialize<ManifestDocument>(stream, ManifestJson)
            ?? throw new InvalidDataException("The supported-release manifest is empty.");
        if (document.SchemaVersion != 1 || document.Repository != ReleaseArtifactSource.Repository ||
            document.Tag != ReleaseArtifactSource.TestedReleaseTag ||
            !CommitShaPattern().IsMatch(document.CommitSha) ||
            !CommitShaPattern().IsMatch(document.HveCoreCommitSha) ||
            document.HveCoreRepository != "microsoft/hve-core" ||
            string.IsNullOrWhiteSpace(document.BehaviorContractPath) ||
            document.ConformanceStatus is not ("in-progress" or "verified") ||
            document.ArtifactContract != "single-plan")
        {
            throw new InvalidDataException("The supported-release manifest has an invalid identity or contract.");
        }

        var assertions = new List<SquadReleaseAssertion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in document.AssertionFamilies)
        {
            var disposition = family.Disposition switch
            {
                "native-equivalent" => SquadAssertionDisposition.NativeEquivalent,
                "host-adaptation" => SquadAssertionDisposition.HostAdaptation,
                "semantic-comparison" => SquadAssertionDisposition.SemanticComparison,
                _ => throw new InvalidDataException($"Unknown disposition for family '{family.Family}'."),
            };
            if (!families.Add(family.Family) || string.IsNullOrWhiteSpace(family.Adaptation) ||
                string.IsNullOrWhiteSpace(family.VerificationOwner) || family.ImplementationTasks.Count == 0 ||
                family.AssertionIds.Count == 0)
            {
                throw new InvalidDataException($"Incomplete or duplicate assertion family '{family.Family}'.");
            }

            foreach (var id in family.AssertionIds)
            {
                if (!AssertionIdPattern().IsMatch(id) ||
                    !id.StartsWith(family.Family + "-", StringComparison.Ordinal) || !ids.Add(id))
                {
                    throw new InvalidDataException($"Invalid or duplicate released assertion '{id}'.");
                }

                assertions.Add(new(id, disposition, family.Adaptation, family.VerificationOwner,
                    new ReadOnlyCollection<string>(family.ImplementationTasks.ToArray())));
            }
        }

        return new SupportedSquadRelease(document, assertions.AsReadOnly());
    }

    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();
    [GeneratedRegex("^(SQ|RTE|PRF|CON|GATE|FD|DLR|DOC|GOV|LRN|PKG|SEM)-[0-9]{2,3}[a-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex AssertionIdPattern();

    private sealed class ManifestDocument
    {
        public int SchemaVersion { get; init; }
        public string Repository { get; init; } = "";
        public string Tag { get; init; } = "";
        public string CommitSha { get; init; } = "";
        public string HveCoreRepository { get; init; } = "";
        public string HveCoreCommitSha { get; init; } = "";
        public string BehaviorContractPath { get; init; } = "";
        public string ConformanceStatus { get; init; } = "";
        public string ArtifactContract { get; init; } = "";
        public string LegacyAdvisoryRelease { get; init; } = "";
        public RoleEffectsDocument RoleEffects { get; init; } = new();
        public List<AssertionFamily> AssertionFamilies { get; init; } = [];
    }

    private sealed class RoleEffectsDocument
    {
        public List<string> ReturnsOnly { get; init; } = [];
        public List<string> ProjectWrite { get; init; } = [];
        public List<string> External { get; init; } = [];
        public List<string> ExternalRead { get; init; } = [];
    }

    private sealed class AssertionFamily
    {
        public string Family { get; init; } = "";
        public string Disposition { get; init; } = "";
        public string Adaptation { get; init; } = "";
        public string VerificationOwner { get; init; } = "";
        public List<string> ImplementationTasks { get; init; } = [];
        public List<string> AssertionIds { get; init; } = [];
    }
}
