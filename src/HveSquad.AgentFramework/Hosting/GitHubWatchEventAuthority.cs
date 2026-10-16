using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>Explicit reviewed workflow authority, separate from task data and terminal mutation permissions.</summary>
public sealed class GitHubWatchWorkflowPolicy
{
    /// <summary>Committed .github/workflows file selected by the trusted host.</summary>
    public required string Path { get; init; }
    /// <summary>Exact Git blob SHA of the reviewed file, not a moving branch name.</summary>
    public required string BlobSha { get; init; }
    /// <summary>Explicit allowed workflow event kinds; an empty set authorizes none.</summary>
    public IReadOnlyList<SquadWatchEventKind> Events { get; init; } = [];
    /// <summary>Explicit branch prefixes for push, without refs/heads/. Empty authorizes no push.</summary>
    public IReadOnlyList<string> PushBranchPrefixes { get; init; } = [];
}

/// <summary>
/// Working GitHub event authority. Re-reads labels, their actual applying actors, comments,
/// PR origin/diffs and committed workflow runs. The caller owns HTTP credentials and transport
/// authentication; a parsed webhook body is not a signature or a tenant authorization.
/// </summary>
public sealed class GitHubWatchEventAuthority : ISquadWatchEventAuthority
{
    private readonly HumanChannelHttp _http;
    private readonly string _repository;
    private readonly string? _approver;
    private readonly GitHubWatchWorkflowPolicy? _workflow;

    /// <summary>Registers an explicit repository and optional exact reviewed workflow policy.</summary>
    /// <param name="client">Caller-owned authenticated HTTP client with automatic redirects disabled.</param>
    /// <param name="repository">Authorized owner/repository, never inferred from the event body.</param>
    /// <param name="approverHandle">Optional registered human command initiator, not a credential.</param>
    /// <param name="workflow">Reviewed committed policy. Required for workflow-level gates.</param>
    /// <param name="apiRoot">Optional trusted HTTPS GitHub Enterprise API root.</param>
    public GitHubWatchEventAuthority(HttpClient client, string repository, string? approverHandle = null,
        GitHubWatchWorkflowPolicy? workflow = null, Uri? apiRoot = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!WatchContracts.IsRepository(repository) || approverHandle is not null &&
            !Regex.IsMatch(approverHandle, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException("Watch authority needs an explicit repository and valid non-secret human account.");
        }
        if (workflow is not null && (!Regex.IsMatch(workflow.Path,
            @"^\.github/workflows/[A-Za-z0-9_.-]+\.ya?ml$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            !Regex.IsMatch(workflow.BlobSha, "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            workflow.Events.Count > 6 || workflow.Events.Any(kind => !Enum.IsDefined(kind)) ||
            workflow.PushBranchPrefixes.Count > 32 || workflow.PushBranchPrefixes.Any(prefix =>
                string.IsNullOrWhiteSpace(prefix) || prefix.Length > 200 || prefix.Any(char.IsControl))))
        {
            throw new ArgumentException("Workflow opt-in needs the exact reviewed Git blob and bounded event/branch filters.", nameof(workflow));
        }
        var root = GitHubWatch.Root(apiRoot);
        _http = new(client, root);
        _repository = repository.ToLowerInvariant();
        _approver = approverHandle;
        _workflow = workflow is null ? null : new()
        {
            Path = workflow.Path,
            BlobSha = workflow.BlobSha.ToLowerInvariant(),
            Events = workflow.Events.ToArray(),
            PushBranchPrefixes = workflow.PushBranchPrefixes.ToArray(),
        };
        Binding = "github-watch:" + HumanActions.Hash(new { Root = root.AbsoluteUri, Repository = _repository, _approver, _workflow });
    }

    /// <inheritdoc/>
    public string Binding { get; }

    /// <inheritdoc/>
    public async ValueTask<SquadWatchAuthorization?> AuthorizeAsync(SquadWatchEvent source, CancellationToken cancellationToken)
    {
        if (!source.Repository.Equals(_repository, StringComparison.OrdinalIgnoreCase) || source.ForkOrigin)
        {
            return null;
        }
        var actual = source;
        string? workflowProof = null;
        JsonElement? run = null;
        if (source.WorkflowRunId is { } runId)
        {
            if (_workflow is null || !_workflow.Events.Contains(source.Kind))
            {
                return null;
            }
            run = await _http.SendAsync(HttpMethod.Get, Repo + "/actions/runs/" + runId.ToString(CultureInfo.InvariantCulture),
                null, cancellationToken).ConfigureAwait(false);
            if (run is not { } observed || HumanChannelHttp.Id(observed) != runId ||
                HumanChannelHttp.Text(observed, "event") != GitHubWatch.EventName(source.Kind) ||
                HumanChannelHttp.Text(GitHubWatch.Child(observed, "repository"), "full_name")?.Equals(_repository, StringComparison.OrdinalIgnoreCase) != true ||
                HumanChannelHttp.Text(GitHubWatch.Child(observed, "head_repository"), "full_name")?.Equals(_repository, StringComparison.OrdinalIgnoreCase) != true ||
                HumanChannelHttp.Text(observed, "path")?.Split('@')[0] != _workflow.Path ||
                HumanChannelHttp.Text(observed, "head_sha") is not { } workflowCommit)
            {
                return null;
            }
            var file = await _http.SendAsync(HttpMethod.Get, Repo + "/contents/" + _workflow.Path + "?ref=" + Uri.EscapeDataString(workflowCommit),
                null, cancellationToken).ConfigureAwait(false);
            if (file is not { } committed || HumanChannelHttp.Text(committed, "sha") != _workflow.BlobSha ||
                HumanChannelHttp.Text(committed, "encoding") != "base64" || HumanChannelHttp.Text(committed, "content") is not { } content)
            {
                return null;
            }
            workflowProof = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(GitHubWatch.Decode(content)));
            actual = actual with
            {
                ReceivedAt = HumanChannelHttp.Time(observed, "created_at")
                    ?? throw new SquadHumanTransportException("The verified workflow has no valid provider timestamp."),
            };
        }
        else if (source.Kind is SquadWatchEventKind.Schedule or SquadWatchEventKind.Dispatch or SquadWatchEventKind.Push)
        {
            return null;
        }
        JsonElement actor;
        string proof;
        if (source.Kind is SquadWatchEventKind.Issue or SquadWatchEventKind.PullRequest or SquadWatchEventKind.Comment ||
            source.Kind == SquadWatchEventKind.Dispatch && source.Number is not null)
        {
            var number = source.Number?.ToString(CultureInfo.InvariantCulture)
                ?? throw new SquadHumanTransportException("The structural event has no thread number.");
            var issue = await _http.SendAsync(HttpMethod.Get, Repo + "/issues/" + number, null, cancellationToken).ConfigureAwait(false)
                ?? throw new SquadHumanTransportException("The source thread is unavailable.");
            if (HumanChannelHttp.Id(issue, "number") != source.Number)
            {
                return null;
            }
            var isPr = issue.TryGetProperty("pull_request", out _);
            if (isPr != (source.Kind == SquadWatchEventKind.PullRequest || source.IsPullRequest))
            {
                return null;
            }
            actual = actual with { Labels = GitHubWatch.Labels(issue), Title = HumanChannelHttp.Text(issue, "title"), Body = HumanChannelHttp.Text(issue, "body") };
            if (isPr)
            {
                var pr = await _http.SendAsync(HttpMethod.Get, Repo + "/pulls/" + number, null, cancellationToken).ConfigureAwait(false)
                    ?? throw new SquadHumanTransportException("The actual pull request is unavailable.");
                var head = GitHubWatch.Child(pr, "head");
                if (HumanChannelHttp.Text(GitHubWatch.Child(head, "repo"), "full_name")?.Equals(_repository, StringComparison.OrdinalIgnoreCase) != true ||
                    HumanChannelHttp.Text(GitHubWatch.Child(GitHubWatch.Child(pr, "base"), "repo"), "full_name")?.Equals(_repository, StringComparison.OrdinalIgnoreCase) != true ||
                    source.CommitSha is not null && HumanChannelHttp.Text(head, "sha") != source.CommitSha)
                {
                    return null;
                }
                var files = await _http.ListAsync(Repo + "/pulls/" + number + "/files", cancellationToken).ConfigureAwait(false);
                actual = actual with { Context = JsonSerializer.SerializeToElement(new { Head = head, Files = files }) };
            }
            if (source.Kind == SquadWatchEventKind.Comment)
            {
                if (!source.EventId.StartsWith("comment:", StringComparison.Ordinal) ||
                    !long.TryParse(source.EventId["comment:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var commentId) ||
                    commentId <= 0)
                {
                    return null;
                }
                var comment = await _http.SendAsync(HttpMethod.Get, Repo + "/issues/comments/" + commentId.ToString(CultureInfo.InvariantCulture),
                    null, cancellationToken).ConfigureAwait(false);
                if (comment is not { } original || HumanChannelHttp.Id(original) != commentId ||
                    HumanChannelHttp.Text(original, "issue_url")?.Equals(_http.Address(Repo + "/issues/" + number).AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase) != true)
                {
                    return null;
                }
                actor = GitHubWatch.Child(original, "user");
                actual = actual with { Body = HumanChannelHttp.Text(original, "body"), Actor = HumanChannelHttp.Text(actor, "login") };
                proof = "github-comment:" + commentId.ToString(CultureInfo.InvariantCulture);
            }
            else if (source.Kind == SquadWatchEventKind.Dispatch)
            {
                actor = GitHubWatch.Child(run!.Value, "actor");
                proof = "github-workflow:" + source.WorkflowRunId!.Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var label = source.Kind == SquadWatchEventKind.Issue ? "squad/auto" : "squad/review";
                var events = await _http.ListAsync(Repo + "/issues/" + number + "/events", cancellationToken).ConfigureAwait(false);
                var applied = events.LastOrDefault(item => HumanChannelHttp.Text(item, "event") == "labeled" &&
                    item.TryGetProperty("label", out var value) && HumanChannelHttp.Text(value, "name") == label);
                if (applied.ValueKind == JsonValueKind.Undefined || HumanChannelHttp.Id(applied) is not { } labelId ||
                    source.WorkflowRunId is null && source.EventId != "label:" + labelId.ToString(CultureInfo.InvariantCulture))
                {
                    return null;
                }
                actor = GitHubWatch.Child(applied, "actor");
                proof = "github-label:" + labelId.ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            actor = GitHubWatch.Child(run!.Value, "actor");
            proof = "github-workflow:" + source.WorkflowRunId!.Value.ToString(CultureInfo.InvariantCulture);
            if (source.Kind == SquadWatchEventKind.Push)
            {
                var branch = HumanChannelHttp.Text(run.Value, "head_branch");
                if (branch is null || source.Ref != "refs/heads/" + branch ||
                    source.CommitSha != HumanChannelHttp.Text(run.Value, "head_sha") ||
                    !_workflow!.PushBranchPrefixes.Any(prefix => branch.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return null;
                }
                var commit = await _http.SendAsync(HttpMethod.Get, Repo + "/commits/" + source.CommitSha,
                    null, cancellationToken).ConfigureAwait(false);
                if (commit is not { } current || HumanChannelHttp.Text(current, "sha") != source.CommitSha)
                {
                    return null;
                }
                actual = actual with { Context = current };
            }
        }
        if (HumanChannelHttp.Id(actor) is not { } actorId || HumanChannelHttp.Text(actor, "login") is not { } login ||
            HumanChannelHttp.Text(actor, "type") != "User" &&
                source.Kind is not (SquadWatchEventKind.Schedule or SquadWatchEventKind.Push))
        {
            return null;
        }
        var permission = await _http.SendAsync(HttpMethod.Get, Repo + "/collaborators/" + Uri.EscapeDataString(login) + "/permission",
            null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        var writer = permission is { } access && HumanChannelHttp.Text(access, "permission") is "write" or "maintain" or "admin" &&
            HumanChannelHttp.Id(GitHubWatch.Child(access, "user")) == actorId;
        var approver = source.Kind == SquadWatchEventKind.Comment && login.Equals(_approver, StringComparison.OrdinalIgnoreCase);
        if (source.Kind is SquadWatchEventKind.Issue or SquadWatchEventKind.PullRequest && !writer ||
            source.Kind == SquadWatchEventKind.Comment && !writer && !approver)
        {
            return null;
        }
        return new(actual, "github:" + actorId.ToString(CultureInfo.InvariantCulture), proof, writer, approver, workflowProof);
    }

    private string Repo => "repos/" + _repository;
}

/// <summary>Bounded native Actions/webhook event parsing. Authentication is deliberately a separate operation.</summary>
public static class GitHubWatchEventParser
{
    /// <summary>Parses structural fields and descriptive input without interpreting model/mode/budget commands.</summary>
    /// <param name="eventName">Platform event name, not text from an issue or comment.</param>
    /// <param name="payload">Bounded JSON read from the authenticated platform's event file.</param>
    /// <param name="workflowRunId">Trusted platform workflow run ID; null is valid for authenticated label/comment transports.</param>
    /// <param name="receivedAt">Trusted UTC receive timestamp; the authority re-reads workflow timestamps.</param>
    /// <returns>A hint which still needs ISquadWatchEventAuthority authentication.</returns>
    /// <exception cref="NotSupportedException">The platform event is outside the released in-repository map.</exception>
    public static SquadWatchEvent Parse(string eventName, JsonElement payload, long? workflowRunId, DateTimeOffset receivedAt)
    {
        var kind = eventName switch
        {
            "issues" => SquadWatchEventKind.Issue,
            "pull_request" => SquadWatchEventKind.PullRequest,
            "issue_comment" => SquadWatchEventKind.Comment,
            "schedule" => SquadWatchEventKind.Schedule,
            "workflow_dispatch" => SquadWatchEventKind.Dispatch,
            "push" => SquadWatchEventKind.Push,
            _ => throw new NotSupportedException("Ignored: the platform event is outside the supported in-repository Watch map."),
        };
        if (payload.ValueKind != JsonValueKind.Object || payload.GetRawText().Length > 200_000)
        {
            throw new ArgumentException("The event file must contain one bounded JSON object.", nameof(payload));
        }
        var repository = HumanChannelHttp.Text(GitHubWatch.Child(payload, "repository"), "full_name")
            ?? throw new ArgumentException("The platform event has no structural repository.", nameof(payload));
        JsonElement? thread = payload.TryGetProperty("pull_request", out var pr) ? pr :
            payload.TryGetProperty("issue", out var issue) ? issue : null;
        var inputs = payload.TryGetProperty("inputs", out var input) ? input : default;
        long? number = thread is { } item ? HumanChannelHttp.Id(item, "number") : null;
        if (kind == SquadWatchEventKind.Dispatch && HumanChannelHttp.Text(inputs, "issue_number") is { Length: > 0 } value)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException("A manual issue number must be a positive structural integer.", nameof(payload));
            }
            number = parsed;
        }
        var isPr = kind == SquadWatchEventKind.PullRequest || thread?.TryGetProperty("pull_request", out _) == true;
        var head = pr.ValueKind == JsonValueKind.Object ? GitHubWatch.Child(pr, "head") : default;
        var headRepository = head.ValueKind == JsonValueKind.Object ? HumanChannelHttp.Text(GitHubWatch.Child(head, "repo"), "full_name") : null;
        var comment = payload.TryGetProperty("comment", out var valueComment) ? valueComment : default;
        var commentId = HumanChannelHttp.Id(comment);
        var eventId = kind == SquadWatchEventKind.Comment && commentId is { } id
            ? "comment:" + id.ToString(CultureInfo.InvariantCulture)
            : workflowRunId is { } runId ? "workflow:" + runId.ToString(CultureInfo.InvariantCulture)
            : payload.TryGetProperty("label_event_id", out var labelId) && labelId.TryGetInt64(out var label)
                ? "label:" + label.ToString(CultureInfo.InvariantCulture)
                : throw new ArgumentException("A verified delivery/workflow/label identity is required.", nameof(payload));
        return new()
        {
            Kind = kind,
            Repository = repository,
            EventId = eventId,
            Action = HumanChannelHttp.Text(payload, "action"),
            Number = number,
            IsPullRequest = isPr,
            ForkOrigin = headRepository is not null && !headRepository.Equals(repository, StringComparison.OrdinalIgnoreCase),
            Ref = HumanChannelHttp.Text(payload, "ref"),
            CommitSha = kind == SquadWatchEventKind.Push ? HumanChannelHttp.Text(payload, "after") : HumanChannelHttp.Text(head, "sha"),
            WorkflowRunId = workflowRunId,
            ReceivedAt = receivedAt,
            Actor = payload.TryGetProperty("sender", out var sender) ? HumanChannelHttp.Text(sender, "login") : null,
            Labels = thread is { } resource ? GitHubWatch.Labels(resource) : [],
            Title = thread is { } titled ? HumanChannelHttp.Text(titled, "title") : null,
            Body = kind == SquadWatchEventKind.Comment ? HumanChannelHttp.Text(comment, "body") :
                kind == SquadWatchEventKind.Dispatch ? HumanChannelHttp.Text(inputs, "request") :
                thread is { } described ? HumanChannelHttp.Text(described, "body") : null,
        };
    }
}

internal static partial class GitHubWatch
{
    internal static Uri Root(Uri? root)
    {
        root ??= new Uri("https://api.github.com/");
        if (!root.IsAbsoluteUri || root.Scheme != "https" || root.UserInfo.Length != 0 ||
            root.Query.Length != 0 || root.Fragment.Length != 0 || !root.AbsolutePath.EndsWith('/'))
        {
            throw new ArgumentException("Use a trusted HTTPS GitHub API root without user info, query or fragment.", nameof(root));
        }
        return root;
    }

    internal static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : throw new SquadHumanTransportException("GitHub Watch returned incomplete structural provider evidence.");

    internal static string[] Labels(JsonElement value) =>
        value.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().Select(label => HumanChannelHttp.Text(label, "name") ??
                throw new SquadHumanTransportException("A provider label lacks its exact name.")).ToArray() : [];

    internal static byte[] Decode(string content)
    {
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw new SquadHumanTransportException("GitHub Watch returned malformed encoded resource evidence.");
        }
    }

    internal static string EventName(SquadWatchEventKind kind) => kind switch
    {
        SquadWatchEventKind.Issue => "issues",
        SquadWatchEventKind.PullRequest => "pull_request",
        SquadWatchEventKind.Comment => "issue_comment",
        SquadWatchEventKind.Schedule => "schedule",
        SquadWatchEventKind.Dispatch => "workflow_dispatch",
        _ => "push",
    };
}
