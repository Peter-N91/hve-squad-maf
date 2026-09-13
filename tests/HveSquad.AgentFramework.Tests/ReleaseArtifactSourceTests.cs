using System.Net;
using System.Text;
using HveSquad.AgentFramework.Sources;

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
                Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false}"""),
            "/repos/Peter-N91/hve-squad/commits/v0.16.2" =>
                Json($$"""{"sha":"{{CommitSha}}"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
        });
        string? acquiredSpec = null;
        var expected = new SquadArtifactRoots(["local"], null, "fake acquisition");
        var source = new ReleaseArtifactSource(
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
            ReleaseArtifactSource.TestedReleaseTag,
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
            ? Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false}""")
            : Json("""{"sha":"main"}"""));
        var acquisitionCalled = false;
        var source = new ReleaseArtifactSource(
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
                : Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false}""");
        });
        var source = new ReleaseArtifactSource(
            httpClient: new HttpClient(handler),
            acquisitionFactory: (_, _) => new FixedSource());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await source.ResolveAsync(cancellation.Token));
        var resolved = await source.ResolveAsync();

        Assert.Equal("v0.16.2", resolved.Release?.Tag);
    }

    private static StubHandler SuccessfulHandler(string tag)
    {
        return new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? Json($$"""{"sha":"{{CommitSha}}"}""")
                : Json($$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false}"""));
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
