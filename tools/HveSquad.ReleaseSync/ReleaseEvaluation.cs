using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal sealed record ReleaseEvaluationReport(int SchemaVersion, string Action, SquadRelease Baseline,
    SquadRelease Candidate, string? ImplementationHead, ReleaseContractSnapshot? Contracts,
    IReadOnlyList<ReleaseContractDelta> Drift, IReadOnlyList<string> PromotionBlockers,
    IReadOnlyList<ScenarioComparison> Observations, string ObservationStatus = "not observed",
    string AnswerStatus = "not judged");

internal sealed class ReleaseEvaluation(HttpClient client, SquadArtifactAcquisitionFactory? acquisition = null)
{
    private static readonly string[] RequiredScenarios =
    [
        "native-default-interactive", "native-full-interactive", "native-default-autonomous",
        "native-full-autonomous", "native-default-autopilot", "native-full-autopilot",
    ];
    private readonly HumanChannelHttp _http = new(client, new Uri("https://api.github.com/"));

    internal async Task<ReleaseEvaluationReport> InspectAsync(ReleaseContractSnapshot? golden,
        string? implementationHead = null, bool evaluateCurrent = false, SquadRelease? requested = null,
        IReadOnlyList<ScenarioObservation>? observations = null, IReadOnlyList<ScenarioObservation>? baselineObservations = null,
        CancellationToken token = default)
    {
        var supported = SupportedSquadRelease.Current;
        var baseline = new SquadRelease(supported.Tag, supported.CommitSha);
        if (implementationHead is not null && !ReleaseArtifactSource.IsCommit(implementationHead))
        {
            throw new ArgumentException("The implementation head must be an exact Git commit.", nameof(implementationHead));
        }
        var source = new ReleaseArtifactSource(requested?.Tag, httpClient: client, acquisitionFactory: acquisition);
        var candidate = await source.ResolveReleaseAsync(token).ConfigureAwait(false);
        if (requested is not null && candidate != requested)
        {
            throw new InvalidDataException("The candidate tag moved from its proposed exact commit. No new proposal or promotion is permitted.");
        }
        if (!Version.TryParse(candidate.Tag.AsSpan(1), out var candidateVersion) ||
            !Version.TryParse(baseline.Tag.AsSpan(1), out var baselineVersion))
        {
            throw new InvalidDataException("The official stable version exceeds the supported semantic-version bounds.");
        }
        if (candidateVersion < baselineVersion)
        {
            throw new InvalidDataException("GitHub's latest stable selector is older than the verified baseline; downgrade proposals are prohibited.");
        }
        if (candidate == baseline && !evaluateCurrent && requested is null)
        {
            return new(1, "up-to-date", baseline, candidate, implementationHead, null, [], [], []);
        }
        var package = await ReadFileAsync(ReleaseArtifactSource.Repository, candidate.CommitSha, "apm.yml", token).ConfigureAwait(false);
        _ = ReleaseContracts.ReadDependencies(package, candidate, out _);
        var behavior = await ReadFileAsync(ReleaseArtifactSource.Repository, candidate.CommitSha,
            supported.BehaviorContractPath, token).ConfigureAwait(false);
        var roots = await source.ResolveAsync(token).ConfigureAwait(false);
        var after = await new ReleaseArtifactSource(candidate.Tag, httpClient: client).ResolveReleaseAsync(token).ConfigureAwait(false);
        if (roots.Release != candidate || after != candidate)
        {
            throw new InvalidDataException("Release identity changed during acquisition; no candidate contract or promotion was accepted.");
        }
        var contracts = ReleaseContracts.Capture(roots, package, behavior);
        var blockers = new List<string>();
        ReleaseContractDelta[] drift = [];
        if (golden is null)
        {
            blockers.Add("contract-baseline-missing: capture and review the exact supported artifact snapshot");
        }
        else
        {
            ReleaseContracts.Validate(golden);
            if (!supported.Matches(golden.Release) || golden.HveCoreCommitSha != supported.HveCoreCommitSha)
            {
                throw new InvalidDataException("The reviewed contract baseline does not belong to the package's canonical supported release/dependency.");
            }
            drift = ReleaseContracts.Compare(golden, contracts);
            if (drift.Length != 0)
            {
                blockers.Add("unknown-contract-drift: review adapter/schema/resource/assertion changes and update their executable evidence");
            }
        }
        if (!contracts.Assertions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(supported.Assertions.Select(assertion => assertion.Id)))
        {
            blockers.Add("unmapped-assertions: every added, removed or renamed numbered assertion requires reviewed ownership and evidence");
        }
        if (!supported.Matches(candidate) || contracts.HveCoreCommitSha != supported.HveCoreCommitSha)
        {
            blockers.Add("candidate-pin-not-reviewed: update the supported manifest and API mirror only in the review branch");
        }
        if (supported.ConformanceStatus != "verified" || !supported.Matches(candidate))
        {
            blockers.Add("mechanical-conformance-unverified: the canonical inventory still has incomplete acceptance evidence");
        }
        var comparisons = ReleaseObservations.Compare(baselineObservations, observations ?? []);
        foreach (var observation in observations ?? [])
        {
            if (observation.ReleaseTag != candidate.Tag || observation.ReleaseCommitSha != candidate.CommitSha)
            {
                throw new InvalidDataException("An observation belongs to a different release than the exact evaluated candidate.");
            }
        }
        return new(1, candidate == baseline ? "current-evaluated" : "candidate", baseline, candidate,
            implementationHead, contracts, drift, blockers, comparisons,
            comparisons.Length == 0 ? "not observed" : comparisons.Any(item => item.Status == "unbaselined") ? "unbaselined" : "compared");
    }

    internal async Task<string> ReadFileAsync(string repository, string commit, string path, CancellationToken token)
    {
        ReleaseProposals.ValidateRepository(repository);
        if (!ReleaseArtifactSource.IsCommit(commit) || !SafePath(path))
        {
            throw new ArgumentException("An upstream file requires an exact commit and contained repository path.");
        }
        var result = await _http.SendAsync(HttpMethod.Get, "repos/" + repository + "/contents/" + path +
            "?ref=" + commit, null, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub omitted an exact release file.");
        return DecodeFile(result, path);
    }

    internal static string DecodeFile(JsonElement file, string expectedPath)
    {
        var content = HumanChannelHttp.Text(file, "content");
        if (HumanChannelHttp.Text(file, "type") != "file" || HumanChannelHttp.Text(file, "encoding") != "base64" ||
            HumanChannelHttp.Text(file, "path") != expectedPath ||
            !ReleaseArtifactSource.IsCommit(HumanChannelHttp.Text(file, "sha") ?? "") || content is null ||
            !file.TryGetProperty("size", out var size) || !size.TryGetInt32(out var length) || length is < 1 or > 512 * 1024)
        {
            throw new InvalidDataException("GitHub returned an incomplete, wrong-path or oversized exact file.");
        }
        try
        {
            var bytes = Convert.FromBase64String(content);
            if (bytes.Length != length)
            {
                throw new InvalidDataException("GitHub file size does not match its actual bytes.");
            }
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (FormatException)
        {
            throw new InvalidDataException("GitHub file content is not valid base64.");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("GitHub file content is not strict UTF-8.");
        }
    }

    internal static T ReadJson<T>(string path)
    {
        ProjectFiles.RejectLinks(path);
        if (new FileInfo(path).Length is < 1 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("A release-sync input is empty or exceeds its bounded size.");
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReleaseContracts.Json)
            ?? throw new InvalidDataException("A release-sync JSON input is empty.");
    }

    internal static ScenarioObservation[] ReadObservations(string path)
    {
        if (File.Exists(path))
        {
            var result = ReadJson<ScenarioObservation[]>(path);
            _ = ReleaseObservations.Latest(result);
            return result;
        }
        if (!Directory.Exists(path))
        {
            return [];
        }
        ProjectFiles.RejectLinks(path);
        var files = Directory.EnumerateFiles(path, "*.json").Take(4097).ToArray();
        if (files.Length > 4096)
        {
            throw new InvalidDataException("The observation-file inventory exceeds its bound.");
        }
        var observations = files.Select(ReadJson<ScenarioObservation>).ToArray();
        _ = ReleaseObservations.Latest(observations);
        return observations;
    }

    internal static void VerifyEvidence(SquadRelease candidate, IEnumerable<ScenarioObservation> observations, string trxPath)
    {
        ReleaseContracts.ValidateIdentity(candidate);
        var actual = ReleaseObservations.Latest(observations);
        if (actual.Count != RequiredScenarios.Length || RequiredScenarios.Any(id => !actual.TryGetValue(id, out var observation) ||
            observation.ReleaseTag != candidate.Tag || observation.ReleaseCommitSha != candidate.CommitSha))
        {
            throw new InvalidDataException("Candidate evidence is missing an actual mode/profile observation or has the wrong release identity.");
        }
        var executed = ReadPassedTests(trxPath);
        KeyValuePair<string, int>[] required =
        [
            new("RuntimeBoundaryTests.ReleasedArtifactTreeRunsWhenExplicitlyProvided", 6),
            new("RuntimeFederationTests.GivenActualReleasedArtifacts_WhenInitializingOrPromoting_ExecutesTheRealMemberEngine", 2),
            new("RuntimeFederationExecutionTests.GivenActualPinnedRelease_WhenRoutingDependency_ExecutesProducerFirstAndActualConsumerWithExplicitInput", 1),
            new("RuntimeWatchTests.GivenActualPinnedRelease_WhenWatchHandlesAnAuthorizedEvent_ExecutesRealInstalledIntakeAndResearch", 1),
            new("RuntimeSkillScriptTests.GivenActualAcquiredRelease_WhenExecutingRegisteredReference_UsesItsRealScriptAndExactProvenance", 1),
            new("RuntimeReportTests.GivenActualAcquiredRelease_WhenExporting_ReadsRealMafResearchEvidenceAndPreservesState", 1),
            new("RuntimeLearningTests.GivenActualAcquiredRelease_WhenLearning_UsesRealMafEvidenceAndNeverMutatesMemoryDuringDiscovery", 1),
        ];
        if (required.Any(check => !executed.TryGetValue(check.Key, out var count) || count < check.Value))
        {
            throw new InvalidDataException("The candidate report omits a required runtime/helper method or theory case; unrelated passing tests are not parity.");
        }
    }

    internal static IReadOnlyDictionary<string, int> ReadPassedTests(string trxPath)
    {
        ProjectFiles.RejectLinks(trxPath);
        using var reader = XmlReader.Create(trxPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var counters = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "Counters")
            ?? throw new InvalidDataException("The candidate test report has no executed counters.");
        int Count(string name) => int.TryParse(counters.Attribute(name)?.Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new InvalidDataException("The candidate test counters are incomplete.");
        var total = Count("total");
        var results = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
        if (total < 1 || Count("executed") != total || Count("passed") != total ||
            results.Length != total || results.Any(result => result.Attribute("outcome")?.Value != "Passed"))
        {
            throw new InvalidDataException("Candidate parity did not execute every required case successfully; failures, skips and empty/partial reports block promotion.");
        }
        var executedIds = results.Select(result => result.Attribute("testId")?.Value).ToHashSet(StringComparer.Ordinal);
        var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest").ToArray();
        if (executedIds.Contains(null) || executedIds.Count != total || definitions.Length != total ||
            !executedIds.SetEquals(definitions.Select(definition => definition.Attribute("id")?.Value)))
        {
            throw new InvalidDataException("Test results and unique executed definitions must match exactly; duplicate or incomplete case evidence is not accepted.");
        }
        const string prefix = "HveSquad.AgentFramework.Tests.";
        var executed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var method = definition.Elements().SingleOrDefault(child => child.Name.LocalName == "TestMethod")
                ?? throw new InvalidDataException("An executed test has no unique method definition.");
            var owner = method.Attribute("className")?.Value;
            var name = method.Attribute("name")?.Value;
            if (owner?.StartsWith(prefix, StringComparison.Ordinal) != true || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("Executed test evidence requires its exact native class and method identity.");
            }
            var key = owner[prefix.Length..] + "." + name;
            executed[key] = executed.GetValueOrDefault(key) + 1;
        }
        return executed;
    }

    internal static async Task AssertCheckoutAsync(string workspace, string expectedHead, CancellationToken token)
    {
        if (!ReleaseArtifactSource.IsCommit(expectedHead))
        {
            throw new ArgumentException("Candidate checks require an exact requested head.", nameof(expectedHead));
        }
        var head = await GitAsync(workspace, ["--no-pager", "rev-parse", "HEAD"], token).ConfigureAwait(false);
        var dirty = await GitAsync(workspace, ["--no-pager", "status", "--porcelain", "--untracked-files=no"], token).ConfigureAwait(false);
        if (head.Trim() != expectedHead || !string.IsNullOrWhiteSpace(dirty))
        {
            throw new InvalidDataException("Checks must execute a clean checkout of the exact candidate head, not a base/merge commit or dirty tree.");
        }
    }

    private static async Task<string> GitAsync(string workspace, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("HVE_SQUAD_RELEASE_GITHUB_TOKEN");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The read-only Git identity process did not start.");
        try
        {
            var output = BoundedAsync(process.StandardOutput, timeout.Token);
            var error = BoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("Read-only Git checkout verification failed; inspect the repository and Git setup.");
            }
            return text;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> BoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + read > 64 * 1024)
            {
                throw new InvalidDataException("Read-only Git diagnostics exceed their bound.");
            }
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static bool SafePath(string path) => path.Length is > 0 and <= 512 &&
        !path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) &&
        path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '-' or '_');
}
