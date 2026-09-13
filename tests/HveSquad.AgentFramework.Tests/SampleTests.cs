using System.Text.Json;
using HveSquad.AgentFramework.Sample;

namespace HveSquad.AgentFramework.Tests;

public sealed class SampleTests
{
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
