using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Tests;

public class SquadCatalogTests
{
    [Fact]
    public void Load_ParsesProfilesPacksAndCast()
    {
        var catalog = SquadCatalog.Load(CreateArtifacts());

        Assert.Equal(["default", "full"], catalog.Profiles.Select(p => p.Name));
        Assert.Equal("power-platform", Assert.Single(catalog.Packs).Name);

        var roster = catalog.ResolveRoster("default");
        Assert.Equal(["developer", "scribe"], roster.Select(r => r.Role));
        Assert.Equal("Agent A", roster[0].PrimaryAgent);
        Assert.Equal(["Agent B"], roster[0].AlternateAgents);
        Assert.Equal("special work → Agent B", roster[0].SelectionCue);
        Assert.Equal("default", roster[0].ModelTier);
        Assert.Equal(".copilot-tracking/changes/", roster[0].DeliverableRoot);
    }

    [Fact]
    public void ResolveRoster_ComposesPackWithoutRequiringOptionalAgent()
    {
        var catalog = SquadCatalog.Load(CreateArtifacts());

        var roster = catalog.ResolveRoster("default", ["power-platform"]);

        Assert.Equal(["developer", "scribe", "pp-architect"], roster.Select(r => r.Role));
        var error = Assert.Throws<InvalidOperationException>(() => catalog.ValidateRoster(roster));
        Assert.Contains("not installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRoster_RejectsUnknownProfileAndPack()
    {
        var catalog = SquadCatalog.Load(CreateArtifacts());

        Assert.Throws<KeyNotFoundException>(() => catalog.ResolveRoster("mystery"));
        Assert.Throws<KeyNotFoundException>(() => catalog.ResolveRoster("default", ["mystery"]));
    }

    [Fact]
    public void ResolveAgent_DefaultsToPrimaryAndRequiresExplicitAlternate()
    {
        var catalog = SquadCatalog.Load(CreateArtifacts());
        var entry = catalog.ResolveRoster("default")[0];

        Assert.Equal("Agent A", catalog.ResolveAgent(entry).Name);
        Assert.Equal("Agent B", catalog.ResolveAgent(entry, "Agent B").Name);
        Assert.Throws<InvalidOperationException>(() => catalog.ResolveAgent(entry, "Agent C"));
    }

    [Fact]
    public void ValidateRoster_RejectsNonDispatchablePrimary()
    {
        var artifacts = CreateArtifacts();
        artifacts = CopyWithCharters(
            artifacts,
            artifacts.Charters.Select(c => c.Name == "Agent A"
                ? new AgentCharter
                {
                    Name = c.Name,
                    Instructions = c.Instructions,
                    SourcePath = c.SourcePath,
                    DisableModelInvocation = true,
                }
                : c).ToList());
        var catalog = SquadCatalog.Load(artifacts);

        var error = Assert.Throws<InvalidOperationException>(
            () => catalog.ValidateRoster(catalog.ResolveRoster("default")));

        Assert.Contains("disable-model-invocation: true", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAgent_RequiresOwnerWhenRoleRepeats()
    {
        var catalog = SquadCatalog.Load(CreateArtifacts());
        var roster = new SquadRoster
        {
            Members =
            [
                Entry("Alpha"),
                Entry("Beta"),
            ],
        };

        Assert.Throws<InvalidOperationException>(
            () => catalog.ResolveAgent(roster, "developer"));
        Assert.Equal("Agent A", catalog.ResolveAgent(roster, "developer", "Beta").Name);
    }

    [Fact]
    public void Load_FailsWhenProfileReferencesUndefinedRole()
    {
        var artifacts = CreateArtifacts(CatalogMarkdown.Replace(
            "developer, scribe",
            "developer, ghost",
            StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(() => SquadCatalog.Load(artifacts));

        Assert.Contains("undefined cast role", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_FailsActionablyForIncompatibleCatalogSchema()
    {
        var artifacts = CreateArtifacts(CatalogMarkdown.Replace(
            "Primary Agent (`name:`)",
            "Implementation",
            StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(() => SquadCatalog.Load(artifacts));

        Assert.Contains("schema may have changed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RequiresExactRosterInstructionUniqueness()
    {
        var artifacts = CreateArtifacts();
        artifacts = new SquadArtifacts
        {
            Charters = artifacts.Charters,
            Roster = artifacts.Roster,
            Instructions =
            [
                .. artifacts.Instructions,
                new SquadInstruction(
                    "squad-roster.instructions.md",
                    null,
                    null,
                    CatalogMarkdown,
                    "other/squad-roster.instructions.md"),
            ],
            Skills = artifacts.Skills,
            SkillDirectories = artifacts.SkillDirectories,
            Warnings = artifacts.Warnings,
        };

        Assert.Throws<InvalidDataException>(() => SquadCatalog.Load(artifacts));
    }

    [Fact]
    public void RealReleaseCatalogParsesWhenSourceRootIsProvided()
    {
        var root = Environment.GetEnvironmentVariable("HVE_SQUAD_SOURCE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var catalog = SquadCatalog.Load(SquadArtifactLoader.Load(root));

        Assert.Equal(11, catalog.Profiles.Count);
        Assert.Equal(3, catalog.Packs.Count);
        Assert.Contains(catalog.ResolveRoster("default"), r => r.Role == "scribe");
        Assert.All(
            catalog.ResolveRoster("default", ["aws"]),
            role =>
            {
                Assert.NotNull(role.Invocation);
                Assert.NotNull(role.ModelTier);
            });
    }

    private static SquadRosterEntry Entry(string owner) => new()
    {
        Role = "developer",
        MemberName = owner,
        PrimaryAgent = "Agent A",
    };

    private static SquadArtifacts CreateArtifacts(string? markdown = null) => new()
    {
        Charters =
        [
            Charter("Agent A"),
            Charter("Agent B"),
            Charter("Squad Scribe"),
        ],
        Roster = SquadRoster.Empty,
        Instructions =
        [
            new SquadInstruction(
                "squad-roster.instructions.md",
                "catalog",
                "**",
                markdown ?? CatalogMarkdown,
                "instructions/squad/squad-roster.instructions.md"),
        ],
        Skills = [],
        SkillDirectories = [],
        Warnings = [],
    };

    private static SquadArtifacts CopyWithCharters(
        SquadArtifacts source,
        IReadOnlyList<AgentCharter> charters) => new()
    {
        Charters = charters,
        Roster = source.Roster,
        Instructions = source.Instructions,
        Skills = source.Skills,
        SkillDirectories = source.SkillDirectories,
        Warnings = source.Warnings,
    };

    private static AgentCharter Charter(string name) => new()
    {
        Name = name,
        Instructions = "Instructions",
        SourcePath = $"{name}.agent.md",
    };

    private const string CatalogMarkdown = """
        # Squad Roster

        ## Cast Catalog

        | Role | Primary Agent (`name:`) | Alternate Agents (`name:`) | Selection Cue | Invocation | Model Tier | Deliverable Root |
        |------|-------------------------|----------------------------|---------------|------------|------------|------------------|
        | developer | Agent A | Agent B | special work → Agent B | runSubagent / task | default | .copilot-tracking/changes/ |
        | scribe | Squad Scribe | — | — | runSubagent / task | fast | (squad state) |
        | pp-architect | Power Platform Expert | — | external architecture | runSubagent / task | default | docs/architecture/ |

        ## Squad Profiles

        | Profile | Members (roles) | Choose when |
        |---------|-----------------|-------------|
        | `default` | developer, scribe | general |
        | `full` | developer, scribe | all bundled roles |

        #### Registered Packs

        | Pack | Adds (roles) | Choose when |
        |------|--------------|-------------|
        | `power-platform` | pp-architect | Power Platform |
        """;
}
