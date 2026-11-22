using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal sealed record ReleaseAlignmentResult(
    int SchemaVersion,
    string Status,
    string Diagnostic,
    SquadRelease Baseline,
    SquadRelease Candidate,
    string? HveCoreCommitSha,
    IReadOnlyList<string> ChangedFiles);

internal static partial class ReleaseAlignment
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Bom = new(true);

    internal static ReleaseAlignmentResult Prepare(string workspace, ReleaseEvaluationReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentNullException.ThrowIfNull(report);
        workspace = Path.GetFullPath(workspace);
        ProjectFiles.RejectLinks(workspace);
        ReleaseContracts.ValidateIdentity(report.Baseline);
        ReleaseContracts.ValidateIdentity(report.Candidate);

        if (report.Candidate == report.Baseline || report.Action != "candidate")
        {
            return new(1, "up-to-date", "The verified production baseline already matches the latest official stable release.",
                report.Baseline, report.Candidate, report.Contracts?.HveCoreCommitSha, []);
        }

        if (report.Contracts is not { } contracts)
        {
            return new(1, "needs-code-changes", "Candidate contracts could not be captured; manual investigation is required.",
                report.Baseline, report.Candidate, null, []);
        }

        ReleaseContracts.Validate(contracts);
        var manifestPath = Path.Combine(workspace, "compatibility", "hve-squad-release.json");
        var conformancePath = Path.Combine(workspace, "compatibility", "hve-squad-conformance.json");
        var contractsPath = Path.Combine(workspace, "compatibility", "hve-squad-contracts.json");
        var sourcePath = Path.Combine(workspace, "src", "HveSquad.AgentFramework", "Sources", "ReleaseArtifactSource.cs");

        if (!File.Exists(manifestPath) || !File.Exists(conformancePath) || !File.Exists(sourcePath))
        {
            throw new InvalidDataException("Workspace is missing canonical release-alignment files.");
        }

        var manifestNode = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject()
            ?? throw new InvalidDataException("Invalid hve-squad-release.json.");
        var priorCoreSha = manifestNode["hveCoreCommitSha"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing hveCoreCommitSha in hve-squad-release.json.");
        var mappedIds = manifestNode["assertionFamilies"]?.AsArray()
            .SelectMany(family => family?["assertionIds"]?.AsArray().Select(id => id!.GetValue<string>()) ?? [])
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var candidateIds = contracts.Assertions.Keys.ToHashSet(StringComparer.Ordinal);
        if (!candidateIds.SetEquals(mappedIds))
        {
            var added = candidateIds.Except(mappedIds, StringComparer.Ordinal).Count();
            var removed = mappedIds.Except(candidateIds, StringComparer.Ordinal).Count();
            return new(1, "needs-code-changes",
                $"Upstream behavior contract changed numbered assertion IDs (+{added}/-{removed}); human adapter and conformance mapping updates are required.",
                report.Baseline, report.Candidate, contracts.HveCoreCommitSha, []);
        }

        var changed = new SortedSet<string>(StringComparer.Ordinal);

        manifestNode["tag"] = report.Candidate.Tag;
        manifestNode["commitSha"] = report.Candidate.CommitSha;
        manifestNode["hveCoreCommitSha"] = contracts.HveCoreCommitSha;
        manifestNode["conformanceStatus"] = "verified";
        WriteTextPreservingBom(workspace, "compatibility/hve-squad-release.json",
            manifestNode.ToJsonString( new JsonSerializerOptions { WriteIndented = true }) + "\n", changed);

        var conformanceNode = JsonNode.Parse(File.ReadAllText(conformancePath, Encoding.UTF8))?.AsObject()
            ?? throw new InvalidDataException("Invalid hve-squad-conformance.json.");
        var releaseObj = conformanceNode["release"]?.AsObject()
            ?? throw new InvalidDataException("Missing release object in hve-squad-conformance.json.");
        releaseObj["tag"] = report.Candidate.Tag;
        releaseObj["commitSha"] = report.Candidate.CommitSha;
        WriteTextPreservingBom(workspace, "compatibility/hve-squad-conformance.json",
            conformanceNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", changed);

        WriteTextPreservingBom(workspace, "compatibility/hve-squad-contracts.json",
            JsonSerializer.Serialize(contracts, ReleaseContracts.Json) + "\n", changed);

        var sourceText = File.ReadAllText(sourcePath, Encoding.UTF8);
        var updatedSource = TestedReleaseTagPattern().Replace(sourceText,
            $"public const string TestedReleaseTag = \"{report.Candidate.Tag}\";", 1);
        if (updatedSource == sourceText && !sourceText.Contains($"TestedReleaseTag = \"{report.Candidate.Tag}\";", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Could not locate ReleaseArtifactSource.TestedReleaseTag.");
        }
        WriteTextPreservingBom(workspace, "src/HveSquad.AgentFramework/Sources/ReleaseArtifactSource.cs", updatedSource, changed);

        foreach (var relative in CandidateDocumentationFiles(workspace))
        {
            var full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }
            var original = File.ReadAllText(full, Encoding.UTF8);
            var updated = original
                .Replace(report.Baseline.Tag, report.Candidate.Tag, StringComparison.Ordinal)
                .Replace(report.Baseline.CommitSha, report.Candidate.CommitSha, StringComparison.OrdinalIgnoreCase)
                .Replace(priorCoreSha, contracts.HveCoreCommitSha, StringComparison.OrdinalIgnoreCase);
            if (updated != original)
            {
                WriteTextPreservingBom(workspace, relative, updated, changed);
            }
        }

        return new(1, "prepared",
            $"Prepared {changed.Count} repository file(s) for {report.Candidate.Tag} ({report.Candidate.CommitSha}); full cross-platform validation is required before opening a ready PR.",
            report.Baseline, report.Candidate, contracts.HveCoreCommitSha, changed.ToArray());
    }

    internal static IReadOnlyDictionary<string, string> CollectAlignedFiles(string workspace, ReleaseEvaluationReport report)
    {
        workspace = Path.GetFullPath(workspace);
        ProjectFiles.RejectLinks(workspace);
        var allowed = AllowedAlignmentPaths(workspace, report.Candidate.Tag);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in allowed)
        {
            var full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }
            ProjectFiles.RejectLinks(full);
            files.Add(relative, File.ReadAllText(full, Encoding.UTF8));
        }
        foreach (var required in new[]
        {
            "compatibility/hve-squad-release.json",
            "compatibility/hve-squad-conformance.json",
            "compatibility/hve-squad-contracts.json",
            "src/HveSquad.AgentFramework/Sources/ReleaseArtifactSource.cs",
        })
        {
            if (!files.ContainsKey(required))
            {
                throw new InvalidDataException($"Required aligned file '{required}' is missing.");
            }
        }
        return files;
    }

    internal static HashSet<string> AllowedAlignmentPaths(string workspace, string candidateTag)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "compatibility/hve-squad-release.json",
            "compatibility/hve-squad-conformance.json",
            "compatibility/hve-squad-contracts.json",
            $"compatibility/observations/{candidateTag}.json",
            "src/HveSquad.AgentFramework/Sources/ReleaseArtifactSource.cs",
        };
        foreach (var doc in CandidateDocumentationFiles(workspace))
        {
            paths.Add(doc);
        }
        return paths;
    }

    private static IEnumerable<string> CandidateDocumentationFiles(string workspace)
    {
        yield return "samples/HveSquad.AgentFramework.Sample/native-run.settings.example.json";
        yield return "README.md";
        yield return "src/HveSquad.AgentFramework/README.md";
        yield return "tools/HveSquad.ReleaseSync/README.md";

        var docsRoot = Path.Combine(workspace, "docs");
        if (!Directory.Exists(docsRoot))
        {
            yield break;
        }
        foreach (var html in Directory.EnumerateFiles(docsRoot, "*.html", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(workspace, html).Replace('\\', '/');
        }
    }

    private static void WriteTextPreservingBom(string workspace, string relativePath, string content, SortedSet<string> changed)
    {
        var full = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        ProjectFiles.RejectLinks(Path.GetDirectoryName(full)!);
        var hasBom = false;
        if (File.Exists(full))
        {
            ProjectFiles.RejectLinks(full);
            var existingBytes = File.ReadAllBytes(full);
            hasBom = existingBytes.Length >= 3 && existingBytes[0] == 0xEF && existingBytes[1] == 0xBB && existingBytes[2] == 0xBF;
            var existingText = (hasBom ? Utf8Bom : Utf8NoBom).GetString(existingBytes).TrimStart('\uFEFF');
            if (existingText == content)
            {
                return;
            }
        }
        File.WriteAllText(full, content, hasBom ? Utf8Bom : Utf8NoBom);
        changed.Add(relativePath);
    }

    [GeneratedRegex(@"public\s+const\s+string\s+TestedReleaseTag\s*=\s*""v[0-9]+\.[0-9]+\.[0-9]+""\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex TestedReleaseTagPattern();
}
