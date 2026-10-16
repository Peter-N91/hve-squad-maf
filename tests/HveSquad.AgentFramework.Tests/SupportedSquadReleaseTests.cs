using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Sources;
using HveSquad.ReleaseSync;

namespace HveSquad.AgentFramework.Tests;

public sealed class SupportedSquadReleaseTests
{
    private static readonly string[] PackageFormats = ["md", "html", "pdf", "docx"];
    [Fact]
    public void GivenCanonicalManifest_WhenLoaded_AllNumberedAssertionsHaveOwnership()
    {
        var manifest = SupportedSquadRelease.Current;
        Assert.Equal(279, manifest.Assertions.Count);
        Assert.Equal(279, manifest.Assertions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(manifest.Assertions, assertion => assertion.Id == "SQ-03a");
        Assert.Contains(manifest.Assertions, assertion => assertion.Id == "SQ-20a");
        Assert.Contains(manifest.Assertions, assertion => assertion.Id == "FD-15a");
        Assert.All(manifest.Assertions, a =>
        {
            Assert.NotEmpty(a.ImplementationTasks);
            Assert.Equal("HveSquad.AgentFramework.Tests", a.VerificationOwner);
            Assert.NotEmpty(a.Adaptation);
        });
        Assert.Equal(7, manifest.Assertions.Count(a => a.Disposition == SquadAssertionDisposition.SemanticComparison));
        Assert.Equal("verified", manifest.ConformanceStatus);
        Assert.Equal("18f5ac75a1556e49f1e90647a4816d17499e1d7f", manifest.HveCoreCommitSha);
    }

    [Fact]
    public void GivenDefaults_WhenCreatingOptionsAndSample_UseCanonicalBaseline()
    {
        Assert.Equal(ReleaseArtifactSource.TestedReleaseTag, SupportedSquadRelease.Current.Tag);
        Assert.Equal(SupportedSquadRelease.Current.Tag, new SquadRuntimeOptions().Version);
        Assert.Equal(SupportedSquadRelease.Current.Tag,
            SampleCommand.Parse(["--run", "Research", "--project", "."]).Version);
    }

    [Fact]
    public void GivenUnknownOrMovedIdentity_WhenCheckingSupport_NotAdvertisedAsSupported()
    {
        var manifest = SupportedSquadRelease.Current;
        Assert.True(manifest.Matches(new(manifest.Tag, manifest.CommitSha)));
        Assert.False(manifest.Matches(null));
        Assert.False(manifest.Matches(new(manifest.Tag, new string('a', 40))));
        Assert.False(manifest.Matches(new("v0.19.0", manifest.CommitSha)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("wrong-release")]
    [InlineData("blank-rationale")]
    [InlineData("no-tests")]
    [InlineData("wildcard-method")]
    [InlineData("zero-cases")]
    [InlineData("valid")]
    public void GivenIndividualConformanceMap_WhenValidating_RejectsMissingOrUnverifiableRows(string defect)
    {
        var catalog = ConformanceFixture();
        var rows = catalog.Assertions.ToArray();
        rows[0] = defect switch
        {
            "duplicate" => rows[1],
            "unknown" => rows[0] with { Id = "UNKNOWN-01" },
            "blank-rationale" => rows[0] with { Adaptation = "" },
            "no-tests" => rows[0] with { Tests = [] },
            "wildcard-method" => rows[0] with { Tests = [new("RuntimeTests.*")] },
            "zero-cases" => rows[0] with { Tests = [new("RuntimeTests.RunsRealMafFunctionsAndAgentsInEvidenceGatedOrder", 0)] },
            _ => rows[0],
        };
        catalog = catalog with
        {
            Assertions = defect == "missing" ? rows.AsSpan(1).ToArray() : rows,
            Release = defect == "wrong-release" ? new("v0.19.0", new string('a', 40)) : catalog.Release,
        };

        if (defect == "valid")
        {
            ReleaseConformance.Validate(catalog);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ReleaseConformance.Validate(catalog));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GivenPartialPassingTheory_WhenVerifyingConformance_RequiresEveryDeclaredCase(int cases)
    {
        var catalog = ConformanceFixture();
        var executed = new Dictionary<string, int>(StringComparer.Ordinal);
        if (cases > 0)
        {
            executed.Add("RuntimeTests.RunsRealMafFunctionsAndAgentsInEvidenceGatedOrder", cases);
        }

        if (cases == 2)
        {
            var results = ReleaseConformance.VerifyTests(catalog, executed);
            Assert.Equal(SupportedSquadRelease.Current.Assertions.Count, results.Length);
            Assert.All(results, row => Assert.Equal("passed", row.Status));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ReleaseConformance.VerifyTests(catalog, executed));
        }
    }

    [Fact]
    public void GivenRepositoryConformanceCatalog_WhenLoading_EveryRowNamesRealExecutableChecks()
    {
        var catalog = ReleaseEvaluation.ReadJson<NativeConformanceCatalog>(
            Path.Combine(Workspace(), "compatibility", "hve-squad-conformance.json"));
        ReleaseConformance.Validate(catalog);
        foreach (var test in catalog.Assertions.SelectMany(row => row.Tests))
        {
            var split = test.Method.IndexOf('.', StringComparison.Ordinal);
            var owner = typeof(SupportedSquadReleaseTests).Assembly.GetType(
                "HveSquad.AgentFramework.Tests." + test.Method[..split]);
            Assert.NotNull(owner);
            var method = owner.GetMethod(test.Method[(split + 1)..]);
            Assert.NotNull(method);
            Assert.Single(method.GetCustomAttributes(typeof(FactAttribute), inherit: true));
            if (test.MinimumCases > 1)
            {
                Assert.True(method.GetCustomAttributes(typeof(InlineDataAttribute), inherit: true).Length >= test.MinimumCases,
                    "The declared minimum must be backed by real distinct data rows: " + test.Method);
            }
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("release")]
    [InlineData("resume")]
    [InlineData("helper-acquisition")]
    [InlineData("format")]
    [InlineData("partial-repack")]
    [InlineData("wrong-manifest")]
    [InlineData("wrong-numeric-type")]
    [InlineData("valid")]
    public void GivenPackageAcceptanceEvidence_WhenVerifying_RequiresActualCompleteManifestBoundConsumerFacts(string defect)
    {
        var supported = SupportedSquadRelease.Current;
        var manifest = Path.Combine(Workspace(), "compatibility", "hve-squad-release.json");
        var consumer = JsonSerializer.SerializeToNode(new
        {
            status = "passed",
            libraryVersion = "0.1.0-preview.1+synthetic-verifier-control",
            release = new { supported.Tag, supported.CommitSha, assertions = supported.Assertions.Count },
            sameRunResume = true,
            helperAcquisitions = 0,
            nativeDispatches = 1,
            modelCalls = 4,
            federation = "actual member execution",
            externalMutations = 0,
            actualFormats = PackageFormats,
        })!;
        switch (defect)
        {
            case "status": consumer["status"] = "pending"; break;
            case "version": consumer["libraryVersion"] = "9.0.0"; break;
            case "release": consumer["release"]!["Tag"] = "v9.0.0"; break;
            case "resume": consumer["sameRunResume"] = false; break;
            case "helper-acquisition": consumer["helperAcquisitions"] = 1; break;
            case "format": consumer["actualFormats"] = new JsonArray("md"); break;
            case "wrong-numeric-type": consumer["helperAcquisitions"] = "0"; break;
        }
        var package = new NativePackageEvidence(1, "passed", "0.1.0-preview.1", new string('A', 64),
            defect == "partial-repack" ? new string('B', 64) : new string('A', 64), new string('C', 64),
            defect == "wrong-manifest" ? new string('D', 64) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))),
            JsonSerializer.SerializeToElement(consumer));

        if (defect == "valid")
        {
            ReleaseConformance.VerifyPackage(package, manifest);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ReleaseConformance.VerifyPackage(package, manifest));
        }
    }

    private static NativeConformanceCatalog ConformanceFixture() => new(1,
        new(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha),
        SupportedSquadRelease.Current.Assertions.Select(assertion => new NativeConformanceAssertion(assertion.Id,
            "Synthetic verifier mutation control, not native behavioral proof.",
            [new("RuntimeTests.RunsRealMafFunctionsAndAgentsInEvidenceGatedOrder", 2)])).ToArray());

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compatibility", "hve-squad-release.json")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("The exact repository compatibility sources are required for conformance validation.");
    }

    [Fact]
    public async Task GivenDefaultReleaseSource_WhenResolving_UsesExactVerifiedTagAndCommit()
    {
        var manifest = SupportedSquadRelease.Current;
        using var handler = new ManifestHandler(manifest.CommitSha);
        using var client = new HttpClient(handler);
        string? acquired = null;
        var source = new ReleaseArtifactSource(httpClient: client, acquisitionFactory: (spec, _) =>
        {
            acquired = spec;
            return new FixedSource();
        });

        var roots = await source.ResolveAsync();

        Assert.Equal($"/repos/Peter-N91/hve-squad/releases/tags/{manifest.Tag}", handler.Paths[0]);
        Assert.Equal($"{manifest.Repository}#{manifest.CommitSha}", acquired);
        Assert.True(manifest.Matches(roots.Release));
    }

    [Fact]
    public async Task GivenMovedOfficialTag_WhenResolving_FailsBeforeAcquisition()
    {
        using var handler = new ManifestHandler(new string('a', 40));
        using var client = new HttpClient(handler);
        var acquired = false;
        var source = new ReleaseArtifactSource(httpClient: client, acquisitionFactory: (_, _) =>
        {
            acquired = true;
            return new FixedSource();
        });

        var error = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains("moved from verified commit", error.Message, StringComparison.Ordinal);
        Assert.False(acquired);
    }

    [Fact]
    public void GivenNoScenarioBaseline_WhenComparing_ReportsUnbaselinedWithoutAFakeFailure()
    {
        var result = Assert.Single(ReleaseObservations.Compare(null, [Observation()]));
        Assert.Equal("unbaselined", result.Status);
        Assert.Null(result.Score);
        Assert.True(result.Advisory);
        Assert.Equal("not judged", result.AnswerStatus);
    }

    [Fact]
    public void GivenFreshBaseline_WhenComparingWithItself_ScoresExactlyOneWithoutAnswerJudging()
    {
        var baseline = Observation();
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [baseline]));
        Assert.Equal(1.0m, result.Score);
        Assert.Equal(1.0m, result.Routing!.Score);
        Assert.Equal(1.0m, result.Deliverables!.Score);
        Assert.Equal(1.0m, result.Gates!.Score);
        Assert.Null(result.AnswerScore);
        Assert.Equal("not judged", result.AnswerStatus);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("dropped")]
    public void GivenChangedActualRoles_WhenComparing_ChangesRoutingAndNamesTheRole(string change)
    {
        var baseline = Observation() with { Roles = ["researcher", "lead"] };
        var candidate = baseline with { Roles = change == "added" ? ["researcher", "lead", "developer"] : ["researcher"] };
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [candidate]));
        Assert.True(result.Routing!.Score < 1m);
        Assert.Equal(change == "added" ? ["developer"] : [], result.Routing.Added);
        Assert.Equal(change == "dropped" ? ["lead"] : [], result.Routing.Dropped);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("extension")]
    [InlineData("dropped")]
    public void GivenChangedActualDeliverables_WhenComparing_ChangesTheRootTypeDimension(string change)
    {
        var baseline = Observation();
        var candidate = baseline with
        {
            Deliverables = change == "dropped" ? [] : [new(change == "root" ? "outputs" : "docs", change == "extension" ? ".txt" : ".md")],
        };
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [candidate]));
        Assert.True(result.Deliverables!.Score < 1m);
        Assert.Contains("docs|.md", result.Deliverables.Dropped);
    }

    [Fact]
    public void GivenFilenameOnlyVariance_WhenComparingRootAndType_DoesNotScoreTopicWordingAsDrift()
    {
        var baseline = Observation();
        var candidate = baseline with { Deliverables = [new("docs", ".md")] };
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [candidate]));
        Assert.Equal(1m, result.Deliverables!.Score);
        Assert.Empty(result.Deliverables.Added);
        Assert.Empty(result.Deliverables.Dropped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenChangedOrDroppedGate_WhenComparing_ChangesGateScoreAndNamesItsVerdict(bool dropped)
    {
        var baseline = Observation();
        var candidate = baseline with { Gates = dropped ? [] : [new("Council", "Stop")] };
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [candidate]));
        Assert.True(result.Gates!.Score < 1m);
        Assert.Contains("Council|Go", result.Gates.Dropped);
        Assert.Equal(dropped ? [] : ["Council|Stop"], result.Gates.Added);
    }

    [Fact]
    public void GivenSeveralScenarioAttempts_WhenComparing_ScoresOnlyTheLatestActualAttempt()
    {
        var baseline = Observation();
        var earlier = baseline with { Attempt = 1, Roles = ["unrelated"] };
        var latest = baseline with { Attempt = 2 };
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [latest, earlier]));
        Assert.Equal(2, result.Attempt);
        Assert.Equal(1m, result.Score);
        Assert.DoesNotContain("unrelated", result.Routing!.Added);
    }

    [Theory]
    [InlineData("zero-attempt")]
    [InlineData("absolute-root")]
    [InlineData("traversal")]
    [InlineData("missing-role")]
    [InlineData("duplicate-attempt")]
    public void GivenUnverifiableObservationShape_WhenComparing_RefusesIt(string defect)
    {
        var observation = Observation();
        var invalid = defect switch
        {
            "zero-attempt" => observation with { Attempt = 0 },
            "absolute-root" => observation with { Deliverables = [new("C:/private", ".md")] },
            "traversal" => observation with { Deliverables = [new("docs/../private", ".md")] },
            "missing-role" => observation with { Roles = [] },
            _ => observation,
        };
        Assert.Throws<InvalidDataException>(() => ReleaseObservations.Compare([observation],
            defect == "duplicate-attempt" ? [invalid, invalid] : [invalid]));
    }

    [Fact]
    public void GivenOptionalJudgedAnswer_WhenComparing_UsesItExplicitlyInsteadOfPretendingASkippedJudgeReturnedZero()
    {
        var baseline = Observation();
        var result = Assert.Single(ReleaseObservations.Compare([baseline], [baseline],
            new Dictionary<string, decimal>(StringComparer.Ordinal) { [baseline.ScenarioId] = 0m }));
        Assert.Equal("judged", result.AnswerStatus);
        Assert.Equal(0.75m, result.Score);
        Assert.Equal(0m, result.AnswerScore);
        Assert.Equal(1m, Assert.Single(ReleaseObservations.Compare([baseline], [baseline])).Score);
    }

    [Fact]
    public async Task GivenActualNativeMafAndMemory_WhenCapturingObservation_ReadsVerifiedFilesNotProseOrNativeOperations()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Inspect the bounded implementation.");
        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        _ = await runtime.CreateMemory(new()
        {
            Binding = "fixture-observation-memory",
            AuthorizeAsync = (_, _) => ValueTask.FromResult(true),
        }).RememberAsync(new("researcher", "Reusable pattern", "Preserve authorized scope", "Local evidence", "Original fixture"));
        var before = NativeBytes(host);
        var calls = host.Client.Calls.Count;

        var observation = await ReleaseObservations.CaptureAsync(host.Project, host.Options.SquadRoot,
            result.RunId, "native-fixture-research", 1,
            (access, _) => ValueTask.FromResult(access.Kind == "release-observation" && access.SquadRoot == host.Options.SquadRoot));

        Assert.Equal(["researcher"], observation.Roles);
        Assert.NotEmpty(observation.Deliverables);
        Assert.DoesNotContain("Local evidence", JsonSerializer.Serialize(observation), StringComparison.Ordinal);
        Assert.DoesNotContain("scribe", observation.Roles);
        Assert.Equal(before, NativeBytes(host));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(1m, Assert.Single(ReleaseObservations.Compare([observation], [observation])).Score);
    }

    [Fact]
    public async Task GivenDeniedObservationScope_WhenReading_DoesNotInitializeRepairOrDispatch()
    {
        using var host = new RuntimeTestHost();
        var before = NativeBytes(host);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReleaseObservations.CaptureAsync(host.Project,
            host.Options.SquadRoot, "uninitialized", "denied-fixture", 1, (_, _) => ValueTask.FromResult(false)));
        Assert.Equal(before, NativeBytes(host));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenChangedActualFixtureOutput_WhenCapturing_RefusesRatherThanScoringModelClaims()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Inspect the bounded implementation.");
        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        File.AppendAllText(Path.Combine(host.Project, Assert.Single(result.Evidence).ArtifactPath), "\nChanged outside its original receipt.");

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => ReleaseObservations.CaptureAsync(host.Project,
            host.Options.SquadRoot, result.RunId, "changed-fixture", 1, (_, _) => ValueTask.FromResult(true)));
    }

    private static ScenarioObservation Observation() => new("native-fixture", SupportedSquadRelease.Current.Tag,
        SupportedSquadRelease.Current.CommitSha, 1, ["researcher"], [new("docs", ".md")], [new("Council", "Go")]);
    private static string[] NativeBytes(RuntimeTestHost host) => RuntimeResources.SafeFiles(host.Project)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(host.Project, path) + ":" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class ManifestHandler(string commit) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = path.Contains("/commits/", StringComparison.Ordinal)
                ? $$"""{"sha":"{{commit}}"}"""
                : $$"""{"tag_name":"{{SupportedSquadRelease.Current.Tag}}","draft":false,"prerelease":false,"published_at":"2026-10-07T18:03:09Z"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FixedSource : SquadArtifactSource
    {
        public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SquadArtifactRoots(["local"], null, "synthetic acquisition"));
    }
}
