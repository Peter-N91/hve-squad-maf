using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Tests;

/// <summary>
/// Loads the real hve-squad artifact tree when it is checked out next to this repository. These
/// tests are the loader half of the ADR-0001 confirmation step.
/// </summary>
public class RealArtifactTests
{
    /// <summary>
    /// Returns the deployed <c>.github</c> tree, which is the surface the Copilot host actually
    /// reads. It contains the squad charters merged with their installed HVE Core dependencies.
    /// </summary>
    private static string? FindDeployedRoot()
    {
        var repo = FindSquadRepo();
        if (repo is null)
        {
            return null;
        }

        var deployed = Path.Combine(repo, ".github");
        return Directory.Exists(Path.Combine(deployed, "agents")) ? deployed : null;
    }

    private static string? FindSquadRepo()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HVE_SQUAD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Directory.Exists(fromEnvironment) ? fromEnvironment : null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var sibling = Path.Combine(dir.FullName, "..", "hve-squad");
            if (Directory.Exists(Path.Combine(sibling, "squad-src")))
            {
                return Path.GetFullPath(sibling);
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void EveryCharterParses()
    {
        var root = FindDeployedRoot();
        if (root is null)
        {
            return; // hve-squad is not installed beside this repo.
        }

        var artifacts = SquadArtifactLoader.Load(root);

        Assert.Empty(artifacts.Warnings);
        Assert.NotEmpty(artifacts.Charters);
        Assert.All(artifacts.Charters, c => Assert.False(string.IsNullOrWhiteSpace(c.Instructions)));
    }

    [Fact]
    public void EveryDelegationEdgeResolvesInTheDeployedTree()
    {
        var root = FindDeployedRoot();
        if (root is null)
        {
            return;
        }

        var artifacts = SquadArtifactLoader.Load(root);

        Assert.Empty(artifacts.FindUnresolvedDelegations());
    }

    /// <summary>
    /// The squad source tree on its own is an incomplete graph: its charters delegate to HVE Core
    /// agents that arrive only through package install. Guarding this keeps callers from pointing
    /// the loader at <c>squad-src</c> and getting a silently broken workflow.
    /// </summary>
    [Fact]
    public void SourceTreeAloneHasUnresolvedDelegations()
    {
        var repo = FindSquadRepo();
        if (repo is null)
        {
            return;
        }

        var artifacts = SquadArtifactLoader.Load(Path.Combine(repo, "squad-src", ".github"));

        Assert.NotEmpty(artifacts.FindUnresolvedDelegations());
    }

    /// <summary>
    /// Asserts precedence only. Whether the merged graph fully resolves depends on how current the
    /// local deploy is relative to <c>squad-src</c>, which is environment state, not an invariant.
    /// </summary>
    [Fact]
    public void SourceTreeTakesPrecedenceOverDeployedTree()
    {
        var repo = FindSquadRepo();
        var deployed = FindDeployedRoot();
        if (repo is null || deployed is null)
        {
            return;
        }

        var source = Path.Combine(repo, "squad-src", ".github");
        var artifacts = SquadArtifactLoader.Load([source, deployed]);

        var coordinator = artifacts.FindCharter("Squad Coordinator");

        Assert.NotNull(coordinator);
        Assert.StartsWith(source, coordinator.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeployedTreeIsFlatAndSourceTreeIsNested()
    {
        var repo = FindSquadRepo();
        var deployed = FindDeployedRoot();
        if (repo is null || deployed is null)
        {
            return;
        }

        var fromSource = SquadArtifactLoader.Load(Path.Combine(repo, "squad-src", ".github"));
        var fromDeployed = SquadArtifactLoader.Load(deployed);

        var sourceCoordinator = fromSource.FindCharter("Squad Coordinator");
        var deployedCoordinator = fromDeployed.FindCharter("Squad Coordinator");

        Assert.NotNull(sourceCoordinator);
        Assert.NotNull(deployedCoordinator);
        Assert.Equal("squad", ParentDirectoryName(sourceCoordinator.SourcePath));
        Assert.Equal("agents", ParentDirectoryName(deployedCoordinator.SourcePath));
    }

    private static string ParentDirectoryName(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path))!;

    [Fact]
    public void SkillDirectoriesAreDiscovered()
    {
        var repo = FindSquadRepo();
        if (repo is null)
        {
            return;
        }

        // Skills deploy to `.agents/skills`, not `.github`, so they need their own root.
        var skillRoot = Path.Combine(repo, ".agents");
        if (!Directory.Exists(skillRoot))
        {
            return;
        }

        var artifacts = SquadArtifactLoader.Load(skillRoot);

        Assert.NotEmpty(artifacts.SkillDirectories);
        Assert.All(artifacts.SkillDirectories, d => Assert.True(File.Exists(Path.Combine(d, "SKILL.md"))));
    }

    [Fact]
    public void CharterWithoutNameFieldFallsBackToFileName()
    {
        var root = FindDeployedRoot();
        if (root is null)
        {
            return;
        }

        var artifacts = SquadArtifactLoader.Load(root);

        Assert.NotNull(artifacts.FindCharter("agentic-workflows"));
    }
}
