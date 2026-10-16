using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeFederationTests
{
    private static string PathFor(RuntimeTestHost host, string path) =>
        Path.Combine(host.Project, path.Replace('/', Path.DirectorySeparatorChar));

    private static string MemberRoot(string name) => ".copilot-tracking/squad/members/" + name;

    private static SquadFederationMemberConfiguration Member(string name, string? profile = null) => new()
    {
        Name = name,
        Description = "Original synthetic outcome for " + name,
        Patterns = [name + " work"],
        Owner = "synthetic-team",
        Roster = profile is null ? null : new() { Profile = profile },
    };

    private static Task<SquadFederation> Create(RuntimeTestHost host) =>
        SquadFederation.CreateAsync(host.Client, host.Options, host.Source());

    private static void QueueResearch(RuntimeTestHost host)
    {
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("researcher");
        host.Client.Text("Research completed, without claiming product delivery.");
    }

    private static Dictionary<string, string> Manifest(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void AssertManifest(Dictionary<string, string> expected, string directory)
    {
        var actual = Manifest(directory);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        Assert.All(expected, pair => Assert.Equal(pair.Value, actual[pair.Key]));
    }

    private static string[] Candidates(SquadFederation federation) =>
        federation.GetPromotionCandidates().Select(candidate => candidate.Path).ToArray();

    private static async Task SeedSingle(RuntimeTestHost host, bool full = false)
    {
        host.UseCurrentPlanContract();
        if (full)
        {
            host.QueueSuccess("technical-writer");
        }
        else
        {
            QueueResearch(host);
        }
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Inspect the existing synthetic implementation.");
        Assert.True(result.Status == (full ? SquadRunStatus.Completed : SquadRunStatus.ResearchCompleted), result.ResponseText);
    }

    [Fact]
    public async Task GivenFreshCase_WhenConfirmingInit_CreatesActualIndependentNativeTreesAndRegistry()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        Assert.Equal(SquadTopology.Initialization, federation.GetSnapshot().Topology);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        host.Options.AskAsync = (_, _) => throw new InvalidOperationException("Init must not re-interview each member.");

        var result = await federation.InitializeAsync([Member("product"), Member("platform", "product")]);

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Empty(host.Client.Calls);
        var approval = Assert.Single(host.Approvals);
        Assert.Equal(SquadApprovalKind.FederationInitialization, approval.Kind);
        Assert.All(approval.Federation!.Members, member => Assert.Null(member.Initialization.NotificationQuestion));
        var snapshot = federation.GetSnapshot();
        Assert.Equal(SquadTopology.Federation, snapshot.Topology);
        Assert.Equal(["product", "platform"], snapshot.Members.Select(member => member.Name));
        Assert.Equal(2, snapshot.Routes.Count);
        foreach (var name in new[] { "product", "platform" })
        {
            foreach (var file in new[] { "team.md", "routing.md", "decisions.md", "notifications.md", "state.json",
                "consumption.md", "consumption-rates.md" })
            {
                Assert.True(File.Exists(PathFor(host, MemberRoot(name) + "/" + file)));
            }
            Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/history/" + name + ".md")));
            using var state = JsonDocument.Parse(File.ReadAllText(PathFor(host, MemberRoot(name) + "/state.json")));
            Assert.Equal("hve-squad-maf/native-squad/v2", state.RootElement.GetProperty("Schema").GetString());
            Assert.Equal(MemberRoot(name), state.RootElement.GetProperty("SquadRoot").GetString());
        }
        foreach (var file in new[] { "team.md", "routing.md", "notifications.md", "consumption.md" })
        {
            Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/" + file)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMissingOrDeniedConfirmation_WhenInitializing_LeavesAllProjectBytesUntouched(bool denied)
    {
        using var host = new RuntimeTestHost();
        host.Options.ApproveAsync = denied ? (_, _) => ValueTask.FromResult(false) : null;
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product")]);

        Assert.Equal(denied ? SquadRunStatus.ApprovalDenied : SquadRunStatus.ApprovalRequired, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Empty(host.Client.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Product")]
    [InlineData("data platform")]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("con")]
    public async Task GivenInvalidName_WhenInitializing_RejectsBeforeAnyFolderOrModel(string name)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);

        await Assert.ThrowsAsync<ArgumentException>(() => federation.InitializeAsync([Member(name)]));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Empty(host.Approvals);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenDuplicateNames_WhenInitializing_NeverAutoSuffixesOrCreatesState()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product"), Member("product")]);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("name", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Empty(host.Approvals);
    }

    [Fact]
    public async Task GivenCaseCollidingOrphanDirectory_WhenInitializing_PreservesItWithoutReuse()
    {
        using var host = new RuntimeTestHost();
        Directory.CreateDirectory(PathFor(host, MemberRoot("Product")));
        File.WriteAllText(PathFor(host, MemberRoot("Product") + "/private.txt"), "KEEP_EXACTLY");
        var original = Manifest(host.Project);
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product")]);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        AssertManifest(original, host.Project);
        Assert.Empty(host.Approvals);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("route")]
    [InlineData("scope")]
    [InlineData("sibling-root")]
    public async Task GivenInvalidMemberConfiguration_WhenInitializing_NeverCreatesAPlausibleFallback(string defect)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var member = Member("product");
        member.Roster = new();
        switch (defect)
        {
            case "profile": member.Roster.Profile = "not-installed"; break;
            case "route": member.Patterns.Add("injected | row"); break;
            case "scope": member.Roster.Scope = "federation"; break;
            case "sibling-root": member.Roster.DeliverableRoots["researcher"] = MemberRoot("other") + "/research"; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        if (defect == "profile")
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => federation.InitializeAsync([member]));
        }
        else if (defect is "route" or "scope")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => federation.InitializeAsync([member]));
        }
        else
        {
            Assert.Equal(SquadRunStatus.Blocked, (await federation.InitializeAsync([member])).Status);
        }
        Assert.Empty(host.Approvals);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenParentNamingAndContacts_WhenInitializing_InheritsOnceAndRetainsExplicitMemberOverride()
    {
        using var host = new RuntimeTestHost();
        var rosterPath = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        host.Write("instructions/squad/squad-roster.instructions.md", File.ReadAllText(rosterPath) +
            "\n#### Deterministic Alias Wordlist\n\n```text\nAlpha, Beta, Gamma, Delta\n```\n");
        host.Options.NamingPolicy = "assigned";
        host.Options.Notifications.Email = "parent@example.invalid";
        var second = Member("platform", "product");
        second.Roster!.Notifications = new() { Email = "platform@example.invalid" };
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product"), second]);

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Single(host.Approvals);
        Assert.Equal("parent@example.invalid", result.Proposal!.Notifications.Email);
        foreach (var name in new[] { "product", "platform" })
        {
            using var runtime = await federation.CreateMemberRuntimeAsync(name);
            Assert.Equal("Alpha", runtime.Roster.Resolve("researcher")!.MemberName);
            using var state = JsonDocument.Parse(File.ReadAllText(PathFor(host, MemberRoot(name) + "/state.json")));
            Assert.Equal(name == "product" ? "parent@example.invalid" : "platform@example.invalid",
                state.RootElement.GetProperty("notify").GetProperty("Email").GetString());
        }
        Assert.Empty(host.Client.Calls);
        Assert.DoesNotContain(host.Log.Messages, message => message.Contains("@example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenRealFederationMember_WhenRunning_UsesMAFAndItsOwnRootWithoutSingleSquadInit()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var init = await federation.InitializeAsync([Member("product"), Member("platform")]);
        Assert.True(init.Status == SquadRunStatus.Completed, init.Diagnostic);
        host.Approvals.Clear();
        QueueResearch(host);
        using var runtime = await federation.CreateMemberRuntimeAsync("product");

        var result = await runtime.RunAsync("Inspect the existing synthetic case.");

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.All(result.Evidence, evidence =>
        {
            Assert.StartsWith(MemberRoot("product") + "/", evidence.ArtifactPath, StringComparison.Ordinal);
            Assert.StartsWith(MemberRoot("product") + "/", evidence.HistoryPath, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(host.Approvals, approval => approval.Kind == SquadApprovalKind.Initialization);
        Assert.NotEmpty(host.Client.Calls);
        using var platform = JsonDocument.Parse(File.ReadAllText(PathFor(host, MemberRoot("platform") + "/state.json")));
        Assert.Empty(platform.RootElement.GetProperty("Runs").EnumerateArray());
    }

    [Fact]
    public async Task GivenExistingFederation_WhenExpanding_PreservesEveryOriginalMemberByteAndRoute()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var init = await federation.InitializeAsync([Member("product")]);
        Assert.True(init.Status == SquadRunStatus.Completed, init.Diagnostic);
        var original = Manifest(PathFor(host, MemberRoot("product")));
        var routes = federation.GetSnapshot().Routes.ToArray();

        var expansion = await federation.InitializeAsync([Member("platform", "product")]);

        Assert.True(expansion.Status == SquadRunStatus.Completed, expansion.Diagnostic);
        AssertManifest(original, PathFor(host, MemberRoot("product")));
        var snapshot = federation.GetSnapshot();
        Assert.Equal(["product", "platform"], snapshot.Members.Select(member => member.Name));
        Assert.All(routes, route => Assert.Contains(route, snapshot.Routes));
        Assert.Equal(SquadApprovalKind.FederationExpansion, host.Approvals[^1].Kind);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenSingleSquad_WhenCallingInit_RequiresExplicitPromotionInsteadOfRebuilding()
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        var original = Manifest(host.Project);
        host.Approvals.Clear();
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product")]);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("Promotion", result.Diagnostic, StringComparison.Ordinal);
        AssertManifest(original, host.Project);
        Assert.Empty(host.Approvals);
    }

    [Fact]
    public async Task GivenDiskOnlyDirectories_WhenOfferingPromotion_EnumeratesAllAndChangesNoBytes()
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/original-session/empty"));
        File.WriteAllText(PathFor(host, ".copilot-tracking/original-session/fact.txt"), "ORIGINAL_CASE");
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/unrelated-session"));
        File.WriteAllText(PathFor(host, ".copilot-tracking/unrelated-session/fact.txt"), "KEEP_PRIVATE");
        var original = Manifest(host.Project);
        using var federation = await Create(host);

        var candidates = federation.GetPromotionCandidates();

        Assert.Contains(candidates, candidate => candidate.Path == ".copilot-tracking/original-session");
        Assert.Contains(candidates, candidate => candidate.Path == ".copilot-tracking/unrelated-session");
        Assert.DoesNotContain(candidates, candidate => candidate.Path == ".copilot-tracking/squad");
        Assert.All(candidates, candidate => Assert.False(candidate.Selected));
        AssertManifest(original, host.Project);
    }

    [Fact]
    public async Task GivenVerifiedSingleSquad_WhenPromoting_PreservesAllOriginalEvidenceNamesAndRealSpend()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        host.Options.NamingPolicy = "provided";
        host.Options.MemberNames["researcher"] = "Partner";
        host.Options.DeliverableRoots["technical-writer"] = ".copilot-tracking/original-documents";
        await SeedSingle(host, full: true);
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/original-session/empty"));
        File.WriteAllBytes(PathFor(host, ".copilot-tracking/original-session/bytes.bin"), [0, 255, 13, 10, 42]);
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/unrelated-session"));
        File.WriteAllText(PathFor(host, ".copilot-tracking/unrelated-session/private.txt"), "UNRELATED_PRIVATE_CASE");
        var originalRoot = PathFor(host, ".copilot-tracking/squad");
        var originalHistory = Manifest(Path.Combine(originalRoot, "history"));
        var ledger = File.ReadAllBytes(Path.Combine(originalRoot, "consumption.md"));
        var decisions = File.ReadAllBytes(Path.Combine(originalRoot, "decisions.md"));
        var stateBytes = File.ReadAllBytes(Path.Combine(originalRoot, "state.json"));
        using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(originalRoot, "state.json")));
        using var federation = await Create(host);
        var selected = Candidates(federation).Where(path => path != ".copilot-tracking/unrelated-session").ToArray();
        var priorCalls = host.Client.Calls.Count;

        var result = await federation.PromoteAsync("product", selected);

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var target = PathFor(host, MemberRoot("product"));
        AssertManifest(originalHistory, Path.Combine(target, "history"));
        Assert.Equal(ledger, File.ReadAllBytes(Path.Combine(target, "consumption.md")));
        Assert.Equal(decisions, File.ReadAllBytes(Path.Combine(target, "decisions.md")));
        Assert.Equal(stateBytes, File.ReadAllBytes(Path.Combine(target, "migrations", "federation-" + result.OperationId, "original-state.json")));
        Assert.Equal(new byte[] { 0, 255, 13, 10, 42 }, File.ReadAllBytes(Path.Combine(target, "original-session", "bytes.bin")));
        Assert.True(Directory.Exists(Path.Combine(target, "original-session", "empty")));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/team.md")));
        Assert.False(Directory.Exists(PathFor(host, ".copilot-tracking/original-session")));
        Assert.Equal("UNRELATED_PRIVATE_CASE", File.ReadAllText(PathFor(host, ".copilot-tracking/unrelated-session/private.txt")));
        Assert.Equal(priorCalls, host.Client.Calls.Count);
        Assert.Equal("default", Assert.Single(federation.GetSnapshot().Members).Profile);
        Assert.True(result.Proposal!.PreservedCost!.ProviderAttempts > 0);
        Assert.NotNull(result.Proposal.PreservedCost.EstimatedCostUsd);
        Assert.True(result.Proposal.PreservedCost.EstimatedCostUsd > 0);
        using var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "state.json")));
        foreach (var field in new[] { "DispatchRecords", "NativeOperations", "Runs", "ModelCallCharges",
            "Consumption", "CostPreflights", "ModelCalls", "Sessions" })
        {
            Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty(field), after.RootElement.GetProperty(field)), field);
        }
        using var runtime = await federation.CreateMemberRuntimeAsync("product");
        Assert.Equal("Partner", runtime.Roster.Resolve("researcher")!.MemberName);
        Assert.Equal(MemberRoot("product") + "/original-documents", runtime.Roster.Resolve("technical-writer")!.DeliverableRoot);
        QueueResearch(host);
        var continued = await runtime.RunAsync("Inspect the existing migrated case.");
        Assert.True(continued.Status == SquadRunStatus.ResearchCompleted, continued.ResponseText);
    }

    [Fact]
    public async Task GivenBOMAndEditedRoster_WhenPromoting_ChangesOnlyTheDeliverableColumn()
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        var path = PathFor(host, ".copilot-tracking/squad/team.md");
        var text = File.ReadAllText(path).Replace(".copilot-tracking/researcher/", ".copilot-tracking/custom-facts/", StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n") + "\r\nKeep outside-table .copilot-tracking/notes unchanged.\r\n";
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);
        using var federation = await Create(host);

        var result = await federation.PromoteAsync("product", Candidates(federation));

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var relocated = File.ReadAllBytes(PathFor(host, MemberRoot("product") + "/team.md"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, relocated[..3]);
        var relocatedText = Encoding.UTF8.GetString(relocated[3..]);
        Assert.Contains("Keep outside-table .copilot-tracking/notes unchanged.", relocatedText, StringComparison.Ordinal);
        Assert.Contains(MemberRoot("product") + "/custom-facts/", relocatedText, StringComparison.Ordinal);
        Assert.Contains("\r\n", relocatedText, StringComparison.Ordinal);
        using var runtime = await federation.CreateMemberRuntimeAsync("product");
        Assert.Equal(MemberRoot("product") + "/custom-facts/", runtime.Roster.Resolve("researcher")!.DeliverableRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMissingOrDeniedPromotionApproval_WhenPromoting_PreservesTheEntireOriginalTree(bool denied)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        host.Options.ApproveAsync = denied ? (_, _) => ValueTask.FromResult(false) : null;
        var original = Manifest(host.Project);
        using var federation = await Create(host);

        var result = await federation.PromoteAsync("product", Candidates(federation));

        Assert.Equal(denied ? SquadRunStatus.ApprovalDenied : SquadRunStatus.ApprovalRequired, result.Status);
        AssertManifest(original, host.Project);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("proposal")]
    [InlineData("new-file")]
    [InlineData("artifact")]
    public async Task GivenChangedConfirmationEvidence_WhenPromoting_BlocksWithoutDeletingOrReplacingSources(string change)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.FederationPromotion)
            {
                switch (change)
                {
                    case "source": File.AppendAllText(PathFor(host, ".copilot-tracking/squad/decisions.md"), "HOST_CHANGED"); break;
                    case "proposal": approval.Federation!.Notifications.Email = "changed@example.invalid"; break;
                    case "new-file": File.WriteAllText(PathFor(host, ".copilot-tracking/researcher/new.txt"), "HOST_ADDED"); break;
                    case "artifact": host.Write("instructions/changed.instructions.md", "---\napplyTo: '**'\n---\nChanged release bytes"); break;
                    default: throw new ArgumentOutOfRangeException(nameof(change));
                }
            }
            return ValueTask.FromResult(true);
        };
        using var federation = await Create(host);

        var result = await federation.PromoteAsync("product", Candidates(federation));

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/team.md")));
        Assert.False(Directory.Exists(PathFor(host, MemberRoot("product"))));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/federation.md")));
    }

    [Theory]
    [InlineData("copying")]
    [InlineData("removing")]
    [InlineData("publishing")]
    public async Task GivenInterruptedPromotion_WhenRestarting_CompletesSameVerifiedImageWithoutAnotherModelOrApproval(string phase)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        var originalHistory = Manifest(PathFor(host, ".copilot-tracking/squad/history"));
        using var cancelled = new CancellationTokenSource();
        host.Log.Observer = id =>
        {
            if (id.Name == "FederationLifecyclePhase" && host.Log.Messages[^1].Contains("reached " + phase + ";", StringComparison.Ordinal))
            {
                cancelled.Cancel();
            }
        };
        string operation;
        var calls = host.Client.Calls.Count;
        using (var federation = await Create(host))
        {
            var result = await federation.PromoteAsync("product", Candidates(federation), cancellationToken: cancelled.Token);
            Assert.Equal(SquadRunStatus.Blocked, result.Status);
            Assert.NotNull(result.OperationId);
            operation = result.OperationId;
            Assert.Equal(operation, federation.GetSnapshot().PendingOperationId);
            if (phase != "publishing")
            {
                Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/team.md")));
            }
        }
        host.Log.Observer = null;
        var approvals = host.Approvals.Count;
        using var restarted = await Create(host);

        var resumed = await restarted.ResumeLifecycleAsync(operation);

        Assert.True(resumed.Status == SquadRunStatus.Completed, resumed.Diagnostic);
        Assert.Equal(operation, resumed.OperationId);
        Assert.Equal(approvals, host.Approvals.Count);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Null(restarted.GetSnapshot().PendingOperationId);
        AssertManifest(originalHistory, PathFor(host, MemberRoot("product") + "/history"));
        using var runtime = await restarted.CreateMemberRuntimeAsync("product");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenChangedPreparedOrCopiedBytes_WhenResuming_NeverOverwritesEitherCopy(bool copied)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        using var cancelled = new CancellationTokenSource();
        host.Log.Observer = id =>
        {
            if (id.Name == "FederationLifecyclePhase" &&
                host.Log.Messages[^1].Contains("reached " + (copied ? "removing" : "copying") + ";", StringComparison.Ordinal))
            {
                cancelled.Cancel();
            }
        };
        using var federation = await Create(host);
        var interrupted = await federation.PromoteAsync("product", Candidates(federation), cancellationToken: cancelled.Token);
        Assert.NotNull(interrupted.OperationId);
        host.Log.Observer = null;
        var modified = copied ? PathFor(host, MemberRoot("product") + "/decisions.md") :
            PathFor(host, ".copilot-tracking/squad/migrations/federation/" + interrupted.OperationId +
                "/image-1/" + MemberRoot("product") + "/decisions.md");
        File.AppendAllText(modified, "HOST_CHANGED");

        var result = await federation.ResumeLifecycleAsync(interrupted.OperationId);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("HOST_CHANGED", File.ReadAllText(modified), StringComparison.Ordinal);
        Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/team.md")));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/federation.md")));
    }

    [Fact]
    public async Task GivenHeldRootWriter_WhenPromoting_BlocksWithoutOverwritingTheOriginalSquad()
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        var original = Manifest(host.Project);
        using var writer = new FileStream(PathFor(host, ".copilot-tracking/squad/native.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var federation = await Create(host);

        var result = await federation.PromoteAsync("product", Candidates(federation));

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.False(Directory.Exists(PathFor(host, MemberRoot("product"))));
        writer.Dispose();
        AssertManifest(original, host.Project);
    }

    [Fact]
    public async Task GivenSuspendedNativeRun_WhenPromoting_RequiresOriginalRunReconciliation()
    {
        using var host = new RuntimeTestHost();
        host.Client.Call("ask_host", new() { ["question"] = "Which authorized case fact is required?" });
        using (var runtime = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.InputRequired, (await runtime.RunAsync("Inspect the bounded existing case.")).Status);
        }
        var original = Manifest(host.Project);
        using var federation = await Create(host);

        var result = await federation.PromoteAsync("product", Candidates(federation));

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("quiescent", result.Diagnostic, StringComparison.Ordinal);
        AssertManifest(original, host.Project);
    }

    [Fact]
    public async Task GivenRealRegistryAndStrayRoster_WhenDetecting_FederationWinsAndSingleRuntimeDefers()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var init = await federation.InitializeAsync([Member("product")]);
        Assert.True(init.Status == SquadRunStatus.Completed, init.Diagnostic);
        File.WriteAllText(PathFor(host, ".copilot-tracking/squad/team.md"), "HOST_STRAY_ROSTER");
        var snapshot = federation.GetSnapshot();
        Assert.Equal(SquadTopology.Federation, snapshot.Topology);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Contains("stray", StringComparison.Ordinal));
        using var single = await host.CreateAsync();

        var result = await single.RunAsync("Inspect the existing federation.");

        Assert.Equal(SquadRunStatus.Unsupported, result.Status);
        Assert.Contains("SquadFederation", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal("HOST_STRAY_ROSTER", File.ReadAllText(PathFor(host, ".copilot-tracking/squad/team.md")));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenHandEditedRoot_WhenDispatchingAndRefreshing_UsesActualCellWithoutReseedOrRepair()
    {
        using var host = new RuntimeTestHost();
        QueueResearch(host);
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("Inspect the existing synthetic case.");
        Assert.True(first.Status == SquadRunStatus.ResearchCompleted, first.ResponseText);
        var team = PathFor(host, ".copilot-tracking/squad/team.md");
        File.WriteAllText(team, File.ReadAllText(team).Replace(".copilot-tracking/researcher/",
            ".copilot-tracking/custom-facts/", StringComparison.Ordinal));
        QueueResearch(host);

        var second = await runtime.RunAsync("Inspect the same authorized case.");

        Assert.True(second.Status == SquadRunStatus.ResearchCompleted, second.ResponseText);
        Assert.StartsWith(".copilot-tracking/custom-facts/", Assert.Single(second.Evidence).ArtifactPath, StringComparison.Ordinal);
        var refreshed = await runtime.UpdateRosterAsync(runtime.RosterConfiguration);
        Assert.True(refreshed.Status == SquadRunStatus.Completed, refreshed.Diagnostic);
        Assert.Contains(".copilot-tracking/custom-facts/", File.ReadAllText(team), StringComparison.Ordinal);
        Assert.DoesNotContain("drift", second.ResponseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivenMemberRosterRefresh_WhenReopening_PreservesConfirmedRootOverrides()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var initialized = await federation.InitializeAsync([Member("product")]);
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        using (var runtime = await federation.CreateMemberRuntimeAsync("product"))
        {
            var configuration = runtime.RosterConfiguration;
            configuration.DeliverableRoots["researcher"] = MemberRoot("product") + "/confirmed-facts";
            var refreshed = await runtime.UpdateRosterAsync(configuration);
            Assert.True(refreshed.Status == SquadRunStatus.Completed, refreshed.Diagnostic);
        }

        using var reopened = await federation.CreateMemberRuntimeAsync("product");

        Assert.Equal(MemberRoot("product") + "/confirmed-facts", reopened.Roster.Resolve("researcher")!.DeliverableRoot);
        QueueResearch(host);
        var result = await reopened.RunAsync("Inspect the existing authorized member case.");
        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.StartsWith(MemberRoot("product") + "/confirmed-facts/", Assert.Single(result.Evidence).ArtifactPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenSharedRepositoryOutput_WhenAnotherMemberWrites_BlocksBeforeActualFunction()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        var writes = 0;
        host.Options.Tools =
        [
            new(AIFunctionFactory.Create(() =>
            {
                writes++;
                Directory.CreateDirectory(PathFor(host, "docs"));
                File.WriteAllText(PathFor(host, "docs/shared.txt"), "FIRST_OWNER_BYTES");
                return "Actually written.";
            }, "apply_change"), "edit/project", ["developer"], SquadToolEffect.ProjectWrite,
                _ => new(["docs/shared.txt"]), ProjectWritePaths: _ => ["docs/shared.txt"]),
        ];
        using var federation = await Create(host);
        var init = await federation.InitializeAsync([Member("product"), Member("platform")]);
        Assert.True(init.Status == SquadRunStatus.Completed, init.Diagnostic);
        host.QueueSuccess();
        using (var first = await federation.CreateMemberRuntimeAsync("product"))
        {
            var result = await first.RunAsync("Implement the bounded local change.");
            Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        }
        Assert.Equal(1, writes);
        host.QueueSuccess();
        using var second = await federation.CreateMemberRuntimeAsync("platform");

        var refused = await second.RunAsync("Implement the bounded local change.");

        Assert.NotEqual(SquadRunStatus.Completed, refused.Status);
        Assert.Contains("another member", refused.ResponseText, StringComparison.Ordinal);
        Assert.Equal(1, writes);
        Assert.Equal("FIRST_OWNER_BYTES", File.ReadAllText(PathFor(host, "docs/shared.txt")));
    }

    [Fact]
    public async Task GivenRegisteredFederation_WhenReadingMetadata_ChangesNoStateOrConsumptionChecksums()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var result = await federation.InitializeAsync([Member("product"), Member("platform")]);
        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        var original = Manifest(host.Project);

        _ = federation.GetSnapshot();
        _ = federation.GetPromotionCandidates();

        AssertManifest(original, host.Project);
    }

    [Fact]
    public async Task GivenDIHost_WhenCreatingFederation_ReusesExistingClientAndExplicitInitializationAuthority()
    {
        using var host = new RuntimeTestHost();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(host.Client);
        services.AddHveSquad(configureHost: options =>
        {
            options.ProjectPath = host.Project;
            options.ApproveAsync = host.Options.ApproveAsync;
        }, source: host.Source());
        using var provider = services.BuildServiceProvider();
        using var federation = await provider.GetRequiredService<SquadRuntimeFactory>().CreateFederationAsync();

        var result = await federation.InitializeAsync([Member("product")]);

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Single(host.Approvals);
        Assert.Empty(host.Client.Calls);
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenRealSecondDraftHistory_WhenPromoting_PreservesRevisionArchivesAndImmutableModeReceipts(string mode)
    {
        using var host = new RuntimeTestHost();
        RuntimeModeTests.Mode(host, mode);
        var writes = 0;
        host.Options.Tools[0] = host.Options.Tools[0] with
        {
            Function = AIFunctionFactory.Create(() =>
            {
                File.WriteAllText(Path.Combine(host.Project, "implementation.txt"),
                    "actual revision " + (++writes).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return "Actually applied.";
            }, "apply_change"),
        };
        RuntimeModeTests.FirstDraft(host, "Conditional", condition: true);
        RuntimeModeTests.Council(host, verdict: "Conditional", condition: true);
        host.QueueStage("developer");
        RuntimeModeTests.Council(host, resolve: true);
        host.QueueStage("tester");
        host.Client.Text("Two bounded independently validated drafts.");
        using (var runtime = await host.CreateAsync())
        {
            var result = await runtime.RunAsync("Implement the bounded local code.");
            Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        }
        var history = Manifest(PathFor(host, ".copilot-tracking/squad/history"));
        var original = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, ".copilot-tracking/squad/state.json")))!;
        var snapshot = Assert.Single(original.OutputRevisions!);
        using var federation = await Create(host);

        var promoted = await federation.PromoteAsync("product", Candidates(federation));

        Assert.True(promoted.Status == SquadRunStatus.Completed, promoted.Diagnostic);
        AssertManifest(history, PathFor(host, MemberRoot("product") + "/history"));
        Assert.Equal("actual revision 1", File.ReadAllText(PathFor(host,
            MemberRoot("product") + snapshot.ArchivePath[".copilot-tracking/squad".Length..])));
        Assert.Equal(2, writes);
        using var reopened = await federation.CreateMemberRuntimeAsync("product");
        var evidence = await reopened.ReconcileEvidenceAsync();
        Assert.True(evidence.HistoryVerified);
    }

    [Fact]
    public async Task GivenRemovedSharedOutputIndex_WhenReading_RejectsItAgainstTheImmutableNativeReceipt()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var initialized = await federation.InitializeAsync([Member("product"), Member("platform")]);
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        FederationPersistence.ClaimOutputs(new(host.Project, host.Options.MaxArtifactCharacters, MemberRoot("product")),
            host.Options, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), ["docs/shared.txt"], federation.ArtifactFingerprint);
        var path = PathFor(host, ".copilot-tracking/squad/state.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        state["Federation"]!["OutputOwners"]!.AsObject().Clear();
        File.WriteAllText(path, state.ToJsonString());

        Assert.Throws<RuntimeHistoryIntegrityException>(() => federation.GetSnapshot());
        Assert.False(File.Exists(PathFor(host, "docs/shared.txt")));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("source")]
    public async Task GivenUnconfirmedFileAfterPreparation_WhenResuming_NeverMergesOrDeletesIt(string location)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        using var cancellation = new CancellationTokenSource();
        host.Log.Observer = id =>
        {
            if (id.Name == "FederationLifecyclePhase" && host.Log.Messages[^1].Contains("reached removing;", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };
        using var federation = await Create(host);
        var pending = await federation.PromoteAsync("product", Candidates(federation), cancellationToken: cancellation.Token);
        Assert.Equal(SquadRunStatus.Blocked, pending.Status);
        host.Log.Observer = null;
        var path = PathFor(host, (location == "target" ? MemberRoot("product") : ".copilot-tracking/researcher") + "/unconfirmed.txt");
        File.WriteAllText(path, "UNCONFIRMED_BYTES");

        var result = await federation.ResumeLifecycleAsync(pending.OperationId!);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal("UNCONFIRMED_BYTES", File.ReadAllText(path));
        Assert.True(File.Exists(PathFor(host, ".copilot-tracking/squad/team.md")));
        Assert.False(File.Exists(PathFor(host, ".copilot-tracking/squad/federation.md")));
    }

    [Fact]
    public async Task GivenWrongResumeOrAnotherLifecycle_WhenPending_LeavesTheExactApprovedOperationIntact()
    {
        using var host = new RuntimeTestHost();
        using var cancelled = new CancellationTokenSource();
        host.Log.Observer = id =>
        {
            if (id.Name == "FederationLifecyclePhase")
            {
                cancelled.Cancel();
            }
        };
        using var federation = await Create(host);
        var pending = await federation.InitializeAsync([Member("product")], cancelled.Token);
        Assert.Equal(SquadRunStatus.Blocked, pending.Status);
        host.Log.Observer = null;
        var original = Manifest(host.Project);

        var wrong = await federation.ResumeLifecycleAsync(Guid.NewGuid().ToString("N"));
        var another = await federation.InitializeAsync([Member("platform")]);

        Assert.Equal(SquadRunStatus.Blocked, wrong.Status);
        Assert.Equal(SquadRunStatus.Blocked, another.Status);
        Assert.Equal(pending.OperationId, federation.GetSnapshot().PendingOperationId);
        AssertManifest(original, host.Project);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenUnregisteredMember_WhenOpeningDirectly_CannotCreateAHiddenSquadInsideTheFederation()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var initialized = await federation.InitializeAsync([Member("product")]);
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        host.Options.SquadRoot = MemberRoot("unregistered");

        await Assert.ThrowsAsync<InvalidDataException>(() => host.CreateAsync());

        Assert.False(Directory.Exists(PathFor(host, MemberRoot("unregistered"))));
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenEditedRoot_WhenUpdatingModelPolicy_DoesNotRestoreTheCatalogDefault()
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        using var runtime = await host.CreateAsync();
        var team = PathFor(host, ".copilot-tracking/squad/team.md");
        File.WriteAllText(team, File.ReadAllText(team).Replace(".copilot-tracking/researcher/",
            ".copilot-tracking/edited-root/", StringComparison.Ordinal));

        var result = await runtime.UpdateModelRoutingAsync(new() { Mode = "off" });

        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Contains(".copilot-tracking/edited-root/", File.ReadAllText(team), StringComparison.Ordinal);
        Assert.Equal(".copilot-tracking/edited-root/", runtime.RosterConfiguration.DeliverableRoots["researcher"]);
    }

    [Fact]
    public async Task GivenExactDirectoryLimit_WhenEnumerating_Accepts4096EntriesAndRejectsTheNext()
    {
        using var host = new RuntimeTestHost();
        var directory = PathFor(host, ".copilot-tracking/bounded-source");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 4095; index++)
        {
            File.WriteAllText(Path.Combine(directory, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "");
        }
        using var federation = await Create(host);
        Assert.Equal(4095, Assert.Single(federation.GetPromotionCandidates()).Files);
        File.WriteAllText(Path.Combine(directory, "4095"), "");

        Assert.Throws<InvalidDataException>(() => federation.GetPromotionCandidates());
        Assert.False(Directory.Exists(PathFor(host, ".copilot-tracking/squad")));
    }

    [Fact]
    public async Task GivenAuthenticatedHistoricalAnswer_WhenPromoting_PreservesItsOriginalPayloadAndReceipt()
    {
        using var host = new RuntimeTestHost();
        using var writer = RuntimeModeTests.Durable(host);
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        using (var runtime = await host.CreateAsync())
        {
            var routed = await runtime.RunAsync("Inspect the existing authorized case.");
            Assert.Equal(SquadRunStatus.ApprovalRequired, routed.Status);
            _ = await RuntimeModeTests.RespondAsync(runtime, "routing");
            host.Client.Call("ask_host", new() { ["question"] = "Which original case fact should ground the research?" });
            var question = await runtime.ResumeAsync(routed.RunId);
            Assert.Equal(SquadRunStatus.InputRequired, question.Status);
            _ = await RuntimeModeTests.RespondAsync(runtime, "question", "/answer: ORIGINAL_AUTHENTICATED_FACT");
            host.Client.Call("ask_host", new() { ["question"] = "Which original case fact should ground the research?" });
            host.QueueStage("researcher");
            host.Client.Text("Authorized research completed.");
            var completed = await runtime.ResumeAsync(routed.RunId);
            Assert.True(completed.Status == SquadRunStatus.ResearchCompleted, completed.ResponseText);
        }
        var original = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, ".copilot-tracking/squad/state.json")))!;
        Assert.Single(original.ConfirmedAnswers);
        var payloads = Manifest(PathFor(host, ".copilot-tracking/squad/payloads"));
        using var federation = await Create(host);

        var promoted = await federation.PromoteAsync("product", Candidates(federation));

        Assert.True(promoted.Status == SquadRunStatus.Completed, promoted.Diagnostic);
        AssertManifest(payloads, PathFor(host, MemberRoot("product") + "/payloads"));
        using var member = await federation.CreateMemberRuntimeAsync("product");
        var verified = await member.ReconcileEvidenceAsync();
        Assert.True(verified.HistoryVerified);
        var after = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, MemberRoot("product") + "/state.json")))!;
        Assert.Equal(HumanActions.Serialize(original.HumanDecisions), HumanActions.Serialize(after.HumanDecisions));
        Assert.Equal(original.ConfirmedAnswers, after.ConfirmedAnswers);
    }

    [Fact]
    public async Task GivenUnprojectedMemberWriter_WhenInvoked_DeniesBeforeAnySharedFileChanges()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.Options.Tools[0] = host.Options.Tools[0] with { ProjectWritePaths = null };
        using var federation = await Create(host);
        var initialized = await federation.InitializeAsync([Member("product")]);
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        host.QueueSuccess();
        using var member = await federation.CreateMemberRuntimeAsync("product");

        var result = await member.RunAsync("Implement the bounded local code.");

        Assert.NotEqual(SquadRunStatus.Completed, result.Status);
        Assert.Contains("Member project writes require", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
        Assert.False(File.Exists(PathFor(host, "implementation.txt")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("archive")]
    public async Task GivenRedirectedHistoricalMap_WhenReopening_RejectsEvenARecomputedLocalSeal(string field)
    {
        using var host = new RuntimeTestHost();
        await SeedSingle(host);
        using var federation = await Create(host);
        var promoted = await federation.PromoteAsync("product", Candidates(federation));
        Assert.True(promoted.Status == SquadRunStatus.Completed, promoted.Diagnostic);
        var path = PathFor(host, MemberRoot("product") + "/state.json");
        var state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path))!;
        var relocation = state.Relocation!;
        relocation = field switch
        {
            "source" => relocation with { Files = [relocation.Files[0] with { Source = "../outside" }, .. relocation.Files.Skip(1)] },
            "destination" => relocation with { Files = [relocation.Files[0] with { Destination = MemberRoot("product") + "/different.bin" }, .. relocation.Files.Skip(1)] },
            "archive" => relocation with { OriginalStatePath = MemberRoot("platform") + "/private-state.json" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        state.Relocation = relocation with { Seal = HumanActions.Hash(relocation with { Seal = null }) };
        File.WriteAllText(path, JsonSerializer.Serialize(state));

        if (field == "source")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => federation.CreateMemberRuntimeAsync("product"));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => federation.CreateMemberRuntimeAsync("product"));
        }
    }

    [Fact]
    public async Task GivenFileCollisionAtMemberName_WhenInitializing_DeniesBeforeApproval()
    {
        using var host = new RuntimeTestHost();
        Directory.CreateDirectory(PathFor(host, ".copilot-tracking/squad/members"));
        File.WriteAllText(PathFor(host, MemberRoot("product")), "KEEP_EXACT_FILE");
        using var federation = await Create(host);

        var result = await federation.InitializeAsync([Member("product")]);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal("KEEP_EXACT_FILE", File.ReadAllText(PathFor(host, MemberRoot("product"))));
        Assert.Empty(host.Approvals);
    }

    [Fact]
    public async Task Given64RegisteredMembers_WhenExpandingPastTheBound_PreservesTheExistingRegistry()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);
        var initialized = await federation.InitializeAsync(Enumerable.Range(0, 64)
            .Select(index => Member("member-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        Assert.True(initialized.Status == SquadRunStatus.Completed, initialized.Diagnostic);
        Assert.Equal(64, federation.GetSnapshot().Members.Count);
        var original = Manifest(host.Project);

        var result = await federation.InitializeAsync([Member("over-limit")]);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("64", result.Diagnostic, StringComparison.Ordinal);
        AssertManifest(original, host.Project);
        Assert.Single(host.Approvals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task GivenInvalidMemberSetSize_WhenInitializing_RejectsWithoutState(int size)
    {
        using var host = new RuntimeTestHost();
        using var federation = await Create(host);

        await Assert.ThrowsAsync<ArgumentException>(() => federation.InitializeAsync(Enumerable.Range(0, size)
            .Select(index => Member("member-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Theory]
    [Trait("Category", "ReleaseCompatibility")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenActualReleasedArtifacts_WhenInitializingOrPromoting_ExecutesTheRealMemberEngine(bool promote)
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        using var releaseClient = ReleaseTestHttpClient.Create(
            Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        var source = ReleaseTestHttpClient.Source(version, releaseClient);
        using var host = new RuntimeTestHost();
        host.Options.NamingPolicy = "assigned";
        if (promote)
        {
            QueueResearch(host);
            using var single = await SquadRuntime.CreateAsync(host.Client, host.Options, source);
            var researched = await single.RunAsync("Inspect the bounded existing synthetic implementation.");
            Assert.True(researched.Status == SquadRunStatus.ResearchCompleted, researched.ResponseText);
        }
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, source);
        var result = promote
            ? await federation.PromoteAsync("product", Candidates(federation))
            : await federation.InitializeAsync([Member("product")]);
        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        Assert.Equal(version, federation.Provenance.Release!.Tag);
        var expanded = await federation.InitializeAsync([Member("platform")]);
        Assert.True(expanded.Status == SquadRunStatus.Completed, expanded.Diagnostic);
        QueueResearch(host);
        using var member = await federation.CreateMemberRuntimeAsync("product");

        var actual = await member.RunAsync("Inspect the bounded existing synthetic implementation.");

        Assert.True(actual.Status == SquadRunStatus.ResearchCompleted, actual.ResponseText);
        Assert.All(actual.Evidence, evidence =>
        {
            Assert.StartsWith(MemberRoot("product") + "/", evidence.ArtifactPath, StringComparison.Ordinal);
            Assert.StartsWith(MemberRoot("product") + "/", evidence.HistoryPath, StringComparison.Ordinal);
        });
        Assert.Equal("Alpha", member.Roster.Resolve("researcher")!.MemberName);
    }
}
