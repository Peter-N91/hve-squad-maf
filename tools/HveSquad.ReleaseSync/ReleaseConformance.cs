using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal sealed record NativeConformanceTest(string Method, int MinimumCases = 1);
internal sealed record NativeConformanceAssertion(string Id, string Adaptation, IReadOnlyList<NativeConformanceTest> Tests);
internal sealed record NativeConformanceCatalog(int SchemaVersion, SquadRelease Release,
    IReadOnlyList<NativeConformanceAssertion> Assertions);
internal sealed record NativeAssertionResult(string Id, string Status, string Adaptation,
    IReadOnlyDictionary<string, int> ExecutedCases);
internal sealed record NativeConformanceReport(int SchemaVersion, SquadRelease Release, string? ImplementationHead,
    string TrxSha256, string PackageSha256, string CatalogSha256, string Status,
    IReadOnlyList<NativeAssertionResult> Assertions, string AnswerStatus = "not judged");
internal sealed record NativePackageEvidence(int SchemaVersion, string Status, string PackageVersion,
    string PackageSha256, string RepackSha256, string AssemblySha256, string ManifestSha256, JsonElement Consumer);

internal static partial class ReleaseConformance
{
    internal static void Validate(NativeConformanceCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || !SupportedSquadRelease.Current.Matches(catalog.Release) ||
            catalog.Assertions.Count != SupportedSquadRelease.Current.Assertions.Count ||
            catalog.Assertions.Any(row => row is null) ||
            !catalog.Assertions.Select(row => row.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(SupportedSquadRelease.Current.Assertions.Select(row => row.Id)))
        {
            throw new InvalidDataException("Native conformance must map every individual assertion of the exact supported release once.");
        }
        foreach (var row in catalog.Assertions)
        {
            if (string.IsNullOrWhiteSpace(row.Adaptation) || row.Adaptation.Length > 2000 ||
                row.Tests.Count is < 1 or > 16 ||
                row.Tests.Any(test => test is null || string.IsNullOrWhiteSpace(test.Method) ||
                    !MethodPattern().IsMatch(test.Method) || test.MinimumCases is < 1 or > 1024) ||
                row.Tests.Select(test => test.Method).Distinct(StringComparer.Ordinal).Count() != row.Tests.Count)
            {
                throw new InvalidDataException("Every native assertion needs a specific rationale and bounded, exact executable method/case evidence: " + row.Id);
            }
        }
    }

    internal static NativeAssertionResult[] VerifyTests(NativeConformanceCatalog catalog,
        IReadOnlyDictionary<string, int> executed)
    {
        Validate(catalog);
        return catalog.Assertions.Select(row =>
        {
            var checks = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var test in row.Tests)
            {
                if (!executed.TryGetValue(test.Method, out var count) || count < test.MinimumCases)
                {
                    throw new InvalidDataException("Unexecuted or partial native conformance evidence for " + row.Id +
                        ": " + test.Method + " requires at least " + test.MinimumCases + " successful cases.");
                }
                checks.Add(test.Method, count);
            }
            return new NativeAssertionResult(row.Id, "passed", row.Adaptation, checks);
        }).ToArray();
    }

    internal static void VerifyPackage(NativePackageEvidence evidence, string manifestPath)
    {
        var consumer = evidence.Consumer;
        if (evidence.SchemaVersion != 1 || evidence.Status != "passed" ||
            string.IsNullOrWhiteSpace(evidence.PackageVersion) ||
            !HashPattern().IsMatch(evidence.PackageSha256) ||
            evidence.RepackSha256 != evidence.PackageSha256 ||
            !HashPattern().IsMatch(evidence.AssemblySha256) ||
            evidence.ManifestSha256 != FileHash(manifestPath) ||
            consumer.ValueKind != JsonValueKind.Object ||
            Text(consumer, "status") != "passed" ||
            !consumer.TryGetProperty("libraryVersion", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            version.GetString()?.Split('+')[0] != evidence.PackageVersion ||
            !consumer.TryGetProperty("release", out var release) ||
            release.ValueKind != JsonValueKind.Object ||
            Text(release, "Tag") != SupportedSquadRelease.Current.Tag ||
            Text(release, "CommitSha") != SupportedSquadRelease.Current.CommitSha ||
            !release.TryGetProperty("assertions", out var assertions) ||
            assertions.ValueKind != JsonValueKind.Number ||
            !assertions.TryGetInt32(out var count) || count != SupportedSquadRelease.Current.Assertions.Count ||
            !consumer.TryGetProperty("sameRunResume", out var resume) || resume.ValueKind != JsonValueKind.True ||
            Number(consumer, "helperAcquisitions") != 0 ||
            Number(consumer, "nativeDispatches") is not > 0 ||
            Number(consumer, "modelCalls") is not > 0 ||
            Text(consumer, "federation") != "actual member execution" ||
            Number(consumer, "externalMutations") != 0 ||
            !consumer.TryGetProperty("actualFormats", out var formats) || formats.ValueKind != JsonValueKind.Array ||
            formats.EnumerateArray().Any(format => format.ValueKind != JsonValueKind.String) ||
            !formats.EnumerateArray().Select(format => format.GetString()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["md", "html", "pdf", "docx"]))
        {
            throw new InvalidDataException("Actual package evidence is missing, partial, unreproducible or bound to a different manifest/consumer.");
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    internal static NativeConformanceReport Verify(string workspace, SquadRelease release,
        IEnumerable<ScenarioObservation> observations, string trxPath, string packageProofPath, string? head)
    {
        if (!SupportedSquadRelease.Current.Matches(release))
        {
            throw new InvalidDataException("Unreviewed candidate identity cannot reuse the supported native conformance catalog.");
        }
        ReleaseEvaluation.VerifyEvidence(release, observations, trxPath);
        var catalogPath = Path.Combine(workspace, "compatibility", "hve-squad-conformance.json");
        var catalog = ReleaseEvaluation.ReadJson<NativeConformanceCatalog>(catalogPath);
        var results = VerifyTests(catalog, ReleaseEvaluation.ReadPassedTests(trxPath));
        var package = ReleaseEvaluation.ReadJson<NativePackageEvidence>(packageProofPath);
        VerifyPackage(package, Path.Combine(workspace, "compatibility", "hve-squad-release.json"));
        var testedAssembly = Path.Combine(workspace, "tests", "HveSquad.AgentFramework.Tests",
            "bin", "Release", "net10.0", "HveSquad.AgentFramework.dll");
        if (package.AssemblySha256 != FileHash(testedAssembly))
        {
            throw new InvalidDataException("The actual package consumer and full native tests must execute identical native DLL bytes.");
        }
        return new(1, release, head, FileHash(trxPath), package.PackageSha256, FileHash(catalogPath), "passed", results);
    }

    private static string FileHash(string path)
    {
        HveSquad.AgentFramework.Runtime.ProjectFiles.RejectLinks(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*Tests\.[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
