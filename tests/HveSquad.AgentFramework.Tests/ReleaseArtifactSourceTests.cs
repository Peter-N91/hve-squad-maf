using System.Net;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using HveSquad.ReleaseSync;

namespace HveSquad.AgentFramework.Tests;

public class ReleaseArtifactSourceTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task LatestStableRelease_ResolvesTagThenExactCommit()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/repos/Peter-N91/hve-squad/releases/latest" =>
                Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}"""),
            "/repos/Peter-N91/hve-squad/commits/v0.16.2" =>
                Json($$"""{"sha":"{{CommitSha}}"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
        });
        string? acquiredSpec = null;
        var expected = new SquadArtifactRoots(["local"], null, "fake acquisition");
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (spec, _) =>
            {
                acquiredSpec = spec;
                return new FixedSource(expected);
            });

        var resolved = await source.ResolveAsync();

        Assert.Equal($"Peter-N91/hve-squad#{CommitSha}", acquiredSpec);
        Assert.Equal(new SquadRelease("v0.16.2", CommitSha), resolved.Release);
        Assert.Contains("v0.16.2", resolved.Origin, StringComparison.Ordinal);
        Assert.Contains(CommitSha, resolved.Origin, StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
            Assert.Contains("hve-squad-maf", request.UserAgent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitStableRelease_UsesPublishedReleaseEndpoint()
    {
        var handler = SuccessfulHandler("v0.16.2");
        var source = new ReleaseArtifactSource(
            "v0.16.2",
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) => new FixedSource());

        await source.ResolveAsync();

        Assert.Equal(
            "/repos/Peter-N91/hve-squad/releases/tags/v0.16.2",
            handler.Requests[0].Path);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("1.2.3")]
    public void Constructor_RejectsMovingPrereleaseAndMalformedRefs(string version)
    {
        Assert.Throws<ArgumentException>(() => new ReleaseArtifactSource(version));
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.16.2","draft":true,"prerelease":false}""", "draft")]
    [InlineData("""{"tag_name":"v0.16.2","draft":false,"prerelease":true}""", "prerelease")]
    [InlineData("""{"tag_name":"release-16","draft":false,"prerelease":false}""", "stable")]
    public async Task Latest_RejectsNonStableReleaseMetadata(string json, string diagnostic)
    {
        var handler = new StubHandler((_, _) => Json(json));
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) => new FixedSource());

        var exception = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains(diagnostic, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidCommitSha_IsRejectedBeforeAcquisition()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.Contains(
            "/releases/",
            StringComparison.Ordinal)
            ? Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}""")
            : Json("""{"sha":"main"}"""));
        var acquisitionCalled = false;
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) =>
            {
                acquisitionCalled = true;
                return new FixedSource();
            });

        var exception = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains("invalid commit SHA", exception.Message, StringComparison.Ordinal);
        Assert.False(acquisitionCalled);
    }

    [Fact]
    public async Task HttpFailure_IsExplicitAndDoesNotFallBack()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"API rate limit exceeded"}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1893456000");
        var handler = new StubHandler((_, _) => response);
        var acquisitionCalled = false;
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) =>
            {
                acquisitionCalled = true;
                return new FixedSource();
            });

        var exception = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains("no fallback", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rate limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(acquisitionCalled);
    }

    [Fact]
    public async Task ConcurrentResolution_SharesOneSuccessfulResult()
    {
        var handler = SuccessfulHandler("v0.16.2");
        var acquisitionCount = 0;
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) =>
            {
                Interlocked.Increment(ref acquisitionCount);
                return new FixedSource();
            });

        var resolutions = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ => await source.ResolveAsync()));

        Assert.All(resolutions, result => Assert.Same(resolutions[0], result));
        Assert.Equal(1, acquisitionCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_DoesNotPoisonLaterResolution()
    {
        var firstRequest = true;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (firstRequest)
            {
                firstRequest = false;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? Json($$"""{"sha":"{{CommitSha}}"}""")
                : Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}""");
        });
        var source = new ReleaseArtifactSource(
            version: null,
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) => new FixedSource());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await source.ResolveAsync(cancellation.Token));
        var resolved = await source.ResolveAsync();

        Assert.Equal("v0.16.2", resolved.Release?.Tag);
    }

    [Fact]
    public async Task GivenPublishedMetadata_WhenInspectingIdentity_DoesNotAcquireOrChangeTheSupportedPin()
    {
        var handler = SuccessfulHandler("v0.16.2");
        using var client = new HttpClient(handler);
        var source = new ReleaseArtifactSource("v0.16.2", httpClient: client,
            acquisitionFactory: (_, _) => throw new InvalidOperationException("Inspection must not install."));
        var before = SupportedSquadRelease.Current;

        var identity = await source.ResolveReleaseAsync();

        Assert.Equal(new("v0.16.2", CommitSha), identity);
        Assert.Same(before, SupportedSquadRelease.Current);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"published_at\":null")]
    [InlineData(",\"published_at\":\"not-a-timestamp\"")]
    public async Task GivenUnpublishedOrMalformedMetadata_WhenResolving_RefusesBeforeAcquisition(string publication)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Json("{\"tag_name\":\"v0.16.2\",\"draft\":false,\"prerelease\":false" + publication + "}")));
        var source = new ReleaseArtifactSource("v0.16.2", httpClient: client,
            acquisitionFactory: (_, _) => throw new InvalidOperationException("Unpublished acquisition is forbidden."));

        await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(async () => await source.ResolveReleaseAsync());
    }

    [Fact]
    public async Task GivenFailedApiWithUnsafeDiagnostics_WhenResolving_DoesNotExportResponseOrHeaders()
    {
        const string secret = "fixture-private-value";
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"" + secret + "\"}") };
        response.Headers.Add("X-RateLimit-Reset", secret);
        response.Headers.Add("X-RateLimit-Remaining", secret);
        using var client = new HttpClient(new StubHandler((_, _) => response));

        var error = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await new ReleaseArtifactSource(httpClient: client).ResolveReleaseAsync());

        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.Contains("unknown", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenUnavailableNetwork_WhenResolving_DoesNotSubstituteARelease()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture failure"))));
        var error = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await new ReleaseArtifactSource(httpClient: client).ResolveReleaseAsync());
        Assert.Contains("no fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivenSameOfficialRelease_WhenCheckingAlignment_SuppressesInstallationAndProposal()
    {
        using var fixture = new SyncFixture();
        var report = await fixture.Evaluation.InspectAsync(null, SyncFixture.MainHead);

        Assert.Equal("up-to-date", report.Action);
        Assert.Equal(0, fixture.Acquisitions);
        Assert.Null(report.Contracts);
        Assert.Equal(SupportedSquadRelease.Current.CommitSha, report.Candidate.CommitSha);
    }

    [Fact]
    public async Task GivenNewStableRelease_WhenEvaluating_ReportsEveryContractDimensionAndBlocksUnverifiedPromotion()
    {
        using var fixture = new SyncFixture(candidate: true);
        var baseline = fixture.Snapshot(baseline: true);

        var report = await fixture.Evaluation.InspectAsync(baseline, SyncFixture.MainHead);

        Assert.Equal("candidate", report.Action);
        Assert.Equal(fixture.Release, report.Candidate);
        Assert.NotEmpty(report.Contracts!.Dependencies);
        Assert.NotEmpty(report.Contracts.Resources);
        Assert.NotEmpty(report.Contracts.Roles);
        Assert.NotEmpty(report.Contracts.Modes);
        Assert.NotEmpty(report.Contracts.Schemas);
        Assert.Equal(279, report.Contracts.Assertions.Count);
        Assert.Contains(report.PromotionBlockers, value => value.StartsWith("unknown-contract-drift:", StringComparison.Ordinal));
        Assert.Contains(report.PromotionBlockers, value => value.StartsWith("candidate-pin-not-reviewed:", StringComparison.Ordinal));
        Assert.Contains(report.PromotionBlockers, value => value.StartsWith("mechanical-conformance-unverified:", StringComparison.Ordinal));
        Assert.Equal("not observed", report.ObservationStatus);
        Assert.Equal("not judged", report.AnswerStatus);
        Assert.Equal(1, fixture.Acquisitions);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("missing")]
    [InlineData("moved")]
    public async Task GivenUnusableOfficialCandidate_WhenInspecting_BlocksWithoutAcquisition(string defect)
    {
        using var fixture = new SyncFixture();
        fixture.MetadataDefect = defect;

        await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            () => fixture.Evaluation.InspectAsync(null, SyncFixture.MainHead));

        Assert.Equal(0, fixture.Acquisitions);
    }

    [Fact]
    public async Task GivenMovedProposedTag_WhenCheckingExactCandidate_RefusesInsteadOfRebinding()
    {
        using var fixture = new SyncFixture(candidate: true);
        var original = fixture.Release;
        fixture.OfficialCommit = new string('f', 40);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Evaluation.InspectAsync(
            fixture.Snapshot(baseline: true), SyncFixture.MainHead, requested: original));

        Assert.Equal(0, fixture.Acquisitions);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("v0.19.0-pre")]
    [InlineData("shortsha")]
    public async Task GivenMutableDependency_WhenEvaluating_StopsBeforeInstallingUnverifiedGraph(string reference)
    {
        using var fixture = new SyncFixture(candidate: true);
        fixture.CoreReference = reference;

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Evaluation.InspectAsync(
            null, SyncFixture.MainHead));

        Assert.Equal(0, fixture.Acquisitions);
    }

    [Fact]
    public async Task GivenUnmappedNumberedAssertion_WhenEvaluating_NamesCoverageAsAPromotionBlocker()
    {
        using var fixture = new SyncFixture(candidate: true);
        var baseline = fixture.Snapshot(baseline: true);
        fixture.ExtraAssertion = true;

        var report = await fixture.Evaluation.InspectAsync(baseline, SyncFixture.MainHead);

        Assert.Contains(report.Drift, delta => delta.Category == "assertion" && delta.Key == "NEW-01" && delta.Change == "added");
        Assert.Contains(report.PromotionBlockers, value => value.StartsWith("unmapped-assertions:", StringComparison.Ordinal));
    }

    [Fact]
    public void GivenEquivalentArtifactLineEndings_WhenCapturing_ProduceTheSameContractFingerprint()
    {
        using var fixture = new SyncFixture();
        var before = fixture.Snapshot();
        foreach (var path in RuntimeResources.SafeFiles(fixture.Host.Artifacts).Where(path => Path.GetExtension(path) == ".md"))
        {
            File.WriteAllText(path, File.ReadAllText(path).ReplaceLineEndings("\r\n"));
        }

        var after = fixture.Snapshot();

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Empty(ReleaseContracts.Compare(before, after));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("role")]
    [InlineData("mode")]
    [InlineData("schema")]
    [InlineData("assertion")]
    [InlineData("dependency")]
    public void GivenChangedContract_WhenComparing_NamesTheActualDriftCategory(string category)
    {
        using var fixture = new SyncFixture();
        var before = fixture.Snapshot();
        switch (category)
        {
            case "resource":
                fixture.Host.Write("skills/apm-renamed-specialist/references/note.md", "Changed original fixture resource.");
                break;
            case "role":
                var roster = Path.Combine(fixture.Host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
                File.WriteAllText(roster, File.ReadAllText(roster).Replace("Test developer", "Different developer", StringComparison.Ordinal));
                break;
            case "mode":
                fixture.Host.Write("skills/apm-renamed-squad/references/gates-and-modes.md", "# Gates\n## New Procedure\nExplicit new fixture contract.");
                break;
            case "schema":
                fixture.Host.Write("skills/apm-renamed-squad/references/entry-schemas.md", "Changed fixture row schema.");
                break;
            case "assertion":
                fixture.ExtraAssertion = true;
                break;
            case "dependency":
                fixture.CoreReference = new string('d', 40);
                break;
        }

        var changed = fixture.Snapshot();

        Assert.Contains(ReleaseContracts.Compare(before, changed), delta => delta.Category == category);
        Assert.NotEqual(before.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void GivenDuplicateAssertion_WhenReadingContract_RefusesIncompleteOwnership()
    {
        Assert.Throws<InvalidDataException>(() => ReleaseContracts.ReadAssertions(
            "| ID | Rule |\n| --- | --- |\n| SQ-01 | first |\n| SQ-01 | second |\n"));
    }

    [Fact]
    public void GivenUnrelatedRatesTableWithLiteralPipes_WhenReadingAssertions_DoesNotMisparseItAsContractOwnership()
    {
        var assertions = ReleaseContracts.ReadAssertions("""
            ## Rates file and calibration
            | Key | Value | Notes |
            | --- | --- | --- |
            | `provider|model` | `1.0` | unrelated literal-pipe documentation |
            ## Required behavior
            | ID | Rule | Source |
            | --- | --- | --- |
            | SQ-01 | original bounded fixture assertion | fixture |
            | SQ-03a | original exact cost-round subassertion | fixture |
            """);
        Assert.Equal(2, assertions.Count);
        Assert.Contains("SQ-01", assertions.Keys);
        Assert.Contains("SQ-03a", assertions.Keys);
    }

    [Fact]
    public void GivenTamperedSnapshot_WhenComparing_DoesNotTreatPublicDigestAsPermission()
    {
        using var fixture = new SyncFixture();
        var snapshot = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => ReleaseContracts.Compare(
            snapshot, snapshot with { Fingerprint = new string('0', 64) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenExplicitProposalWriteChoice_WhenPublishing_RequiresWriteOptInAndPreservesMain(bool allow)
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        if (!allow)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => fixture.Proposals.ProposeAsync(report, false, default));
            Assert.Equal(0, fixture.Mutations);
            return;
        }

        var result = await fixture.Proposals.ProposeAsync(report, true, default);

        Assert.Equal("checks-missing", result.Status);
        Assert.Equal(7, result.PullRequest);
        Assert.Equal(SyncFixture.MainHead, fixture.DefaultHead);
        Assert.Equal(3, fixture.Mutations);
        Assert.Equal(1, fixture.CommitWrites);
        Assert.Equal(1, fixture.PrWrites);
        Assert.Contains("Approve workflows to run", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenExistingProposal_WhenRepeatingTheScheduledCheck_DoesNotWriteAnotherBranchCommitOrPr()
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        _ = await fixture.Proposals.ProposeAsync(report, true, default);
        var mutations = fixture.Mutations;

        var repeated = await fixture.Proposals.ProposeAsync(report, true, default);

        Assert.Equal(7, repeated.PullRequest);
        Assert.Equal(mutations, fixture.Mutations);
        Assert.Equal(1, fixture.CommitWrites);
        Assert.Equal(1, fixture.PrWrites);
    }

    [Theory]
    [InlineData("lost-commit-response")]
    [InlineData("lost-pr-response")]
    public async Task GivenLostProviderResponse_WhenInspectingAndExplicitlyRetrying_DoesNotReplayTheObservedWrite(string defect)
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        fixture.ProposalDefect = defect;
        var error = await Assert.ThrowsAsync<SquadHumanTransportException>(
            () => fixture.Proposals.ProposeAsync(report, true, default));
        Assert.True(error.OutcomeUncertain);
        var mutations = fixture.Mutations;

        var inspected = await fixture.Proposals.StatusAsync(report, default);

        Assert.Equal(defect == "lost-commit-response" ? "proposal-missing" : "checks-missing", inspected.Status);
        Assert.Equal(mutations, fixture.Mutations);
        fixture.ProposalDefect = null;
        var completed = await fixture.Proposals.ProposeAsync(report, true, default);
        Assert.Equal("checks-missing", completed.Status);
        Assert.Equal(1, fixture.CommitWrites);
        Assert.Equal(1, fixture.PrWrites);
        Assert.Equal(SyncFixture.MainHead, fixture.DefaultHead);
    }

    [Fact]
    public async Task GivenIncompleteOrUnsafeReport_WhenProposing_RefusesBeforeAnyProviderMutation()
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Proposals.ProposeAsync(
            report with { Contracts = null }, true, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Proposals.ProposeAsync(
            report with { PromotionBlockers = ["unsafe\nworkflow-command"] }, true, default));
        Assert.Equal(0, fixture.Mutations);
    }

    [Theory]
    [InlineData("permissions", typeof(SquadHumanTransportException))]
    [InlineData("stale-main", typeof(InvalidDataException))]
    [InlineData("branch-collision", typeof(InvalidDataException))]
    [InlineData("wrong-file", typeof(InvalidDataException))]
    [InlineData("graph-error", typeof(InvalidDataException))]
    [InlineData("moved-source", typeof(InvalidDataException))]
    public async Task GivenUnsafeProviderState_WhenProposing_RefusesRatherThanClaimingPublication(string defect, Type exceptionType)
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        fixture.ProposalDefect = defect;
        if (defect == "moved-source")
        {
            fixture.OfficialCommit = new string('f', 40);
        }
        var before = fixture.DefaultHead;

        var exception = await Record.ExceptionAsync(() => fixture.Proposals.ProposeAsync(report, true, default));

        Assert.IsType(exceptionType, exception);
        Assert.Equal(0, fixture.PrWrites);
        Assert.Equal(before, fixture.DefaultHead);
    }

    [Theory]
    [InlineData("waiting", "approval-pending")]
    [InlineData("queued", "checks-running")]
    [InlineData("failure", "checks-failed")]
    [InlineData("action_required", "approval-pending")]
    [InlineData("wrong-head", "checks-missing")]
    [InlineData("wrong-pr", "checks-missing")]
    [InlineData("missing-linux", "checks-failed")]
    [InlineData("wrong-job-head", "checks-failed")]
    [InlineData("skipped-step", "checks-failed")]
    [InlineData("skipped-package", "checks-failed")]
    [InlineData("skipped-conformance", "checks-failed")]
    [InlineData("missing-legacy", "checks-failed")]
    [InlineData("success", "checks-passed")]
    public async Task GivenCandidateChecks_WhenReadingStatus_OnlyExactExecutedSuccessfulHeadJobsCount(string check, string expected)
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        _ = await fixture.Proposals.ProposeAsync(report, true, default);
        fixture.CheckState = check;
        var before = fixture.Mutations;

        var result = await fixture.Proposals.StatusAsync(report, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(SyncFixture.CandidateHead, result.Head);
        Assert.Equal(before, fixture.Mutations);
        if (expected == "checks-passed")
        {
            Assert.Contains("human review", result.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, "closed-requires-maintainer")]
    [InlineData(true, "merged-baseline-unpromoted")]
    public async Task GivenClosedProposal_WhenScheduling_DoesNotReopenOrDuplicate(bool merged, string expected)
    {
        using var fixture = new SyncFixture(candidate: true);
        var report = fixture.Report();
        _ = await fixture.Proposals.ProposeAsync(report, true, default);
        fixture.Closed = true;
        fixture.Merged = merged;
        var before = fixture.Mutations;

        var result = await fixture.Proposals.ProposeAsync(report, true, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(before, fixture.Mutations);
    }

    private sealed class SyncFixture : HttpMessageHandler
    {
        internal const string MainHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string CandidateHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Repository = "fixture-owner/repo";
        private static readonly string[] Platforms = ["linux", "windows"];
        private static readonly string[] RequiredSteps =
        [
            "Build the exact checked-out head",
            "Run candidate release contracts",
            "Preserve exact legacy behavior",
            "Execute the isolated actual-package consumer",
            "Verify every individual native assertion",
            "Verify candidate evidence and reviewed contracts",
        ];
        private readonly HttpClient _client;
        private string? _branchHead;
        private string? _candidateContent;
        private string? _proposalDefect;
        internal RuntimeTestHost Host { get; } = new();
        internal SquadRelease Release { get; }
        internal ReleaseEvaluation Evaluation { get; }
        internal ReleaseProposals Proposals { get; }
        internal string OfficialCommit { get; set; }
        internal string CoreReference { get; set; } = SupportedSquadRelease.Current.HveCoreCommitSha;
        internal string? MetadataDefect { get; set; }
        internal string? ProposalDefect
        {
            get => _proposalDefect;
            set
            {
                _proposalDefect = value;
                if (value == "stale-main")
                {
                    DefaultHead = CandidateHead;
                }
            }
        }
        internal string? CheckState { get; set; }
        internal bool ExtraAssertion { get; set; }
        internal bool Closed { get; set; }
        internal bool Merged { get; set; }
        internal int Acquisitions { get; private set; }
        internal int Mutations { get; private set; }
        internal int CommitWrites { get; private set; }
        internal int PrWrites { get; private set; }
        internal string DefaultHead { get; private set; } = MainHead;
        internal SyncFixture(bool candidate = false)
        {
            var supported = SupportedSquadRelease.Current;
            var version = Version.Parse(supported.Tag[1..]);
            Release = candidate ? new("v" + new Version(version.Major, version.Minor, version.Build + 1), CommitSha) :
                new(supported.Tag, supported.CommitSha);
            OfficialCommit = Release.CommitSha;
            Host.UseCurrentPlanContract();
            Host.Write("skills/apm-renamed-squad/references/gates-and-modes.md",
                "# Gates\n## Interactive Procedure\nfixture interactive\n## Autonomous Procedure\nfixture autonomous\n## Autopilot Procedure\nfixture autopilot\n");
            _client = new HttpClient(this, disposeHandler: false);
            Evaluation = new(_client, (_, _) =>
            {
                Acquisitions++;
                return new FixedSource(Roots(Release));
            });
            Proposals = new(_client, Repository);
        }
        internal ReleaseContractSnapshot Snapshot(bool baseline = false)
        {
            var release = baseline ? new SquadRelease(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha) : Release;
            return ReleaseContracts.Capture(Roots(release), Package(release), Behavior());
        }
        internal ReleaseEvaluationReport Report()
        {
            var baseline = Snapshot(baseline: true);
            var candidate = Snapshot();
            return new(1, "candidate", baseline.Release, Release, MainHead, candidate,
                ReleaseContracts.Compare(baseline, candidate), ["unknown-contract-drift"], [], "not observed");
        }
        private SquadArtifactRoots Roots(SquadRelease release) => new([Host.Artifacts], null, "original synthetic installed fixture") { Release = release };
        private string Package(SquadRelease release) => "name: hve-squad\nversion: " + release.Tag[1..] +
            "\ndependencies:\n  apm:\n    - microsoft/hve-core/fixture#" + CoreReference +
            "\n    - Peter-N91/hve-squad/fixture#" + release.Tag + "\n  mcp: []\n";
        private string Behavior() => "| ID | Rule | Source |\n| --- | --- | --- |\n" +
            string.Join('\n', SupportedSquadRelease.Current.Assertions.Select(assertion => "| " + assertion.Id + " | original fixture rule | fixture |")) +
            (ExtraAssertion ? "\n| NEW-01 | original new fixture rule | fixture |\n" : "\n");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (request.Method != HttpMethod.Get)
            {
                Mutations++;
                if (ProposalDefect == "permissions")
                {
                    return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"fixture token must not be logged\"}") };
                }
            }
            if (path.StartsWith("/repos/Peter-N91/hve-squad/releases/", StringComparison.Ordinal))
            {
                return MetadataDefect == "missing" ? new(HttpStatusCode.NotFound) : Json(JsonSerializer.Serialize(new
                {
                    tag_name = Release.Tag,
                    draft = MetadataDefect == "draft",
                    prerelease = MetadataDefect == "prerelease",
                    published_at = "2026-10-07T18:03:09Z",
                }, ReleaseContracts.Json));
            }
            if (path.StartsWith("/repos/Peter-N91/hve-squad/commits/", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new { sha = MetadataDefect == "moved" ? new string('f', 40) : OfficialCommit }));
            }
            if (path == "/repos/Peter-N91/hve-squad/contents/apm.yml")
            {
                return Blob("apm.yml", Package(Release));
            }
            if (path == "/repos/Peter-N91/hve-squad/contents/tests/squad-behavior-contract.md")
            {
                return Blob("tests/squad-behavior-contract.md", Behavior());
            }
            if (path == "/repos/" + Repository)
            {
                return Response(new { full_name = Repository, default_branch = "main", archived = false, disabled = false });
            }
            if (path == "/repos/" + Repository + "/git/ref/heads/main")
            {
                return Ref("main", DefaultHead);
            }
            var branch = "automation/hve-squad/" + Release.Tag;
            if (path == "/repos/" + Repository + "/git/ref/heads/" + branch)
            {
                return ProposalDefect == "branch-collision" ? Ref(branch, CandidateHead) :
                    _branchHead is null ? new(HttpStatusCode.NotFound) : Ref(branch, _branchHead);
            }
            if (path == "/repos/" + Repository + "/git/refs" && request.Method == HttpMethod.Post)
            {
                _branchHead = MainHead;
                return Ref(branch, _branchHead);
            }
            if (path == "/repos/" + Repository + "/contents/" + ReleaseProposals.CandidatePath)
            {
                return _candidateContent is null ? new(HttpStatusCode.NotFound) : Blob(ReleaseProposals.CandidatePath, _candidateContent);
            }
            if (path == "/graphql")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var input = body.RootElement.GetProperty("variables").GetProperty("input");
                Assert.Equal(MainHead, input.GetProperty("expectedHeadOid").GetString());
                Assert.Equal(Repository, input.GetProperty("branch").GetProperty("repositoryNameWithOwner").GetString());
                Assert.Equal(branch, input.GetProperty("branch").GetProperty("branchName").GetString());
                var addition = Assert.Single(input.GetProperty("fileChanges").GetProperty("additions").EnumerateArray());
                Assert.Equal(ReleaseProposals.CandidatePath, addition.GetProperty("path").GetString());
                _candidateContent = Encoding.UTF8.GetString(Convert.FromBase64String(addition.GetProperty("contents").GetString()!));
                _branchHead = CandidateHead;
                CommitWrites++;
                if (ProposalDefect == "lost-commit-response")
                {
                    return new(HttpStatusCode.InternalServerError);
                }
                return ProposalDefect == "graph-error" ? Response(new { errors = new[] { new { message = "fixture-private-do-not-export" } } }) :
                    Response(new { data = new { createCommitOnBranch = new { commit = new { oid = CandidateHead } } } });
            }
            if (path == "/repos/" + Repository + "/commits/" + CandidateHead)
            {
                return Response(new
                {
                    sha = CandidateHead,
                    parents = new[] { new { sha = MainHead } },
                    commit = new { message = "chore(compatibility): propose HVE Squad " + Release.Tag + "\n\nfixture footer" },
                    files = new[] { new { filename = ProposalDefect == "wrong-file" ? "README.md" : ReleaseProposals.CandidatePath, status = "added" } },
                });
            }
            if (path == "/repos/" + Repository + "/pulls")
            {
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.True(body.RootElement.GetProperty("draft").GetBoolean());
                    Assert.DoesNotContain("fixture rule", body.RootElement.GetProperty("body").GetString()!, StringComparison.Ordinal);
                    PrWrites++;
                    if (ProposalDefect == "lost-pr-response")
                    {
                        return new(HttpStatusCode.InternalServerError);
                    }
                    return Response(Pull(branch));
                }
                return Response(PrWrites == 0 ? Array.Empty<object>() : new[] { Pull(branch) });
            }
            if (path == "/repos/" + Repository + "/pulls/7")
            {
                return Response(Pull(branch));
            }
            if (path == "/repos/" + Repository + "/actions/workflows/release-candidate.yml/runs")
            {
                if (CheckState is null)
                {
                    return Response(new { total_count = 0, workflow_runs = Array.Empty<object>() });
                }
                return Response(new
                {
                    total_count = 1,
                    workflow_runs = new[] { new
                    {
                        id = 20,
                        head_sha = CheckState == "wrong-head" ? MainHead : CandidateHead,
                        @event = "pull_request",
                        path = ReleaseProposals.WorkflowPath,
                        status = CheckState is "waiting" or "queued" ? CheckState : "completed",
                        conclusion = CheckState is "waiting" or "queued" ? null : CheckState is "failure" or "action_required" ? CheckState : "success",
                        pull_requests = new[] { new { number = CheckState == "wrong-pr" ? 8 : 7 } },
                    } },
                });
            }
            if (path == "/repos/" + Repository + "/actions/runs/20/jobs")
            {
                var jobs = Platforms.Where(platform => CheckState != "missing-linux" || platform != "linux")
                    .Select(platform => new
                    {
                        name = "candidate-" + platform,
                        head_sha = CheckState == "wrong-job-head" ? MainHead : CandidateHead,
                        status = "completed",
                        conclusion = "success",
                        steps = RequiredSteps
                            .Where(name => CheckState != "missing-legacy" || name != "Preserve exact legacy behavior")
                            .Select(name => new
                            {
                                name,
                                status = "completed",
                                conclusion = CheckState == "skipped-step" ||
                                    CheckState == "skipped-package" && name == "Execute the isolated actual-package consumer" ||
                                    CheckState == "skipped-conformance" && name == "Verify every individual native assertion" ? "skipped" : "success",
                            }).ToArray(),
                    }).ToArray();
                return Response(new { total_count = jobs.Length, jobs });
            }
            throw new InvalidOperationException("Unexpected original provider fixture request: " + request.Method + " " + path);
        }

        private object Pull(string branch) => new
        {
            number = 7,
            state = Closed ? "closed" : "open",
            html_url = "https://github.com/" + Repository + "/pull/7",
            merged_at = Merged ? "2026-10-12T08:00:00Z" : null,
            head = new { @ref = branch, sha = CandidateHead, repo = new { full_name = Repository } },
            @base = new { @ref = "main", sha = MainHead, repo = new { full_name = Repository } },
        };
        private static HttpResponseMessage Ref(string branch, string head) => Response(new { @ref = "refs/heads/" + branch, @object = new { sha = head } });
        private static HttpResponseMessage Blob(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return Response(new { type = "file", encoding = "base64", path, sha = CommitSha, size = bytes.Length, content = Convert.ToBase64String(bytes) });
        }
        private static HttpResponseMessage Response(object value) => Json(JsonSerializer.Serialize(value, ReleaseContracts.Json));
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
                Host.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static StubHandler SuccessfulHandler(string tag)
    {
        return new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? Json($$"""{"sha":"{{CommitSha}}"}""")
                : Json($$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z"}"""));
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FixedSource : SquadArtifactSource
    {
        private readonly SquadArtifactRoots _roots;

        public FixedSource(SquadArtifactRoots? roots = null)
        {
            _roots = roots ?? new SquadArtifactRoots(["local"], null, "fake acquisition");
        }

        public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_roots);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
            : this((request, cancellationToken) => Task.FromResult(response(request, cancellationToken)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public List<(string Path, string UserAgent)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.UserAgent.ToString()));
            return await _response(request, cancellationToken);
        }
    }
}
