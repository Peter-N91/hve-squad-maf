using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class HostingTests
{
    [Fact]
    public void AddHveSquadBindsSupportedScalarListAndDictionarySettings()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ProjectPath"] = "configured-project",
            ["Profile"] = "default",
            ["Packs:0"] = "test-pack",
            ["Version"] = "v0.16.2",
            ["NamingPolicy"] = "provided",
            ["MemberNames:developer"] = "Dana",
            ["ApprovalChannel"] = "in-chat",
            ["Mode"] = "interactive",
            ["InputPaths:0"] = "requirements.md",
            ["RequireCouncil"] = "true",
            ["IncludeRaiInCouncil"] = "true",
            ["MaxDispatches"] = "12",
            ["MaxDepth"] = "3",
            ["MaxModelCalls"] = "20",
            ["MaxToolCalls"] = "30",
            ["MaxArtifactCharacters"] = "4096",
            ["MaxArtifactsPerDispatch"] = "5",
            ["RunTimeout"] = "00:05:00",
            ["EnableOpenTelemetry"] = "true",
            ["OpenTelemetrySourceName"] = "sample.runtime",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        SquadRuntimeOptions? bound = null;

        _ = new ServiceCollection().AddHveSquad(configuration, options => bound = options);

        Assert.NotNull(bound);
        Assert.Equal("configured-project", bound.ProjectPath);
        Assert.Equal(["test-pack"], bound.Packs);
        Assert.Equal("v0.16.2", bound.Version);
        Assert.Equal("Dana", bound.MemberNames["developer"]);
        Assert.Equal(["requirements.md"], bound.InputPaths);
        Assert.True(bound.RequireCouncil);
        Assert.True(bound.IncludeRaiInCouncil);
        Assert.Equal(12, bound.MaxDispatches);
        Assert.Equal(3, bound.MaxDepth);
        Assert.Equal(20, bound.MaxModelCalls);
        Assert.Equal(30, bound.MaxToolCalls);
        Assert.Equal(4096, bound.MaxArtifactCharacters);
        Assert.Equal(5, bound.MaxArtifactsPerDispatch);
        Assert.Equal(TimeSpan.FromMinutes(5), bound.RunTimeout);
        Assert.True(bound.EnableOpenTelemetry);
        Assert.Equal("sample.runtime", bound.OpenTelemetrySourceName);
    }

    [Theory]
    [InlineData("ApproveAsync", "true", "host-only")]
    [InlineData("Tools:0:CharterPermission", "edit", "host-only")]
    [InlineData("Unexpected", "value", "unknown")]
    public void AddHveSquadRejectsUnknownAndHostObjectSettings(
        string key,
        string value,
        string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHveSquad(configuration));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key.Split(':')[0], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddHveSquadReportsMalformedValuesWithTheirConfigurationPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MaxDepth"] = "many" })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHveSquad(configuration));

        Assert.Contains("HVE Squad configuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxDepth", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsyncRequiresAConsumerRegisteredChatClientBeforeAcquisition()
    {
        var source = new CountingSource("unused");
        var services = new ServiceCollection();
        services.AddHveSquad(source: source);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<SquadRuntimeFactory>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync());

        Assert.Contains("register", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(IChatClient), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.Resolutions);
    }

    [Fact]
    public async Task SourceOverrideIsLazyUntilFactoryCreateAsync()
    {
        using var host = new RuntimeTestHost();
        var source = new CountingSource(host.Artifacts);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(host.Client);
        services.AddHveSquad(
            configureHost: options => options.ProjectPath = host.Project,
            source: source);
        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<SquadRuntimeFactory>();
        Assert.Equal(0, source.Resolutions);

        using var runtime = await factory.CreateAsync();

        Assert.Equal(1, source.Resolutions);
        Assert.Equal("injected test source", runtime.Provenance.Origin);
        Assert.Equal("v0.16.2", runtime.Provenance.Release?.Tag);
    }

    [Fact]
    public async Task FactoryCreatesIndependentRuntimesWithoutOwningTheChatClient()
    {
        using var host = new RuntimeTestHost();
        var source = new CountingSource(host.Artifacts);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(host.Client);
        services.AddHveSquad(
            configureHost: options =>
            {
                options.ProjectPath = host.Project;
                options.ApproveAsync = (_, _) => ValueTask.FromResult(false);
            },
            source: source);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<SquadRuntimeFactory>();
        var first = await factory.CreateAsync();
        using var second = await factory.CreateAsync();

        Assert.NotSame(first, second);
        Assert.Equal(2, source.Resolutions);
        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.RunAsync("after disposal"));

        var result = await second.RunAsync("independent runtime");

        Assert.Equal(SquadRunStatus.ApprovalDenied, result.Status);
        Assert.False(host.Client.Disposed);
    }

    private sealed class CountingSource(string root) : SquadArtifactSource
    {
        public int Resolutions { get; private set; }

        public override ValueTask<SquadArtifactRoots> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Resolutions++;
            return ValueTask.FromResult(new SquadArtifactRoots([root], null, "injected test source")
            {
                Release = new SquadRelease(ReleaseArtifactSource.TestedReleaseTag, new string('a', 40)),
            });
        }
    }
}
