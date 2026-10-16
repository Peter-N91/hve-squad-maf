using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>
/// Real GitHub issue/comment/label approval with authenticated event re-reads. The caller owns
/// HttpClient, authentication, issue-label setup and a handler with automatic redirects disabled.
/// Only safe pending metadata is published; protected case details are never posted.
/// </summary>
public sealed class GitHubIssueApprovalChannel : ISquadHumanApprovalChannel
{
    private static readonly string[] ApprovalLabels = ["squad-approval"];
    private static readonly string[] ChangesLabels = ["squad-approval", "squad/changes"];
    private readonly HumanChannelHttp _http;
    private readonly string _repositoryPath;

    /// <summary>Registers the explicit approval repository/account, using caller-owned HTTP credentials.</summary>
    /// <param name="client">Authenticated caller-owned client; do not log its headers or enable automatic redirects.</param>
    /// <param name="repository">Authorized owner/repository with Issues write and collaborator-read access.</param>
    /// <param name="approverHandle">The configured human account; current write/maintain/admin collaborators also qualify.</param>
    /// <param name="apiRoot">Optional trusted HTTPS GitHub Enterprise API root, without user info, query or fragment.</param>
    public GitHubIssueApprovalChannel(HttpClient client, string repository, string approverHandle, Uri? apiRoot = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(repository) || !Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            string.IsNullOrWhiteSpace(approverHandle) || !Regex.IsMatch(approverHandle, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException("GitHub approvals require a non-secret owner/repository and human account handle.");
        }
        apiRoot ??= new Uri("https://api.github.com/");
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme != "https" || apiRoot.UserInfo.Length != 0 ||
            apiRoot.Query.Length != 0 || apiRoot.Fragment.Length != 0 || !apiRoot.AbsolutePath.EndsWith('/'))
        {
            throw new ArgumentException("Use a trusted HTTPS GitHub API root without user info, query or fragment.", nameof(apiRoot));
        }
        Repository = repository;
        ApproverHandle = approverHandle;
        Binding = "github-issue:" + ProjectFiles.Hash(apiRoot.AbsoluteUri + "\n" + repository.ToLowerInvariant() + "\n" + approverHandle.ToLowerInvariant());
        _http = new(client, apiRoot);
        _repositoryPath = "repos/" + repository + "/issues";
    }

    /// <inheritdoc/>
    public string Name => "github-issue";
    /// <inheritdoc/>
    public string Binding { get; }
    /// <summary>The explicit non-secret approval repository, never inferred from case text.</summary>
    public string Repository { get; }
    /// <summary>The explicitly registered human account; never an approval credential.</summary>
    public string ApproverHandle { get; }

    /// <inheritdoc/>
    public async ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken)
    {
        ValidateAction(action);
        var body = Body(action);
        var issues = await _http.ListAsync(_repositoryPath + "?state=all&labels=squad-approval", cancellationToken).ConfigureAwait(false);
        var matches = issues.Where(issue => HumanChannelHttp.Text(issue, "body")?.Contains(Marker(action), StringComparison.Ordinal) == true).ToArray();
        if (matches.Length > 1)
        {
            throw new SquadHumanTransportException("More than one issue claims this exact human action; reconcile duplicate ownership.");
        }
        if (matches.Length == 1)
        {
            var number = HumanChannelHttp.Id(matches[0], "number");
            if (number is null || HumanChannelHttp.Text(matches[0], "body") != body ||
                matches[0].TryGetProperty("pull_request", out _))
            {
                throw new SquadHumanTransportException("The approval issue binding was altered; do not reuse it.");
            }
            return new("sent", Reference(number.Value));
        }
        if (action.DeliveryStatus is "publishing" or "uncertain")
        {
            return new("uncertain", Diagnostic: "Previous issue publication is uncertain and no exact issue was found. Reconcile the remote outcome before creating another.");
        }
        var account = await _http.SendAsync(HttpMethod.Get, "users/" + ApproverHandle, null, cancellationToken).ConfigureAwait(false);
        if (account is not { } user || HumanChannelHttp.Text(user, "type") != "User")
        {
            throw new SquadHumanTransportException("The configured GitHub approver is not a verified human account.");
        }
        var created = await _http.SendAsync(HttpMethod.Post, _repositoryPath, new
        {
            title = "Native Squad decision " + action.Binding.Id,
            body,
            assignees = new[] { ApproverHandle },
            labels = ApprovalLabels,
        }, cancellationToken).ConfigureAwait(false);
        if (created is not { } issue || HumanChannelHttp.Id(issue, "number") is not { } issueNumber ||
            HumanChannelHttp.Text(issue, "body") != body)
        {
            throw new SquadHumanTransportException("GitHub did not return the exact created approval issue.", outcomeUncertain: true);
        }
        return new("sent", Reference(issueNumber));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SquadHumanResponse>> ReadResponsesAsync(
        SquadPendingHumanAction action, CancellationToken cancellationToken)
    {
        var number = await VerifyIssueAsync(action, cancellationToken).ConfigureAwait(false);
        var responses = new List<SquadHumanResponse>();
        foreach (var comment in await _http.ListAsync(IssuePath(number) + "/comments", cancellationToken).ConfigureAwait(false))
        {
            if (ParseComment(action, comment, number) is { } response)
            {
                responses.Add(response);
            }
        }
        foreach (var item in await _http.ListAsync(IssuePath(number) + "/events", cancellationToken).ConfigureAwait(false))
        {
            if (ParseEvent(action, item, number) is { } response)
            {
                responses.Add(response);
            }
        }
        return responses.OrderBy(response => response.CreatedAt).ThenBy(response => response.ResponseId, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<SquadHumanAuthorization?> AuthorizeAsync(
        SquadPendingHumanAction action, SquadHumanResponse response, CancellationToken cancellationToken)
    {
        var number = await VerifyIssueAsync(action, cancellationToken).ConfigureAwait(false);
        var parts = response.ResponseId.Split(':');
        if (parts.Length != 4 || parts[2] != "issue" || parts[3] != number.ToString(CultureInfo.InvariantCulture) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }
        JsonElement source;
        SquadHumanResponse? actual;
        string actorProperty;
        if (parts[0] == "comment")
        {
            var comment = await _http.SendAsync(HttpMethod.Get, _repositoryPath + "/comments/" + id.ToString(CultureInfo.InvariantCulture),
                null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
            if (comment is not { } value)
            {
                return null;
            }
            source = value;
            actual = ParseComment(action, source, number);
            actorProperty = "user";
        }
        else if (parts[0] == "event")
        {
            var events = await _http.ListAsync(IssuePath(number) + "/events", cancellationToken).ConfigureAwait(false);
            var found = events.Where(item => HumanChannelHttp.Id(item) == id).ToArray();
            if (found.Length != 1)
            {
                return null;
            }
            source = found[0];
            actual = ParseEvent(action, source, number);
            actorProperty = "actor";
        }
        else
        {
            return null;
        }
        if (actual is null || actual != response || !source.TryGetProperty(actorProperty, out var actor) ||
            HumanChannelHttp.Text(actor, "type") != "User" || HumanChannelHttp.Id(actor) is not { } actorId ||
            HumanChannelHttp.Text(actor, "login") is not { } login ||
            !Regex.IsMatch(login, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return null;
        }
        var configured = await _http.SendAsync(HttpMethod.Get, "users/" + ApproverHandle, null,
            cancellationToken, allowNotFound: true).ConfigureAwait(false);
        var registered = configured is { } human && HumanChannelHttp.Text(human, "type") == "User" &&
            HumanChannelHttp.Id(human) == actorId;
        if (!registered)
        {
            var permission = await _http.SendAsync(HttpMethod.Get, "repos/" + Repository + "/collaborators/" + login + "/permission",
                null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
            if (permission is not { } access || HumanChannelHttp.Text(access, "permission") is not ("write" or "maintain" or "admin") ||
                !access.TryGetProperty("user", out var collaborator) || HumanChannelHttp.Id(collaborator) != actorId)
            {
                return null;
            }
        }
        return new("github", ProjectFiles.Hash(Binding + "\n" + actorId.ToString(CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc/>
    public async ValueTask<SquadHumanDelivery> ResolveAsync(
        SquadPendingHumanAction action, SquadHumanDecision decision, CancellationToken cancellationToken)
    {
        var number = await VerifyIssueAsync(action, cancellationToken).ConfigureAwait(false);
        var marker = "<!-- native-squad-human-resolution:" + action.Binding.Id + ":" + decision + " -->";
        var body = "Authenticated " + decision + " recorded for this exact native action. Private input is retained only in the authorized case.\n\n" + marker;
        var comments = await _http.ListAsync(IssuePath(number) + "/comments", cancellationToken).ConfigureAwait(false);
        if (!comments.Any(comment => HumanChannelHttp.Text(comment, "body") == body))
        {
            _ = await _http.SendAsync(HttpMethod.Post, IssuePath(number) + "/comments", new { body }, cancellationToken).ConfigureAwait(false);
        }
        _ = await _http.SendAsync(HttpMethod.Patch, IssuePath(number),
            decision == SquadHumanDecision.Changes ? new { state = "open", labels = ChangesLabels } :
                new { state = "closed", labels = ApprovalLabels }, cancellationToken).ConfigureAwait(false);
        return new("sent", Reference(number));
    }

    private static void ValidateAction(SquadPendingHumanAction action)
    {
        var binding = action.Binding;
        if (!Guid.TryParseExact(binding.Id, "N", out _) || !Guid.TryParseExact(binding.RunId, "N", out _) ||
            !HumanActions.IsHash(binding.RootSha256) || !HumanActions.IsHash(binding.ReleaseFingerprint) ||
            !HumanActions.IsHash(binding.HostBinding) || !HumanActions.IsHash(binding.PayloadSha256) ||
            !HumanActions.IsHash(binding.ActionSha256) || !HumanActions.IsHash(binding.EvidenceSha256) ||
            binding.Mode is not ("interactive" or "autonomous" or "autopilot") ||
            action.ExpiresAt <= action.CreatedAt || action.Channel != "github-issue")
        {
            throw new ArgumentException("The notification is not a valid exact native human-action binding.", nameof(action));
        }
    }

    private static string Marker(SquadPendingHumanAction action) =>
        "<!-- native-squad-human:" + action.Binding.Id + ":" + action.Binding.PayloadSha256 + " -->";

    internal static string Body(SquadPendingHumanAction action) =>
        "# Native Squad human decision\n\n" +
        "* Mode: " + action.Binding.Mode + "\n* Trigger: " + action.Binding.Gate +
        "\n* Topic: authorized case " + action.Binding.RunId +
        "\n* Awaiting: " + (action.IsQuestion ? "an answer to the exact question" : "approval of the unchanged identified action") +
        "\n* Detail: Private case text, tool arguments and artifact paths are not published. Inspect the authorized case UI." +
        "\n* Expires: " + action.ExpiresAt.ToString("O", CultureInfo.InvariantCulture) +
        "\n\n## Exact opaque binding\n\n```json\n" + JsonSerializer.Serialize(action.Binding) +
        "\n```\n\n## How to respond\n\n" +
        (action.IsQuestion ? "* Answer: `/answer: <answer>`\n" : "* Approve this action only: `/approve` or `squad/approved`\n") +
        "* Request changes: `/changes: <note>`\n* Stop: `/stop` or `squad/stop`\n" +
        "Only the registered human or a current write/maintain/admin collaborator qualifies. Other prose is not a control signal. " +
        "Blanket approvals are not accepted.\n\n" + Marker(action);

    private async ValueTask<long> VerifyIssueAsync(SquadPendingHumanAction action, CancellationToken cancellationToken)
    {
        ValidateAction(action);
        var prefix = Repository + "#";
        if (action.Reference is null || !action.Reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(action.Reference[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new SquadHumanTransportException("No exact verified approval-issue reference is available.");
        }
        var issue = await _http.SendAsync(HttpMethod.Get, IssuePath(number), null, cancellationToken).ConfigureAwait(false);
        if (issue is not { } value || HumanChannelHttp.Id(value, "number") != number ||
            HumanChannelHttp.Text(value, "body") != Body(action) || value.TryGetProperty("pull_request", out _))
        {
            throw new SquadHumanTransportException("Approval issue evidence was altered or refers to another action; no approval is recognized.");
        }
        return number;
    }

    private SquadHumanResponse? ParseComment(SquadPendingHumanAction action, JsonElement comment, long issue)
    {
        var created = HumanChannelHttp.Time(comment, "created_at");
        var updated = HumanChannelHttp.Time(comment, "updated_at");
        if (HumanChannelHttp.Id(comment) is not { } id || HumanChannelHttp.Text(comment, "body") is not { } body ||
            HumanChannelHttp.Text(comment, "issue_url") != _http.Address(IssuePath(issue)).AbsoluteUri ||
            created is null || created < action.CreatedAt || updated is null || updated < created ||
            updated >= action.ExpiresAt)
        {
            return null;
        }
        try
        {
            return SquadHumanResponse.FromCommand(action, body, "comment:" + id.ToString(CultureInfo.InvariantCulture) +
                ":issue:" + issue.ToString(CultureInfo.InvariantCulture), updated);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static SquadHumanResponse? ParseEvent(SquadPendingHumanAction action, JsonElement item, long issue)
    {
        var created = HumanChannelHttp.Time(item, "created_at");
        if (HumanChannelHttp.Id(item) is not { } id || HumanChannelHttp.Text(item, "event") != "labeled" ||
            created is null || created < action.CreatedAt || created >= action.ExpiresAt ||
            !item.TryGetProperty("label", out var label))
        {
            return null;
        }
        var decision = HumanChannelHttp.Text(label, "name") switch
        {
            "squad/approved" when !action.IsQuestion => SquadHumanDecision.Approve,
            "squad/changes" => SquadHumanDecision.Changes,
            "squad/stop" => SquadHumanDecision.Stop,
            _ => (SquadHumanDecision?)null,
        };
        return decision is null ? null : new(action.Binding, decision.Value,
            "event:" + id.ToString(CultureInfo.InvariantCulture) + ":issue:" + issue.ToString(CultureInfo.InvariantCulture),
            Input: decision == SquadHumanDecision.Changes ?
                "Authenticated human requested changes with the squad/changes label; no detailed note was supplied." : null,
            CreatedAt: created);
    }

    private string IssuePath(long number) => _repositoryPath + "/" + number.ToString(CultureInfo.InvariantCulture);
    private string Reference(long number) => Repository + "#" + number.ToString(CultureInfo.InvariantCulture);
}
