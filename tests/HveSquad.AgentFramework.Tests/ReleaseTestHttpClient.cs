using System.Net.Http.Headers;
using System.Globalization;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using HveSquad.ReleaseSync;

namespace HveSquad.AgentFramework.Tests;

internal static class ReleaseTestHttpClient
{
    internal static SquadArtifactSource Source(string version, HttpClient client, string? expectedCommit = null,
        SquadArtifactAcquisitionFactory? acquisition = null) =>
        new BoundSource(new ReleaseArtifactSource(version == "latest" ? null : version,
            httpClient: client, acquisitionFactory: acquisition),
            expectedCommit ?? Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_COMMIT"));

    internal static async Task RecordObservationAsync(SquadRuntime runtime, string project, SquadRunResult result,
        string scenarioId, string squadRoot)
    {
        var output = Environment.GetEnvironmentVariable("HVE_SQUAD_OBSERVATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }
        var attemptText = Environment.GetEnvironmentVariable("HVE_SQUAD_OBSERVATION_ATTEMPT") ?? "1";
        if (!int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1)
        {
            throw new InvalidDataException("A released observation requires a positive actual attempt.");
        }
        var observation = await ReleaseObservations.CaptureAsync(project, squadRoot, result.RunId, scenarioId, attempt,
            (access, _) => ValueTask.FromResult(access.Kind == "release-observation" && access.SquadRoot == squadRoot));
        Assert.Equal(runtime.Provenance.Release!.Tag, observation.ReleaseTag);
        Assert.Equal(runtime.Provenance.Release.CommitSha, observation.ReleaseCommitSha);
        HveSquad.ReleaseSync.Program.Write(Path.Combine(output, scenarioId + ".json"), observation);
    }

    internal static HttpClient Create(string? token, HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private sealed class BoundSource(SquadArtifactSource source, string? expectedCommit) : SquadArtifactSource
    {
        public override async ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
        {
            if (expectedCommit is not null && !ReleaseArtifactSource.IsCommit(expectedCommit))
            {
                throw new InvalidDataException("The required candidate test commit is malformed; no baseline fallback is allowed.");
            }
            var roots = await source.ResolveAsync(cancellationToken);
            if (expectedCommit is not null && roots.Release?.CommitSha != expectedCommit)
            {
                throw new InvalidDataException("The release test acquired a different candidate commit; a moved tag/base run cannot satisfy parity.");
            }
            return roots;
        }
    }
}
