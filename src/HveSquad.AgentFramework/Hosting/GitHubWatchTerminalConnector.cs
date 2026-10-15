using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>Fixed GitHub terminal capabilities, with no push, merge, deployment or generic tracker-write API.</summary>
public sealed class GitHubWatchTerminalOptions
{
    /// <summary>Explicit allowed terminal shapes. Empty authorizes no publication.</summary>
    public IReadOnlyList<SquadWatchTerminalKind> Kinds { get; init; } = [];
    /// <summary>Host-prepared, non-protected squad-watch/ branch. The connector never creates or pushes it.</summary>
    public string? DraftHead { get; init; }
    /// <summary>Explicit existing base branch for a draft PR.</summary>
    public string? DraftBase { get; init; }
    /// <summary>Exact host-prepared head commit, verified again at execution/reconciliation.</summary>
    public string? DraftHeadSha { get; init; }
    /// <summary>Explicit already-published local output paths to verify. Empty defaults to actual project-write outputs.</summary>
    public IReadOnlyList<string> PublishedOutputPaths { get; init; } = [];
}

/// <summary>
/// Working provider-observed draft PR, COMMENT-only review, source comment, issue and commit-comment
/// connector. It publishes safe native metadata, never input bodies or private artifact content.
/// Draft code/audit publication is a separate host-approved preparation step; this connector cannot push.
/// The caller owns client authentication, least privilege, tenant authorization and redirects.
/// </summary>
public sealed class GitHubWatchTerminalConnector : ISquadWatchTerminalConnector
{
    private readonly Func<SquadWatchTerminalRequest, CancellationToken, ValueTask<bool>> _authorize;
    private readonly HumanChannelHttp _http;
    private readonly GitHubWatchTerminalOptions _options;
    private readonly string _repository;
    private readonly string _webRoot;

    /// <summary>Registers an explicit bounded provider policy and separate action authorizer.</summary>
    /// <param name="client">Caller-owned authenticated client with automatic redirects disabled.</param>
    /// <param name="repository">Explicit authorized owner/repository.</param>
    /// <param name="options">Actual terminal kinds and already-prepared branch/output bindings.</param>
    /// <param name="authorizeAsync">Independent resource/action authorization; a label is not this permission.</param>
    /// <param name="authorizationBinding">Opaque stable action-policy revision, changed when host authorization changes.</param>
    /// <param name="apiRoot">Optional trusted HTTPS GitHub Enterprise API root.</param>
    /// <param name="webRoot">Optional trusted HTTPS provider UI origin.</param>
    public GitHubWatchTerminalConnector(HttpClient client, string repository, GitHubWatchTerminalOptions options,
        Func<SquadWatchTerminalRequest, CancellationToken, ValueTask<bool>> authorizeAsync,
        string authorizationBinding, Uri? apiRoot = null, Uri? webRoot = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizeAsync);
        if (!WatchContracts.IsRepository(repository) || !HumanActions.IsReference(authorizationBinding) ||
            options.Kinds.Count > 5 || options.Kinds.Any(kind => !Enum.IsDefined(kind)) ||
            options.PublishedOutputPaths.Count > 256 || options.PublishedOutputPaths.Any(path =>
                string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Any(char.IsControl) ||
                path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':', StringComparison.Ordinal) ||
                path.Replace('\\', '/').Split('/').Any(part => part is "." or ".." or "")) ||
            options.DraftHead is not null && !ValidBranch(options.DraftHead) ||
            options.DraftBase is not null && !ValidBranch(options.DraftBase) ||
            options.DraftHeadSha is not null && !System.Text.RegularExpressions.Regex.IsMatch(options.DraftHeadSha, "^[a-fA-F0-9]{40}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException("GitHub terminal capabilities need bounded explicit kinds, safe prepared branch/output bindings and independent authorization.");
        }
        var root = GitHubWatch.Root(apiRoot);
        webRoot ??= new Uri("https://github.com/");
        _ = GitHubWatch.Root(webRoot);
        _repository = repository.ToLowerInvariant();
        _webRoot = webRoot.AbsoluteUri.TrimEnd('/') + "/" + _repository + "/";
        _http = new(client, root);
        _authorize = authorizeAsync;
        _options = new()
        {
            Kinds = options.Kinds.ToArray(),
            DraftHead = options.DraftHead,
            DraftBase = options.DraftBase,
            DraftHeadSha = options.DraftHeadSha?.ToLowerInvariant(),
            PublishedOutputPaths = options.PublishedOutputPaths.Select(path => path.Replace('\\', '/')).ToArray(),
        };
        Binding = "github-watch-terminal:" + HumanActions.Hash(new
        {
            Root = root.AbsoluteUri,
            WebRoot = _webRoot,
            Repository = _repository,
            Options = _options,
            authorizationBinding,
        });
    }

    /// <inheritdoc/>
    public string Binding { get; }

    /// <inheritdoc/>
    public async ValueTask<bool> AuthorizeAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
    {
        if (!request.Repository.Equals(_repository, StringComparison.OrdinalIgnoreCase) ||
            !_options.Kinds.Contains(request.Kind) ||
            request.Kind is SquadWatchTerminalKind.PullRequestReview or SquadWatchTerminalKind.SourceComment && request.Number is null ||
            request.Kind == SquadWatchTerminalKind.CommitComment && request.CommitSha is null ||
            request.Kind == SquadWatchTerminalKind.DraftPullRequest &&
                (_options.DraftHead?.StartsWith("squad-watch/", StringComparison.Ordinal) != true ||
                    _options.DraftBase is null || _options.DraftHead == _options.DraftBase || _options.DraftHeadSha is null))
        {
            return false;
        }
        return await _authorize(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<SquadWatchTerminalReceipt> ExecuteAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            throw new SquadHumanTransportException("This exact GitHub Watch terminal action is not independently authorized.");
        }
        if (request.Kind == SquadWatchTerminalKind.DraftPullRequest)
        {
            await VerifyPreparedBranchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (await FindOriginalAsync(request, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return await ObserveAsync(request, existing, cancellationToken).ConfigureAwait(false);
        }
        var body = Body(request);
        JsonElement? created;
        if (request.Previous is { } previous && previous.Kind == request.Kind)
        {
            if (previous.ConnectorBinding != Binding || previous.Repository != request.Repository ||
                !long.TryParse(previous.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var priorId) || priorId <= 0)
            {
                throw new SquadHumanTransportException("The previous terminal resource lost its exact original provider binding.");
            }
            var original = await _http.SendAsync(HttpMethod.Get, ResourcePath(request, priorId), null, cancellationToken).ConfigureAwait(false);
            if (original is not { } prior || ResourceId(request.Kind, prior) != priorId ||
                HumanActions.Hash(prior) != previous.ProviderObservationSha256 ||
                HumanChannelHttp.Text(prior, "body")?.Contains("<!-- native-squad-watch:" + previous.OperationId + ":" +
                    previous.RequestSha256 + " -->", StringComparison.Ordinal) != true ||
                request.Kind == SquadWatchTerminalKind.DraftPullRequest && !GitHubWatch.Boolean(prior, "draft"))
            {
                throw new SquadHumanTransportException("The prior draft/terminal resource was changed externally; Watch will not overwrite it.");
            }
            if (request.Kind == SquadWatchTerminalKind.PullRequestReview)
            {
                created = await _http.SendAsync(HttpMethod.Put, ResourcePath(request, priorId), new { body }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                created = await _http.SendAsync(HttpMethod.Patch, ResourcePath(request, priorId), new { body }, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            created = request.Kind switch
            {
                SquadWatchTerminalKind.DraftPullRequest => await _http.SendAsync(HttpMethod.Post, Repo + "/pulls", new
                {
                    title = "Native Squad draft for " + request.SourceRef,
                    head = _options.DraftHead,
                    @base = _options.DraftBase,
                    draft = true,
                    body,
                }, cancellationToken).ConfigureAwait(false),
                SquadWatchTerminalKind.PullRequestReview => await _http.SendAsync(HttpMethod.Post,
                    Repo + "/pulls/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture) + "/reviews",
                    new { body, @event = "COMMENT", commit_id = request.CommitSha }, cancellationToken).ConfigureAwait(false),
                SquadWatchTerminalKind.SourceComment => await _http.SendAsync(HttpMethod.Post,
                    Repo + "/issues/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture) + "/comments",
                    new { body }, cancellationToken).ConfigureAwait(false),
                SquadWatchTerminalKind.Issue => await _http.SendAsync(HttpMethod.Post, Repo + "/issues",
                    new { title = "Native Squad outcome for " + request.SourceRef, body }, cancellationToken).ConfigureAwait(false),
                SquadWatchTerminalKind.CommitComment => await _http.SendAsync(HttpMethod.Post,
                    Repo + "/commits/" + request.CommitSha + "/comments", new { body }, cancellationToken).ConfigureAwait(false),
                _ => throw new SquadHumanTransportException("The terminal action is outside the fixed Watch connector contract."),
            };
        }
        if (created is not { } value || ResourceId(request.Kind, value) is not { } id)
        {
            throw new SquadHumanTransportException("GitHub did not return an actual terminal resource identity.", outcomeUncertain: true);
        }
        return await ObserveAsync(request, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<SquadWatchTerminalReconciliation> ReconcileAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken)
    {
        if (!request.Repository.Equals(_repository, StringComparison.OrdinalIgnoreCase) || !_options.Kinds.Contains(request.Kind))
        {
            throw new SquadHumanTransportException("The original terminal action is outside this connector binding.");
        }
        var id = await FindOriginalAsync(request, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return new(SquadEffectDisposition.CannotConfirm, Diagnostic:
                "No exact original provider marker was observed. Absence is not proof of non-execution; nothing was replayed.");
        }
        if (request.Kind == SquadWatchTerminalKind.DraftPullRequest)
        {
            await VerifyPreparedBranchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return new(SquadEffectDisposition.Executed, await ObserveAsync(request, id.Value, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask VerifyPreparedBranchAsync(SquadWatchTerminalRequest request, CancellationToken token)
    {
        var branch = await _http.SendAsync(HttpMethod.Get, Repo + "/branches/" + Uri.EscapeDataString(_options.DraftHead!),
            null, token).ConfigureAwait(false);
        if (branch is not { } head || GitHubWatch.Boolean(head, "protected") ||
            HumanChannelHttp.Text(GitHubWatch.Child(head, "commit"), "sha") != _options.DraftHeadSha)
        {
            throw new SquadHumanTransportException("The explicit host-prepared branch is missing, protected or not at its exact authorized commit.");
        }
        var compare = await _http.SendAsync(HttpMethod.Get, Repo + "/compare/" + Uri.EscapeDataString(_options.DraftBase!) +
            "..." + Uri.EscapeDataString(_options.DraftHeadSha!), null, token).ConfigureAwait(false);
        if (compare is not { } diff || HumanChannelHttp.Text(diff, "status") is not ("ahead" or "diverged") ||
            !diff.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
        {
            throw new SquadHumanTransportException("The prepared draft branch has no actual provider-observed changes against its explicit base.");
        }
        var all = request.ProjectOutputs.Concat(request.Artifacts).ToArray();
        var outputs = _options.PublishedOutputPaths.Count == 0 ? request.ProjectOutputs :
            _options.PublishedOutputPaths.Select(path => all.SingleOrDefault(item => item.Path == path)
                ?? throw new SquadHumanTransportException("An authorized published output is absent from the actual native evidence.")).ToArray();
        if (FederationExecution.Successful(new(request.RunId, request.Outcome, "", [])) && outputs.Count == 0)
        {
            throw new SquadHumanTransportException("A successful draft needs actual host-approved published output evidence; an unrelated branch is not proof of delivery.");
        }
        foreach (var output in outputs)
        {
            if (!files.EnumerateArray().Any(file => HumanChannelHttp.Text(file, "filename") == output.Path))
            {
                throw new SquadHumanTransportException("The prepared branch does not contain the actual native output change.");
            }
            var resource = await _http.SendAsync(HttpMethod.Get, Repo + "/contents/" +
                string.Join('/', output.Path.Split('/').Select(Uri.EscapeDataString)) + "?ref=" + _options.DraftHeadSha,
                null, token).ConfigureAwait(false);
            if (resource is not { } actual || HumanChannelHttp.Text(actual, "encoding") != "base64" ||
                HumanChannelHttp.Text(actual, "content") is not { } content ||
                Convert.ToHexString(SHA256.HashData(GitHubWatch.Decode(content))) != output.Sha256)
            {
                throw new SquadHumanTransportException("The provider's actual published bytes do not match the original verified native output.");
            }
        }
    }

    private async ValueTask<long?> FindOriginalAsync(SquadWatchTerminalRequest request, CancellationToken token)
    {
        var path = request.Kind switch
        {
            SquadWatchTerminalKind.DraftPullRequest => Repo + "/pulls?state=all&head=" +
                Uri.EscapeDataString(_repository.Split('/')[0] + ":" + _options.DraftHead),
            SquadWatchTerminalKind.PullRequestReview => Repo + "/pulls/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture) + "/reviews",
            SquadWatchTerminalKind.SourceComment => Repo + "/issues/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture) + "/comments",
            SquadWatchTerminalKind.Issue => Repo + "/issues?state=all",
            SquadWatchTerminalKind.CommitComment => Repo + "/commits/" + request.CommitSha + "/comments",
            _ => throw new SquadHumanTransportException("The original terminal kind is invalid."),
        };
        var resources = await _http.ListAsync(path, token).ConfigureAwait(false);
        var matches = resources.Where(item => HumanChannelHttp.Text(item, "body")?.Contains(Marker(request), StringComparison.Ordinal) == true).ToArray();
        if (matches.Length > 1)
        {
            throw new SquadHumanTransportException("More than one provider resource claims this exact original operation; reconcile duplicates explicitly.");
        }
        return matches.Length == 0 ? null : ResourceId(request.Kind, matches[0])
            ?? throw new SquadHumanTransportException("The observed terminal resource has no actual provider identity.");
    }

    private async ValueTask<SquadWatchTerminalReceipt> ObserveAsync(SquadWatchTerminalRequest request, long id, CancellationToken token)
    {
        var item = await _http.SendAsync(HttpMethod.Get, ResourcePath(request, id), null, token).ConfigureAwait(false);
        if (item is not { } actual || ResourceId(request.Kind, actual) != id ||
            HumanChannelHttp.Text(actual, "body") != Body(request) ||
            HumanChannelHttp.Text(actual, "html_url") is not { } reference ||
            !ValidReference(request, id, reference) ||
            request.Kind == SquadWatchTerminalKind.DraftPullRequest &&
                (!GitHubWatch.Boolean(actual, "draft") ||
                    HumanChannelHttp.Text(GitHubWatch.Child(actual, "head"), "ref") != _options.DraftHead ||
                    HumanChannelHttp.Text(GitHubWatch.Child(actual, "head"), "sha") != _options.DraftHeadSha ||
                    HumanChannelHttp.Text(GitHubWatch.Child(actual, "base"), "ref") != _options.DraftBase) ||
            request.Kind == SquadWatchTerminalKind.PullRequestReview &&
                (HumanChannelHttp.Text(actual, "state") != "COMMENTED" ||
                    request.CommitSha is not null && HumanChannelHttp.Text(actual, "commit_id") != request.CommitSha) ||
            request.Kind == SquadWatchTerminalKind.SourceComment && HumanChannelHttp.Text(actual, "issue_url") !=
                _http.Address(Repo + "/issues/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture)).AbsoluteUri ||
            request.Kind == SquadWatchTerminalKind.CommitComment && HumanChannelHttp.Text(actual, "commit_id") != request.CommitSha ||
            request.Kind == SquadWatchTerminalKind.Issue && actual.TryGetProperty("pull_request", out _))
        {
            throw new SquadHumanTransportException("The re-read provider postcondition did not prove the exact draft/comment action.", outcomeUncertain: true);
        }
        var receipt = new SquadWatchTerminalReceipt(request.OperationId, request.Sha256, Binding, request.Repository,
            request.SourceRef, request.Kind, id.ToString(CultureInfo.InvariantCulture), reference,
            HumanActions.Hash(actual), request.Kind == SquadWatchTerminalKind.DraftPullRequest);
        WatchContracts.VerifyTerminal(request, Binding, receipt);
        return receipt;
    }

    private string ResourcePath(SquadWatchTerminalRequest request, long id) => request.Kind switch
    {
        SquadWatchTerminalKind.DraftPullRequest => Repo + "/pulls/" + id.ToString(CultureInfo.InvariantCulture),
        SquadWatchTerminalKind.Issue => Repo + "/issues/" + id.ToString(CultureInfo.InvariantCulture),
        SquadWatchTerminalKind.SourceComment => Repo + "/issues/comments/" + id.ToString(CultureInfo.InvariantCulture),
        SquadWatchTerminalKind.CommitComment => Repo + "/comments/" + id.ToString(CultureInfo.InvariantCulture),
        SquadWatchTerminalKind.PullRequestReview => Repo + "/pulls/" + request.Number!.Value.ToString(CultureInfo.InvariantCulture) + "/reviews/" + id.ToString(CultureInfo.InvariantCulture),
        _ => throw new SquadHumanTransportException("The terminal resource path is invalid."),
    };

    private string Repo => "repos/" + _repository;

    private static long? ResourceId(SquadWatchTerminalKind kind, JsonElement resource) =>
        HumanChannelHttp.Id(resource, kind is SquadWatchTerminalKind.DraftPullRequest or SquadWatchTerminalKind.Issue ? "number" : "id");

    private bool ValidReference(SquadWatchTerminalRequest request, long id, string reference)
    {
        var resource = id.ToString(CultureInfo.InvariantCulture);
        var number = request.Number?.ToString(CultureInfo.InvariantCulture);
        var expected = request.Kind switch
        {
            SquadWatchTerminalKind.DraftPullRequest => "pull/" + resource,
            SquadWatchTerminalKind.Issue => "issues/" + resource,
            SquadWatchTerminalKind.PullRequestReview => "pull/" + number + "#pullrequestreview-" + resource,
            SquadWatchTerminalKind.CommitComment => "commit/" + request.CommitSha + "#commitcomment-" + resource,
            _ => "issues/" + number + "#issuecomment-" + resource,
        };
        return reference.Equals(_webRoot + expected, StringComparison.OrdinalIgnoreCase) ||
            request.Kind == SquadWatchTerminalKind.SourceComment &&
                reference.Equals(_webRoot + "pull/" + number + "#issuecomment-" + resource, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Body(SquadWatchTerminalRequest request) =>
        "# Native Squad governed outcome\n\n" +
        "Source: " + request.SourceRef + "\n\n" +
        (request.Kind == SquadWatchTerminalKind.DraftPullRequest && request.Number is { } number
            ? "Closes #" + number.ToString(CultureInfo.InvariantCulture) + "\n\n" : "") +
        "Native run: `" + request.RunId + "`\n\nNative outcome: " + request.Outcome +
        "\n\nVerified artifacts: " + request.Artifacts.Count.ToString(CultureInfo.InvariantCulture) +
        "; applied project outputs: " + request.ProjectOutputs.Count.ToString(CultureInfo.InvariantCulture) +
        ". Exact private decisions/history are retained in the authorized case UI, not published by this connector." +
        "\n\n## Blocking findings\n\n" +
        (request.BlockingFindings.Count == 0 ? "None reported by this native outcome." : string.Join("\n", request.BlockingFindings.Select(item => "* " + item))) +
        "\n\nHuman review is still required. This surface is not approval to merge, push, deploy or release.\n\n" + Marker(request);

    private static string Marker(SquadWatchTerminalRequest request) =>
        "<!-- native-squad-watch:" + request.OperationId + ":" + request.Sha256 + " -->";

    private static bool ValidBranch(string branch) => branch.Length is > 0 and <= 200 &&
        !branch.Any(char.IsControl) && !branch.Contains("..", StringComparison.Ordinal) &&
        !branch.Contains('@', StringComparison.Ordinal) && !branch.Contains('\\', StringComparison.Ordinal) &&
        !branch.Any(character => character is ':' or '?' or '*' or '[' or ']' or '~' or '^' or ' ') &&
        !branch.StartsWith('/') && !branch.EndsWith('/') && !branch.EndsWith('.');
}

internal static partial class GitHubWatch
{
    internal static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw new SquadHumanTransportException("The provider's structural boolean is missing.");
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SquadHumanTransportException("The provider's structural boolean is missing or invalid."),
        };
    }
}
