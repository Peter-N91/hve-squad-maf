using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Tests;

public class ArtifactSourceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "hve-squad-maf-tests", Guid.NewGuid().ToString("n"));

    private string CreateInstalledProject()
    {
        var project = Path.Combine(_temp, "project");
        var agents = Path.Combine(project, ".github", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, "sample.agent.md"), "---\nname: Sample\n---\n\nBody.\n");
        return project;
    }

    [Fact]
    public async Task ProjectSource_FindsRootByWalkingUp()
    {
        var project = CreateInstalledProject();
        var nested = Path.Combine(project, "src", "deep", "bin");
        Directory.CreateDirectory(nested);

        var resolved = await new ProjectArtifactSource(nested).ResolveAsync();

        Assert.Equal(Path.Combine(project, ".github"), resolved.Roots[0]);
        Assert.Null(resolved.RosterPath);
        Assert.Contains(project, resolved.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectSource_IncludesSkillRootWhenPresent()
    {
        var project = CreateInstalledProject();
        Directory.CreateDirectory(Path.Combine(project, ".agents", "skills"));

        var resolved = await new ProjectArtifactSource(project).ResolveAsync();

        Assert.Equal(2, resolved.Roots.Count);
        Assert.Equal(Path.Combine(project, ".agents"), resolved.Roots[1]);
    }

    [Fact]
    public async Task ProjectSource_FindsRosterWhenSquadHasRun()
    {
        var project = CreateInstalledProject();
        var squadState = Path.Combine(project, ".copilot-tracking", "squad");
        Directory.CreateDirectory(squadState);
        File.WriteAllText(Path.Combine(squadState, "team.md"), "## Members\n");

        var resolved = await new ProjectArtifactSource(project).ResolveAsync();

        Assert.NotNull(resolved.RosterPath);
    }

    [Fact]
    public async Task ProjectSource_ThrowsActionableErrorWhenNotInstalled()
    {
        var empty = Path.Combine(_temp, "empty");
        Directory.CreateDirectory(empty);

        var ex = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await new ProjectArtifactSource(empty).ResolveAsync());

        Assert.Contains("apm install", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Peter-N91/hve-squad")]                       // unpinned
    [InlineData("hve-squad#v1.0.0")]                          // no owner
    [InlineData("--upload-pack=calc#v1")]                     // argument injection
    [InlineData("Peter-N91/hve-squad#v1 && calc")]            // command chaining
    [InlineData("Peter-N91/hve-squad#$(whoami)")]             // substitution
    public void ApmSource_RejectsUnsafeOrUnpinnedSpecs(string spec) =>
        Assert.Throws<ArgumentException>(() => new ApmArtifactSource(spec));

    [Fact]
    public void ApmSource_RejectsUnsafeTarget() =>
        Assert.Throws<ArgumentException>(() => new ApmArtifactSource("Peter-N91/hve-squad#v1.0.0", "--exec=calc"));

    [Fact]
    public void ApmSource_CacheDirectoryIsStableAndSpecific()
    {
        var a = new ApmArtifactSource("Peter-N91/hve-squad#v0.12.7").CacheDirectory;
        var b = new ApmArtifactSource("Peter-N91/hve-squad#v0.12.7").CacheDirectory;
        var c = new ApmArtifactSource("Peter-N91/hve-squad#v0.12.8").CacheDirectory;

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task CompositeSource_PreservesOrder()
    {
        var first = new DirectoryArtifactSource(["/one"], "/one/team.md");
        var second = new DirectoryArtifactSource(["/two"], "/two/team.md");

        var resolved = await new CompositeArtifactSource(first, second) .ResolveAsync();

        Assert.Equal(["/one", "/two"], resolved.Roots);
        Assert.Equal("/one/team.md", resolved.RosterPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

