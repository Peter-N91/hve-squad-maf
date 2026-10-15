using System.Globalization;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>
/// Working narrowly scoped GitHub learning fork/branch/atomic commit-and-push/PR connector.
/// It never merges, edits a local deployed playbook or receives local memory/source paths.
/// Caller-owned credentials, redirects and independent tenant/resource rights remain host responsibilities.
/// </summary>
public sealed class GitHubLearningConnector : ISquadLearningConnector
{
    private const int MaximumBlobBytes = 512 * 1024;
    private const string CommitMutation = """
        mutation SquadLearningWrite($input: CreateCommitOnBranchInput!) {
          createCommitOnBranch(input: $input) {
            clientMutationId
            commit { oid }
          }
        }
        """;
    private readonly Func<SquadLearningMutation, CancellationToken, ValueTask<bool>> _authorize;
    private readonly HumanChannelHttp _http;
    private readonly SquadLearningTarget _target;
    private readonly string _webRoot;
    private readonly string _graphqlPath;

    /// <summary>Registers only one exact source/fork pair and an independent resource/action authorizer.</summary>
    /// <param name="client">Caller-owned authenticated client; disable automatic redirects and never log auth headers.</param>
    /// <param name="target">Explicit approved public-upstream or private-tenant source/fork pair.</param>
    /// <param name="authorizeAsync">Exact host resource rights, not the human impactful-action confirmation.</param>
    /// <param name="authorizationBinding">Opaque policy revision.</param>
    /// <param name="apiRoot">Optional trusted HTTPS Enterprise REST API root.</param>
    /// <param name="webRoot">Optional trusted HTTPS UI origin.</param>
    /// <param name="graphqlEndpoint">Optional same-origin GraphQL endpoint for atomic expected-head writes.</param>
    public GitHubLearningConnector(HttpClient client, SquadLearningTarget target,
        Func<SquadLearningMutation, CancellationToken, ValueTask<bool>> authorizeAsync, string authorizationBinding,
        Uri? apiRoot = null, Uri? webRoot = null, Uri? graphqlEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authorizeAsync);
        if (!HumanActions.IsReference(authorizationBinding))
        {
            throw new ArgumentException("Use an opaque non-secret learning action-policy revision.", nameof(authorizationBinding));
        }
        var root = GitHubWatch.Root(apiRoot);
        webRoot ??= new("https://github.com/");
        _ = GitHubWatch.Root(webRoot);
        graphqlEndpoint ??= new(root, root.AbsolutePath.TrimEnd('/').EndsWith("/api/v3", StringComparison.Ordinal)
            ? "/api/graphql" : "/graphql");
        if (!graphqlEndpoint.IsAbsoluteUri || graphqlEndpoint.Scheme != Uri.UriSchemeHttps ||
            graphqlEndpoint.UserInfo.Length != 0 || graphqlEndpoint.Query.Length != 0 || graphqlEndpoint.Fragment.Length != 0 ||
            graphqlEndpoint.GetLeftPart(UriPartial.Authority) != root.GetLeftPart(UriPartial.Authority))
        {
            throw new ArgumentException("Learning GraphQL credentials may only reach the explicitly registered REST API origin.", nameof(graphqlEndpoint));
        }
        _target = LearningContracts.ValidateTarget(target);
        _authorize = authorizeAsync;
        _http = new(client, root);
        _webRoot = webRoot.AbsoluteUri.TrimEnd('/');
        _graphqlPath = graphqlEndpoint.AbsolutePath;
        Binding = "github-learning:" + HumanActions.Hash(new
        {
            Target = _target,
            Root = root.AbsoluteUri,
            WebRoot = _webRoot,
            Graphql = _graphqlPath,
            authorizationBinding,
        });
    }

    /// <inheritdoc/>
    public string Binding { get; }

    /// <inheritdoc/>
    public async ValueTask<SquadLearningTargetSnapshot> ReadTargetAsync(SquadLearningTarget target, CancellationToken cancellationToken)
    {
        if (LearningContracts.ValidateTarget(target) != _target)
        {
            throw new UnauthorizedAccessException("This learning connector cannot read another source/tenant repository.");
        }
        var repository = await ReadRepositoryAsync(_target.Repository, cancellationToken).ConfigureAwait(false);
        if (repository is not { } metadata || !Bool(metadata, "private", out var isPrivate) ||
            isPrivate != (_target.Kind == "tenant") || !Bool(metadata, "archived", out var archived) || archived ||
            !Bool(metadata, "disabled", out var disabled) || disabled)
        {
            throw new SquadHumanTransportException("Learning source visibility/status does not prove the explicit public-upstream or private-tenant contract.");
        }
        var branch = HumanChannelHttp.Text(metadata, "default_branch");
        if (!LearningContracts.IsBranch(branch))
        {
            throw new SquadHumanTransportException("Learning source default branch is missing or invalid.");
        }
        var commit = await ReadRefAsync(_target.Repository, branch, cancellationToken).ConfigureAwait(false)
            ?? throw new SquadHumanTransportException("Learning source default branch has no verified commit.");
        var file = await ReadFileAsync(_target.Repository, LearningContracts.TargetPath(_target), commit, cancellationToken).ConfigureAwait(false)
            ?? throw new SquadHumanTransportException("The exact released learning file is absent at the source commit.");
        return new(_target.Repository, LearningContracts.TargetPath(_target), branch, commit, file.Sha, file.Content);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> AuthorizeAsync(SquadLearningMutation mutation, CancellationToken cancellationToken)
    {
        if (!ValidMutation(mutation))
        {
            return false;
        }
        return await _authorize(mutation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<SquadLearningMutationReceipt> ExecuteAsync(SquadLearningMutation mutation, CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(mutation, cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The exact GitHub learning action lacks independently registered resource rights.");
        }
        if (await ReconcileAsync(mutation, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }
        switch (mutation.Kind)
        {
            case "fork":
                if (!_target.ForkOrganization)
                {
                    var user = Require(await _http.SendAsync(HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false));
                    if (HumanChannelHttp.Id(user) is null || HumanChannelHttp.Text(user, "type") != "User" ||
                        !string.Equals(HumanChannelHttp.Text(user, "login"), mutation.Repository.Split('/')[0], StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("Personal learning fork destination is not the actual authenticated provider account.");
                    }
                }
                _ = await _http.SendAsync(HttpMethod.Post, "repos/" + mutation.OriginRepository + "/forks",
                    _target.ForkOrganization
                        ? new Dictionary<string, object> { ["organization"] = mutation.Repository.Split('/')[0] }
                        : new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);
                break;
            case "branch":
                _ = await _http.SendAsync(HttpMethod.Post, "repos/" + mutation.Repository + "/git/refs",
                    new { @ref = "refs/heads/" + mutation.Branch, sha = mutation.BaseCommitSha }, cancellationToken).ConfigureAwait(false);
                break;
            case "write":
                var result = Require(await _http.SendAsync(HttpMethod.Post, _graphqlPath, new
                {
                    query = CommitMutation,
                    variables = new
                    {
                        input = new
                        {
                            clientMutationId = mutation.Id,
                            branch = new { repositoryNameWithOwner = mutation.Repository, branchName = mutation.Branch },
                            expectedHeadOid = mutation.BaseCommitSha,
                            message = new { headline = "feat(learnings): add " + mutation.EntryId, body = Marker(mutation) },
                            fileChanges = new
                            {
                                additions = new[] { new { path = mutation.Path, contents = Convert.ToBase64String(Encoding.UTF8.GetBytes(mutation.Content)) } },
                            },
                        },
                    },
                }, cancellationToken).ConfigureAwait(false));
                if (result.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null &&
                    (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0) ||
                    !Object(result, "data", out var data) || !Object(data, "createCommitOnBranch", out var created) ||
                    HumanChannelHttp.Text(created, "clientMutationId") != mutation.Id ||
                    !Object(created, "commit", out var committed) || !LearningContracts.IsCommit(HumanChannelHttp.Text(committed, "oid")))
                {
                    throw new SquadHumanTransportException(
                        "Atomic expected-head learning commit returned no exact successful postcondition. Reconcile read-only; do not retry the mutation.", true);
                }
                break;
            case "pull-request":
                _ = await _http.SendAsync(HttpMethod.Post, "repos/" + mutation.OriginRepository + "/pulls", new
                {
                    title = "feat(learnings): add " + mutation.EntryId,
                    head = mutation.Repository.Split('/')[0] + ":" + mutation.Branch,
                    @base = mutation.BaseBranch,
                    body = Marker(mutation) + "\n\nSanitization review checklist:\n" +
                        "- [ ] Secrets, tokens, credentials and connection strings removed\n" +
                        "- [ ] Customer, organization and individual names removed\n" +
                        "- [ ] Absolute local paths and internal URLs removed\n" +
                        "- [ ] Stack/environment details generalized and broad applicability confirmed\n" +
                        "- [ ] Original playbook license notices preserved\n\nHuman source-repository review is required before merge.",
                    maintainer_can_modify = false,
                }, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Only the four specifically registered learning mutation kinds are available.");
        }
        return await ReconcileAsync(mutation, cancellationToken).ConfigureAwait(false)
            ?? throw new SquadHumanTransportException(
                "The remote learning action may have occurred, but its exact provider postconditions are not visible. Preserve the attempt and reconcile read-only.", true);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> VerifyAsync(SquadLearningMutation mutation, SquadLearningMutationReceipt receipt, CancellationToken cancellationToken)
    {
        if (!ValidMutation(mutation) || receipt.ActionSha256 != HumanActions.Hash(mutation) || receipt.ConnectorBinding != Binding)
        {
            return false;
        }
        var observed = await ReconcileAsync(mutation, cancellationToken).ConfigureAwait(false);
        return observed == receipt;
    }

    /// <inheritdoc/>
    public async ValueTask<SquadLearningMutationReceipt?> ReconcileAsync(SquadLearningMutation mutation, CancellationToken cancellationToken)
    {
        if (!ValidMutation(mutation))
        {
            throw new UnauthorizedAccessException("Read-only learning reconciliation is outside the exact registered target/action scope.");
        }
        switch (mutation.Kind)
        {
            case "fork":
                var fork = await ReadRepositoryAsync(mutation.Repository, cancellationToken).ConfigureAwait(false);
                if (fork is null)
                {
                    return null;
                }
                if (!Bool(fork.Value, "fork", out var isFork) || !isFork ||
                    !Bool(fork.Value, "private", out var forkPrivate) || forkPrivate ||
                    !Object(fork.Value, "parent", out var parent) ||
                    !string.Equals(HumanChannelHttp.Text(parent, "full_name"), mutation.OriginRepository, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SquadHumanTransportException("The chosen fork destination collides with an unrelated or incorrectly scoped repository.");
                }
                return Receipt(mutation, "repository:" + HumanChannelHttp.Id(fork.Value)!.Value.ToString(CultureInfo.InvariantCulture),
                    Web(mutation.Repository));
            case "branch":
                var branch = await ReadRefAsync(mutation.Repository, mutation.Branch, cancellationToken).ConfigureAwait(false);
                if (branch is null)
                {
                    return null;
                }
                if (branch != mutation.BaseCommitSha &&
                    !await IsOriginalWriteAsync(mutation with
                    {
                        Id = LearningContracts.ActionId(mutation.PromotionId, "write"),
                        Kind = "write",
                    }, branch, cancellationToken).ConfigureAwait(false))
                {
                    throw new SquadHumanTransportException("Learning branch has an unrelated head; no overwrite or force-push is permitted.");
                }
                return Receipt(mutation, "branch:" + mutation.Id, Web(mutation.Repository) + "/tree/" + mutation.Branch,
                    mutation.BaseCommitSha);
            case "write":
                var head = await ReadRefAsync(mutation.Repository, mutation.Branch, cancellationToken).ConfigureAwait(false);
                if (head is null || head == mutation.BaseCommitSha)
                {
                    return null;
                }
                if (!await IsOriginalWriteAsync(mutation, head, cancellationToken).ConfigureAwait(false))
                {
                    throw new SquadHumanTransportException("The remote learning commit does not prove the exact original parent, marker and sole target-file change.");
                }
                return Receipt(mutation, "commit:" + head, Web(mutation.Repository) + "/commit/" + head, head, mutation.ContentSha256);
            case "pull-request":
                var prs = await _http.ListAsync("repos/" + mutation.OriginRepository + "/pulls?state=all&head=" +
                    Uri.EscapeDataString(mutation.Repository.Split('/')[0] + ":" + mutation.Branch) +
                    "&base=" + Uri.EscapeDataString(mutation.BaseBranch), cancellationToken).ConfigureAwait(false);
                var matches = prs.Where(pr => HumanChannelHttp.Text(pr, "body")?.Contains(Marker(mutation), StringComparison.Ordinal) == true).ToArray();
                if (matches.Length == 0)
                {
                    return null;
                }
                if (matches.Length != 1)
                {
                    throw new SquadHumanTransportException("Multiple learning PRs claim the same original action; no arbitrary PR is selected.");
                }
                var pull = matches[0];
                var number = HumanChannelHttp.Id(pull, "number");
                if (number is null || HumanChannelHttp.Id(pull) is null ||
                    !Object(pull, "base", out var baseObject) || !Object(baseObject, "repo", out var baseRepository) ||
                    !Object(pull, "head", out var headObject) || !Object(headObject, "repo", out var headRepository) ||
                    !string.Equals(HumanChannelHttp.Text(baseRepository, "full_name"), mutation.OriginRepository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(HumanChannelHttp.Text(headRepository, "full_name"), mutation.Repository, StringComparison.OrdinalIgnoreCase) ||
                    HumanChannelHttp.Text(baseObject, "ref") != mutation.BaseBranch ||
                    HumanChannelHttp.Text(headObject, "ref") != mutation.Branch ||
                    HumanChannelHttp.Text(headObject, "sha")?.ToLowerInvariant() != mutation.HeadCommitSha ||
                    !await IsOriginalWriteAsync(mutation with
                    {
                        Id = LearningContracts.ActionId(mutation.PromotionId, "write"),
                        Kind = "write",
                        HeadCommitSha = null,
                    }, mutation.HeadCommitSha!, cancellationToken).ConfigureAwait(false))
                {
                    throw new SquadHumanTransportException("The original learning PR does not prove the exact source/fork/base/head and target content.");
                }
                var url = Web(mutation.OriginRepository) + "/pull/" + number.Value.ToString(CultureInfo.InvariantCulture);
                if (HumanChannelHttp.Text(pull, "html_url") != url)
                {
                    throw new SquadHumanTransportException("Learning PR URL does not match its actual registered provider resource.");
                }
                return Receipt(mutation, "pull-request:" + number.Value.ToString(CultureInfo.InvariantCulture), url, mutation.HeadCommitSha);
            default:
                throw new InvalidOperationException("Unsupported learning reconciliation kind.");
        }
    }

    private bool ValidMutation(SquadLearningMutation mutation) =>
        Guid.TryParseExact(mutation.PromotionId, "N", out _) &&
        mutation.Id == LearningContracts.ActionId(mutation.PromotionId, mutation.Kind) &&
        mutation.OriginRepository == _target.Repository && mutation.Repository == _target.DestinationRepository &&
        mutation.Path == LearningContracts.TargetPath(_target) &&
        mutation.ForkOrganization == _target.ForkOrganization &&
        mutation.Kind is "fork" or "branch" or "write" or "pull-request" &&
        (mutation.Kind != "fork" || _target.Kind == "upstream") &&
        mutation.EntryId.StartsWith(LearningContracts.Prefix(_target), StringComparison.Ordinal) &&
        LearningContracts.IsBranch(mutation.Branch) && mutation.Branch.StartsWith("squad-learning/", StringComparison.Ordinal) &&
        LearningContracts.IsBranch(mutation.BaseBranch) && mutation.BaseBranch != mutation.Branch &&
        LearningContracts.IsCommit(mutation.BaseCommitSha) && LearningContracts.IsCommit(mutation.FileSha) &&
        !string.IsNullOrWhiteSpace(mutation.Content) && Encoding.UTF8.GetByteCount(mutation.Content) <= MaximumBlobBytes &&
        mutation.ContentSha256 == ProjectFiles.Hash(mutation.Content) &&
        (mutation.Kind != "pull-request" || LearningContracts.IsCommit(mutation.HeadCommitSha));

    private async ValueTask<bool> IsOriginalWriteAsync(SquadLearningMutation mutation, string commit, CancellationToken token)
    {
        var observed = await _http.SendAsync(HttpMethod.Get, "repos/" + mutation.Repository + "/commits/" + commit,
            null, token, allowNotFound: true).ConfigureAwait(false);
        if (observed is not { } value || HumanChannelHttp.Text(value, "sha")?.ToLowerInvariant() != commit ||
            !Object(value, "commit", out var details) ||
            HumanChannelHttp.Text(details, "message")?.Contains(Marker(mutation), StringComparison.Ordinal) != true ||
            !value.TryGetProperty("parents", out var parents) || parents.ValueKind != JsonValueKind.Array ||
            parents.GetArrayLength() != 1 || HumanChannelHttp.Text(parents[0], "sha")?.ToLowerInvariant() != mutation.BaseCommitSha ||
            !value.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() != 1 || HumanChannelHttp.Text(files[0], "filename") != mutation.Path ||
            HumanChannelHttp.Text(files[0], "status") != "modified")
        {
            return false;
        }
        var file = await ReadFileAsync(mutation.Repository, mutation.Path, commit, token).ConfigureAwait(false);
        return file is { } actual && ProjectFiles.Hash(actual.Content) == mutation.ContentSha256 && actual.Content == mutation.Content;
    }

    private async ValueTask<JsonElement?> ReadRepositoryAsync(string repository, CancellationToken token)
    {
        var result = await _http.SendAsync(HttpMethod.Get, "repos/" + repository, null, token, allowNotFound: true).ConfigureAwait(false);
        if (result is { } value && (HumanChannelHttp.Id(value) is null ||
            !string.Equals(HumanChannelHttp.Text(value, "full_name"), repository, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SquadHumanTransportException("Learning repository observation does not match its explicit provider identity.");
        }
        return result;
    }

    private async ValueTask<string?> ReadRefAsync(string repository, string branch, CancellationToken token)
    {
        var result = await _http.SendAsync(HttpMethod.Get, "repos/" + repository + "/git/ref/heads/" +
            Uri.EscapeDataString(branch), null, token, allowNotFound: true).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        if (HumanChannelHttp.Text(result.Value, "ref") != "refs/heads/" + branch ||
            !Object(result.Value, "object", out var gitObject) || HumanChannelHttp.Text(gitObject, "type") != "commit" ||
            !LearningContracts.IsCommit(HumanChannelHttp.Text(gitObject, "sha")))
        {
            throw new SquadHumanTransportException("Learning branch/ref returned no exact commit identity.");
        }
        return HumanChannelHttp.Text(gitObject, "sha")!.ToLowerInvariant();
    }

    private async ValueTask<(string Sha, string Content)?> ReadFileAsync(string repository, string path, string commit, CancellationToken token)
    {
        var result = await _http.SendAsync(HttpMethod.Get, "repos/" + repository + "/contents/" + path +
            "?ref=" + Uri.EscapeDataString(commit), null, token, allowNotFound: true).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        var value = result.Value;
        var content = HumanChannelHttp.Text(value, "content");
        var sha = HumanChannelHttp.Text(value, "sha");
        if (HumanChannelHttp.Text(value, "type") != "file" || HumanChannelHttp.Text(value, "path") != path ||
            HumanChannelHttp.Text(value, "encoding") != "base64" || content is null || !LearningContracts.IsCommit(sha) ||
            !value.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var expectedBytes) ||
            expectedBytes is < 0 or > MaximumBlobBytes)
        {
            throw new SquadHumanTransportException("Learning file is missing its exact bounded blob/encoding/path identity.");
        }
        try
        {
            var bytes = Convert.FromBase64String(content);
            if (bytes.LongLength != expectedBytes)
            {
                throw new SquadHumanTransportException("Learning file byte count differs from the provider's blob observation.");
            }
            return (sha!.ToLowerInvariant(), new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (FormatException)
        {
            throw new SquadHumanTransportException("Learning file has invalid base64 bytes.");
        }
        catch (DecoderFallbackException)
        {
            throw new SquadHumanTransportException("Learning file is not valid UTF-8; no replacement-text snapshot was accepted.");
        }
    }

    private SquadLearningMutationReceipt Receipt(SquadLearningMutation mutation, string resource, string url,
        string? commit = null, string? content = null) =>
        new(HumanActions.Hash(mutation), Binding, mutation.Kind, mutation.Repository, resource, url, commit, content);

    private string Web(string repository) => _webRoot + "/" + repository;
    private static string Marker(SquadLearningMutation mutation) =>
        "<!-- hve-squad-learning:" + mutation.Id + ":" + HumanActions.Hash(mutation) + " -->";

    private static JsonElement Require(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } result ? result :
            throw new SquadHumanTransportException("GitHub learning action returned no bounded object response.", true);

    private static bool Object(JsonElement value, string property, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out result) &&
            result.ValueKind == JsonValueKind.Object;
    }

    private static bool Bool(JsonElement value, string property, out bool result)
    {
        result = false;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var field) ||
            field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        result = field.GetBoolean();
        return true;
    }
}
