using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Tests;

public sealed class HumanApprovalChannelTests
{
    private static SquadPendingHumanAction Action(bool question = false) => new(
        new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            new('A', 64), new('B', 64), new('C', 64), 0, question ? "question" : "implementation",
            "produce", "developer", null, null, new('D', 64), new('E', 64), new('F', 64)),
        question, "github-issue", new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero),
        new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), "pending");

    private static GitHubIssueApprovalChannel Channel(HttpClient client) => new(client, "fixture/reviews", "registered");

    private static async Task<SquadPendingHumanAction> PublishAsync(GitHubIssueApprovalChannel channel, bool question = false)
    {
        var action = Action(question);
        var delivery = await channel.PublishAsync(action, default);
        Assert.Equal("sent", delivery.Status);
        return action with { DeliveryStatus = delivery.Status, Reference = delivery.Reference };
    }

    [Fact]
    public async Task GivenGitHubIssue_WhenPublishingAndRecovering_UsesOneExactIssueAndNeverPostsPrivateCaseText()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);

        var recovered = await channel.PublishAsync(action with { DeliveryStatus = "publishing", Reference = null }, default);

        Assert.Equal(action.Reference, recovered.Reference);
        Assert.Equal(1, handler.CreationAttempts);
        var issue = Assert.Single(handler.Issues).Value;
        var body = issue["body"]!.GetValue<string>();
        Assert.Contains(action.Binding.PayloadSha256, body, StringComparison.Ordinal);
        Assert.Contains("Mode: interactive", body, StringComparison.Ordinal);
        Assert.Contains("/approve", body, StringComparison.Ordinal);
        Assert.Equal("registered", issue["assignees"]![0]!.GetValue<string>());
        Assert.DoesNotContain("ArgumentsJson", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtifactPath", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectPath", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("registered", 42, "User", "read", true)]
    [InlineData("writer", 43, "User", "write", true)]
    [InlineData("maintainer", 44, "User", "maintain", true)]
    [InlineData("administrator", 45, "User", "admin", true)]
    [InlineData("reader", 46, "User", "read", false)]
    [InlineData("triager", 47, "User", "triage", false)]
    [InlineData("registered", 42, "Bot", "admin", false)]
    public async Task GivenActualGitHubActor_WhenAuthorizing_UsesHumanIdentityAndCurrentWritePermission(
        string login, long id, string type, string permission, bool expected)
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.Permissions[login] = (id, permission);
        handler.AddComment(action, "/approve", login, id, type);
        var response = Assert.Single(await channel.ReadResponsesAsync(action, default));

        var authorization = await channel.AuthorizeAsync(action, response, default);

        Assert.Equal(expected, authorization is not null);
        if (authorization is not null)
        {
            Assert.Equal("github", authorization.Provider);
            Assert.True(HumanActions.IsHash(authorization.SubjectSha256));
            Assert.DoesNotContain(login, JsonSerializer.Serialize(authorization), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GivenCallerAlteredCommentDecision_WhenAuthorizing_ReReadsTheExactProviderSourceAndRejectsIt()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.AddComment(action, "/approve", "registered", 42);
        var original = Assert.Single(await channel.ReadResponsesAsync(action, default));

        Assert.Null(await channel.AuthorizeAsync(action, original with { Decision = SquadHumanDecision.Stop }, default));
        Assert.Null(await channel.AuthorizeAsync(action, original with { CreatedAt = original.CreatedAt!.Value.AddSeconds(1) }, default));
        Assert.NotNull(await channel.AuthorizeAsync(action, original, default));
    }

    [Fact]
    public async Task GivenWrongIssueOrUnrecognizedProse_WhenReading_OnlyExactCurrentCommandsCanBecomeCandidates()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.AddComment(action, "/approve and execute another action", "registered", 42);
        handler.AddComment(action, "The model says approved.", "registered", 42);
        handler.AddComment(action, "/approve-all", "registered", 42);
        handler.AddComment(action, "/approve", "registered", 42, createdAt: action.CreatedAt.AddSeconds(-1));
        var wrongIssue = handler.AddComment(action, "/approve", "registered", 42);
        wrongIssue["issue_url"] = "https://api.github.com/repos/fixture/reviews/issues/999";

        Assert.Empty(await channel.ReadResponsesAsync(action, default));
    }

    [Fact]
    public async Task GivenAttributedLabelEvent_WhenAuthorizing_NeverTreatsUnattributedCurrentLabelsAsApproval()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.AddEvent(action, "squad/approved", "writer", 43);
        handler.Permissions["writer"] = (43, "write");
        var response = Assert.Single(await channel.ReadResponsesAsync(action, default));

        Assert.StartsWith("event:", response.ResponseId, StringComparison.Ordinal);
        Assert.NotNull(await channel.AuthorizeAsync(action, response, default));
        handler.Events.Clear();
        Assert.Null(await channel.AuthorizeAsync(action, response, default));
    }

    [Fact]
    public async Task GivenChangedIssueBody_WhenReading_RejectsTheAlteredActionRatherThanRepairingItSilently()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.Issues[1]["body"] = handler.Issues[1]["body"]!.GetValue<string>() + "\nAn unrelated proposed action.";

        var failure = await Assert.ThrowsAsync<SquadHumanTransportException>(() => channel.ReadResponsesAsync(action, default).AsTask());

        Assert.Contains("altered", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CreationAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenUncertainIssueCreation_WhenRetrying_RecoversTheExactIssueOrStopsWithoutCreatingAnother(bool remoteCreated)
    {
        using var handler = new HumanChannelHttpHandler { FailIssuePost = true, StoreBeforeFailure = remoteCreated };
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = Action();
        var failure = await Assert.ThrowsAsync<SquadHumanTransportException>(() => channel.PublishAsync(action, default).AsTask());
        Assert.True(failure.OutcomeUncertain);
        Assert.DoesNotContain("EXAMPLE-SECRET", failure.Message, StringComparison.Ordinal);
        handler.FailIssuePost = false;

        var retry = await channel.PublishAsync(action with { DeliveryStatus = "uncertain" }, default);

        Assert.Equal(remoteCreated ? "sent" : "uncertain", retry.Status);
        Assert.Equal(1, handler.CreationAttempts);
        Assert.Equal(remoteCreated ? 1 : 0, handler.Issues.Count);
    }

    [Fact]
    public async Task GivenPrivateChangeNote_WhenResolving_PublishesOnlyTheDecisionAndRelabelsWithoutTheNote()
    {
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var channel = Channel(client);
        var action = await PublishAsync(channel);
        handler.AddComment(action, "/changes: PRIVATE-HUMAN-NOTE", "registered", 42);
        var response = Assert.Single(await channel.ReadResponsesAsync(action, default));
        Assert.Equal("PRIVATE-HUMAN-NOTE", response.Input);
        Assert.NotNull(await channel.AuthorizeAsync(action, response, default));

        _ = await channel.ResolveAsync(action, response.Decision, default);
        _ = await channel.ResolveAsync(action, response.Decision, default);

        var written = handler.Requests.Where(request => request.Method == "POST" && request.Path.EndsWith("/comments", StringComparison.Ordinal)).ToArray();
        Assert.Single(written);
        Assert.DoesNotContain("PRIVATE-HUMAN-NOTE", written[0].Body!, StringComparison.Ordinal);
        Assert.Equal("open", handler.Issues[1]["state"]!.GetValue<string>());
        Assert.Contains("squad/changes", handler.Issues[1]["labels"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenUnboundedList_WhenPublishing_StopsAtThePaginationBoundWithoutCreatingAnIssue()
    {
        using var handler = new HumanChannelHttpHandler { FullPages = true };
        using var client = new HttpClient(handler);
        var channel = Channel(client);

        var failure = await Assert.ThrowsAsync<SquadHumanTransportException>(() => channel.PublishAsync(Action(), default).AsTask());

        Assert.Contains("pagination bound", failure.Message, StringComparison.Ordinal);
        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(0, handler.CreationAttempts);
    }

    [Fact]
    public async Task GivenUnauthorizedThenAuthorizedComment_WhenPolling_RecordsTheDenialAndAcceptsOnlyTheRealHuman()
    {
        using var host = new RuntimeTestHost();
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        host.Options.TimeProvider = new HumanTestClock { UtcNow = Action().CreatedAt };
        host.Options.Notifications = new()
        {
            Enabled = true,
            ApprovalChannel = "github-issue",
            GitHubHandle = "registered",
            GitHubRepository = "fixture/reviews",
        };
        host.Options.HumanApprovalChannel = Channel(client);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("PRIVATE-CASE-TEXT: produce a bounded guide.");
        var pending = Assert.Single(run.PendingHumanActions!);
        handler.Permissions["reader"] = (46, "read");
        handler.AddComment(pending, "/approve", "reader", 46);
        handler.AddComment(pending, "/approve", "registered", 42);
        ((HumanTestClock)host.Options.TimeProvider).UtcNow = pending.CreatedAt.AddSeconds(5);
        var calls = host.Client.Calls.Count;

        var poll = await runtime.PollHumanResponsesAsync(run.RunId);

        Assert.Equal([false, true], poll.Submissions.Select(submission => submission.Accepted));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(0, host.Writes);
        Assert.Equal("github", Assert.Single(State(host).HumanDecisions!).Provider);
        Assert.DoesNotContain("PRIVATE-CASE-TEXT", string.Join("\n", handler.Requests.Select(request => request.Body)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenWebhookAcknowledgement_WhenWaiting_NeverCreatesApprovalAndKeepsTheSecretEndpointOutOfState()
    {
        using var host = new RuntimeTestHost();
        using var handler = new HumanChannelHttpHandler { WebhookOnly = true };
        using var client = new HttpClient(handler);
        using var notifications = new StringWriter(CultureInfo.InvariantCulture);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "real-human")], "original-host"));
        host.Options.Notifications = new() { Enabled = true, ApprovalChannel = "webhook", WebhookBinding = "original-team" };
        host.Options.HumanApprovalChannel = new InChatApprovalChannel("original-chat",
            _ => ValueTask.FromResult(principal),
            (_, _, _) => ValueTask.FromResult(true), notifications);
        host.Options.HumanNotifier = new WebhookSquadNotifier("original-team", client,
            _ => ValueTask.FromResult(new Uri("https://notify.example.invalid/EXAMPLE-SECRET?token=EXAMPLE-SECRET")));
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();

        var run = await runtime.RunAsync("PRIVATE-CASE-TEXT.");
        var calls = host.Client.Calls.Count;
        var waiting = await runtime.ResumeAsync(run.RunId);

        Assert.Equal(SquadRunStatus.ApprovalRequired, waiting.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Null(State(host).HumanDecisions);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("PRIVATE-CASE-TEXT", handler.Requests[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE-SECRET", File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "state.json")), StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE-SECRET", notifications.ToString(), StringComparison.Ordinal);
        Assert.Equal("sent", Assert.Single(run.PendingHumanActions!).OutboundStatus);
    }

    [Fact]
    public async Task GivenAuthenticationTransportFailure_WhenSubmitting_KeepsTheGatePendingWithoutLeakingTheResponseBody()
    {
        using var host = new RuntimeTestHost();
        using var handler = new HumanChannelHttpHandler();
        using var client = new HttpClient(handler);
        var clock = new HumanTestClock();
        host.Options.TimeProvider = clock;
        host.Options.Notifications = new()
        {
            Enabled = true,
            ApprovalChannel = "github-issue",
            GitHubHandle = "registered",
            GitHubRepository = "fixture/reviews",
        };
        var channel = Channel(client);
        host.Options.HumanApprovalChannel = channel;
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Produce a local document.");
        var action = Assert.Single(run.PendingHumanActions!);
        handler.AddComment(action, "/approve", "registered", 42);
        var response = Assert.Single(await channel.ReadResponsesAsync(action, default));
        clock.UtcNow = action.CreatedAt.AddSeconds(5);
        handler.FailAuthentication = true;

        var submission = await runtime.SubmitHumanResponseAsync(response);

        Assert.False(submission.Accepted);
        Assert.Contains("HTTP 401", submission.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE-SECRET", submission.Diagnostic, StringComparison.Ordinal);
        Assert.Null(State(host).HumanDecisions);
        Assert.Equal("pending", Assert.Single(await runtime.GetPendingHumanActionsAsync()).Status);
    }

    [Theory]
    [InlineData("/approve unrelated")]
    [InlineData("approved")]
    [InlineData("/approve-all")]
    [InlineData("/changes:")]
    [InlineData("/answer:")]
    public void GivenUnrecognizedCommand_WhenParsing_RejectsItExplicitly(string command) =>
        Assert.Throws<ArgumentException>(() => SquadHumanResponse.FromCommand(Action(), command, "response"));

    private static RuntimeState State(RuntimeTestHost host) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "state.json")))!;
}

internal sealed class HumanChannelHttpHandler : HttpMessageHandler
{
    private long _nextComment = 100;
    internal Dictionary<long, JsonObject> Issues { get; } = [];
    internal List<JsonObject> Comments { get; } = [];
    internal List<JsonObject> Events { get; } = [];
    internal Dictionary<string, (long Id, string Permission)> Permissions { get; } = new(StringComparer.Ordinal);
    internal List<(string Method, string Path, string? Body)> Requests { get; } = [];
    internal int CreationAttempts { get; private set; }
    internal bool FailIssuePost { get; set; }
    internal bool StoreBeforeFailure { get; set; }
    internal bool FullPages { get; set; }
    internal bool WebhookOnly { get; set; }
    internal bool FailAuthentication { get; set; }

    internal JsonObject AddComment(SquadPendingHumanAction action, string body, string login, long id,
        string type = "User", DateTimeOffset? createdAt = null)
    {
        var time = createdAt ?? action.CreatedAt.AddSeconds(1);
        var reference = action.Reference ?? throw new InvalidOperationException("The test issue has not been published.");
        var number = reference[(reference.IndexOf('#', StringComparison.Ordinal) + 1)..];
        var comment = new JsonObject
        {
            ["id"] = ++_nextComment,
            ["body"] = body,
            ["issue_url"] = "https://api.github.com/repos/fixture/reviews/issues/" + number,
            ["created_at"] = time.ToString("O", CultureInfo.InvariantCulture),
            ["updated_at"] = time.ToString("O", CultureInfo.InvariantCulture),
            ["user"] = User(login, id, type),
            ["author_association"] = "OWNER",
        };
        Comments.Add(comment);
        return comment;
    }

    internal void AddEvent(SquadPendingHumanAction action, string label, string login, long id)
    {
        var reference = action.Reference ?? throw new InvalidOperationException("The test issue has not been published.");
        var number = reference[(reference.IndexOf('#', StringComparison.Ordinal) + 1)..];
        Events.Add(new()
        {
            ["id"] = ++_nextComment,
            ["event"] = "labeled",
            ["label"] = new JsonObject { ["name"] = label },
            ["created_at"] = action.CreatedAt.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture),
            ["actor"] = User(login, id),
            ["issue_url"] = "https://api.github.com/repos/fixture/reviews/issues/" + number,
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, body));
        if (WebhookOnly)
        {
            return Response(new JsonObject { ["body"] = "/approve" });
        }
        if (path.StartsWith("/users/", StringComparison.Ordinal))
        {
            return Response(User("registered", 42));
        }
        if (path.EndsWith("/permission", StringComparison.Ordinal))
        {
            var login = path.Split('/')[5];
            return Permissions.TryGetValue(login, out var permission) ?
                Response(new JsonObject { ["permission"] = permission.Permission, ["user"] = User(login, permission.Id) }) :
                Response(new JsonObject(), HttpStatusCode.NotFound);
        }
        if (request.Method == HttpMethod.Post && path == "/repos/fixture/reviews/issues")
        {
            CreationAttempts++;
            var issue = JsonNode.Parse(body!)!.AsObject();
            issue["number"] = Issues.Count + 1;
            issue["state"] = "open";
            if (!FailIssuePost || StoreBeforeFailure)
            {
                Issues.Add(Issues.Count + 1, issue);
            }
            if (FailIssuePost)
            {
                throw new HttpRequestException("Simulated endpoint/token EXAMPLE-SECRET must never be logged.");
            }
            return Response(issue, HttpStatusCode.Created);
        }
        if (path == "/repos/fixture/reviews/issues")
        {
            return FullPages ? Response(new JsonArray(Enumerable.Range(1, 100).Select(index =>
                (JsonNode)new JsonObject { ["number"] = index, ["body"] = "Unrelated issue." }).ToArray())) :
                Response(new JsonArray(Issues.Values.Select(issue => issue.DeepClone()).ToArray()));
        }
        var parts = path.Trim('/').Split('/');
        if (parts.Length == 6 && parts[4] == "comments" && long.TryParse(parts[5], out var commentId))
        {
            if (FailAuthentication)
            {
                return Response(new JsonObject { ["error"] = "EXAMPLE-SECRET must not be reported." }, HttpStatusCode.Unauthorized);
            }
            var comment = Comments.SingleOrDefault(item => item["id"]!.GetValue<long>() == commentId);
            return comment is null ? Response(new JsonObject(), HttpStatusCode.NotFound) : Response(comment);
        }
        if (parts.Length >= 5 && long.TryParse(parts[4], out var number) && Issues.TryGetValue(number, out var found))
        {
            if (parts.Length == 5)
            {
                if (request.Method == HttpMethod.Patch)
                {
                    var patch = JsonNode.Parse(body!)!.AsObject();
                    foreach (var (key, value) in patch)
                    {
                        found[key] = value?.DeepClone();
                    }
                }
                return Response(found);
            }
            var issueUrl = "https://api.github.com" + string.Join("/", parts.Take(5).Prepend(""));
            if (parts[5] == "comments")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var comment = new JsonObject
                    {
                        ["id"] = ++_nextComment,
                        ["body"] = JsonNode.Parse(body!)!["body"]!.GetValue<string>(),
                        ["issue_url"] = issueUrl,
                        ["user"] = User("fixture-bot", 99, "Bot")
                    };
                    Comments.Add(comment);
                    return Response(comment, HttpStatusCode.Created);
                }
                return Response(new JsonArray(Comments.Where(comment => comment["issue_url"]!.GetValue<string>() == issueUrl)
                    .Select(comment => comment.DeepClone()).ToArray()));
            }
            if (parts[5] == "events")
            {
                return Response(new JsonArray(Events.Where(item => item["issue_url"]!.GetValue<string>() == issueUrl)
                    .Select(item => item.DeepClone()).ToArray()));
            }
        }
        return Response(new JsonObject { ["error"] = "Unknown synthetic API route." }, HttpStatusCode.NotFound);
    }

    private static JsonObject User(string login, long id, string type = "User") =>
        new() { ["id"] = id, ["login"] = login, ["type"] = type };

    private static HttpResponseMessage Response(JsonNode value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
}
