using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using YamlDotNet.RepresentationModel;

namespace HveSquad.ReleaseSync;

internal sealed record ReleaseContractSnapshot(
    int SchemaVersion, SquadRelease Release, string HveCoreCommitSha,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyDictionary<string, string> Roles,
    IReadOnlyDictionary<string, string> Modes,
    IReadOnlyDictionary<string, string> Schemas,
    IReadOnlyDictionary<string, string> Assertions,
    string Fingerprint);

internal sealed record ReleaseContractDelta(string Category, string Key, string Change,
    string? Baseline, string? Candidate);

internal static partial class ReleaseContracts
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    private const int MaximumFiles = 50_000;
    private const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ReleaseContractSnapshot Capture(SquadArtifactRoots roots, string package, string behavior)
    {
        var release = roots.Release ?? throw new InvalidDataException("A contract snapshot requires actual official release provenance.");
        ValidateIdentity(release);
        var dependencies = ReadDependencies(package, release, out var core);
        var artifacts = SquadArtifactLoader.Load(roots.Roots);
        if (artifacts.Warnings.Count != 0)
        {
            throw new InvalidDataException("The acquired candidate has skipped or ambiguous artifacts; no complete contract snapshot is available.");
        }
        var catalog = SquadCatalog.Load(artifacts);
        var runtime = new RuntimeResources(artifacts, roots);
        var resources = Map();
        var count = 0;
        var bytes = 0L;
        void AddFile(string key, string path)
        {
            ProjectFiles.RejectLinks(path);
            var length = new FileInfo(path).Length;
            if (++count > MaximumFiles || length > 64L * 1024 * 1024 ||
                (bytes = checked(bytes + length)) > MaximumBytes)
            {
                throw new InvalidDataException("The release contract inventory exceeds its file/byte bounds; partial inventories are not verified.");
            }
            Add(resources, key, FileDigest(path));
        }
        foreach (var charter in artifacts.Charters)
        {
            AddFile("agent:" + charter.Name, charter.SourcePath);
        }
        foreach (var instruction in artifacts.Instructions)
        {
            AddFile("instruction:" + instruction.FileName, instruction.SourcePath);
        }
        foreach (var skill in artifacts.Skills)
        {
            foreach (var file in RuntimeResources.SafeFiles(skill.DirectoryPath))
            {
                AddFile("skill:" + skill.Name + "/" + Path.GetRelativePath(skill.DirectoryPath, file).Replace('\\', '/'), file);
            }
        }
        Add(resources, "package:apm.yml", Digest(package));
        var roles = Map();
        foreach (var role in catalog.Roles)
        {
            Add(roles, role.Name, Digest(JsonSerializer.Serialize(role, Json)));
        }
        foreach (var profile in catalog.Profiles)
        {
            Add(roles, "profile:" + profile.Name, Digest(JsonSerializer.Serialize(profile, Json)));
        }
        foreach (var pack in catalog.Packs)
        {
            Add(roles, "pack:" + pack.Name, Digest(JsonSerializer.Serialize(pack, Json)));
        }
        var modes = Sections(runtime.Reference("gates-and-modes"), "Procedure");
        var schemas = Map();
        foreach (var reference in new[] { "entry-schemas", "seed-templates" })
        {
            Add(schemas, reference, Digest(runtime.Reference(reference)));
        }
        Add(schemas, "plan-contract", Digest(runtime.RequiresSeparatePlanDetails ? "linked-plan-details" : "single-plan"));
        var assertions = ReadAssertions(behavior);
        var snapshot = new ReleaseContractSnapshot(1, release, core, dependencies, resources, roles,
            modes, schemas, assertions, "");
        return snapshot with { Fingerprint = Fingerprint(snapshot) };
    }

    internal static SortedDictionary<string, string> ReadDependencies(string package, SquadRelease release, out string core)
    {
        ValidateIdentity(release);
        if (package.Length is 0 or > 1_000_000)
        {
            throw new InvalidDataException("The candidate APM manifest is empty or exceeds its bound.");
        }
        var yaml = new YamlStream();
        try
        {
            using var reader = new StringReader(package);
            yaml.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            throw new InvalidDataException("The candidate APM manifest is not valid bounded YAML.");
        }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            Scalar(root, "name") != "hve-squad" || Scalar(root, "version") != release.Tag[1..] ||
            !root.Children.TryGetValue(new YamlScalarNode("dependencies"), out var dependencyNode) ||
            dependencyNode is not YamlMappingNode dependencyMap ||
            !dependencyMap.Children.TryGetValue(new YamlScalarNode("apm"), out var apmNode) ||
            apmNode is not YamlSequenceNode apm || apm.Children.Count is 0 or > 4096)
        {
            throw new InvalidDataException("The candidate APM manifest has an unknown package/dependency schema.");
        }
        if (dependencyMap.Children.Keys.OfType<YamlScalarNode>().Any(key => key.Value is not ("apm" or "mcp")) ||
            dependencyMap.Children.TryGetValue(new YamlScalarNode("mcp"), out var mcp) &&
            mcp is not YamlSequenceNode { Children.Count: 0 })
        {
            throw new InvalidDataException("Unverified external package dependencies block candidate acquisition.");
        }
        var dependencies = Map();
        var coreCommits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in apm.Children)
        {
            var match = Dependency().Match((node as YamlScalarNode)?.Value ?? "");
            if (!match.Success)
            {
                throw new InvalidDataException("A candidate dependency has a malformed repository/resource/ref.");
            }
            var repository = match.Groups["repo"].Value;
            var reference = match.Groups["ref"].Value;
            var self = repository == ReleaseArtifactSource.Repository && reference == release.Tag;
            if (!self && !ReleaseArtifactSource.IsCommit(reference))
            {
                throw new InvalidDataException("A dependency is not commit-pinned or the exact verified self-release tag; mutable dependency acquisition is blocked.");
            }
            var commit = self ? release.CommitSha : reference.ToLowerInvariant();
            if (repository == "microsoft/hve-core")
            {
                coreCommits.Add(commit);
            }
            Add(dependencies, repository + match.Groups["path"].Value, commit);
        }
        if (coreCommits.Count != 1)
        {
            throw new InvalidDataException("The candidate must have one exact HVE Core dependency commit.");
        }
        core = coreCommits.Single();
        return dependencies;
    }

    internal static SortedDictionary<string, string> ReadAssertions(string behavior)
    {
        if (behavior.Length is 0 or > 1_000_000)
        {
            throw new InvalidDataException("The released behavior contract is empty or exceeds its bound.");
        }
        var result = Map();
        foreach (var line in behavior.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = AssertionRow().Match(line.Trim());
            if (match.Success)
            {
                Add(result, match.Groups["id"].Value, Digest(line.Trim()));
            }
            else if (NumberedRow().IsMatch(line.Trim()))
            {
                throw new InvalidDataException("A numbered released assertion has an incomplete or unknown row/ID shape.");
            }
        }
        if (result.Count is 0 or > 4096)
        {
            throw new InvalidDataException("The released behavior contract has no complete numbered assertion inventory.");
        }
        return result;
    }

    internal static ReleaseContractDelta[] Compare(ReleaseContractSnapshot baseline, ReleaseContractSnapshot candidate)
    {
        Validate(baseline);
        Validate(candidate);
        var result = new List<ReleaseContractDelta>();
        foreach (var (category, oldMap, newMap) in new[]
        {
            ("dependency", baseline.Dependencies, candidate.Dependencies),
            ("resource", baseline.Resources, candidate.Resources),
            ("role", baseline.Roles, candidate.Roles),
            ("mode", baseline.Modes, candidate.Modes),
            ("schema", baseline.Schemas, candidate.Schemas),
            ("assertion", baseline.Assertions, candidate.Assertions),
        })
        {
            foreach (var key in oldMap.Keys.Union(newMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                oldMap.TryGetValue(key, out var oldValue);
                newMap.TryGetValue(key, out var newValue);
                if (category == "dependency" && key.StartsWith(ReleaseArtifactSource.Repository + "/", StringComparison.Ordinal) &&
                    oldValue == baseline.Release.CommitSha && newValue == candidate.Release.CommitSha)
                {
                    continue;
                }
                if (oldValue != newValue)
                {
                    result.Add(new(category, key, oldValue is null ? "added" : newValue is null ? "removed" : "changed", oldValue, newValue));
                }
            }
        }
        return result.ToArray();
    }

    internal static void Validate(ReleaseContractSnapshot snapshot)
    {
        ValidateIdentity(snapshot.Release);
        if (snapshot.SchemaVersion != 1 || !ReleaseArtifactSource.IsCommit(snapshot.HveCoreCommitSha) ||
            !Hash().IsMatch(snapshot.Fingerprint) || snapshot.Fingerprint != Fingerprint(snapshot))
        {
            throw new InvalidDataException("The release contract snapshot has an invalid schema, identity or content digest.");
        }
        foreach (var map in new[] { snapshot.Dependencies, snapshot.Resources, snapshot.Roles, snapshot.Modes, snapshot.Schemas, snapshot.Assertions })
        {
            if (map is null || map.Count is 0 or > MaximumFiles || map.Any(pair => !SafeKey(pair.Key) ||
                    !(Hash().IsMatch(pair.Value) || ReleaseArtifactSource.IsCommit(pair.Value))))
            {
                throw new InvalidDataException("A contract snapshot contains an incomplete, unsafe or oversized inventory.");
            }
        }
    }

    internal static void ValidateIdentity(SquadRelease release)
    {
        if (release is null || !ReleaseArtifactSource.IsStableTag(release.Tag) ||
            !ReleaseArtifactSource.IsCommit(release.CommitSha))
        {
            throw new InvalidDataException("A release identity must be a stable published tag and an exact commit.");
        }
    }

    internal static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n").TrimStart('\uFEFF')))).ToLowerInvariant();

    private static string Fingerprint(ReleaseContractSnapshot snapshot) => Digest(JsonSerializer.Serialize(new
    {
        snapshot.HveCoreCommitSha,
        Dependencies = Ordered(snapshot.Dependencies).Select(pair => new
        {
            pair.Key,
            Value = pair.Key.StartsWith(ReleaseArtifactSource.Repository + "/", StringComparison.Ordinal) &&
                pair.Value == snapshot.Release.CommitSha ? "release-self" : pair.Value,
        }),
        Resources = Ordered(snapshot.Resources),
        Roles = Ordered(snapshot.Roles),
        Modes = Ordered(snapshot.Modes),
        Schemas = Ordered(snapshot.Schemas),
        Assertions = Ordered(snapshot.Assertions),
    }, Json));

    private static IEnumerable<KeyValuePair<string, string>> Ordered(IReadOnlyDictionary<string, string> map) =>
        map.OrderBy(pair => pair.Key, StringComparer.Ordinal);

    private static SortedDictionary<string, string> Sections(string text, string suffix)
    {
        var result = Map();
        string? name = null;
        var section = new StringBuilder();
        void Finish()
        {
            if (name is not null)
            {
                Add(result, name, Digest(section.ToString()));
            }
        }
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Finish();
                name = line[3..].EndsWith(suffix, StringComparison.Ordinal) ? line[3..] : null;
                section.Clear();
            }
            section.AppendLine(line);
        }
        Finish();
        return result;
    }

    private static string FileDigest(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var text = StrictUtf8.GetString(bytes);
            if (!text.Contains('\0', StringComparison.Ordinal))
            {
                return Digest(text);
            }
        }
        catch (DecoderFallbackException)
        {
            // Binary resources retain their byte identity rather than replacement-text hashes.
        }
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static SortedDictionary<string, string> Map() => new(StringComparer.Ordinal);
    private static string? Scalar(YamlMappingNode node, string name) =>
        node.Children.TryGetValue(new YamlScalarNode(name), out var value) ? (value as YamlScalarNode)?.Value : null;

    private static bool SafeKey(string key) => key.Length is > 0 and <= 1024 && !key.Any(char.IsControl) &&
        !key.Contains("..", StringComparison.Ordinal) && !key.Contains('\\', StringComparison.Ordinal);

    private static void Add(SortedDictionary<string, string> map, string key, string value)
    {
        if (!SafeKey(key) || !map.TryAdd(key, value))
        {
            throw new InvalidDataException("The release has an unsafe or duplicate canonical contract key.");
        }
    }

    [GeneratedRegex(@"\A(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?<path>(?:/[A-Za-z0-9_./-]+)?)#(?<ref>[A-Za-z0-9.-]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Dependency();
    [GeneratedRegex(@"\A\|\s*(?<id>[A-Z]{2,8}-[0-9]{2,3}[a-z]?)\s*\|.+\|\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssertionRow();
    [GeneratedRegex(@"\A\|\s*[A-Z]{2,8}-[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedRow();
    [GeneratedRegex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hash();
}
