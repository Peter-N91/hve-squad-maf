using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Sources;

/// <summary>Creates the immutable acquisition source used for a resolved release commit.</summary>
public delegate SquadArtifactSource SquadArtifactAcquisitionFactory(string packageSpec, string target);

/// <summary>
/// Resolves a published stable HVE Squad GitHub release to its exact commit before acquisition.
/// </summary>
public sealed partial class ReleaseArtifactSource : SquadArtifactSource
{
    public const string RepositoryOwner = "Peter-N91";
    public const string RepositoryName = "hve-squad";
    public const string Repository = RepositoryOwner + "/" + RepositoryName;

    /// <summary>API mirror of the canonical production baseline, validated by its embedded manifest.</summary>
    public const string TestedReleaseTag = "v0.18.1";

    private const string GitHubApiRoot = "https://api.github.com";
    private const int MaximumResponseCharacters = 64 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly object _resolutionGate = new();
    private readonly string? _version;
    private readonly string _target;
    private readonly HttpClient _httpClient;
    private readonly SquadArtifactAcquisitionFactory _acquisitionFactory;
    private Task<SquadArtifactRoots>? _resolutionTask;

    /// <param name="version">
    /// Stable semantic-version tag to acquire. Defaults to the canonical production baseline.
    /// Explicit null inspects GitHub's latest published release; it does not promote that candidate.
    /// </param>
    /// <param name="target">APM target used by the acquisition source.</param>
    /// <param name="httpClient">
    /// Optional host-configured client. Hosts may configure authentication on this client.
    /// </param>
    /// <param name="acquisitionFactory">
    /// Optional factory receiving the exact commit-pinned package spec and target.
    /// </param>
    public ReleaseArtifactSource(
        string? version = TestedReleaseTag,
        string target = "copilot",
        HttpClient? httpClient = null,
        SquadArtifactAcquisitionFactory? acquisitionFactory = null)
    {
        if (version is not null && !StableVersionTagPattern().IsMatch(version))
        {
            throw new ArgumentException(
                $"'{version}' is not a stable semantic-version tag of the form vX.Y.Z.",
                nameof(version));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!TargetPattern().IsMatch(target))
        {
            throw new ArgumentException($"'{target}' is not a valid target name or target list.", nameof(target));
        }

        _version = version;
        _target = target;
        _httpClient = httpClient ?? SharedHttpClient;
        _acquisitionFactory = acquisitionFactory ?? CreateApmSource;
    }

    public override async ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            Task<SquadArtifactRoots> resolution;
            lock (_resolutionGate)
            {
                if (_resolutionTask is { IsCompleted: true, IsCompletedSuccessfully: false })
                {
                    _resolutionTask = null;
                }

                resolution = _resolutionTask ??= ResolveCoreAsync(cancellationToken);
            }

            try
            {
                return await resolution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_resolutionGate)
                {
                    if (ReferenceEquals(_resolutionTask, resolution))
                    {
                        _resolutionTask = null;
                    }
                }

                continue;
            }
            catch
            {
                if (resolution.IsCompleted && !resolution.IsCompletedSuccessfully)
                {
                    lock (_resolutionGate)
                    {
                        if (ReferenceEquals(_resolutionTask, resolution))
                        {
                            _resolutionTask = null;
                        }
                    }
                }

                throw;
            }
        }
    }

    private async Task<SquadArtifactRoots> ResolveCoreAsync(CancellationToken cancellationToken)
    {
        var release = await ResolveReleaseAsync(cancellationToken).ConfigureAwait(false);
        var packageSpec = $"{Repository}#{release.CommitSha}";
        var acquisitionSource = _acquisitionFactory(packageSpec, _target)
            ?? throw new InvalidOperationException("The acquisition factory returned null.");
        var roots = await acquisitionSource.ResolveAsync(cancellationToken).ConfigureAwait(false);
        return roots with
        {
            Origin = $"HVE Squad release '{release.Tag}' at commit '{release.CommitSha}'; {roots.Origin}",
            Release = release,
        };
    }

    /// <summary>Reads fresh published stable metadata without acquiring artifacts or changing the supported pin.</summary>
    /// <param name="cancellationToken">Cancellation for the two bounded metadata requests.</param>
    /// <returns>The actual published tag and exact commit, never a branch or prerelease.</returns>
    /// <exception cref="SquadArtifactsNotFoundException">Metadata is unavailable, invalid, unpublished or a moved supported tag.</exception>
    public async ValueTask<SquadRelease> ResolveReleaseAsync(CancellationToken cancellationToken = default)
    {
        var releasePath = _version is null
            ? $"/repos/{Repository}/releases/latest"
            : $"/repos/{Repository}/releases/tags/{Uri.EscapeDataString(_version)}";
        using var release = await GetJsonAsync(releasePath, "release", cancellationToken).ConfigureAwait(false);
        var releaseRoot = release.RootElement;

        var tag = GetRequiredString(releaseRoot, "tag_name", "release");
        if (!StableVersionTagPattern().IsMatch(tag))
        {
            throw new SquadArtifactsNotFoundException(
                "GitHub returned a release tag that is not a stable semantic-version identity.");
        }

        if (_version is not null && !string.Equals(tag, _version, StringComparison.Ordinal))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub returned release tag '{tag}' when '{_version}' was requested.");
        }

        if (GetRequiredBoolean(releaseRoot, "draft", "release"))
        {
            throw new SquadArtifactsNotFoundException($"GitHub release '{tag}' is a draft.");
        }

        if (GetRequiredBoolean(releaseRoot, "prerelease", "release"))
        {
            throw new SquadArtifactsNotFoundException($"GitHub release '{tag}' is a prerelease.");
        }

        var published = GetRequiredString(releaseRoot, "published_at", "release");
        if (!DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _))
        {
            throw new SquadArtifactsNotFoundException("GitHub release has an invalid publication timestamp.");
        }

        var commitPath = $"/repos/{Repository}/commits/{Uri.EscapeDataString(tag)}";
        using var commit = await GetJsonAsync(commitPath, "release commit", cancellationToken).ConfigureAwait(false);
        var commitSha = GetRequiredString(commit.RootElement, "sha", "release commit");
        if (!CommitShaPattern().IsMatch(commitSha))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub returned an invalid commit SHA for release '{tag}'.");
        }

        var supported = SupportedSquadRelease.Current;
        if (tag.Equals(supported.Tag, StringComparison.Ordinal) &&
            !commitSha.Equals(supported.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new SquadArtifactsNotFoundException(
                $"Official release '{tag}' moved from verified commit '{supported.CommitSha}' to " +
                $"'{commitSha}'. Review the compatibility manifest; no acquisition or promotion occurred.");
        }

        return new(tag, commitSha.ToLowerInvariant());
    }

    internal static bool IsStableTag(string value) => StableVersionTagPattern().IsMatch(value);
    internal static bool IsCommit(string value) => CommitShaPattern().IsMatch(value);

    private async Task<JsonDocument> GetJsonAsync(
        string path,
        string resourceName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiRoot + path);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("hve-squad-maf", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub API request for {resourceName} timed out; no fallback source was used.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub API request for {resourceName} failed; no fallback source was used.",
                ex);
        }

        using (response)
        {
            var body = await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var rateLimit = RateLimitDiagnostic(response);
                throw new SquadArtifactsNotFoundException(
                    $"GitHub API request for {resourceName} failed with HTTP {(int)response.StatusCode} " +
                    $".{rateLimit} No fallback source was used. Check API availability and host permissions.");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new SquadArtifactsNotFoundException(
                    $"GitHub returned malformed JSON for {resourceName}.",
                    ex);
            }
        }
    }

    private static async Task<string> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var result = new StringBuilder();

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return result.ToString();
            }

            if (result.Length + count > MaximumResponseCharacters)
            {
                throw new SquadArtifactsNotFoundException(
                    $"GitHub API response exceeded {MaximumResponseCharacters} characters.");
            }

            result.Append(buffer, 0, count);
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string resourceName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub {resourceName} response omitted required '{propertyName}' data.");
        }

        return property.GetString()!;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName, string resourceName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub {resourceName} response omitted required '{propertyName}' data.");
        }

        return property.GetBoolean();
    }

    private static string RateLimitDiagnostic(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return string.Empty;
        }

        var remaining = int.TryParse(HeaderValue(response, "X-RateLimit-Remaining"),
            NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count.ToString(CultureInfo.InvariantCulture) : "unknown";
        var resetValue = HeaderValue(response, "X-RateLimit-Reset");
        var reset = long.TryParse(resetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unixTime) &&
            unixTime is >= 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime).ToString("u", CultureInfo.InvariantCulture)
            : "unknown";

        return $" GitHub rate limit remaining: {remaining}; reset: {reset}.";
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private static ApmArtifactSource CreateApmSource(string packageSpec, string target)
    {
        return new ApmArtifactSource(packageSpec, target);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    [GeneratedRegex(
        @"^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionTagPattern();

    [GeneratedRegex(@"^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    [GeneratedRegex(
        @"^[a-z][a-z0-9-]*(?:,[a-z][a-z0-9-]*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}
