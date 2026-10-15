using System.Diagnostics;
using System.Net;
using System.Text;
using HveSquad.AgentFramework.Sources;
using HveSquad.ReleaseSync;

namespace HveSquad.AgentFramework.Tests;

public sealed class ReleaseTestHttpClientTests
{
    private static readonly string[] Profiles = ["default", "full"];
    private static readonly string[] Modes = ["interactive", "autonomous", "autopilot"];
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fixture-token-not-a-credential")]
    public async Task ReleaseMetadataUsesTheExplicitHostTokenOnly(string? token)
    {
        var expectedToken = string.IsNullOrWhiteSpace(token) ? null : token;
        var requests = 0;
        using var client = ReleaseTestHttpClient.Create(token, new Handler(request =>
        {
            requests++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal(expectedToken, request.Headers.Authorization?.Parameter);
            Assert.Equal(expectedToken is null ? null : "Bearer", request.Headers.Authorization?.Scheme);
            return request.RequestUri.AbsolutePath.Contains("/releases/", StringComparison.Ordinal)
                ? Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}""")
                : Json("""{"sha":"0123456789abcdef0123456789abcdef01234567"}""");
        }));
        var source = new ReleaseArtifactSource("v0.16.2", httpClient: client, acquisitionFactory: (spec, _) =>
        {
            Assert.Equal("Peter-N91/hve-squad#0123456789abcdef0123456789abcdef01234567", spec);
            return new FixedSource();
        });

        var result = await source.ResolveAsync();

        Assert.Equal(2, requests);
        Assert.Equal("v0.16.2", result.Release?.Tag);
        if (expectedToken is not null)
        {
            Assert.DoesNotContain(expectedToken, result.Origin, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RateLimitStillFailsRatherThanSkippingReleaseTests()
    {
        using var client = ReleaseTestHttpClient.Create("fixture-token-not-a-credential",
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"API rate limit exceeded"}"""),
            }));
        var source = new ReleaseArtifactSource(httpClient: client, acquisitionFactory: (_, _) =>
            throw new InvalidOperationException("Acquisition must not run after a metadata failure."));

        await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("ffffffffffffffffffffffffffffffffffffffff", false)]
    [InlineData("main", false)]
    public async Task GivenCandidateCommitBinding_WhenAcquiring_RequiresTheExactCommitRatherThanJustTheTag(string expected, bool succeeds)
    {
        using var client = ReleaseTestHttpClient.Create(null, new Handler(request =>
            request.RequestUri!.AbsolutePath.Contains("/releases/", StringComparison.Ordinal)
                ? Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}""")
                : Json("""{"sha":"0123456789abcdef0123456789abcdef01234567"}""")));
        var source = ReleaseTestHttpClient.Source("v0.16.2", client, expected, (_, _) => new FixedSource());
        if (succeeds)
        {
            var roots = await source.ResolveAsync();
            Assert.Equal(expected, roots.Release!.CommitSha);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await source.ResolveAsync());
        }
    }

    [Theory]
    [InlineData("missing-observation")]
    [InlineData("wrong-release")]
    [InlineData("empty-tests")]
    [InlineData("failed-test")]
    [InlineData("skipped-test")]
    [InlineData("duplicate-test")]
    [InlineData("missing-case")]
    [InlineData("wrong-class")]
    [InlineData("missing-category")]
    [InlineData("valid")]
    public void GivenCandidateEvidence_WhenVerifying_RequiresActualCompleteIdentityBoundCategories(string defect)
    {
        using var host = new RuntimeTestHost();
        var release = new SquadRelease(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha);
        var observations = Profiles.SelectMany(profile => Modes
            .Select(mode => new ScenarioObservation("native-" + profile + "-" + mode, release.Tag,
                defect == "wrong-release" ? new string('f', 40) : release.CommitSha, 1, ["researcher"], [], []))).ToArray();
        if (defect == "missing-observation")
        {
            observations = observations.AsSpan(1).ToArray();
        }
        string[] methods =
        [
            "RuntimeBoundaryTests.ReleasedArtifactTreeRunsWhenExplicitlyProvided",
            "RuntimeFederationTests.GivenActualReleasedArtifacts_WhenInitializingOrPromoting_ExecutesTheRealMemberEngine",
            "RuntimeFederationExecutionTests.GivenActualPinnedRelease_WhenRoutingDependency_ExecutesProducerFirstAndActualConsumerWithExplicitInput",
            "RuntimeWatchTests.GivenActualPinnedRelease_WhenWatchHandlesAnAuthorizedEvent_ExecutesRealInstalledIntakeAndResearch",
            "RuntimeSkillScriptTests.GivenActualAcquiredRelease_WhenExecutingRegisteredReference_UsesItsRealScriptAndExactProvenance",
            "RuntimeReportTests.GivenActualAcquiredRelease_WhenExporting_ReadsRealMafResearchEvidenceAndPreservesState",
            "RuntimeLearningTests.GivenActualAcquiredRelease_WhenLearning_UsesRealMafEvidenceAndNeverMutatesMemoryDuringDiscovery",
        ];
        var cases = Enumerable.Repeat(methods[0], 6).Concat(Enumerable.Repeat(methods[1], 2)).Concat(methods.AsSpan(2).ToArray()).ToArray();
        if (defect == "missing-case")
        {
            cases = cases.AsSpan(1).ToArray();
        }
        var trx = Path.Combine(host.Root, "candidate.trx");
        var total = defect == "empty-tests" ? 0 : cases.Length;
        var results = string.Join("", cases.Select((_, index) => "<UnitTestResult testId=\"test" +
            (defect == "duplicate-test" && index == 1 ? 0 : index) +
            "\" outcome=\"" + (index == 0 && defect == "failed-test" ? "Failed" :
                index == 0 && defect == "skipped-test" ? "NotExecuted" : "Passed") + "\" />"));
        var definitions = string.Join("", cases.Select((method, index) =>
        {
            var split = method.IndexOf('.', StringComparison.Ordinal);
            var owner = defect == "wrong-class" ? "UnrelatedTests" : method[..split];
            var name = defect == "missing-category" && method == methods[^1] ? "UnrelatedPassingTest" : method[(split + 1)..];
            return "<UnitTest id=\"test" + index + "\"><TestMethod className=\"HveSquad.AgentFramework.Tests." +
                owner + "\" name=\"" + name + "\" /></UnitTest>";
        }));
        File.WriteAllText(trx, "<TestRun><Results>" + results + "</Results><TestDefinitions>" + definitions +
            "</TestDefinitions><ResultSummary><Counters total=\"" + total + "\" executed=\"" + total + "\" passed=\"" +
            total + "\" /></ResultSummary></TestRun>");

        if (defect == "valid")
        {
            ReleaseEvaluation.VerifyEvidence(release, observations, trx);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ReleaseEvaluation.VerifyEvidence(release, observations, trx));
        }
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("wrong-head")]
    [InlineData("dirty")]
    public async Task GivenActualGitCheckout_WhenVerifyingCandidate_RequiresCleanExactHead(string state)
    {
        using var host = new RuntimeTestHost();
        try
        {
            await GitFixtureAsync(host.Root, ["init", "--quiet", "--initial-branch=main"]);
            var tracked = Path.Combine(host.Root, "fixture.txt");
            await File.WriteAllTextAsync(tracked, "Original controlled fixture.");
            await GitFixtureAsync(host.Root, ["add", "--", "fixture.txt"]);
            await GitFixtureAsync(host.Root,
            [
                "-c", "user.name=Fixture", "-c", "user.email=fixture@example.com", "-c", "commit.gpgSign=false",
                "-c", "core.hooksPath=" + Path.Combine(host.Root, "empty-hooks"),
                "commit", "--quiet", "-m",
                "test(fixtures): create exact-head fixture\n\n\u2705 - Generated by Copilot\n\nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>",
            ]);
            var head = (await GitFixtureAsync(host.Root, ["rev-parse", "HEAD"])).Trim();
            if (state == "dirty")
            {
                await File.AppendAllTextAsync(tracked, " Changed tracked bytes.");
            }
            if (state == "clean")
            {
                await ReleaseEvaluation.AssertCheckoutAsync(host.Root, head, default);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseEvaluation.AssertCheckoutAsync(host.Root,
                    state == "wrong-head" ? new string('f', 40) : head, default));
            }
        }
        finally
        {
            var git = Path.Combine(host.Root, ".git");
            if (Directory.Exists(git))
            {
                foreach (var path in HveSquad.AgentFramework.Runtime.RuntimeResources.SafeFiles(git))
                {
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                }
            }
        }
    }

    private static async Task<string> GitFixtureAsync(string root, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FixedSource : SquadArtifactSource
    {
        public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SquadArtifactRoots(["test-fixture"], null, "fixture installation"));
    }
}
