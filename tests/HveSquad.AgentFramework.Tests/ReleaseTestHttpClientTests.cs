using System.Net;
using System.Text;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Tests;

public sealed class ReleaseTestHttpClientTests
{
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
                ? Json("""{"tag_name":"v0.16.2","draft":false,"prerelease":false}""")
                : Json("""{"sha":"0123456789abcdef0123456789abcdef01234567"}""");
        }));
        var source = new ReleaseArtifactSource(httpClient: client, acquisitionFactory: (spec, _) =>
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
