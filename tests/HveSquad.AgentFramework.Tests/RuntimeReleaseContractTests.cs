using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeReleaseContractTests
{
    private static readonly string[] InitializationHistoryNames = ["Squad Scribe.md", "Squad Coordinator.md"];

    [Fact]
    [Trait("Category", "ReleaseConformance")]
    public async Task GivenActualSupportedRelease_WhenSeedingEveryProfile_PreservesExactReleasedRolesRootsAndPackComposition()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        Assert.Equal(SupportedSquadRelease.Current.Tag, version);
        using var client = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        var source = ReleaseTestHttpClient.Source(version, client);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["default"] = "researcher,lead,developer,tester,scribe",
            ["security"] = "researcher,lead,developer,tester,security,supply-chain,rai,privacy,fact-checker,scribe",
            ["design"] = "researcher,lead,developer,tester,designer,accessibility,scribe",
            ["accessibility"] = "researcher,lead,developer,tester,accessibility,designer,scribe",
            ["architecture"] = "researcher,lead,developer,tester,architect,azure-architect,cost-manager,scribe",
            ["azure"] = "researcher,lead,developer,tester,azure-architect,iac-author,deployer,asbuilt-author,azure-diagnose,architect,cost-manager,security,modernizer,scribe",
            ["modernization"] = "researcher,lead,developer,tester,modernizer,architect,azure-architect,iac-author,cost-manager,asbuilt-author,scribe",
            ["compliance"] = "researcher,lead,developer,tester,security,supply-chain,vuln-manager,privacy,rai,accessibility,risk-manager,scribe",
            ["operations"] = "researcher,lead,developer,tester,azure-diagnose,performance,observability,asbuilt-author,iac-author,deployer,scribe",
            ["product"] = "researcher,lead,developer,tester,analyst,designer,product-owner,presenter,technical-writer,experimenter,data-scientist,intake-validator,scribe",
            ["full"] = "researcher,lead,developer,tester,challenger,architect,azure-architect,iac-author,deployer,asbuilt-author,azure-diagnose,security,supply-chain,vuln-manager,rai,privacy,accessibility,risk-manager,performance,observability,designer,fact-checker,cost-manager,modernizer,prompt-engineer,analyst,product-owner,presenter,technical-writer,experimenter,data-scientist,intake-validator,scribe",
        };
        foreach (var (profile, members) in expected)
        {
            using var host = new RuntimeTestHost();
            host.Options.Profile = profile;
            host.Client.Text("Only initialize the confirmed profile; no role work.");
            using var runtime = await SquadRuntime.CreateAsync(host.Client, host.Options, source);
            _ = await runtime.RunAsync("Inspect the initialized roster.");
            Assert.Equal(members.Split(','), runtime.Roster.Members.Select(member => member.Role));
            Assert.Equal(11, runtime.Catalog.Profiles.Count);
            foreach (var member in runtime.Roster.Members)
            {
                Assert.NotNull(runtime.Artifacts.FindCharter(member.PrimaryAgent));
                if (member.Role != "scribe" && !SupportedSquadRelease.Current.ReturnsOnly(member.Role))
                {
                    Assert.False(string.IsNullOrWhiteSpace(member.DeliverableRoot));
                }
            }
            Assert.All(Directory.EnumerateFiles(Path.Combine(host.Project, host.Options.SquadRoot, "history")),
                path => Assert.Contains(Path.GetFileName(path), InitializationHistoryNames));
            foreach (var (pack, roles) in new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["power-platform"] = "pp-architect,pp-connector",
                ["m365-copilot"] = "m365-agent-architect,m365-agent-integrator",
                ["aws"] = "aws-architect,aws-diagnose",
            })
            {
                var composed = runtime.Catalog.ResolveRoster("default", [pack]);
                Assert.Equal(expected["default"].Split(',').Concat(roles.Split(',')), composed.Select(member => member.Role));
            }
        }
    }

    [Fact]
    [Trait("Category", "ReleaseConformance")]
    public async Task GivenActualSupportedDistribution_WhenAuditing_PreservesNativeBindingsFloorBudgetsAndCanonicalReferences()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        Assert.Equal(SupportedSquadRelease.Current.Tag, version);
        using var client = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        var roots = await ReleaseTestHttpClient.Source(version, client).ResolveAsync();
        var artifacts = SquadArtifactLoader.Load(roots.Roots);
        Assert.Empty(artifacts.Warnings);
        Assert.All(artifacts.Charters, charter =>
        {
            MarkdownFrontmatter.Split(File.ReadAllText(charter.SourcePath), out _, out var body);
            Assert.NotEmpty(body);
            Assert.Equal(body.Trim(), charter.Instructions);
        });
        var floor = Assert.Single(artifacts.Instructions, instruction => instruction.FileName == "squad-floor.instructions.md");
        Assert.Equal("**", floor.ApplyTo);
        var squad = Assert.Single(artifacts.Skills, skill => skill.Name == "squad");
        var unresolved = new List<string>();
        foreach (var charter in artifacts.Charters.Where(charter => charter.Name.StartsWith("Squad ", StringComparison.Ordinal)))
        {
            MarkdownFrontmatter.Split(await File.ReadAllTextAsync(charter.SourcePath), out var yaml, out _);
            var parsed = new YamlStream();
            using var reader = new StringReader(yaml);
            parsed.Load(reader);
            var mapping = Assert.IsType<YamlMappingNode>(Assert.Single(parsed.Documents).RootNode);
            foreach (var key in new[] { "name", "description" })
            {
                Assert.True(mapping.Children.ContainsKey(new YamlScalarNode(key)), charter.Name + ": " + key);
            }
            foreach (var (key, expected, fallback) in new[]
            {
                ("user-invocable", charter.UserInvocable, true),
                ("disable-model-invocation", charter.DisableModelInvocation, false),
            })
            {
                if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var node))
                {
                    Assert.True(bool.TryParse(Assert.IsType<YamlScalarNode>(node).Value, out var value), charter.Name + ": " + key);
                    Assert.Equal(expected, value);
                }
                else
                {
                    Assert.Equal(fallback, expected);
                }
            }
            foreach (Match match in Regex.Matches(charter.Instructions, @"`(references/[A-Za-z0-9_./-]+\.md)`",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var providers = artifacts.Skills.Where(skill =>
                    (skill.Name == squad.Name || charter.Instructions.Contains("`" + skill.Name + "`", StringComparison.OrdinalIgnoreCase)) &&
                    File.Exists(new ProjectFiles(skill.DirectoryPath, 200_000).Resolve(match.Groups[1].Value))).ToArray();
                if (providers.Length == 0)
                {
                    unresolved.Add(charter.Name + ": " + match.Groups[1].Value);
                }
            }
        }
        Assert.Empty(unresolved);
        foreach (var name in new[] { "Squad Coordinator", "Squad Federation Coordinator", "Squad Document", "Squad Governance Report", "Squad Learn" })
        {
            var charter = artifacts.FindCharter(name);
            Assert.NotNull(charter);
            Assert.True(charter.UserInvocable);
            Assert.Equal(name is not ("Squad Document" or "Squad Governance Report"), charter.DisableModelInvocation);
        }
        var resources = new RuntimeResources(artifacts, roots);
        foreach (var reference in new[] { "00-index", "profiles-and-packs", "operating-procedure", "gates-and-modes", "seed-templates", "scribe-procedure", "entry-schemas" })
        {
            Assert.False(string.IsNullOrWhiteSpace(resources.Reference(reference)));
        }
    }

    [Theory]
    [InlineData(30_000)]
    [InlineData(30_001)]
    [InlineData(38_078)]
    public async Task GivenCharterBeyondCopilotBudget_WhenDispatching_PreservesTheWholeNativeMafInstructions(int characters)
    {
        using var host = new RuntimeTestHost();
        const string marker = "NATIVE_CHARTER_END_WITHOUT_TRUNCATION";
        var source = Path.Combine(host.Artifacts, "agents", "researcher.agent.md");
        File.AppendAllText(source, "\n" + new string('x', characters) + "\n" + marker);
        ConsumptionTestData.QueueResearch(host);
        var steps = host.Client.Steps.ToArray();
        host.Client.Steps.Clear();
        var seen = 0;
        foreach (var step in steps)
        {
            host.Client.Steps.Enqueue((messages, options) =>
            {
                if (options?.Instructions?.Contains("Synthetic agent for researcher.", StringComparison.Ordinal) == true)
                {
                    Assert.Contains(marker, options.Instructions, StringComparison.Ordinal);
                    Assert.Contains(new string('x', characters), options.Instructions, StringComparison.Ordinal);
                    seen++;
                }
                return step(messages, options);
            });
        }
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Inspect the bounded synthetic case.");

        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        Assert.True(seen > 0);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task GivenCurrentPlanContract_WhenDelivering_ValidatesOneCompletePlanWithoutLegacyDetails()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.QueueSuccess();
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded fixture.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var plan = Assert.Single(result.Evidence, e => e.Stage == "plan");
        Assert.Equal("primary", Assert.Single(plan.Artifacts!).Name);
        Assert.Contains("P01-T01", File.ReadAllText(Path.Combine(host.Project, plan.ArtifactPath)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("section")]
    [InlineData("context")]
    [InlineData("marker")]
    [InlineData("dependency")]
    [InlineData("placeholder")]
    [InlineData("fence")]
    [InlineData("readiness")]
    [InlineData("duplicate-section")]
    public async Task GivenIncompleteSinglePlan_WhenCompleting_BlocksBeforeProduction(string defect)
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.PlanTransform = plan => defect switch
        {
            "section" => plan.Replace("## Planning Readiness and Next Step", "## Wrong section", StringComparison.Ordinal),
            "context" => plan.Replace("Details:", "Missing:", StringComparison.Ordinal),
            "marker" => plan.Replace("<!-- rpi:task id=P01-T01 -->", "<!-- missing marker -->", StringComparison.Ordinal),
            "dependency" => plan.Replace("* None", "* P99-T01", StringComparison.Ordinal),
            "placeholder" => plan.Replace("native-fixture", "{{task_slug}}", StringComparison.Ordinal),
            "fence" => plan + "\n```markdown\nunfinished",
            "readiness" => plan.Replace("Ready for the approved implementation.", "Not Ready for implementation.", StringComparison.Ordinal),
            "duplicate-section" => plan + "\n## Handoff\nDuplicate handoff.",
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the bounded fixture.");

        Assert.True(result.Status is SquadRunStatus.Blocked or SquadRunStatus.Failed, result.ResponseText);
        Assert.Contains("Single-plan", result.ResponseText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Writes);
        Assert.DoesNotContain(result.Evidence, e => e.Stage == "produce");
    }

    [Fact]
    public void GivenSplitCatalog_WhenLoading_ProfilesRemainAuthoritativeInRosterInstruction()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        var roster = File.ReadAllText(path);
        var split = roster.IndexOf("## Squad Profiles", StringComparison.Ordinal);
        host.Write("skills/apm-renamed-squad/references/roster-catalog.md",
            roster[..split] + "\n## Squad Profiles\n| Profile | Members |\n| --- | --- |\n| ignored | developer |\n");
        host.Write("instructions/squad/squad-roster.instructions.md", roster[split..]);

        var catalog = SquadCatalog.Load(SquadArtifactLoader.Load(host.Artifacts));

        Assert.Equal(["default", "product", "full"], catalog.Profiles.Select(profile => profile.Name));
        Assert.Contains(catalog.Roles, r => r.Name == "developer");
        Assert.Equal("test-pack", Assert.Single(catalog.Packs).Name);
    }

    [Fact]
    public void GivenInvalidCanonicalCatalog_WhenInlineTableAlsoExists_DoesNotFallBackSilently()
    {
        using var host = new RuntimeTestHost();
        host.Write("skills/apm-renamed-squad/references/roster-catalog.md", "# Missing cast table");

        var error = Assert.Throws<InvalidDataException>(() =>
            SquadCatalog.Load(SquadArtifactLoader.Load(host.Artifacts)));

        Assert.Contains("Cast Catalog", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivenRegisteredMissingAgent_WhenResolving_ReturnsExactInstallAndPrerequisiteGuidance()
    {
        using var host = new RuntimeTestHost();
        var path = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        var roster = File.ReadAllText(path).Replace("Test developer", "Missing Registered Developer", StringComparison.Ordinal);
        host.Write("skills/apm-renamed-squad/references/roster-catalog.md", roster + """

            ## Registered External Cast
            | Role | Tier | Resource | Resource Kind | Origin | Install or Entry | License | Prerequisites | Dispatchability Verified | Verified On |
            | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
            | developer | opt-in | fixture-worker | agent | fixture/source | fixture install pinned-worker | MIT | approved fixture executor | yes | 2026-10-10 |
            """);
        var catalog = SquadCatalog.Load(SquadArtifactLoader.Load(host.Artifacts));

        var error = Assert.Throws<InvalidOperationException>(() =>
            catalog.ResolveAgent(catalog.ResolveRoster("default").Single(r => r.Role == "developer")));

        Assert.Contains("fixture install pinned-worker", error.Message, StringComparison.Ordinal);
        Assert.Contains("approved fixture executor", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenCurrentMethodologyWithoutFloor_WhenCreating_ReportsBrokenInstallation()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        File.Delete(Path.Combine(host.Artifacts, "instructions", "squad", "squad-floor.instructions.md"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => host.CreateAsync());

        Assert.Contains("squad-floor.instructions.md", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Client.Calls);
    }

    [Fact]
    public async Task GivenExplicitInstructionFilter_WhenDispatching_LoadsOnlyHostSelectedAdditionalBody()
    {
        using var host = new RuntimeTestHost();
        host.Options.InstructionFilter = (_, instruction) => instruction.FileName == "squad-new-rule.instructions.md";
        host.Client.Steps.Enqueue((messages, options) =>
        {
            var prompt = (options?.Instructions ?? "") + string.Join("\n", messages.Select(m => m.Text));
            Assert.Contains("SYNTHETIC_NEW_RULE", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("IRRELEVANT_RULE", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC_REFERENCE_gates-and-modes", prompt, StringComparison.Ordinal);
            return new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "No dispatch."));
        });
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Inspect only.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
    }
}
