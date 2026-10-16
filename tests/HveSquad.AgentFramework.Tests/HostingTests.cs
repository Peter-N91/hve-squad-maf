using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class HostingTests
{
    [Theory]
    [InlineData("Tools:0:SkillScript:ResourcePath", "scripts/injected.ps1")]
    [InlineData("Tools:0:ProjectReadPaths", "private")]
    [InlineData("ExecutorPath", "pwsh")]
    [InlineData("ProcessArguments:0", "injected")]
    public void GivenProcessAuthorityInDataConfiguration_WhenBinding_RejectsIt(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHveSquad(configuration));
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public void GivenExecutionModeSettings_WhenBinding_PreservesTheImplementedMode(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Mode"] = mode }).Build();
        SquadRuntimeOptions? bound = null;

        _ = new ServiceCollection().AddHveSquad(configuration, options => bound = options);

        Assert.NotNull(bound);
        Assert.Equal(mode, bound.Mode);
    }

    [Fact]
    public async Task GivenConsumptionData_WhenBinding_ValidatesAndDefensivelyCopiesRatesAndCalibration()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host);
        var values = new Dictionary<string, string?>
        {
            ["CostCeiling"] = "10.0000",
            ["Consumption:ObservedOn"] = "2026-10-11",
            ["Consumption:Source"] = "original-host-tariff",
            ["Consumption:EstimatorRevision"] = "2",
            ["Consumption:Models:model-fixed:FlatRateConfirmed"] = "true",
            ["Consumption:Models:model-fixed:Standard:Input"] = "3",
            ["Consumption:Models:model-fixed:Standard:Cached"] = "0.3",
            ["Consumption:Models:model-fixed:Standard:CacheWrite"] = "3.75",
            ["Consumption:Models:model-fixed:Standard:Output"] = "15",
            ["Consumption:Calibration:Factor"] = "1.2",
            ["Consumption:Calibration:Observations"] = "1",
            ["Consumption:Calibration:LastReconciled"] = "2026-10-11",
            ["Consumption:Calibration:Basis"] = "2026-10-11|2",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        SquadRuntimeOptions? bound = null;
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(host.Client);
        services.AddHveSquad(configuration, options =>
        {
            options.ProjectPath = host.Project;
            options.TimeProvider = host.Options.TimeProvider;
            options.ApproveAsync = host.Options.ApproveAsync;
            options.ModelSelector = host.Options.ModelSelector;
            bound = options;
        }, new DirectoryArtifactSource([host.Artifacts]));

        Assert.NotNull(bound);
        Assert.Equal("10.0000", bound.CostCeiling);
        Assert.Equal(1.2m, bound.Consumption!.Calibration.Factor);
        Assert.Equal(3.75m, bound.Consumption.Models["model-fixed"].Standard.CacheWrite);
        bound.Consumption.Models["model-fixed"].Standard.Input = 100;
        bound.Consumption.Calibration.Factor = 9;
        ConsumptionTestData.QueueResearch(host);
        using var provider = services.BuildServiceProvider();
        using var runtime = await provider.GetRequiredService<SquadRuntimeFactory>().CreateAsync();
        var result = await runtime.RunAsync("Research the bounded configured case.");
        Assert.True(result.Status == SquadRunStatus.ResearchCompleted, result.ResponseText);
        Assert.Equal(1.397952m, result.CostPreflight!.Demand.Single(row => row.Slot == "research:researcher").ProjectedCostUsd);
    }

    [Theory]
    [InlineData("Consumption:TokenUsageSelector", "delegate")]
    [InlineData("Consumption:Models:model-fixed:Execute", "delegate")]
    [InlineData("Consumption:Calibration:Secret", "not-a-setting")]
    public void GivenUnknownOrHostOnlyConsumptionField_WhenBinding_RejectsIt(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHveSquad(configuration));
    }

    [Fact]
    public void GivenIncompleteTariff_WhenBinding_DoesNotDefaultMissingRatesToZero()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Consumption:Models:model-fixed:Standard:Input"] = "1",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHveSquad(configuration));
    }

    [Fact]
    public void AddHveSquadBindsSupportedScalarListAndDictionarySettings()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ProjectPath"] = "configured-project",
            ["SquadRoot"] = ".copilot-tracking/squad/members/product",
            ["AllowNativeV1Migration"] = "true",
            ["DeliverableRoots:developer"] = "outputs/approved-source",
            ["Profile"] = "default",
            ["Packs:0"] = "test-pack",
            ["Roles:0"] = "researcher",
            ["AdditionalRoles:0"] = "intake-validator",
            ["InitializationScope"] = "single-squad",
            ["Notifications:ApprovalChannel"] = "github-issue",
            ["Notifications:Enabled"] = "true",
            ["Notifications:GitHubHandle"] = "fixture-reviewer",
            ["Notifications:GitHubRepository"] = "fixture/review",
            ["Notifications:Email"] = "reviewer@example.com",
            ["ModelRouting:Mode"] = "manual",
            ["ModelRouting:Models:researcher"] = "model-fixture",
            ["Version"] = "v0.16.2",
            ["NamingPolicy"] = "provided",
            ["MemberNames:developer"] = "Dana",
            ["ApprovalChannel"] = "in-chat",
            ["Mode"] = "interactive",
            ["InputPaths:0"] = "requirements.md",
            ["RequireCouncil"] = "true",
            ["IncludeRaiInCouncil"] = "true",
            ["Discovery"] = "deep",
            ["TopicId"] = "configured-topic",
            ["HumanPresent"] = "false",
            ["Council:Roles:0"] = "security",
            ["Council:WaiverReason"] = "An explicitly scoped local council.",
            ["Council:OverrideReason"] = "Accept a measured low-risk finding.",
            ["MaxDispatches"] = "12",
            ["MaxConcurrentDispatches"] = "2",
            ["MaxDepth"] = "3",
            ["MaxModelCalls"] = "20",
            ["MaxToolCalls"] = "30",
            ["MaxArtifactCharacters"] = "4096",
            ["MaxArtifactsPerDispatch"] = "5",
            ["RunTimeout"] = "00:05:00",
            ["HumanResponseTimeout"] = "02:00:00",
            ["EnableOpenTelemetry"] = "true",
            ["OpenTelemetrySourceName"] = "sample.runtime",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        SquadRuntimeOptions? bound = null;

        _ = new ServiceCollection().AddHveSquad(configuration, options => bound = options);

        Assert.NotNull(bound);
        Assert.Equal("configured-project", bound.ProjectPath);
        Assert.Equal(".copilot-tracking/squad/members/product", bound.SquadRoot);
        Assert.True(bound.AllowNativeV1Migration);
        Assert.Equal("outputs/approved-source", bound.DeliverableRoots["developer"]);
        Assert.Equal(["test-pack"], bound.Packs);
        Assert.Equal(["researcher"], bound.Roles);
        Assert.Equal(["intake-validator"], bound.AdditionalRoles);
        Assert.Equal("single-squad", bound.InitializationScope);
        Assert.True(bound.Notifications.Enabled);
        Assert.Equal("reviewer@example.com", bound.Notifications.Email);
        Assert.Equal("manual", bound.ModelRouting!.Mode);
        Assert.Equal("model-fixture", bound.ModelRouting.Models["researcher"]);
        Assert.Equal("v0.16.2", bound.Version);
        Assert.Equal("Dana", bound.MemberNames["developer"]);
        Assert.Equal(["requirements.md"], bound.InputPaths);
        Assert.True(bound.RequireCouncil);
        Assert.True(bound.IncludeRaiInCouncil);
        Assert.Equal("deep", bound.Discovery);
        Assert.Equal("configured-topic", bound.TopicId);
        Assert.False(bound.HumanPresent);
        Assert.Equal(["security"], bound.Council!.Roles);
        Assert.Equal("An explicitly scoped local council.", bound.Council.WaiverReason);
        Assert.Equal("Accept a measured low-risk finding.", bound.Council.OverrideReason);
        Assert.Equal(12, bound.MaxDispatches);
        Assert.Equal(2, bound.MaxConcurrentDispatches);
        Assert.Equal(3, bound.MaxDepth);
        Assert.Equal(20, bound.MaxModelCalls);
        Assert.Equal(30, bound.MaxToolCalls);
        Assert.Equal(TimeSpan.FromHours(2), bound.HumanResponseTimeout);
        Assert.Equal(4096, bound.MaxArtifactCharacters);
        Assert.Equal(5, bound.MaxArtifactsPerDispatch);
        Assert.Equal(TimeSpan.FromMinutes(5), bound.RunTimeout);
        Assert.True(bound.EnableOpenTelemetry);
        Assert.Equal("sample.runtime", bound.OpenTelemetrySourceName);
    }

    [Theory]
    [InlineData("ApproveAsync", "true", "host-only")]
    [InlineData("SelectCouncilAsync", "true", "host-only")]
    [InlineData("Council:Authorize", "true", "unknown")]
    [InlineData("Tools:0:CharterPermission", "edit", "host-only")]
    [InlineData("Unexpected", "value", "unknown")]
    [InlineData("AvailableModelsAsync", "value", "host-only")]
    [InlineData("AgentSelector", "value", "host-only")]
    [InlineData("TimeProvider", "value", "host-only")]
    [InlineData("Memory:AuthorizeAsync", "value", "host-only")]
    [InlineData("Reports:Renderers:0", "value", "host-only")]
    [InlineData("Learning:Connector", "value", "host-only")]
    [InlineData("HumanApprovalChannel", "value", "host-only")]
    [InlineData("HumanNotifier", "value", "host-only")]
    [InlineData("Source", "value", "host-only")]
    [InlineData("ModelRouting:Unexpected", "value", "unknown")]
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
    public async Task GivenPublicHelperFactoriesWithoutClient_WhenReadingAnAuthorizedCase_PreserveLazyAcquisitionAndStateBytes()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        ConsumptionTestData.QueueResearch(host);
        using (var runtime = await host.CreateAsync())
        {
            Assert.Equal(SquadRunStatus.ResearchCompleted, (await runtime.RunAsync("Inspect the bounded fixture.")).Status);
        }
        var statePath = Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json");
        var original = await File.ReadAllBytesAsync(statePath);
        var source = new CountingSource(host.Artifacts);
        var services = new ServiceCollection();
        services.AddHveSquad(configureHost: options => options.ProjectPath = host.Project, source: source);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<SquadRuntimeFactory>();

        var reports = factory.CreateReports(new() { AuthorizeAsync = (_, _) => ValueTask.FromResult(true) });
        Assert.True(File.Exists((await reports.GovernanceAsync()).Path));
        Assert.Empty(await factory.CreateMemory(new()
        {
            Binding = "helper-factory-memory",
            AuthorizeAsync = (_, _) => ValueTask.FromResult(true),
        }).ReadAsync("researcher"));
        var learning = factory.CreateLearning(new() { Binding = "denied-human-learning" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => learning.DiscoverAsync(new("unverified-session")));

        Assert.Equal(0, source.Resolutions);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateFederationAsync());
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
                Release = new SquadRelease("v0.16.2", new string('a', 40)),
            });
        }
    }
}
