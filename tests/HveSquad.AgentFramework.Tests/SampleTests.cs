using System.Text.Json;
using System.Security.Cryptography;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Tests;

public sealed class SampleTests
{
    [Theory]
    [InlineData("--report", "governance")]
    [InlineData("--memory", "read")]
    [InlineData("--learn", "discover")]
    public void GivenNativeHelper_WhenParsing_DoesNotConstructAnArtifactSourceOrLiveCommand(string selector, string operation)
    {
        var arguments = new List<string> { selector, operation, "--project", "." };
        if (selector == "--memory")
        {
            arguments.AddRange(["--role", "researcher"]);
        }
        var command = SampleCommand.Parse(arguments.ToArray());
        Assert.Equal(SampleCommandKind.NativeHelper, command.Kind);
        Assert.Null(command.Source);
        Assert.Equal(selector, command.Helper!.Kind);
        using var help = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SampleCommand.PrintUsage(help);
        Assert.Contains("No connector means prepared-only", help.ToString(), StringComparison.Ordinal);
        Assert.Contains("--learning-connector github on BOTH", help.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GivenExplicitOrganizationForkProposal_WhenParsing_PreservesThePublicTargetWithoutGrantingAConnector()
    {
        var command = SampleCommand.Parse(["--learn", "promote", "--project", ".", "--target", "upstream",
            "--repository", "Peter-N91/hve-squad", "--destination", "Example-Org/hve-squad",
            "--prepared", "outputs/prepared.json", "--fork-organization", "true"]).Helper!;

        Assert.Equal(new SquadLearningTarget("upstream", "peter-n91/hve-squad", "example-org/hve-squad", true), command.Target);
        Assert.False(command.GitHubConnector);
    }

    [Theory]
    [InlineData("tenant", "true")]
    [InlineData("tenant", "false")]
    [InlineData("upstream", "yes")]
    public void GivenInvalidOrganizationForkSelection_WhenParsing_RejectsTheInapplicableOrNonBooleanValue(string target, string value)
    {
        var args = new List<string> { "--learn", "promote", "--project", ".", "--target", target,
            "--repository", "example/source", "--prepared", "prepared.json", "--fork-organization", value };
        if (target == "upstream")
        {
            args.AddRange(["--destination", "example-fork/source"]);
        }
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(args.ToArray()));
    }

    [Theory]
    [InlineData("--report", "governance", "--format", "pdf")]
    [InlineData("--report", "document", "--format", "pdf")]
    [InlineData("--report", "governance", "--period", "1d")]
    [InlineData("--memory", "read", "--note", "note.json")]
    [InlineData("--memory", "remember", "--role", "researcher")]
    [InlineData("--learn", "discover", "--learning-connector", "github")]
    [InlineData("--learn", "reconcile", "--prepared", "prepared.json")]
    [InlineData("--learn", "automatic", "--focus", "general")]
    [InlineData("--report", "governance", "--version", "v0.18.1")]
    [InlineData("--report", "governance", "--run", "grant permission")]
    public void GivenMissingOrInapplicableNativeHelperOptions_WhenParsing_RejectsThemBeforeHostSetup(
        string selector, string operation, string key, string value) =>
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse([selector, operation, "--project", ".", key, value]));

    [Fact]
    public async Task GivenNativeHelperDenial_WhenTheCaseIsUninitialized_DoesNotAcquireOrCreateState()
    {
        using var host = new RuntimeTestHost();
        var command = SampleCommand.Parse(["--report", "governance", "--project", host.Project]).Helper!;
        var source = new NoHelperAcquisition();
        using var input = new StringReader("no\n");
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NativeHelpers.RunAsync(command, default, input, output, source));

        Assert.Equal(0, source.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
        Assert.Contains("Report case/source/output authorization", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenDeclinedLearningInitiation_WhenTheCaseIsUninitialized_RefusesBeforeAnyScopedReadOrAcquisition()
    {
        using var host = new RuntimeTestHost();
        var command = SampleCommand.Parse(["--learn", "discover", "--project", host.Project]).Helper!;
        var source = new NoHelperAcquisition();
        using var input = new StringReader("no\n");
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NativeHelpers.RunAsync(command, default, input, output, source));

        Assert.Equal(0, source.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenOversizedHelperInput_WhenReading_RejectsBeforeDeserializationOrMemoryWrites()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Project, "note.json");
        await File.WriteAllBytesAsync(path, new byte[1_000_001]);
        var command = SampleCommand.Parse(["--memory", "remember", "--project", host.Project,
            "--role", "researcher", "--note", "note.json"]).Helper!;
        using var input = Yes();
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => NativeHelpers.RunAsync(command, default, input, output));

        Assert.Contains("one million bytes", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1_000_001, new FileInfo(path).Length);
        Assert.False(Directory.Exists(Path.Combine(host.Project, ".copilot-tracking")));
    }

    [Fact]
    public async Task GivenExactlyOneMillionByteHelperInput_WhenReading_AcceptsTheFullValidNoteWithoutTruncation()
    {
        using var host = new RuntimeTestHost();
        await SeedHelperCaseAsync(host);
        var note = new SquadMemoryNote("researcher", "When independent evidence is missing",
            "Keep unknown observations explicit and require independent verification before reuse.");
        var json = JsonSerializer.SerializeToUtf8Bytes(note);
        var bytes = new byte[1_000_000];
        Array.Fill(bytes, (byte)' ');
        json.CopyTo(bytes.AsSpan());
        await File.WriteAllBytesAsync(Path.Combine(host.Project, "note.json"), bytes);
        var command = SampleCommand.Parse(["--memory", "remember", "--project", host.Project,
            "--role", "researcher", "--note", "note.json"]).Helper!;
        using var input = Yes();
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, await NativeHelpers.RunAsync(command, default, input, output));

        var memory = new SquadRoleMemory(host.Project, new()
        {
            Binding = "console-local-memory-v1",
            AuthorizeAsync = (_, _) => ValueTask.FromResult(true),
        });
        Assert.Equal(note, Assert.Single(await memory.ReadAsync("researcher")).Note);
    }

    [Theory]
    [InlineData("packs")]
    [InlineData("roles")]
    [InlineData("memberNames")]
    [InlineData("mode")]
    [InlineData("squadRoot")]
    public void GivenNullNonnullableSampleConfiguration_WhenParsing_RejectsBeforeHostConstruction(string property)
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Root, "invalid-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["request"] = "Research the bounded fixture.",
            ["project"] = host.Project,
            [property] = null,
        }));

        Assert.Throws<JsonException>(() => SampleCommand.Parse(["--config", path]));
    }

    [Fact]
    public void GivenDocumentedNativeRunConfiguration_WhenParsing_KeepsOnlyDataAndTheActualPinnedDefaults()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HveSquad.AgentFramework.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "samples", "HveSquad.AgentFramework.Sample", "native-run.settings.example.json");

        var command = SampleCommand.Parse(["--config", path]);

        Assert.Equal(SampleCommandKind.Run, command.Kind);
        Assert.Equal(HveSquad.AgentFramework.Sources.SupportedSquadRelease.Current.Tag, command.Version);
        Assert.Equal("interactive", command.Mode);
        Assert.True(command.DurableApprovals);
        Assert.Equal("in-chat", command.RosterSettings!.Notifications!.ApprovalChannel);
        Assert.Null(command.Helper);
    }

    [Theory]
    [InlineData("md")]
    [InlineData("html")]
    [InlineData("pdf")]
    [InlineData("docx")]
    public async Task GivenAuthorizedNativeReportCommand_WhenRunning_ProducesRealFormatsWithoutModelResolution(string format)
    {
        using var host = new RuntimeTestHost();
        await SeedHelperCaseAsync(host);
        var original = StateHashes(host.Project);
        var calls = host.Client.Calls.Count;
        var source = new NoHelperAcquisition();
        var command = SampleCommand.Parse(["--report", "document", "--project", host.Project,
            "--request", "Current roster", "--format", format]).Helper!;
        using var input = Yes();
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, await NativeHelpers.RunAsync(command, default, input, output, source));

        var path = Assert.Single(Directory.GetFiles(Path.Combine(host.Project, "docs"), "*." + format));
        var bytes = await File.ReadAllBytesAsync(path);
        if (format is "pdf" or "docx")
        {
            ReportFormats.Verify(format, bytes);
        }
        else
        {
            Assert.StartsWith(format == "md" ? "---" : "<!doctype html>", System.Text.Encoding.UTF8.GetString(bytes),
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(original.OrderBy(pair => pair.Key), StateHashes(host.Project).OrderBy(pair => pair.Key));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task GivenLocalMemoryAndActualConsoleInitiation_WhenPreparingAndPromotingWithoutConnector_StaysPreparedAndStateReadOnly()
    {
        using var host = new RuntimeTestHost();
        await SeedHelperCaseAsync(host);
        var source = new NoHelperAcquisition();
        var note = new SquadMemoryNote("researcher", "When independent evidence is missing",
            "Keep unknown observations explicit and require independent verification before reuse.");
        await File.WriteAllBytesAsync(Path.Combine(host.Project, "note.json"),
            [.. System.Text.Encoding.UTF8.GetPreamble(), .. JsonSerializer.SerializeToUtf8Bytes(note)]);
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        async Task<int> Run(params string[] arguments)
        {
            using var input = Yes();
            return await NativeHelpers.RunAsync(SampleCommand.Parse([.. arguments, "--project", host.Project]).Helper!,
                default, input, output, source);
        }
        Assert.Equal(0, await Run("--memory", "remember", "--role", "researcher", "--note", "note.json"));
        Assert.Equal(0, await Run("--memory", "read", "--role", "researcher"));
        var before = StateHashes(host.Project);
        var outputStart = output.GetStringBuilder().Length;
        Assert.Equal(0, await Run("--learn", "discover"));
        var discoveredText = output.ToString()[outputStart..].ReplaceLineEndings("\n");
        var jsonStart = discoveredText.IndexOf("{\n  \"Candidates\"", StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, discoveredText);
        var discovery = JsonSerializer.Deserialize<SquadLearningDiscovery>(discoveredText[jsonStart..])!;
        var candidate = Assert.Single(discovery.Candidates);
        var snapshot = new SquadLearningTargetSnapshot("fixture/tenant",
            "squad-src/.github/skills/squad-learnings-tenant/tenant-learnings.md", "main",
            new string('a', 40), new string('b', 40), """
            Synthetic local target. No live repository is claimed.

            | id | Scenario / Trigger | Learning / Rule | Scope / Applicability | Source Context | Date Added |
            | --- | --- | --- | --- | --- | --- |
            | TL-001 | An unrelated first scenario | Require independent review before reuse | All consumers | Original synthetic evidence | 2026-10-01 |
            """);
        await File.WriteAllTextAsync(Path.Combine(host.Project, "snapshot.json"), JsonSerializer.Serialize(snapshot));

        Assert.Equal(0, await Run("--learn", "prepare", "--candidate", candidate.Id, "--target", "tenant",
            "--repository", "Fixture/Tenant", "--snapshot", "snapshot.json", "--prepared", "outputs\\prepared.json"));
        Assert.Equal(0, await Run("--learn", "promote", "--target", "tenant",
            "--repository", "fixture/tenant", "--prepared", "outputs\\prepared.json"));

        var prepared = JsonSerializer.Deserialize<SquadLearningPrepared>(
            await File.ReadAllTextAsync(Path.Combine(host.Project, "outputs", "prepared.json")))!;
        Assert.Equal("TL-002", prepared.Entry.Id);
        Assert.Equal(before.OrderBy(pair => pair.Key), StateHashes(host.Project).OrderBy(pair => pair.Key));
        Assert.Contains("No connector is registered", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\": \"pull-request-created\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData(".copilot-tracking/squad/state.json")]
    [InlineData("sub/.github/settings.json")]
    [InlineData("sub\\..\\outside.json")]
    public async Task GivenHelperInputOutsideOrdinaryCaseFiles_WhenReading_RefusesWithoutMutation(string path)
    {
        using var host = new RuntimeTestHost();
        using var input = Yes();
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var command = SampleCommand.Parse(["--memory", "remember", "--project", host.Project,
            "--role", "researcher", "--note", path]).Helper!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeHelpers.RunAsync(command, default, input, output));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenFreshFederationDemo_WhenRunning_ExecutesTheActualNamedMemberAndRefusesOverwrite()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        var command = SampleCommand.Parse(["--federation-demo", "research", "--project", host.Project]);
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, await FederationDemo.RunAsync(command, default, host.Source(), output));
        Assert.Contains("Member research: ResearchCompleted", output.ToString(), StringComparison.Ordinal);
        var state = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(
            Path.Combine(host.Project, ".copilot-tracking", "squad", "members", "research", "state.json")))!;
        Assert.Equal("researcher", Assert.Single(Assert.Single(state.Runs).Evidence).Role);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FederationDemo.RunAsync(command, default, host.Source(), output));
    }

    private static async Task SeedHelperCaseAsync(RuntimeTestHost host)
    {
        host.UseCurrentPlanContract();
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();
        Assert.Equal(SquadRunStatus.ResearchCompleted, (await runtime.RunAsync("Inspect the original bounded fixture.")).Status);
    }

    private static StringReader Yes() => new(string.Concat(Enumerable.Repeat("yes\n", 100)));
    private static Dictionary<string, string> StateHashes(string project) =>
        Directory.EnumerateFiles(Path.Combine(project, ".copilot-tracking", "squad"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(project, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private sealed class NoHelperAcquisition : HveSquad.AgentFramework.Sources.SquadArtifactSource
    {
        internal int Calls { get; private set; }
        public override ValueTask<HveSquad.AgentFramework.Sources.SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A native helper must not acquire an artifact source.");
        }
    }

    [Fact]
    public void GivenRegisteredSkillDemo_WhenParsing_RequiresAnExplicitHostExecutableAndIsolatedProject()
    {
        var executable = Path.Combine(Path.GetTempPath(), "trusted-pwsh.exe");
        var command = SampleCommand.Parse(["--skill-executor-demo", executable, "--project", "."]);
        Assert.Equal(SampleCommandKind.SkillExecutorDemo, command.Kind);
        Assert.Equal(executable, command.ExecutorPath);
        Assert.Equal(HveSquad.AgentFramework.Sources.SupportedSquadRelease.Current.Tag, command.Version);
        using var help = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SampleCommand.PrintUsage(help);
        Assert.Contains("Only pr-reference/scripts/read-diff.ps1", help.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--skill-executor-demo", "pwsh", "--project", "."]));
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--skill-executor-demo", executable, "--project", ".", "--run", "Unbounded work"]));
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public void GivenModeFlag_WhenParsing_PreservesTrustedSelectionAndDocumentsItsBoundaries(string mode)
    {
        var command = SampleCommand.Parse(["--run", "Deliver local code", "--project", ".", "--mode", mode]);
        Assert.Equal(mode, command.Mode);
        using var help = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SampleCommand.PrintUsage(help);
        Assert.Contains("--mode interactive|autonomous|autopilot", help.ToString(), StringComparison.Ordinal);
        Assert.Contains("final human validation", help.ToString(), StringComparison.Ordinal);
        Assert.Contains("at most two independent revalidations", help.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("unbounded")]
    public void GivenInvalidExecutionMode_WhenParsing_RejectsBeforeHostSetup(string mode) =>
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Deliver", "--project", ".", "--mode", mode]));

    [Fact]
    public void GivenDurableHostFlags_WhenParsing_PreservesDataAndRejectsUnboundReplies()
    {
        var command = SampleCommand.Parse(["--resume", "exact-run", "--project", ".", "--durable-approvals", "true",
            "--human-response-timeout", "02:00:00", "--reply", "/approve"]);
        Assert.True(command.DurableApprovals);
        Assert.Equal(TimeSpan.FromHours(2), command.HumanResponseTimeout);
        Assert.Equal("/approve", command.HumanReply);
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Research", "--project", ".", "--reply", "/approve"]));
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--resume", "run", "--project", ".", "--human-response-timeout", "00:00:00"]));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    public void GivenApprovalDemo_WhenParsing_RequiresExplicitOperationAndReplyOnlyForResume(string operation)
    {
        var arguments = new List<string> { "--approval-demo", operation, "--project", "." };
        if (operation == "resume")
        {
            arguments.AddRange(["--reply", "/approve"]);
        }
        var command = SampleCommand.Parse(arguments.ToArray());
        Assert.Equal(SampleCommandKind.ApprovalDemo, command.Kind);
        Assert.Equal(operation, command.DemoOperation);
        Assert.True(command.DurableApprovals);
    }

    [Fact]
    public async Task GivenScriptedApprovalDemo_WhenRecreatingTheHost_ProducesRealSameRunResearchAndAuthenticatedEvidence()
    {
        using var host = new RuntimeTestHost();
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var start = SampleCommand.Parse(["--approval-demo", "start", "--project", host.Project]);
        var source = new HveSquad.AgentFramework.Sources.DirectoryArtifactSource([host.Artifacts]);
        Assert.Equal(0, await DurableApprovalDemo.RunAsync(start, default, source, output));
        var path = Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json");
        var pending = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path))!;
        var runId = pending.Checkpoint!.RunId;
        Assert.Equal("pending", Assert.Single(pending.Checkpoint.HumanActions!).Action.Status);
        Assert.Empty(pending.Checkpoint.Evidence);
        var resume = SampleCommand.Parse(["--approval-demo", "resume", "--project", host.Project, "--reply", "/approve"]);

        Assert.Equal(0, await DurableApprovalDemo.RunAsync(resume, default, source, output));

        var completed = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path))!;
        var run = Assert.Single(completed.Runs);
        Assert.Equal(runId, run.RunId);
        Assert.Equal(SquadRunStatus.ResearchCompleted, run.Status);
        Assert.Single(run.Evidence);
        Assert.Equal("in-chat", Assert.Single(completed.HumanDecisions!).Provider);
        Assert.Contains("Status: ApprovalRequired", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Status: ResearchCompleted", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("standard")]
    [InlineData("deep")]
    [InlineData("skip")]
    public void GivenDiscoveryFlag_WhenParsingSample_PreservesExplicitDepthAndCouncilData(string depth)
    {
        var command = SampleCommand.Parse(["--run", "Shape a case", "--project", ".", "--discovery", depth,
            "--topic-id", "shape-case", "--council-roles", "security,rai"]);
        Assert.Equal(depth, command.Discovery);
        Assert.Equal("shape-case", command.TopicId);
        Assert.Equal(["security", "rai"], command.Council!.Roles);
        Assert.True(command.HumanPresent);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("auto")]
    [InlineData("DEEP")]
    public void GivenInvalidDiscoveryFlag_WhenParsingSample_RejectsBeforeProviderSetup(string depth) =>
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Shape", "--project", ".", "--discovery", depth]));

    [Fact]
    public void GivenJsonGateData_WhenParsingSample_PreservesUnattendedAndScopedWaiver()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Root, "gate-run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            request = "Shape a case",
            project = host.Project,
            discovery = "deep",
            humanPresent = false,
            topicId = "shape-case",
            council = new { roles = Array.Empty<string>(), waiverReason = "Local fixture only." },
        }));
        var command = SampleCommand.Parse(["--config", path]);
        Assert.False(command.HumanPresent);
        Assert.Equal("deep", command.Discovery);
        Assert.Equal("Local fixture only.", command.Council!.WaiverReason);
        Assert.Empty(command.Council.Roles);
    }

    [Theory]
    [InlineData("10.0000")]
    [InlineData("unset")]
    public void GivenCostFlag_WhenParsingSample_PreservesExactPositiveCeilingOrUnset(string ceiling)
    {
        var command = SampleCommand.Parse(["--run", "Research", "--project", ".", "--cost-ceiling", ceiling]);
        Assert.Equal(ceiling, command.CostCeiling);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void GivenInvalidCostFlag_WhenParsingSample_RejectsItBeforeProviderSetup(string ceiling) =>
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Research", "--project", ".", "--cost-ceiling", ceiling]));

    [Fact]
    public void GivenJsonTariff_WhenParsingSample_PreservesDataOnlyExactRates()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Root, "cost-run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            request = "Research",
            project = host.Project,
            costCeiling = "10.00",
            consumption = ConsumptionTestData.Configuration(),
        }));
        var command = SampleCommand.Parse(["--config", path]);
        Assert.Equal("10.00", command.CostCeiling);
        Assert.Equal(3.75m, command.Consumption!.Models["model-fixed"].Standard.CacheWrite);
        Assert.Equal(1, command.Consumption.Calibration.Observations);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("ranked")]
    [InlineData("manual")]
    public void GivenModelRoutingFlag_WhenParsingSample_PreservesDeclaredMode(string mode)
    {
        var command = SampleCommand.Parse(["--run", "Research", "--project", ".", "--routing", mode]);
        Assert.Equal(mode, command.ModelRouting!.Mode);
    }

    [Fact]
    public void GivenInvalidModelRoutingFlag_WhenParsingSample_ReportsItBeforeProviderSetup() =>
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Research", "--project", ".", "--routing", "latest"]));

    [Theory]
    [InlineData("v0.16.2")]
    [InlineData("v0.17.0")]
    public void SampleAcceptsPublishedVersionSyntaxWithoutHardcodingTheCurrentRelease(string version)
    {
        Assert.Equal(SampleCommandKind.Inspect, SampleCommand.Parse(["--release", version]).Kind);
        var command = SampleCommand.Parse(["--run", "Research a feature", "--project", ".", "--version", version]);
        Assert.Equal(version, command.Version);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("v0.17.0-preview.1")]
    [InlineData("owner/repo#main")]
    public void SampleRejectsMovingOrPrereleaseReferences(string version)
    {
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--release", version]));
        Assert.Throws<ArgumentException>(() => SampleCommand.Parse(["--run", "Research", "--project", ".", "--version", version]));
    }

    [Fact]
    public void SampleResumeUsesExplicitIdentityAndDataOnlyRootAndMigrationSettings()
    {
        var command = SampleCommand.Parse(["--resume", "exact-run", "--project", ".",
            "--squad-root", ".copilot-tracking/squad/members/product", "--migrate-v1", "true"]);
        Assert.Equal("exact-run", command.ResumeRunId);
        Assert.Null(command.Request);
        Assert.True(command.AllowNativeV1Migration);
        Assert.Equal(".copilot-tracking/squad/members/product", command.SquadRoot);
        Assert.Throws<ArgumentException>(() =>
            SampleCommand.Parse(["--run", "request", "--resume", "exact-run", "--project", "."]));
        Assert.Throws<ArgumentException>(() =>
            SampleCommand.Parse(["--run", "request", "--project", ".", "--migrate-v1", "maybe"]));
    }

    [Fact]
    public async Task SampleReconcilerVerifiesExactIdempotentBytesWithoutReplayingAWrite()
    {
        using var host = new RuntimeTestHost();
        var writer = new ProjectFileWriter(host.Project);
        var payload = JsonSerializer.SerializeToElement(new { path = "src/approved.cs", content = "class Approved;" });
        var request = new SquadEffectReconciliationRequest("run", ".copilot-tracking/squad",
            "operation", "dispatch", "developer", "write_project_file", "digest", payload);
        Assert.Equal(SquadEffectDisposition.CannotConfirm, (await writer.ReconcileAsync(request, default)).Disposition);
        await writer.WriteAsync("src/approved.cs", "class Approved;", default);
        var result = await writer.ReconcileAsync(request, default);
        Assert.Equal(SquadEffectDisposition.Executed, result.Disposition);
        Assert.Equal("src\\approved.cs", result.Result!.Value.GetString()!.Replace('/', '\\'));
        File.WriteAllText(Path.Combine(host.Project, "src", "approved.cs"), "different");
        Assert.Equal(SquadEffectDisposition.CannotConfirm, (await writer.ReconcileAsync(request, default)).Disposition);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData(".copilot-tracking/squad/state.json")]
    [InlineData(".git/config")]
    [InlineData(".git /config")]
    [InlineData(".agents/skill.txt")]
    [InlineData(".github/workflows/action.yml")]
    [InlineData("sub\\..\\escape.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("\\outside.txt")]
    public async Task SampleWriterRejectsAmbiguousAndProtectedPaths(string path)
    {
        using var host = new RuntimeTestHost();
        var writer = new ProjectFileWriter(host.Project);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(path, "content", default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task SampleWriterReturnsEvidenceOfItsActualOutput()
    {
        using var host = new RuntimeTestHost();
        var result = await new ProjectFileWriter(host.Project).WriteAsync("src/example.cs", "class Example;", default);
        var evidence = ProjectFileWriter.GetOutputEvidence(result);
        Assert.Equal("class Example;", File.ReadAllText(Path.Combine(host.Project, Assert.Single(evidence.ProjectPaths))));
        Assert.Equal(evidence.ProjectPaths, ProjectFileWriter.GetOutputEvidence(JsonSerializer.SerializeToElement(result)).ProjectPaths);
    }

    [Fact]
    public async Task SampleWriterCancellationDoesNotCreateDirectories()
    {
        using var host = new RuntimeTestHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProjectFileWriter(host.Project).WriteAsync("src/example.cs", "class Example;", cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public void SampleWriterRejectsMissingOutputAttestation() =>
        Assert.Throws<InvalidOperationException>(() => ProjectFileWriter.GetOutputEvidence(null));
}
