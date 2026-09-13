using System.Diagnostics;
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
    [InlineData("Peter-N91/hve-squad#main")]                  // moving branch
    [InlineData("Peter-N91/hve-squad#latest")]                // moving alias
    [InlineData("Peter-N91/hve-squad#0123456789abcdef")]      // abbreviated SHA
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
    public void ApmSource_CacheDirectoryIsSpecificToSourceAndTarget()
    {
        var root = Path.Combine(_temp, "cache");
        var sourceA = new ApmArtifactSource("Peter-N91/hve-squad#v0.16.2", "copilot", root);
        var sourceB = new ApmArtifactSource("Peter-N91/hve-squad#v0.16.3", "copilot", root);
        var targetB = new ApmArtifactSource("Peter-N91/hve-squad#v0.16.2", "agent-skills", root);

        Assert.NotEqual(sourceA.CacheDirectory, sourceB.CacheDirectory);
        Assert.NotEqual(sourceA.CacheDirectory, targetB.CacheDirectory);
        Assert.StartsWith(Path.GetFullPath(root), sourceA.CacheDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApmSource_DoesNotTrustArtifactTreeWithoutMatchingManifest()
    {
        var installs = 0;
        var source = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: Path.Combine(_temp, "cache"),
            installer: (request, _) =>
            {
                installs++;
                CreateCompleteInstallation(request.WorkingDirectory);
                return ValueTask.CompletedTask;
            });
        Directory.CreateDirectory(Path.Combine(source.CacheDirectory, ".github", "agents"));
        File.WriteAllText(
            Path.Combine(source.CacheDirectory, ".github", "agents", "untrusted.agent.md"),
            "untrusted");

        await source.ResolveAsync();
        var manifest = await File.ReadAllTextAsync(source.CacheManifestPath);

        Assert.Equal(1, installs);
        Assert.Contains("Peter-N91/hve-squad#v0.16.2", manifest, StringComparison.Ordinal);
        Assert.Contains("copilot", manifest, StringComparison.Ordinal);
        Assert.False(File.Exists(
            Path.Combine(source.CacheDirectory, ".github", "agents", "untrusted.agent.md")));
    }

    [Fact]
    public async Task ApmSource_ManifestInventoriesDeployedArtifactsOnly()
    {
        var source = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: Path.Combine(_temp, "cache"),
            installer: (request, _) =>
            {
                CreateCompleteInstallation(request.WorkingDirectory);
                return ValueTask.CompletedTask;
            });

        await source.ResolveAsync();
        var manifest = await File.ReadAllTextAsync(source.CacheManifestPath);

        Assert.Contains("\"FormatVersion\":2", manifest, StringComparison.Ordinal);
        Assert.Contains(".github/agents/sample.agent.md", manifest, StringComparison.Ordinal);
        Assert.Contains(".github/instructions/sample.instructions.md", manifest, StringComparison.Ordinal);
        Assert.Contains(".agents/skills/sample/references/reference.md", manifest, StringComparison.Ordinal);
        Assert.Contains(".agents/skills/sample/scripts/run.ps1", manifest, StringComparison.Ordinal);
        Assert.Contains(".agents/skills/sample/resources/template.txt", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("apm_modules", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(".copilot-tracking", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApmSource_ReacquiresWhenCachedCharterContentChanges()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        var charter = Path.Combine(source.CacheDirectory, ".github", "agents", "sample.agent.md");
        await File.WriteAllTextAsync(charter, "other");

        await source.ResolveAsync();

        Assert.Equal(2, installs.Value);
        Assert.Equal("agent", await File.ReadAllTextAsync(charter));
    }

    [Fact]
    public async Task ApmSource_ReacquiresWhenCachedSkillContentChanges()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        var skill = Path.Combine(source.CacheDirectory, ".agents", "skills", "sample", "SKILL.md");
        await File.WriteAllTextAsync(skill, "other");

        await source.ResolveAsync();

        Assert.Equal(2, installs.Value);
        Assert.Equal("skill", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task ApmSource_ReacquiresWhenCachedSkillReferenceIsMissing()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        var reference = Path.Combine(
            source.CacheDirectory,
            ".agents",
            "skills",
            "sample",
            "references",
            "reference.md");
        File.Delete(reference);

        await source.ResolveAsync();

        Assert.Equal(2, installs.Value);
        Assert.True(File.Exists(reference));
    }

    [Fact]
    public async Task ApmSource_ReacquiresWhenCachedArtifactHasExtraFile()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        var extra = Path.Combine(
            source.CacheDirectory,
            ".agents",
            "skills",
            "sample",
            "resources",
            "injected.txt");
        await File.WriteAllTextAsync(extra, "injected");

        await source.ResolveAsync();

        Assert.Equal(2, installs.Value);
        Assert.False(File.Exists(extra));
    }

    [Fact]
    public async Task ApmSource_ReacquiresWhenManifestIsCorruptOrLegacy()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        await File.WriteAllTextAsync(source.CacheManifestPath, "{not-json");
        await source.ResolveAsync();
        await File.WriteAllTextAsync(
            source.CacheManifestPath,
            """
            {"FormatVersion":1,"PackageSpec":"Peter-N91/hve-squad#v0.16.2","Target":"copilot"}
            """);

        await source.ResolveAsync();

        Assert.Equal(3, installs.Value);
        Assert.Contains(
            "\"FormatVersion\":2",
            await File.ReadAllTextAsync(source.CacheManifestPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApmSource_ReacquiresCacheContainingSymlinkWithoutFollowingIt()
    {
        var installs = new InstallCounter();
        var source = CreateCountingApmSource(installs);
        await source.ResolveAsync();
        var unrelated = Path.Combine(_temp, "unrelated");
        Directory.CreateDirectory(unrelated);
        var unrelatedFile = Path.Combine(unrelated, "secret.txt");
        await File.WriteAllTextAsync(unrelatedFile, "do not change");
        var link = Path.Combine(
            source.CacheDirectory,
            ".agents",
            "skills",
            "sample",
            "resources",
            "linked");
        CreateDirectoryLink(link, unrelated);

        await source.ResolveAsync();

        Assert.Equal(2, installs.Value);
        Assert.False(Directory.Exists(link));
        Assert.Equal("do not change", await File.ReadAllTextAsync(unrelatedFile));
    }

    [Fact]
    public async Task ApmSource_RejectsSymlinkProducedByAcquisition()
    {
        var unrelated = Path.Combine(_temp, "unrelated");
        Directory.CreateDirectory(unrelated);
        var unrelatedFile = Path.Combine(unrelated, "secret.txt");
        await File.WriteAllTextAsync(unrelatedFile, "do not change");
        var source = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: Path.Combine(_temp, "cache"),
            installer: (request, _) =>
            {
                CreateCompleteInstallation(request.WorkingDirectory);
                CreateDirectoryLink(
                    Path.Combine(
                        request.WorkingDirectory,
                        ".agents",
                        "skills",
                        "sample",
                        "resources",
                        "linked"),
                    unrelated);
                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains("unsafe", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(source.CacheDirectory));
        Assert.Equal("do not change", await File.ReadAllTextAsync(unrelatedFile));
    }

    [Fact]
    public async Task ApmSource_ConcurrentSourcesPublishOnceUnderProcessLock()
    {
        var installs = 0;
        var cacheRoot = Path.Combine(_temp, "cache");
        ApmInstaller installer = async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref installs);
            await Task.Delay(30, cancellationToken);
            CreateCompleteInstallation(request.WorkingDirectory);
        };
        var first = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: cacheRoot,
            installer: installer);
        var second = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: cacheRoot,
            installer: installer);

        var results = await Task.WhenAll(
            first.ResolveAsync().AsTask(),
            second.ResolveAsync().AsTask());

        Assert.Equal(1, installs);
        Assert.Equal(results[0].Roots, results[1].Roots);
    }

    [Fact]
    public async Task ApmSource_CancellationCleansStagingAndAllowsRetry()
    {
        var attempts = 0;
        var cacheRoot = Path.Combine(_temp, "cache");
        var source = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: cacheRoot,
            installer: async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, ".github"));
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                CreateCompleteInstallation(request.WorkingDirectory);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await source.ResolveAsync(cancellation.Token));
        var resolved = await source.ResolveAsync();

        Assert.Equal(2, attempts);
        Assert.True(File.Exists(source.CacheManifestPath));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(cacheRoot),
            path => path.Contains(".staging-", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(source.CacheDirectory, ".agents"), resolved.Roots);
    }

    [Fact]
    public async Task ApmSource_InstallerFailureIsExplicitAndLeavesNoCache()
    {
        var source = new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: Path.Combine(_temp, "cache"),
            installer: (_, _) => ValueTask.FromException(new InvalidOperationException("installer failed")));

        var exception = await Assert.ThrowsAsync<SquadArtifactsNotFoundException>(
            async () => await source.ResolveAsync());

        Assert.Contains("acquisition", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.False(Directory.Exists(source.CacheDirectory));
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

    private static void CreateCompleteInstallation(string root)
    {
        var agents = Path.Combine(root, ".github", "agents");
        var instructions = Path.Combine(root, ".github", "instructions");
        var skills = Path.Combine(root, ".agents", "skills", "sample");
        var references = Path.Combine(skills, "references");
        var scripts = Path.Combine(skills, "scripts");
        var resources = Path.Combine(skills, "resources");
        var dependency = Path.Combine(skills, "apm_modules", "dependency");
        var userState = Path.Combine(root, ".copilot-tracking", "squad");
        Directory.CreateDirectory(agents);
        Directory.CreateDirectory(instructions);
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(references);
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(resources);
        Directory.CreateDirectory(dependency);
        Directory.CreateDirectory(userState);
        File.WriteAllText(Path.Combine(agents, "sample.agent.md"), "agent");
        File.WriteAllText(Path.Combine(instructions, "sample.instructions.md"), "instructions");
        File.WriteAllText(Path.Combine(skills, "SKILL.md"), "skill");
        File.WriteAllText(Path.Combine(references, "reference.md"), "reference");
        File.WriteAllText(Path.Combine(scripts, "run.ps1"), "script");
        File.WriteAllText(Path.Combine(resources, "template.txt"), "resource");
        File.WriteAllText(Path.Combine(dependency, "dependency.txt"), "excluded dependency");
        File.WriteAllText(Path.Combine(userState, "state.json"), "excluded user state");
    }

    private ApmArtifactSource CreateCountingApmSource(InstallCounter installs)
    {
        return new ApmArtifactSource(
            "Peter-N91/hve-squad#v0.16.2",
            cacheDirectory: Path.Combine(_temp, "cache"),
            installer: (request, _) =>
            {
                installs.Value++;
                CreateCompleteInstallation(request.WorkingDirectory);
                return ValueTask.CompletedTask;
            });
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-NonInteractive",
                     "-Command",
                     $"New-Item -ItemType Junction -Path '{link.Replace("'", "''", StringComparison.Ordinal)}' -Target '{target.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class InstallCounter
    {
        public int Value { get; set; }
    }
}
