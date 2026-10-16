using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HveSquad.AgentFramework.Agents;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeLearningTests
{
    private const string FakeCredential = "fictional-sample-credential";
    private const string SourceRepository = "peter-n91/hve-squad";
    private const string ForkRepository = "learning-consumer/hve-squad";
    private const string TenantRepository = "fixture-tenant/reviewed-learnings";
    private static readonly SquadLearningInvocation Human = new("human:event-01");
    private static readonly SquadLearningTarget Upstream = new("upstream", SourceRepository, ForkRepository);
    private static readonly SquadLearningTarget Tenant = new("tenant", TenantRepository);
    private static readonly string BaseCommit = new('a', 40);
    private static readonly string WrittenCommit = new('c', 40);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string PathFor(RuntimeTestHost host, string relative) =>
        Path.Combine(host.Project, relative.Replace('/', Path.DirectorySeparatorChar));
    private static Dictionary<string, string> Manifest(RuntimeTestHost host) =>
        Directory.Exists(PathFor(host, ".copilot-tracking/squad"))
            ? Directory.EnumerateFiles(PathFor(host, ".copilot-tracking/squad"), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(host.Project, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal)
            : [];
    private static void Unchanged(RuntimeTestHost host, Dictionary<string, string> before)
    {
        var after = Manifest(host);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }
    private static RuntimeState State(RuntimeTestHost host, string? root = null) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, (root ?? host.Options.SquadRoot) + "/state.json")))!;
    private static SquadLearningTargetSnapshot Snapshot(SquadLearningTarget target, string? content = null) => new(
        target.Repository, LearningContracts.TargetPath(target), "main", BaseCommit, new('b', 40),
        content ?? Playbook(LearningContracts.Prefix(target)));
    private static string Playbook(string prefix, string newline = "\n") => """
        ---
        title: Original synthetic playbook
        license: MIT
        ---
        Synthetic license notice: preserve this original notice.

        ## Learnings

        | id | Scenario / Trigger | Learning / Rule | Scope / Applicability | Source Context | Date Added |
        | --- | --- | --- | --- | --- | --- |
        """.ReplaceLineEndings(newline) + newline +
        $"| {prefix}001 | An unrelated first scenario | Verify an independent boundary before delivery | All workflows | Original synthetic public evidence | 2026-10-01 |" + newline +
        $"| {prefix}003 | An unrelated third scenario | Preserve original byte hashes during relocation | All workflows | Original synthetic public evidence | 2026-10-02 |" + newline +
        newline + "## Original footer" + newline + "Do not remove this notice." + newline;

    [Fact]
    public async Task GivenAuthorizedRole_WhenRememberingAndRestarting_UsesOnlyScribeMemoryAndPreservesOriginalPrefix()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var calls = fixture.Host.Client.Calls.Count;
        var memory = fixture.Memory();
        var first = await memory.RememberAsync(fixture.Note);
        var path = PathFor(fixture.Host, first.Path);
        var original = await File.ReadAllBytesAsync(path);

        var second = await fixture.Memory().RememberAsync(fixture.Note with { Rule = "Check the independent cache receipt before reuse." });
        var restored = await fixture.Memory().ReadAsync("researcher");

        Assert.Equal([first, second], restored);
        Assert.Equal(original, (await File.ReadAllBytesAsync(path))[..original.Length]);
        Assert.Equal(calls, fixture.Host.Client.Calls.Count);
        Assert.All(State(fixture.Host).NativeOperations.Where(operation => operation.RoleMemory is not null),
            operation => Assert.Equal(0, operation.ModelInvocations));
        Assert.Contains(FakeCredential, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenNoCaseAuthorityAndCorruptState_WhenReadingOrLearning_DeniesBeforeAnyScopedRead()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/state.json"), "{invalid}");
        fixture.AllowMemory = false;
        fixture.AllowHuman = false;
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Memory().ReadAsync("researcher"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Learning().DiscoverAsync(Human));

        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenUninitializedAuthorizedCase_WhenRemembering_RefusesWithoutCreatingStateOrLocks()
    {
        using var fixture = new Fixture();
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Memory().RememberAsync(fixture.Note));

        Unchanged(fixture.Host, before);
        Assert.False(Directory.Exists(PathFor(fixture.Host, ".copilot-tracking/squad")));
    }

    [Theory]
    [InlineData("missing-role")]
    [InlineData("other/role")]
    [InlineData("../researcher")]
    [InlineData("Researcher")]
    public async Task GivenInvalidRole_WhenRemembering_RefusesAndLeavesEveryByteUnchanged(string role)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Memory().RememberAsync(fixture.Note with { Role = role }));

        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("password=fictional-value")]
    [InlineData("Authorization: Bearer fictional-value")]
    [InlineData("https://service.invalid/?sig=fictional-value")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nfictional\n-----END PRIVATE KEY-----")]
    public async Task GivenCredentialTransportInNote_WhenRemembering_RefusesRatherThanPersistingIt(string text)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Memory().RememberAsync(fixture.Note with { Rule = text }));

        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenRoleMemoryLimit_WhenAppendingAgain_RetainsEveryOriginalNoteWithoutEviction()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.MemoryConfiguration.MaximumEntriesPerRole = 1;
        _ = await fixture.Memory().RememberAsync(fixture.Note);
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Memory().RememberAsync(fixture.Note with
        {
            Rule = "Preserve an independent replacement proof before reuse.",
        }));

        Unchanged(fixture.Host, before);
        Assert.Single(await fixture.Memory().ReadAsync("researcher"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GivenExactMemoryCharacterBoundary_WhenRemembering_AllowsEqualityAndRefusesOneCharacterOver(int additional)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.MemoryConfiguration.MaximumNoteCharacters = 100;
        var note = new SquadMemoryNote("researcher", "When cache reuse recurs", "",
            "All consumers", "A reviewed local convention");
        note = note with { Rule = new('x', 100 - note.Scenario.Length - note.Scope.Length - note.SourceContext.Length + additional) };
        var before = Manifest(fixture.Host);

        if (additional == 0)
        {
            Assert.Equal(note, (await fixture.Memory().RememberAsync(note)).Note);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Memory().RememberAsync(note));
            Unchanged(fixture.Host, before);
        }
    }

    [Fact]
    public async Task GivenDifferentPolicyBinding_WhenReading_RefusesWithoutRepairOrRebinding()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        fixture.MemoryConfiguration.Binding = "different-case-policy";
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Memory().ReadAsync("researcher"));

        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("state-index")]
    [InlineData("unbound-file")]
    public async Task GivenTamperedMemory_WhenReadingOrDiscovering_RejectsOriginalEvidenceWithoutRepair(string defect)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var path = PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md");
        if (defect == "bytes")
        {
            await File.AppendAllTextAsync(path, "unbound extra bytes");
        }
        else if (defect == "state-index")
        {
            var statePath = PathFor(fixture.Host, ".copilot-tracking/squad/state.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
            json["RoleMemories"] = new JsonArray();
            await File.WriteAllTextAsync(statePath, json.ToJsonString(IndentedJson));
        }
        else
        {
            await File.WriteAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-tester.md"), "unbound");
        }
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => fixture.Memory().ReadAsync("researcher"));
        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => fixture.Learning().DiscoverAsync(Human));

        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenOriginalPendingMemoryAppend_WhenRestarting_ReaderRefusesAndScribeFinishesExactBytesOnce()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var files = new ProjectFiles(fixture.Host.Project, 200_000);
        var scribe = new RuntimeScribe(files);
        var state = scribe.Load()!;
        var path = ".copilot-tracking/squad/memory/squad-researcher.md";
        var original = files.ReadBytes(path);
        var pending = new SquadRoleMemoryEntry(Guid.NewGuid().ToString("N"), path,
            HumanActions.Hash(fixture.MemoryConfiguration.Binding),
            fixture.Note with { Rule = "Preserve a recovered original convention before new work." }, fixture.Clock.GetUtcNow());
        var content = $"\n## Memory {pending.Id}\n\n```json\n{JsonSerializer.Serialize(pending, IndentedJson)}\n```\n";
        state.RoleMemories!.Add(pending);
        state.PendingHistoryAppend = new(path, new(original.Length, Convert.ToHexString(SHA256.HashData(original))),
            content, Convert.ToHexString(SHA256.HashData([.. original, .. Encoding.UTF8.GetBytes(content)])));
        scribe.Save(state);
        files.AppendBytes(path, Encoding.UTF8.GetBytes(content).AsSpan(0, 17));
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => fixture.Memory().ReadAsync("researcher"));
        Unchanged(fixture.Host, before);
        _ = await fixture.Memory().RememberAsync(fixture.Note with { Rule = "Check the new convention only after the recovered receipt." });
        var restored = await fixture.Memory().ReadAsync("researcher");

        Assert.Equal(3, restored.Count);
        Assert.Equal(pending, restored[1]);
        Assert.Equal(original, files.ReadBytes(path)[..original.Length]);
        Assert.Single(State(fixture.Host).NativeOperations, operation => operation.RoleMemory?.Id == pending.Id);
    }

    [Fact]
    public async Task GivenCodeOnlyMemoryOptIn_WhenRunningRealMaf_OnlyTheOwningRoleReceivesUntrustedMemoryData()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        _ = await fixture.Memory().RememberAsync(fixture.Note with { Rule = "Check CACHE_CONTEXT_MARKER before reusing inputs." });
        _ = await fixture.Memory().RememberAsync(fixture.Note with { Role = "tester", Rule = "Keep TESTER_CONTEXT_MARKER inside this role." });
        var memoryBytes = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));
        fixture.Host.Options.Memory = fixture.MemoryConfiguration;
        ConsumptionTestData.QueueResearch(fixture.Host);
        using var runtime = await fixture.Host.CreateAsync();
        fixture.MemoryConfiguration.SecretValues.Add("CACHE_CONTEXT_MARKER");
        fixture.MemoryConfiguration.MaximumEntriesPerRole = 1;

        var result = await runtime.RunAsync("Research the unchanged synthetic cache convention.");

        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        var roleCalls = fixture.Host.Client.Calls.Where(call => call.Options?.Instructions?.StartsWith("Synthetic agent for researcher", StringComparison.Ordinal) == true);
        Assert.Contains(roleCalls, call => call.Messages.Any(message => message.Text.Contains("CACHE_CONTEXT_MARKER", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.Host.Client.Calls,
            call => call.Messages.Any(message => message.Text.Contains("TESTER_CONTEXT_MARKER", StringComparison.Ordinal)));
        Assert.Equal(memoryBytes, await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md")));
    }

    [Fact]
    public async Task GivenMemoryDisclosureRevoked_WhenRunningMaf_BlocksBeforeExposingTheNote()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        fixture.Host.Options.Memory = fixture.MemoryConfiguration;
        fixture.AllowMemory = false;
        fixture.Host.Client.Text("No provider note disclosure is allowed.");
        using var runtime = await fixture.Host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded convention.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("Consumer-local memory requires", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Client.Calls, call =>
            call.Messages.Any(message => message.Text.Contains(FakeCredential, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("model:event")]
    [InlineData("child:event")]
    [InlineData("coordinator:event")]
    public async Task GivenHumanOwnedSessionButNonHumanInitiation_WhenLearning_DeniesBeforeStateReads(string reference)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/state.json"), "{invalid}");
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Learning().DiscoverAsync(new(reference)));

        Unchanged(fixture.Host, before);
        Assert.Empty(fixture.Confirmations);
    }

    [Fact]
    public async Task GivenOrdinaryAuthenticatedChannelWithoutLearningOptIn_WhenInvoked_DoesNotInheritReportOrCallbackPermission()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var channel = new InChatApprovalChannel("ordinary", _ => ValueTask.FromResult(fixture.Principal),
            (_, _, _) => ValueTask.FromResult(true), TextWriter.Null);
        fixture.Configuration.AuthorizeInvocationAsync = channel.AuthorizeLearningInvocationAsync;
        fixture.Configuration.HumanApprovalChannel = channel;
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Learning().DiscoverAsync(Human));

        Unchanged(fixture.Host, before);
    }

    [Fact]
    public void GivenReleasedHumanOnlyLearnCharter_WhenUsingRawFactory_CannotMaterializeOrEnumerateAModelAgent()
    {
        using var host = new RuntimeTestHost();
        host.Write("agents/squad-learn.agent.md",
            "---\nname: Squad Learn\nuser-invocable: true\ndisable-model-invocation: true\n---\nSynthetic human-only helper.");
        var artifacts = SquadArtifactLoader.Load(host.Artifacts);
        var factory = new SquadAgentFactory(host.Client);

        Assert.Throws<InvalidOperationException>(() => factory.Create(artifacts.FindCharter("Squad Learn")!));
        var agents = factory.CreateAll(artifacts);

        Assert.DoesNotContain("Squad Learn", agents.Keys);
        Assert.Contains("Squad Coordinator", agents.Keys);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenNamedPrivateMemory_WhenDiscovering_SanitizesAllDraftFieldsAndPreservesMemoryAndStateChecksums()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var before = Manifest(fixture.Host);
        var calls = fixture.Host.Client.Calls.Count;

        var discovery = await fixture.Learning().DiscoverAsync(Human, "cache");

        var candidate = Assert.Single(discovery.Candidates);
        var draft = JsonSerializer.Serialize(candidate.Draft);
        Assert.DoesNotContain(FakeCredential, draft, StringComparison.Ordinal);
        Assert.DoesNotContain("Fictional customer", draft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fictional Person", draft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProjectCorp", draft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\case", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("intranet.invalid", draft, StringComparison.Ordinal);
        Assert.Contains("[credential]", draft, StringComparison.Ordinal);
        Assert.Contains("[name]", draft, StringComparison.Ordinal);
        Assert.Contains("[path]", draft, StringComparison.Ordinal);
        Assert.All(candidate.Sources, source => Assert.True(HumanActions.IsHash(source.Sha256)));
        Assert.Equal(calls, fixture.Host.Client.Calls.Count);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenOriginalMarkedMafHistoryAndScribeDecision_WhenDiscovering_UsesOnlyVerifiedApprovedStateSources()
    {
        using var fixture = new Fixture();
        fixture.Host.UseCurrentPlanContract();
        fixture.Host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        fixture.Host.Client.Call("write_artifact", new() { ["content"] = "Original independently produced research evidence." });
        fixture.Host.Client.Call("complete_stage", new()
        {
            ["summary"] = "Convention: Preserve independent cache evidence before any repeated source reuse.",
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
        });
        fixture.Host.Client.Text("Original researcher completed.");
        fixture.Host.Client.Text("Original coordinator completed.");
        using (var runtime = await fixture.Host.CreateAsync())
        {
            var result = await runtime.RunAsync("Inspect the original bounded implementation.");
            Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        }
        var files = new ProjectFiles(fixture.Host.Project, 200_000);
        var scribe = new RuntimeScribe(files);
        using (scribe.AcquireLock())
        {
            scribe.Decision(scribe.Load()!, "Correction: Require an independent source receipt before trusting a repeated workflow outcome.");
        }
        var before = Manifest(fixture.Host);

        var discovered = await fixture.Learning().DiscoverAsync(Human);

        Assert.Equal(2, discovered.Candidates.Count);
        Assert.Contains(discovered.Candidates, candidate => candidate.Sources.Any(source => source.Kind == "history"));
        Assert.Contains(discovered.Candidates, candidate => candidate.Sources.Any(source => source.Kind == "decisions"));
        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("https://intranet.invalid/secret", "[url]")]
    [InlineData("ftp://intranet.invalid/secret", "[url]")]
    [InlineData("/home/customer/private.txt", "[path]")]
    [InlineData(@"\\internal-server\share\private.txt", "[path]")]
    [InlineData("Élodie", "[name]")]
    [InlineData("AcmeCorp", "[name]")]
    [InlineData("fictional.person@example.invalid", "[contact]")]
    public void GivenSensitiveMarker_WhenSanitizing_GeneralizesItWithoutANameRecognizerService(string marker, string expected)
    {
        var draft = new SquadLearningDraft("When a cache correction recurs",
            "Preserve independent cache invalidation proof before reuse for " + marker,
            "All consumers with the same scenario", "A reviewed local convention");

        var clean = LearningContracts.Sanitize(draft, [], [], 16_000);

        Assert.NotNull(clean);
        Assert.DoesNotContain(marker, clean.Rule, StringComparison.Ordinal);
        Assert.Contains(expected, clean.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenSourcePermissionWithheld_WhenDiscovering_ReportsTheGapAndReadsNoCandidateContentOutward()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        fixture.AllowSources = false;
        var before = Manifest(fixture.Host);

        var result = await fixture.Learning().DiscoverAsync(Human);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("withheld", StringComparison.Ordinal));
        Assert.Empty(fixture.Confirmations);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenChangedSourceDuringDisclosure_WhenDiscovering_RejectsAnIncoherentSnapshot()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        fixture.Configuration.AuthorizeSourceAsync = (access, _) =>
        {
            if (access.Kind == "role-memory")
            {
                File.AppendAllText(PathFor(fixture.Host, access.Path), "changed while reading");
            }
            return ValueTask.FromResult(true);
        };

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() => fixture.Learning().DiscoverAsync(Human));
    }

    [Theory]
    [InlineData("upstream", "SL-004", LearningContracts.UpstreamPath)]
    [InlineData("tenant", "TL-004", LearningContracts.TenantPath)]
    public async Task GivenActualTargetRows_WhenPreparing_UsesExactSixColumnsAndNextFreeIdentityWithoutWriting(string kind, string id, string path)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var target = kind == "upstream" ? Upstream : Tenant;
        var learning = fixture.Learning();
        var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
        var before = Manifest(fixture.Host);
        var targetSnapshot = Snapshot(target);

        var result = await learning.PrepareAsync(Human, candidate, target, targetSnapshot);

        Assert.Equal("prepared", result.Status);
        Assert.NotNull(result.Prepared);
        Assert.Equal(id, result.Prepared.Entry.Id);
        Assert.Equal(path, result.Prepared.Snapshot.Path);
        Assert.Equal("2026-10-12", result.Prepared.Entry.DateAdded);
        var rows = MarkdownTables.Parse(result.Prepared.Content).Single(table =>
            table.Headers.SequenceEqual(LearningContracts.Columns, StringComparer.OrdinalIgnoreCase)).Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(id, rows[^1]["id"]);
        Assert.Equal(targetSnapshot.Content, result.Prepared.Content.Replace(
            LearningContracts.Row(result.Prepared.Entry) + "\n", "", StringComparison.Ordinal));
        Assert.Contains("Do not remove this notice.", result.Prepared.Content, StringComparison.Ordinal);
        Assert.Contains("+| " + id + " |", result.Prepared.Diff, StringComparison.Ordinal);
        Assert.Empty(result.Receipts);
        Assert.Contains(result.Commands, command => command.StartsWith("git ", StringComparison.Ordinal) && command.Contains(" push ", StringComparison.Ordinal));
        Assert.Equal(["LearningSanitization", "LearningRepository"], fixture.Confirmations.Select(item => item.Action.Binding.Gate));
        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("LearningSanitization")]
    [InlineData("LearningRepository")]
    public async Task GivenPreparationApprovalWithheld_WhenPreparing_StopsBeforeReadingOrMutatingARepository(string gate)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        fixture.RefusedGate = gate;
        var learning = fixture.Learning();
        var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
        var before = Manifest(fixture.Host);

        var result = await learning.PrepareAsync(Human, candidate, Upstream);

        Assert.Equal("stopped", result.Status);
        Assert.Equal(0, connector.Reads);
        Assert.Empty(connector.Executions);
        Assert.Null(result.Prepared);
        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("only-secret")]
    [InlineData("incomplete-key")]
    public async Task GivenUnestablishedSanitization_WhenPreparing_StopsWithoutTargetAccess(string defect)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        fixture.RefusedGate = defect == "unconfirmed" ? "LearningSanitization" : null;
        var learning = fixture.Learning();
        var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
        var draft = defect switch
        {
            "only-secret" => candidate.Draft with { Rule = FakeCredential },
            "incomplete-key" => candidate.Draft with { Rule = "-----BEGIN PRIVATE KEY-----\nfictional incomplete value" },
            _ => candidate.Draft,
        };

        var result = await learning.PrepareAsync(Human, candidate, Upstream, curatedDraft: draft);

        Assert.Equal("stopped", result.Status);
        Assert.Equal(0, connector.Reads);
        Assert.Empty(connector.Executions);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("actor")]
    [InlineData("expiry")]
    [InlineData("same-response")]
    public async Task GivenInvalidActualHumanConfirmation_WhenPreparing_RefusesRatherThanTreatingTextAsConsent(string defect)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        var learning = fixture.Learning();
        var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
        fixture.ResponseTransform = response =>
        {
            if (defect == "binding")
            {
                return response with { Binding = response.Binding with { ActionSha256 = new('D', 64) } };
            }
            if (defect == "actor")
            {
                fixture.Principal = Fixture.Authenticated("different-human");
            }
            if (defect == "expiry")
            {
                fixture.Clock.Utc += TimeSpan.FromDays(2);
            }
            return defect == "same-response" ? response with { ResponseId = "replayed-response" } : response;
        };
        var before = Manifest(fixture.Host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => learning.PrepareAsync(Human, candidate, Upstream));

        Assert.Equal(0, connector.Reads);
        Assert.Empty(connector.Executions);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenNoConnector_WhenPromoting_ReturnsPreparedCommandsAndNeverClaimsPublicationOrWritesState()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var learning = fixture.Learning();
        var prepared = await fixture.PrepareAsync(learning);
        var before = Manifest(fixture.Host);

        var result = await learning.PromoteAsync(Human, prepared);

        Assert.Equal("prepared", result.Status);
        Assert.Empty(result.Receipts);
        Assert.NotEmpty(result.Commands);
        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("fork", 0)]
    [InlineData("branch", 1)]
    [InlineData("write", 2)]
    [InlineData("pull-request", 3)]
    public async Task GivenSpecificMutationApprovalWithheld_WhenPromoting_ExecutesOnlyEarlierSeparatelyApprovedActions(string kind, int count)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        var learning = fixture.Learning();
        var prepared = await fixture.PrepareAsync(learning);
        fixture.RefusedGate = "Learning" + kind;
        var memoryBytes = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));

        var result = await learning.PromoteAsync(Human, prepared);

        Assert.Equal("stopped", result.Status);
        Assert.Equal(count, connector.Executions.Count);
        Assert.Equal(count, result.Receipts.Count);
        Assert.Equal(memoryBytes, await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md")));
    }

    [Fact]
    public async Task GivenRegisteredTenantTarget_WhenPromoting_UsesOnlyBranchWriteAndPrWithoutForking()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector(Tenant);
        var learning = fixture.Learning();
        var prepared = await fixture.PrepareAsync(learning, Tenant);
        var calls = fixture.Host.Client.Calls.Count;
        var memoryBytes = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));

        var result = await learning.PromoteAsync(Human, prepared);
        var repeated = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("pull-request-created", result.Status);
        Assert.Equal(["branch", "write", "pull-request"], connector.Executions.Select(action => action.Kind));
        Assert.Equal(result.Receipts, repeated.Receipts);
        Assert.Equal(3, connector.Executions.Count);
        Assert.Equal(calls, fixture.Host.Client.Calls.Count);
        Assert.Equal(memoryBytes, await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md")));
    }

    [Fact]
    public async Task GivenPreparedHashesForgedWithoutFinalSanitization_WhenPromoting_StillRequiresFreshActualHumanConfirmation()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        var learning = fixture.Learning();
        var prepared = await fixture.PrepareAsync(learning);
        var changed = LearningContracts.Append(prepared.Target, prepared.Snapshot,
            new(prepared.Entry.Scenario, "Require independent resource evidence before approving source changes.",
                prepared.Entry.Scope, prepared.Entry.SourceContext), fixture.Clock.GetUtcNow(), 200_000);
        prepared = prepared with { Entry = changed.Entry, Content = changed.Content, Diff = changed.Diff, Seal = "" };
        prepared = prepared with { Seal = HumanActions.Hash(prepared) };
        fixture.RefusedGate = "LearningSanitization";
        var before = Manifest(fixture.Host);

        var result = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("stopped", result.Status);
        Assert.Empty(connector.Executions);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenActualRemoteEffectThenLostResponse_WhenRestarting_ReconcilesOriginalEffectOnceAndNeverReplays()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        connector.ThrowAfterEffect = "write";
        var prepared = await fixture.PrepareAsync(fixture.Learning());
        var originalMemory = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));
        var blocked = await fixture.Learning().PromoteAsync(Human, prepared);
        Assert.Equal("reconciliation-required", blocked.Status);
        Assert.Equal(3, connector.Executions.Count);
        connector.ThrowAfterEffect = null;
        var reconciled = await fixture.Learning().ReconcileAsync(Human, prepared);
        var completed = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("prepared", reconciled.Status);
        Assert.Equal("pull-request-created", completed.Status);
        Assert.Equal(["fork", "branch", "write", "pull-request"], connector.Executions.Select(action => action.Kind));
        Assert.Equal(originalMemory, await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md")));
        Assert.Single(State(fixture.Host).NativeOperations, operation =>
            operation.LearningRecord is { Status: "applied", Mutation.Kind: "write" });
    }

    [Fact]
    public async Task GivenMissingRemotePostcondition_WhenReconciling_LeavesUncertaintyAndDoesNotReexecute()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        connector.ThrowBeforeEffect = "fork";
        var prepared = await fixture.PrepareAsync(fixture.Learning());

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));
        var before = Manifest(fixture.Host);
        var result = await fixture.Learning().ReconcileAsync(Human, prepared);

        Assert.Equal("reconciliation-required", result.Status);
        Assert.Single(connector.Executions);
        Assert.Empty(result.Receipts);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenChangedSourcePlaybook_WhenPromoting_StopsBeforeAnyRemoteMutationAndRequestsNewIdConfirmation()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        var prepared = await fixture.PrepareAsync(fixture.Learning());
        connector.Snapshot = connector.Snapshot with { CommitSha = new('d', 40) };
        var before = Manifest(fixture.Host);

        var result = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("stopped", result.Status);
        Assert.Empty(connector.Executions);
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenForgedReceiptWithCredential_WhenPromoting_DoesNotPersistItOrClaimPublication()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        var prepared = await fixture.PrepareAsync(fixture.Learning());
        connector.BadReceipt = true;

        var result = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("reconciliation-required", result.Status);
        Assert.Empty(result.Receipts);
        var records = State(fixture.Host).NativeOperations.Where(operation => operation.LearningRecord is not null).ToArray();
        Assert.Equal(2, records.Length);
        Assert.All(records, operation => Assert.Null(operation.LearningRecord!.Receipt));
        var scribeHistory = await File.ReadAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/history/Squad Scribe.md"));
        Assert.DoesNotContain("credential-in-unverified-receipt", scribeHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenActualLearningActions_WhenReporting_CountsRecordedHumanGatesWithoutModelOrSpendInflation()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        _ = fixture.UseConnector();
        var prepared = await fixture.PrepareAsync(fixture.Learning());
        var calls = State(fixture.Host).ModelCalls.Count;
        _ = await fixture.Learning().PromoteAsync(Human, prepared);
        var state = State(fixture.Host);

        var result = await new SquadReports(fixture.Host.Project, new()
        {
            AuthorizeAsync = (_, _) => ValueTask.FromResult(true),
        }, timeProvider: fixture.Clock).GovernanceAsync();

        Assert.Equal(calls, result.Metrics!.ProviderAttempts);
        Assert.Equal(calls, state.ModelCalls.Count);
        Assert.All(state.ModelCalls, call => Assert.Equal(fixture.Clock.GetUtcNow(), call.RecordedAt));
        Assert.All(state.NativeOperations, operation => Assert.Equal(fixture.Clock.GetUtcNow(), operation.RecordedAt));
        Assert.Equal(6, state.NativeOperations.Where(operation => operation.HumanGate?.Mode == "human-only-learning")
            .Select(operation => operation.HumanGate!.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Learning Pull Requests Created", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.Contains("1 independently verified learning PRs", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.All(state.NativeOperations.Where(operation => operation.LearningRecord is not null),
            operation => Assert.Equal(0, operation.ModelInvocations));
    }

    [Theory]
    [InlineData("Memory:Binding")]
    [InlineData("Memory:AuthorizeAsync")]
    [InlineData("Learning:AuthorizeInvocationAsync")]
    [InlineData("Learning:Connector")]
    public void GivenMemoryOrLearningAuthorityInDataConfiguration_WhenBinding_RefusesHostCapabilityInjection(string setting)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [setting] = "not-authority" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHveSquad(configuration));
    }

    [Fact]
    public async Task GivenFactoryWithoutChatClient_WhenCreatingMemoryAndLearning_ReadsNativeEvidenceWithoutResolvingAModel()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var services = new ServiceCollection();
        services.AddHveSquad(configureHost: options => options.ProjectPath = fixture.Host.Project);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<SquadRuntimeFactory>();
        var before = Manifest(fixture.Host);

        Assert.Single(await factory.CreateMemory(fixture.MemoryConfiguration).ReadAsync("researcher"));
        Assert.Single((await factory.CreateLearning(fixture.Configuration).DiscoverAsync(Human)).Candidates);

        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("upstream")]
    [InlineData("tenant")]
    public async Task GivenRealGitHubProtocol_WhenPromoting_ProvesActualAtomicCommitAndPrWithoutSendingPrivateMemory(string kind)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var target = kind == "upstream" ? Upstream : Tenant;
        using var handler = new LearningHttpHandler(target);
        using var client = new HttpClient(handler);
        fixture.Configuration.Connector = new GitHubLearningConnector(client, target,
            (_, _) => ValueTask.FromResult(true), "exact-learning-action-policy");
        var learning = fixture.Learning();
        var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
        var prepared = Assert.IsType<SquadLearningPrepared>((await learning.PrepareAsync(Human, candidate, target)).Prepared);
        var memoryBytes = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));

        var result = await learning.PromoteAsync(Human, prepared);
        var repeated = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("pull-request-created", result.Status);
        Assert.Equal(result.Receipts, repeated.Receipts);
        Assert.Equal(kind == "upstream" ? 4 : 3, handler.Writes.Count);
        Assert.Equal(1, handler.CommitCount);
        Assert.Equal(1, handler.PullRequestCount);
        Assert.Contains(handler.Writes, call => call.Path == "graphql");
        var write = handler.Writes.Single(call => call.Path == "graphql").Body!["variables"]!["input"]!;
        Assert.Equal(BaseCommit, write["expectedHeadOid"]!.GetValue<string>());
        Assert.Equal(prepared.Branch, write["branch"]!["branchName"]!.GetValue<string>());
        Assert.Equal(prepared.Snapshot.Path, write["fileChanges"]!["additions"]![0]!["path"]!.GetValue<string>());
        var actualContent = Encoding.UTF8.GetString(Convert.FromBase64String(
            write["fileChanges"]!["additions"]![0]!["contents"]!.GetValue<string>()));
        Assert.Equal(prepared.Content, actualContent);
        Assert.DoesNotContain(FakeCredential, actualContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Fictional customer", actualContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intranet.invalid", actualContent, StringComparison.Ordinal);
        Assert.Contains("Synthetic license notice", actualContent, StringComparison.Ordinal);
        Assert.Equal(WrittenCommit, result.Receipts.Single(receipt => receipt.Kind == "pull-request").CommitSha);
        Assert.Equal(memoryBytes, await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md")));
    }

    [Theory]
    [InlineData("extra-file")]
    [InlineData("renamed-file")]
    [InlineData("changed-content")]
    [InlineData("wrong-pr-head")]
    public async Task GivenFalseGitHubPostconditions_WhenPublishing_RefusesReceiptAndNeverClaimsCompletion(string defect)
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        using var handler = new LearningHttpHandler(Upstream) { Defect = defect };
        using var client = new HttpClient(handler);
        fixture.Configuration.Connector = new GitHubLearningConnector(client, Upstream,
            (_, _) => ValueTask.FromResult(true), "exact-learning-action-policy");
        var prepared = await fixture.PrepareAsync(fixture.Learning());

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));

        Assert.DoesNotContain(State(fixture.Host).NativeOperations,
            operation => operation.LearningRecord is { Status: "applied", Mutation.Kind: "pull-request" });
        Assert.Equal(1, handler.CommitCount);
        Assert.Equal(defect == "wrong-pr-head" ? 1 : 0, handler.PullRequestCount);
    }

    [Fact]
    public async Task GivenConcurrentRemoteHeadChange_WhenCommitting_AtomicExpectedHeadRefusesWriteAndNoPrIsOpened()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        using var handler = new LearningHttpHandler(Upstream) { Defect = "moved-head" };
        using var client = new HttpClient(handler);
        fixture.Configuration.Connector = new GitHubLearningConnector(client, Upstream,
            (_, _) => ValueTask.FromResult(true), "exact-learning-action-policy");
        var prepared = await fixture.PrepareAsync(fixture.Learning());

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));
        var writes = handler.Writes.Count;
        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));

        Assert.Equal(0, handler.CommitCount);
        Assert.Equal(0, handler.PullRequestCount);
        Assert.Equal(writes, handler.Writes.Count);
        Assert.Contains(State(fixture.Host).NativeOperations, operation =>
            operation.LearningRecord is { Status: "started", Mutation.Kind: "write" });
    }

    [Fact]
    public async Task GivenActualGitHubWriteAndLostResponse_WhenReconciling_UsesProviderReadsAndNeverIssuesASecondWrite()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        using var handler = new LearningHttpHandler(Upstream) { Defect = "lost-write-response" };
        using var client = new HttpClient(handler);
        fixture.Configuration.Connector = new GitHubLearningConnector(client, Upstream,
            (_, _) => ValueTask.FromResult(true), "exact-learning-action-policy");
        var prepared = await fixture.PrepareAsync(fixture.Learning());

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));
        var writes = handler.Writes.Count;
        var reconciled = await fixture.Learning().ReconcileAsync(Human, prepared);
        Assert.Equal(writes, handler.Writes.Count);
        var completed = await fixture.Learning().PromoteAsync(Human, prepared);

        Assert.Equal("prepared", reconciled.Status);
        Assert.Equal("pull-request-created", completed.Status);
        Assert.Equal(1, handler.CommitCount);
        Assert.Single(handler.Writes, call => call.Path == "graphql");
        Assert.Equal(1, handler.PullRequestCount);
    }

    [Fact]
    public async Task GivenPublicRepositoryMarkedAsTenant_WhenReading_RefusesFalseTenantIsolation()
    {
        using var handler = new LearningHttpHandler(Tenant) { Defect = "public-tenant" };
        using var client = new HttpClient(handler);
        var connector = new GitHubLearningConnector(client, Tenant, (_, _) => ValueTask.FromResult(true), "exact-policy");

        await Assert.ThrowsAsync<SquadHumanTransportException>(() => connector.ReadTargetAsync(Tenant, default).AsTask());

        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("https://different-provider.invalid/graphql")]
    [InlineData("http://api.github.com/graphql")]
    [InlineData("https://api.github.com/graphql?token=fictional")]
    [InlineData("https://api.github.com/graphql#unapproved")]
    [InlineData("https://fictional:fictional@api.github.com/graphql")]
    public void GivenInvalidGraphqlEndpoint_WhenRegistering_RejectsCredentialsQueriesFragmentsAndCrossOrigin(string endpoint)
    {
        using var handler = new LearningHttpHandler(Upstream);
        using var client = new HttpClient(handler);

        Assert.Throws<ArgumentException>(() => new GitHubLearningConnector(client, Upstream,
            (_, _) => ValueTask.FromResult(true), "exact-policy", graphqlEndpoint: new(endpoint)));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("wrong-repository")]
    [InlineData("different-path")]
    [InlineData("different-branch")]
    public async Task GivenOutOfScopeMutation_WhenCallingConnector_RefusesBeforeAnyNetworkMutation(string defect)
    {
        using var handler = new LearningHttpHandler(Upstream);
        using var client = new HttpClient(handler);
        var connector = new GitHubLearningConnector(client, Upstream, (_, _) => ValueTask.FromResult(true), "exact-policy");
        var promotion = Guid.NewGuid().ToString("N");
        var mutation = new SquadLearningMutation(LearningContracts.ActionId(promotion, "fork"), promotion, "fork",
            SourceRepository, ForkRepository, LearningContracts.UpstreamPath, "squad-learning/prepared", "main",
            BaseCommit, new('b', 40), "prepared source bytes", ProjectFiles.Hash("prepared source bytes"), "SL-004");
        mutation = defect switch
        {
            "unknown-kind" => mutation with { Kind = "delete" },
            "wrong-repository" => mutation with { Repository = "unapproved/other" },
            "different-path" => mutation with { Path = "unapproved.md" },
            _ => mutation with { Branch = "main" },
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.ExecuteAsync(mutation, default).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GivenExplicitMemberScopeWithBrokenHubAndSibling_WhenReadingMemoryAndLearning_NeverReadsOutsideMember()
    {
        using var fixture = new Fixture();
        fixture.Host.UseCurrentPlanContract();
        using var federation = await SquadFederation.CreateAsync(fixture.Host.Client, fixture.Host.Options, fixture.Host.Source());
        var initialized = await federation.InitializeAsync(
        [
            new() { Name = "product", Description = "Original product case" },
            new() { Name = "platform", Description = "Original platform case" },
        ]);
        Assert.Equal(SquadRunStatus.Completed, initialized.Status);
        fixture.Root = ".copilot-tracking/squad/members/product";
        _ = await federation.CreateMemory(fixture.MemoryConfiguration, "product").RememberAsync(fixture.Note);
        await File.WriteAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/state.json"), "{invalid hub}");
        await File.WriteAllTextAsync(PathFor(fixture.Host, ".copilot-tracking/squad/members/platform/state.json"), "{invalid sibling}");
        var before = Manifest(fixture.Host);

        Assert.Single(await federation.CreateMemory(fixture.MemoryConfiguration, "product").ReadAsync("researcher"));
        var discovered = await federation.CreateLearning(fixture.Configuration, "product").DiscoverAsync(Human);

        Assert.Single(discovered.Candidates);
        Assert.All(discovered.Candidates.SelectMany(candidate => candidate.Sources),
            source => Assert.StartsWith(fixture.Root + "/", source.Path, StringComparison.Ordinal));
        Unchanged(fixture.Host, before);
    }

    [Fact]
    public async Task GivenNativeMemoryThenConfirmedPromotion_WhenReadingMember_PreservesExactOriginalMemoryAndHistoricalReceipts()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var original = await File.ReadAllBytesAsync(PathFor(fixture.Host, ".copilot-tracking/squad/memory/squad-researcher.md"));
        using var federation = await SquadFederation.CreateAsync(fixture.Host.Client, fixture.Host.Options, fixture.Host.Source());

        var promoted = await federation.PromoteAsync("product", [".copilot-tracking/researcher"], description: "Original promoted case");

        Assert.True(promoted.Status == SquadRunStatus.Completed, promoted.Diagnostic);
        fixture.Root = ".copilot-tracking/squad/members/product";
        var restored = await federation.CreateMemory(fixture.MemoryConfiguration, "product").ReadAsync("researcher");
        var discovered = await federation.CreateLearning(fixture.Configuration, "product").DiscoverAsync(Human);
        Assert.Single(restored);
        Assert.Single(discovered.Candidates);
        Assert.Equal(original, await File.ReadAllBytesAsync(PathFor(fixture.Host, fixture.Root + "/memory/squad-researcher.md")));
    }

    [Fact]
    public async Task GivenUncertainLearningMutation_WhenPromotingFederation_RefusesToRelocateActiveAuthority()
    {
        using var fixture = new Fixture();
        await fixture.SeedMemoryAsync();
        var connector = fixture.UseConnector();
        connector.ThrowBeforeEffect = "fork";
        var prepared = await fixture.PrepareAsync(fixture.Learning());
        await Assert.ThrowsAsync<SquadHumanTransportException>(() => fixture.Learning().PromoteAsync(Human, prepared));
        using var federation = await SquadFederation.CreateAsync(fixture.Host.Client, fixture.Host.Options, fixture.Host.Source());
        var before = Manifest(fixture.Host);

        var promoted = await federation.PromoteAsync("product", [".copilot-tracking/researcher"], description: "Cannot move uncertain authority");

        Assert.NotEqual(SquadRunStatus.Completed, promoted.Status);
        Assert.Contains("quiescent", promoted.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Unchanged(fixture.Host, before);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GivenOriginalPlaybookLineEndings_WhenAppending_PreservesAllOriginalBytesAndLicenseText(string newline)
    {
        var snapshot = Snapshot(Upstream, Playbook("SL-", newline));
        var draft = new SquadLearningDraft("When cache reuse recurs", "Require independent invalidation proof before reusing cache inputs.",
            "All consumers with cached inputs", "A reviewed local correction");

        var added = LearningContracts.Append(Upstream, snapshot, draft, new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), 200_000);

        Assert.Equal(snapshot.Content, added.Content.Replace(LearningContracts.Row(added.Entry) + newline, "", StringComparison.Ordinal));
        Assert.Contains("Synthetic license notice", added.Content, StringComparison.Ordinal);
        Assert.Equal("SL-004", added.Entry.Id);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("invalid-date")]
    [InlineData("wrong-prefix")]
    [InlineData("missing-column")]
    public void GivenInvalidSourceRowContract_WhenPreparing_RefusesRatherThanGuessingAnIdOrSchema(string defect)
    {
        var content = Playbook("SL-");
        content = defect switch
        {
            "duplicate-id" => content.Replace("SL-003", "SL-001", StringComparison.Ordinal),
            "invalid-date" => content.Replace("2026-10-01", "not-a-date", StringComparison.Ordinal),
            "wrong-prefix" => content.Replace("SL-003", "TL-003", StringComparison.Ordinal),
            _ => content.Replace("Date Added", "Unknown Column", StringComparison.Ordinal),
        };

        Assert.Throws<InvalidDataException>(() => LearningContracts.Append(Upstream, Snapshot(Upstream, content),
            new("When cache reuse recurs", "Require independent invalidation proof before reuse.", "All consumers", "A reviewed local convention"),
            new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), 200_000));
    }

    [Trait("Category", "ReleaseCompatibility")]
    [Fact]
    public async Task GivenActualAcquiredRelease_WhenLearning_UsesRealMafEvidenceAndNeverMutatesMemoryDuringDiscovery()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION") ?? SupportedSquadRelease.Current.Tag;
        Assert.False(string.IsNullOrWhiteSpace(version));
        using var releaseClient = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        using var fixture = new Fixture();
        ConsumptionTestData.QueueResearch(fixture.Host);
        using var runtime = await SquadRuntime.CreateAsync(fixture.Host.Client, fixture.Host.Options,
            ReleaseTestHttpClient.Source(version, releaseClient));
        var run = await runtime.RunAsync("Inspect the existing bounded synthetic implementation.");
        Assert.True(run.Status == SquadRunStatus.ResearchCompleted, run.ResponseText);
        _ = await runtime.CreateMemory(fixture.MemoryConfiguration).RememberAsync(fixture.Note);
        var before = Manifest(fixture.Host);
        var calls = fixture.Host.Client.Calls.Count;
        var learning = runtime.CreateLearning(fixture.Configuration);

        var discovered = await learning.DiscoverAsync(Human);
        var prepared = await learning.PrepareAsync(Human, Assert.Single(discovered.Candidates), Upstream, Snapshot(Upstream));

        Assert.Equal("prepared", prepared.Status);
        Assert.Equal("SL-004", prepared.Prepared!.Entry.Id);
        Assert.Equal(version, runtime.Provenance.Release!.Tag);
        Assert.True(runtime.Artifacts.FindCharter("Squad Learn")?.DisableModelInvocation ?? version == "v0.16.2");
        Assert.Equal(calls, fixture.Host.Client.Calls.Count);
        Unchanged(fixture.Host, before);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class Fixture : IDisposable
    {
        private int _responses;
        internal RuntimeTestHost Host { get; } = new();
        internal TestClock Clock { get; } = new();
        internal bool AllowHuman { get; set; } = true;
        internal bool AllowMemory { get; set; } = true;
        internal bool AllowSources { get; set; } = true;
        internal string Root { get; set; } = ".copilot-tracking/squad";
        internal string? RefusedGate { get; set; }
        internal ClaimsPrincipal Principal { get; set; } = Authenticated("original-human");
        internal Func<SquadHumanResponse, SquadHumanResponse>? ResponseTransform { get; set; }
        internal List<SquadLearningConfirmation> Confirmations { get; } = [];
        internal SquadMemoryConfiguration MemoryConfiguration { get; }
        internal SquadLearningConfiguration Configuration { get; }
        internal SquadMemoryNote Note { get; } = new("researcher", "When the same cache correction recurs",
            "Before cache reuse, verify independent invalidation. Fictional customer uses " + FakeCredential +
            @" at C:\case\private.txt and https://intranet.invalid/path.",
            "All consumers with the same scenario", "A reviewed local correction for Fictional Person in ProjectCorp.");

        internal Fixture()
        {
            Host.Options.TimeProvider = Clock;
            MemoryConfiguration = new()
            {
                Binding = "case-memory-policy",
                AuthorizeAsync = (access, _) => ValueTask.FromResult(AllowMemory && access.SquadRoot == Root),
            };
            var channel = new InChatApprovalChannel("learning-human-policy", _ => ValueTask.FromResult(Principal),
                (_, action, _) => ValueTask.FromResult(AllowHuman && action.Binding.Step == "Squad Learn"), TextWriter.Null,
                authorizeLearningInvocation: (_, access, _) => ValueTask.FromResult(AllowHuman &&
                    access.SquadRoot == Root && access.Invocation.Reference.StartsWith("human:", StringComparison.Ordinal)));
            Configuration = new()
            {
                Binding = "human-learning-policy",
                AuthorizeInvocationAsync = channel.AuthorizeLearningInvocationAsync,
                AuthorizeSourceAsync = (access, _) => ValueTask.FromResult(AllowSources && access.SquadRoot == Root),
                HumanApprovalChannel = channel,
                TenantRepository = TenantRepository,
                SecretValues = [FakeCredential, "credential-in-unverified-receipt"],
                SensitiveNames = ["Fictional customer", "Fictional Person", "ProjectCorp"],
                ConfirmAsync = (confirmation, _) =>
                {
                    Confirmations.Add(confirmation);
                    if (confirmation.Action.Binding.Gate == RefusedGate)
                    {
                        return ValueTask.FromResult<SquadHumanResponse?>(null);
                    }
                    var response = SquadHumanResponse.FromCommand(confirmation.Action, "/approve",
                        "learning-response-" + (++_responses).ToString(CultureInfo.InvariantCulture), Clock.GetUtcNow());
                    return ValueTask.FromResult<SquadHumanResponse?>(ResponseTransform?.Invoke(response) ?? response);
                },
            };
        }

        internal static ClaimsPrincipal Authenticated(string subject) =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "actual-test-session"));
        internal SquadRoleMemory Memory() => new(Host.Project, MemoryConfiguration, Root, Clock);
        internal SquadLearning Learning() => new(Host.Project, Configuration, Root, Clock);
        internal async Task SeedAsync()
        {
            Host.UseCurrentPlanContract();
            ConsumptionTestData.QueueResearch(Host);
            using var runtime = await Host.CreateAsync();
            var result = await runtime.RunAsync("Inspect the original bounded synthetic cache flow.");
            Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        }
        internal async Task SeedMemoryAsync()
        {
            await SeedAsync();
            _ = await Memory().RememberAsync(Note);
        }
        internal FakeConnector UseConnector(SquadLearningTarget? target = null)
        {
            var connector = new FakeConnector(Snapshot(target ?? Upstream));
            Configuration.Connector = connector;
            return connector;
        }
        internal async Task<SquadLearningPrepared> PrepareAsync(SquadLearning learning, SquadLearningTarget? target = null)
        {
            var candidate = Assert.Single((await learning.DiscoverAsync(Human)).Candidates);
            var result = await learning.PrepareAsync(Human, candidate, target ?? Upstream, Snapshot(target ?? Upstream));
            Assert.Equal("prepared", result.Status);
            Assert.Equal(Root, result.Prepared?.SquadRoot);
            return Assert.IsType<SquadLearningPrepared>(result.Prepared);
        }
        public void Dispose() => Host.Dispose();
    }

    private sealed class FakeConnector(SquadLearningTargetSnapshot snapshot) : ISquadLearningConnector
    {
        private readonly Dictionary<string, SquadLearningMutationReceipt> _effects = new(StringComparer.Ordinal);
        internal SquadLearningTargetSnapshot Snapshot { get; set; } = snapshot;
        internal List<SquadLearningMutation> Executions { get; } = [];
        internal int Reads { get; private set; }
        internal string? ThrowAfterEffect { get; set; }
        internal string? ThrowBeforeEffect { get; set; }
        internal bool BadReceipt { get; set; }
        public string Binding => "original-learning-provider";
        public ValueTask<SquadLearningTargetSnapshot> ReadTargetAsync(SquadLearningTarget target, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Snapshot);
        }
        public ValueTask<bool> AuthorizeAsync(SquadLearningMutation mutation, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<SquadLearningMutationReceipt> ExecuteAsync(SquadLearningMutation mutation, CancellationToken cancellationToken)
        {
            Executions.Add(mutation);
            if (ThrowBeforeEffect == mutation.Kind)
            {
                throw new SquadHumanTransportException("Synthetic transport failed before a visible postcondition.", true);
            }
            var receipt = new SquadLearningMutationReceipt(HumanActions.Hash(mutation), Binding, mutation.Kind,
                mutation.Repository, "resource-" + mutation.Id,
                "https://github.com/" + (mutation.Kind == "pull-request" ? mutation.OriginRepository : mutation.Repository) +
                    "/learning/" + mutation.Id,
                mutation.Kind == "write" ? WrittenCommit : mutation.HeadCommitSha,
                mutation.Kind == "write" ? mutation.ContentSha256 : null);
            _effects.Add(mutation.Id, receipt);
            if (ThrowAfterEffect == mutation.Kind)
            {
                throw new SquadHumanTransportException("Synthetic actual postcondition exists, but the response was lost.", true);
            }
            return ValueTask.FromResult(BadReceipt ? receipt with { ResourceId = "credential-in-unverified-receipt" } : receipt);
        }
        public ValueTask<bool> VerifyAsync(SquadLearningMutation mutation, SquadLearningMutationReceipt receipt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_effects.TryGetValue(mutation.Id, out var actual) && actual == receipt);
        public ValueTask<SquadLearningMutationReceipt?> ReconcileAsync(SquadLearningMutation mutation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_effects.GetValueOrDefault(mutation.Id));
    }

    private sealed class LearningHttpHandler(SquadLearningTarget target) : HttpMessageHandler
    {
        private readonly SquadLearningTarget _target = LearningContracts.ValidateTarget(target);
        private readonly Dictionary<(string Repository, string Branch), string> _refs = new() { [(target.Repository, "main")] = BaseCommit };
        private readonly Dictionary<(string Repository, string Commit), (string Sha, string Content)> _files =
            new() { [(target.Repository, BaseCommit)] = (new('b', 40), Playbook(LearningContracts.Prefix(target))) };
        private readonly Dictionary<(string Repository, string Commit), JsonObject> _commits = [];
        private JsonObject? _pull;
        private bool _fork;
        internal string? Defect { get; set; }
        internal List<(string Method, string Path, JsonNode? Body)> Requests { get; } = [];
        internal List<(string Method, string Path, JsonNode? Body)> Writes => Requests.Where(request => request.Method != "GET").ToList();
        internal int CommitCount { get; private set; }
        internal int PullRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.Method.Method, path, body?.DeepClone()));
            if (path == "user")
            {
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["id"] = 42,
                    ["type"] = "User",
                    ["login"] = _target.DestinationRepository!.Split('/')[0],
                });
            }
            if (path == "graphql")
            {
                var input = body!["variables"]!["input"]!;
                var repository = input["branch"]!["repositoryNameWithOwner"]!.GetValue<string>();
                var branch = input["branch"]!["branchName"]!.GetValue<string>();
                var expected = input["expectedHeadOid"]!.GetValue<string>();
                if (Defect == "moved-head")
                {
                    _refs[(repository, branch)] = new('d', 40);
                }
                if (_refs.GetValueOrDefault((repository, branch)) != expected)
                {
                    return Reply(HttpStatusCode.OK, new JsonObject
                    {
                        ["errors"] = new JsonArray(new JsonObject { ["message"] = "Expected head mismatch" }),
                    });
                }
                var addition = input["fileChanges"]!["additions"]![0]!;
                var filePath = addition["path"]!.GetValue<string>();
                var content = Encoding.UTF8.GetString(Convert.FromBase64String(addition["contents"]!.GetValue<string>()));
                CommitCount++;
                _refs[(repository, branch)] = WrittenCommit;
                _files[(repository, WrittenCommit)] = (new('e', 40), Defect == "changed-content" ? content + "unapproved extra bytes" : content);
                var files = new JsonArray(new JsonObject
                {
                    ["filename"] = filePath,
                    ["status"] = Defect == "renamed-file" ? "renamed" : "modified",
                });
                if (Defect == "extra-file")
                {
                    files.Add(new JsonObject { ["filename"] = "unapproved.txt", ["status"] = "added" });
                }
                _commits[(repository, WrittenCommit)] = new()
                {
                    ["sha"] = WrittenCommit,
                    ["commit"] = new JsonObject
                    {
                        ["message"] = input["message"]!["headline"]!.GetValue<string>() + "\n\n" + input["message"]!["body"]!.GetValue<string>(),
                    },
                    ["parents"] = new JsonArray(new JsonObject { ["sha"] = expected }),
                    ["files"] = files,
                };
                if (Defect == "lost-write-response")
                {
                    return Reply(HttpStatusCode.InternalServerError, new JsonObject { ["message"] = "Synthetic lost successful response" });
                }
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["createCommitOnBranch"] = new JsonObject
                        {
                            ["clientMutationId"] = input["clientMutationId"]!.GetValue<string>(),
                            ["commit"] = new JsonObject { ["oid"] = WrittenCommit },
                        },
                    },
                });
            }
            var parts = path.Split('/');
            if (parts.Length < 3 || parts[0] != "repos")
            {
                return Reply(HttpStatusCode.NotFound, new JsonObject());
            }
            var repo = parts[1] + "/" + parts[2];
            var suffix = string.Join('/', parts.Skip(3));
            var exists = repo == _target.Repository || repo == _target.DestinationRepository && (_fork || _target.Kind == "tenant");
            if (suffix.Length == 0)
            {
                if (!exists)
                {
                    return Reply(HttpStatusCode.NotFound, new JsonObject());
                }
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["id"] = repo == _target.Repository ? 40 : 41,
                    ["full_name"] = repo,
                    ["private"] = _target.Kind == "tenant" && Defect != "public-tenant",
                    ["archived"] = false,
                    ["disabled"] = false,
                    ["default_branch"] = "main",
                    ["fork"] = repo != _target.Repository,
                    ["parent"] = new JsonObject { ["full_name"] = _target.Repository },
                });
            }
            if (suffix == "forks" && request.Method == HttpMethod.Post && repo == _target.Repository)
            {
                _fork = true;
                _refs[(_target.DestinationRepository!, "main")] = BaseCommit;
                _files[(_target.DestinationRepository!, BaseCommit)] = _files[(_target.Repository, BaseCommit)];
                return Reply(HttpStatusCode.Accepted, new JsonObject { ["id"] = 41, ["full_name"] = _target.DestinationRepository });
            }
            if (!exists)
            {
                return Reply(HttpStatusCode.NotFound, new JsonObject());
            }
            if (suffix.StartsWith("git/ref/heads/", StringComparison.Ordinal))
            {
                var branch = suffix["git/ref/heads/".Length..];
                if (!_refs.TryGetValue((repo, branch), out var commit))
                {
                    return Reply(HttpStatusCode.NotFound, new JsonObject());
                }
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["ref"] = "refs/heads/" + branch,
                    ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = commit },
                });
            }
            if (suffix == "git/refs" && request.Method == HttpMethod.Post)
            {
                var reference = body!["ref"]!.GetValue<string>();
                _refs.Add((repo, reference["refs/heads/".Length..]), body["sha"]!.GetValue<string>());
                return Reply(HttpStatusCode.Created, new JsonObject { ["ref"] = reference });
            }
            if (suffix.StartsWith("contents/", StringComparison.Ordinal))
            {
                var commit = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')["ref=".Length..]);
                if (!_files.TryGetValue((repo, commit), out var file))
                {
                    return Reply(HttpStatusCode.NotFound, new JsonObject());
                }
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["type"] = "file",
                    ["path"] = suffix["contents/".Length..],
                    ["encoding"] = "base64",
                    ["content"] = Convert.ToBase64String(bytes),
                    ["sha"] = file.Sha,
                    ["size"] = bytes.Length,
                });
            }
            if (suffix.StartsWith("commits/", StringComparison.Ordinal))
            {
                return _commits.TryGetValue((repo, suffix["commits/".Length..]), out var commit)
                    ? Reply(HttpStatusCode.OK, commit.DeepClone())
                    : Reply(HttpStatusCode.NotFound, new JsonObject());
            }
            if (suffix == "pulls" && request.Method == HttpMethod.Post)
            {
                PullRequestCount++;
                var branch = body!["head"]!.GetValue<string>().Split(':', 2)[1];
                _pull = new()
                {
                    ["id"] = 70,
                    ["number"] = 7,
                    ["body"] = body["body"]!.GetValue<string>(),
                    ["html_url"] = "https://github.com/" + _target.Repository + "/pull/7",
                    ["base"] = new JsonObject
                    {
                        ["ref"] = body["base"]!.GetValue<string>(),
                        ["repo"] = new JsonObject { ["full_name"] = _target.Repository },
                    },
                    ["head"] = new JsonObject
                    {
                        ["ref"] = branch,
                        ["sha"] = Defect == "wrong-pr-head" ? new string('d', 40) : _refs[(_target.DestinationRepository!, branch)],
                        ["repo"] = new JsonObject { ["full_name"] = _target.DestinationRepository },
                    },
                };
                return Reply(HttpStatusCode.Created, _pull.DeepClone());
            }
            if (suffix == "pulls")
            {
                return Reply(HttpStatusCode.OK, _pull is null ? new JsonArray() : new JsonArray(_pull.DeepClone()));
            }
            return Reply(HttpStatusCode.NotFound, new JsonObject());
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
