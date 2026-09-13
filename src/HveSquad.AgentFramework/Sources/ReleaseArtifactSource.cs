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

    /// <summary>A known compatible release used by tests and examples, not a latest-version marker.</summary>
    public const string TestedReleaseTag = "v0.16.2";

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
    /// Stable semantic-version tag to acquire. Null resolves GitHub's latest published release.
    /// </param>
    /// <param name="target">APM target used by the acquisition source.</param>
    /// <param name="httpClient">
    /// Optional host-configured client. Hosts may configure authentication on this client.
    /// </param>
    /// <param name="acquisitionFactory">
    /// Optional factory receiving the exact commit-pinned package spec and target.
    /// </param>
    public ReleaseArtifactSource(
        string? version = null,
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
        var releasePath = _version is null
            ? $"/repos/{Repository}/releases/latest"
            : $"/repos/{Repository}/releases/tags/{Uri.EscapeDataString(_version)}";
        using var release = await GetJsonAsync(releasePath, "release", cancellationToken).ConfigureAwait(false);
        var releaseRoot = release.RootElement;

        var tag = GetRequiredString(releaseRoot, "tag_name", "release");
        if (!StableVersionTagPattern().IsMatch(tag))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub release '{tag}' is not a stable semantic-version release.");
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

        var commitPath = $"/repos/{Repository}/commits/{Uri.EscapeDataString(tag)}";
        using var commit = await GetJsonAsync(commitPath, "release commit", cancellationToken).ConfigureAwait(false);
        var commitSha = GetRequiredString(commit.RootElement, "sha", "release commit");
        if (!CommitShaPattern().IsMatch(commitSha))
        {
            throw new SquadArtifactsNotFoundException(
                $"GitHub returned invalid commit SHA '{commitSha}' for release '{tag}'.");
        }

        var packageSpec = $"{Repository}#{commitSha}";
        var acquisitionSource = _acquisitionFactory(packageSpec, _target)
            ?? throw new InvalidOperationException("The acquisition factory returned null.");
        var roots = await acquisitionSource.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var provenance = new SquadRelease(tag, commitSha);

        return roots with
        {
            Origin = $"HVE Squad release '{tag}' at commit '{commitSha}'; {roots.Origin}",
            Release = provenance,
        };
    }

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
                    $"({response.ReasonPhrase}).{rateLimit} No fallback source was used. " +
                    $"{ExtractApiMessage(body)}");
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

        var remaining = HeaderValue(response, "X-RateLimit-Remaining") ?? "unknown";
        var resetValue = HeaderValue(response, "X-RateLimit-Reset");
        var reset = long.TryParse(resetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unixTime)
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime).ToString("u", CultureInfo.InvariantCulture)
            : resetValue ?? "unknown";

        return $" GitHub rate limit remaining: {remaining}; reset: {reset}.";
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private static string ExtractApiMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
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
