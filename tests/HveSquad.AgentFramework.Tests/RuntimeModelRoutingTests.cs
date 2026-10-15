using System.Globalization;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeModelRoutingTests
{
    private static readonly string[] ModelRoles =
        ["researcher", "lead", "developer", "tester", "technical-writer", "architect", "security",
         "cost-manager", "product-owner", "rai", "intake-validator", "iac-author", "deployer", "asbuilt-author"];

    private static readonly ModelRow[] Rows =
    [
        new("model-older", "line", "1", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0]),
        new("model-newer", "line", "2", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0]),
        new("model-frontier", "reason", "1", "frontier-reasoning", 3m, [3, 3, 2, 3, 3, 2, 0]),
        new("model-light", "light", "1", "fast-lightweight", 0.1m, [1, 0, 1, 1, 0, 1, 3]),
        new("model-coder", "code", "1", "code-specialized", 0.5m, [1, 1, 3, 2, 0, 1, 0]),
    ];

    private static (string Catalog, string Routing) Metadata(DateOnly retrieved, IReadOnlyList<ModelRow>? rows = null)
    {
        rows ??= Rows;
        var catalog = new StringBuilder("**Retrieved: " + retrieved.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "**\n");
        catalog.AppendLine("## Declared capabilities");
        catalog.AppendLine("| Catalog ID | Display name | Capability class |");
        catalog.AppendLine("| --- | --- | --- |");
        foreach (var row in rows)
        {
            catalog.AppendLine(CultureInfo.InvariantCulture, $"| `{row.Id}` | Original {row.Id} | `{row.Capability}` |");
        }
        catalog.AppendLine("\n## Catalog: Assignment Fit");
        catalog.AppendLine("| Catalog ID | Family | Generation | research | planning | implementation | review | council | intake | bookkeeping | Blended |");
        catalog.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            catalog.AppendLine($"| `{row.Id}` | {row.Family} | {row.Generation} | " +
                string.Join(" | ", row.Scores.Select(score => score.ToString(CultureInfo.InvariantCulture))) +
                " | " + row.Blended.ToString(CultureInfo.InvariantCulture) + " |");
        }
        const string routing = """
            ## Assignment Classes
            | Class | Roles |
            | --- | --- |
            | `research` | `researcher` |
            | `planning` | `lead` |
            | `implementation` | `developer`, `technical-writer`, `product-owner`, `iac-author`, `deployer`, `asbuilt-author` |
            | `review` | `tester` |
            | `council` | `architect`, `security`, `cost-manager`, `rai`, and `product-owner` when dispatched in a council batch |
            | `intake` | `intake-validator` |
            | `bookkeeping` | `scribe` |
            """;
        return (catalog.ToString(), routing);
    }

    private static void UseModels(RuntimeTestHost host, IReadOnlyList<ModelRow>? rows = null, int age = 0)
    {
        var asOf = new DateOnly(2026, 10, 11);
        var metadata = Metadata(asOf.AddDays(-age), rows);
        host.Write("skills/apm-renamed-squad/references/model-catalog.md", metadata.Catalog);
        host.Write("skills/apm-renamed-squad/references/model-routing.md", metadata.Routing);
        host.Options.TimeProvider = new FixedClock(asOf);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            (rows ?? Rows).Select(row => new SquadAvailableModel(row.Id, host.Client)).ToArray());
    }

    private static SquadModelRoutingConfiguration Manual(string? missing = null) => new()
    {
        Mode = "manual",
        Models = ModelRoles.Where(role => role != missing).ToDictionary(role => role, _ => "model-newer"),
    };

    private static void SetFloor(RuntimeTestHost host, string role, string floor)
    {
        var path = Path.Combine(host.Artifacts, "skills", "apm-renamed-squad", "references", "seed-templates.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace($"| {role} | {RuntimeTestHost.Agent(role)} | fast |",
            $"| {role} | {RuntimeTestHost.Agent(role)} | {floor} |", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(91, false)]
    [InlineData(-1, false)]
    public async Task GivenDatedCatalog_WhenBuildingChoices_UsesExactNinetyDayBoundary(int age, bool current)
    {
        using var host = new RuntimeTestHost();
        UseModels(host, age: age);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        using var runtime = await host.CreateAsync();

        var choices = await runtime.GetModelRoutingChoicesAsync();

        Assert.Equal(current, choices.Diagnostics.Count == 0);
        Assert.Equal(current, choices.Roles.Single(role => role.Role == "researcher").Candidates.Count > 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public void GivenEqualFitAndCost_WhenRanking_PrefersSameFamilyGenerationNotIdSpelling()
    {
        var rows = new[]
        {
            new ModelRow("z-old", "family", "1", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0]),
            new ModelRow("a-new", "family", "2", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0]),
            new ModelRow("a-other", "another-family", "9", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0]),
        };
        var metadata = Metadata(new(2026, 10, 11), rows);
        var catalog = SquadModelCatalog.Parse(metadata.Catalog, metadata.Routing);

        Assert.Equal(["a-new", "z-old", "a-other"],
            catalog.Rank(rows.Select(row => row.Id), "planning", "default").Select(row => row.Id));
    }

    [Fact]
    public void GivenHigherFit_WhenRanking_PrefersAssignmentFitBeforePrice()
    {
        var metadata = Metadata(new(2026, 10, 11));
        var catalog = SquadModelCatalog.Parse(metadata.Catalog, metadata.Routing);

        Assert.Equal("model-frontier", catalog.Rank(Rows.Select(row => row.Id), "planning", "default")[0].Id);
        Assert.Equal("model-coder", catalog.Rank(Rows.Select(row => row.Id), "implementation", "default")[0].Id);
        Assert.Equal("implementation", catalog.AssignmentClass("product-owner", "produce"));
        Assert.Equal("council", catalog.AssignmentClass("product-owner", "council"));
        Assert.DoesNotContain(catalog.Rank(Rows.Select(row => row.Id), "research", "fast"), row => row.Id == "model-frontier");
        Assert.Contains(catalog.Rank(Rows.Select(row => row.Id), "research", "fast", manual: true), row => row.Id == "model-frontier");
    }

    [Theory]
    [InlineData("model;command")]
    [InlineData("model with space")]
    [InlineData("model\nother")]
    [InlineData("model$(command)")]
    [InlineData("model'quoted")]
    public void GivenMetacharacters_WhenValidatingModelId_RefusesExactValue(string id) =>
        Assert.False(SquadModelCatalog.IsModelId(id));

    [Fact]
    public async Task GivenNoPolicy_WhenRunning_PreservesOmittedParameterAndDoesNotDiscoverAvailability()
    {
        using var host = new RuntimeTestHost();
        host.Options.AvailableModelsAsync = _ => throw new InvalidOperationException("No-policy must not query model availability.");
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Completed, result.Status);
        Assert.All(host.Client.Calls, call => Assert.Null(call.Options?.ModelId));
        Assert.All(result.Evidence, evidence => Assert.Null(evidence.ModelRouting));
        var team = File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "team.md"));
        Assert.DoesNotContain("Model routing:", team, StringComparison.Ordinal);
        Assert.DoesNotContain("| Model |", team, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenLegacyModelPairs_WhenRunning_IgnoresEveryPickAndReportsManualMigrationOnce()
    {
        using var host = new RuntimeTestHost();
        host.Options.AvailableModelsAsync = _ => throw new InvalidOperationException("Legacy request text cannot activate model routing.");
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Research the bounded fixture models=researcher:invented-one,lead:invented-two.");

        Assert.Equal(SquadRunStatus.ResearchCompleted, result.Status);
        Assert.All(host.Client.Calls, call => Assert.Null(call.Options?.ModelId));
        const string notice = "Legacy models= picks were not applied.";
        Assert.Equal(1, result.ResponseText.Split(notice, StringSplitOptions.None).Length - 1);
        var decisions = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "decisions.md"));
        Assert.Equal(1, decisions.Split(notice, StringSplitOptions.None).Length - 1);
        var team = File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "team.md"));
        Assert.DoesNotContain("| Model |", team, StringComparison.Ordinal);
        Assert.DoesNotContain("invented-one", decisions, StringComparison.Ordinal);
        Assert.Contains("routing=manual", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenFastFloorManualMenu_WhenOfferingSuggestions_ExcludesFrontierFromSuggestionButNotExplicitChoices()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = new() { Mode = "manual" };
        using var runtime = await host.CreateAsync();

        var choices = await runtime.GetModelRoutingChoicesAsync();

        var researcher = choices.Roles.Single(role => role.Role == "researcher");
        Assert.Equal("model-newer", researcher.SuggestedModel);
        Assert.Equal("model-newer", researcher.Candidates[0].ModelId);
        Assert.Contains(researcher.Candidates, candidate => candidate.ModelId == "model-frontier");
    }

    [Fact]
    public async Task GivenAdvertisedUnevaluatedModel_WhenMenuHasRoom_ListsItLastAndNeverSuggestsIt()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("unreviewed-model", host.Client), new("model-newer", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "manual" };
        using var runtime = await host.CreateAsync();

        var choices = await runtime.GetModelRoutingChoicesAsync();

        var researcher = choices.Roles.Single(role => role.Role == "researcher");
        Assert.Equal("model-newer", researcher.SuggestedModel);
        Assert.Equal("unreviewed-model", researcher.Candidates[^1].ModelId);
        Assert.True(researcher.Candidates[^1].Unevaluated);
        Assert.Null(researcher.Candidates[^1].BlendedUsdPerMillion);
    }

    [Fact]
    public async Task GivenRankedPolicy_WhenRunning_UsesActualBindingsAndConfirmedFloors()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        SetFloor(host, "lead", "default");
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("model-newer", result.Evidence[0].Model);
        Assert.Equal("model-frontier", result.Evidence[1].Model);
        Assert.Equal("model-coder", result.Evidence[2].Model);
        Assert.All(result.Evidence, evidence => Assert.Equal("host-advertised", evidence.ModelSource));
        Assert.Contains(host.Client.Calls, call => call.Options?.ModelId == "model-frontier");
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Role == "scribe");
        var history = File.ReadAllText(Path.Combine(host.Project, result.Evidence[0].HistoryPath));
        Assert.Contains("- Requested model: model-newer", history, StringComparison.Ordinal);
        Assert.Contains("- Effective model: model-newer", history, StringComparison.Ordinal);
        Assert.Contains("- Observed model: unreported", history, StringComparison.Ordinal);
        Assert.Contains("- Route rationale:", history, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-mismatch:", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenUnavailableCatalogWinner_WhenRanking_NeverOffersOrDispatchesIt()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new("model-older", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var choices = await runtime.GetModelRoutingChoicesAsync();
        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(choices.Roles.SelectMany(role => role.Candidates), candidate => Assert.Equal("model-older", candidate.ModelId));
        Assert.All(result.Evidence, evidence => Assert.Equal("model-older", evidence.Model));
    }

    [Fact]
    public async Task GivenStaleCatalog_WhenRunning_UsesExplicitLoggedStaticFallbackWithoutStaleRanking()
    {
        using var host = new RuntimeTestHost();
        UseModels(host, age: 91);
        host.Options.ModelSelector = (role, _) => role is null ? null : new(host.Client, "model-older");
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.Equal("model-older", evidence.Model));
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("stale-catalog fallback", StringComparison.Ordinal));
        Assert.Contains(host.Log.Messages, message => message.Contains("Model routing used a fallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenMalformedCatalog_WhenRunning_LogsAllowedFallbackRatherThanInventingRows()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Write("skills/apm-renamed-squad/references/model-catalog.md", "Not a catalog.");
        host.Options.ModelSelector = (role, _) => role is null ? null : new(host.Client, "model-older", "balanced");
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.Equal("host-configured", evidence.ModelSource));
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("no valid Retrieved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenManualMenu_WhenTypingAvailableIdBeyondEight_ValidatesItIncludingUnevaluatedIds(bool unevaluated)
    {
        using var host = new RuntimeTestHost();
        var rows = Enumerable.Range(0, 12).Select(index => new ModelRow("choice-" + index.ToString(CultureInfo.InvariantCulture),
            "choices", "1", "balanced", 1m, [2, 2, 2, 2, 2, 2, 0])).Concat(Rows).ToArray();
        UseModels(host, rows);
        var ids = rows.Select(row => row.Id).Append("advertised-unreviewed").ToArray();
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            ids.Select(id => new SquadAvailableModel(id, host.Client)).ToArray());
        host.Options.ModelRouting = Manual("researcher");
        var questions = new List<SquadQuestion>();
        var pick = unevaluated ? "advertised-unreviewed" : "choice-10";
        host.Options.AskAsync = (question, _) =>
        {
            questions.Add(question);
            return ValueTask.FromResult<string?>(question.Role == "model-routing:researcher" ? pick :
                question.Question.StartsWith("Choose one role", StringComparison.Ordinal) ? "Keep confirmed picks" : "3");
        };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var menu = Assert.Single(questions, question => question.Role == "model-routing:researcher");
        Assert.Equal(8, menu.Choices!.Count);
        Assert.DoesNotContain(menu.Choices, choice => choice.StartsWith(pick + " ", StringComparison.Ordinal));
        Assert.Equal(pick, result.Evidence[0].Model);
        Assert.Equal(unevaluated, result.Evidence[0].ModelRouting!.Unevaluated);
        Assert.Contains(host.Client.Calls, call => call.Options?.ModelId == pick);
    }

    [Fact]
    public async Task GivenBelowFloorManualCell_WhenRunning_RefusesOnlyThatCellAndAsksBeforeAnyModelWork()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        SetFloor(host, "lead", "default");
        host.Options.ModelRouting = Manual();
        host.Options.ModelRouting.Models["lead"] = "model-light";
        host.Options.AskAsync = (question, _) => ValueTask.FromResult<string?>(
            question.Role == "model-routing:lead" ? "model-newer" :
            question.Question.StartsWith("Choose one role", StringComparison.Ordinal) ? "Keep confirmed picks" : "3");
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.DoesNotContain(host.Client.Calls, call => call.Options?.ModelId == "model-light");
        Assert.All(result.Evidence, evidence => Assert.Equal("model-newer", evidence.Model));
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("lead refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenRankedPreExecutionRejection_WhenRunning_ReranksReportedAvailableClientsAndRecordsBothAttempts()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var rejecting = new ControlledClient(host.Client, options =>
            options?.ModelId == "model-newer" ? new SquadModelUnavailableException("model-newer", ["model-older"]) : null);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", rejecting), new("model-older", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("model-newer", result.Evidence[0].ModelRouting!.RequestedModel);
        Assert.Equal("model-older", result.Evidence[0].ModelRouting!.EffectiveModel);
        Assert.Contains("identity-mismatch: requested model-newer, effective model-older", result.ResponseText, StringComparison.Ordinal);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
        Assert.Single(state.RootElement.GetProperty("ModelCalls").EnumerateArray(), call => call.GetProperty("Status").GetString() == "rejected");
        Assert.Equal(1, rejecting.Attempts);
    }

    [Fact]
    public async Task GivenManualPreExecutionRejection_WhenHumanUnavailable_DoesNotSilentlySubstitute()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var rejecting = new ControlledClient(host.Client, _ => new SquadModelUnavailableException("model-newer", ["model-older"]));
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", rejecting), new("model-older", host.Client)]);
        host.Options.ModelRouting = Manual();
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.InputRequired, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(1, rejecting.Attempts);
        Assert.DoesNotContain(host.Client.Calls, call => call.Options?.ModelId == "model-older");
    }

    [Fact]
    public async Task GivenOrdinaryProviderFailure_WhenRunning_DoesNotTreatItAsModelRejectionOrRetry()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var failing = new ControlledClient(host.Client, _ => new InvalidOperationException("Actual provider failure."));
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new("model-newer", failing)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Equal(1, failing.Attempts);
        Assert.Contains("Actual provider failure", result.ResponseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenConcreteBelowFloorResponse_WhenRunning_EscalatesBeforeInvokingItsTools(string mode)
    {
        using var host = new RuntimeTestHost();
        host.Options.Mode = mode;
        UseModels(host);
        SetFloor(host, "lead", "default");
        var reporting = new ControlledClient(host.Client, _ => null, responseModel: "model-light");
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new("model-newer", reporting)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Single(result.Evidence);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Role == "lead");
        Assert.Contains("below the confirmed consequence floor", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task GivenSameFloorIdentityMismatch_WhenRunning_PreservesActualIdentityAndSurfacesIt()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var reporting = new ControlledClient(host.Client, _ => null, responseModel: "model-older");
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new("model-newer", reporting)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence =>
        {
            Assert.Equal("model-older", evidence.Model);
            Assert.Equal("model-newer", evidence.ModelRouting!.RequestedModel);
            Assert.Equal("model-older", evidence.ModelRouting.ObservedModel);
            Assert.Equal("model-newer", evidence.ModelRouting.EffectiveModel);
            Assert.Equal("provider-response", evidence.ModelSource);
        });
        Assert.Contains("identity-mismatch: requested model-newer, observed model-older", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenConfirmedManualPolicy_WhenTurningOff_RemovesCellsAndRetainsPriorChoicesAndEvidence()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = Manual();
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Write documentation.");
        Assert.True(run.Status == SquadRunStatus.Completed, run.ResponseText);
        var bytes = File.ReadAllBytes(Path.Combine(host.Project, run.Evidence[0].HistoryPath));

        var update = await runtime.UpdateModelRoutingAsync(new() { Mode = "off" });

        Assert.Equal(SquadRunStatus.Completed, update.Status);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, run.Evidence[0].HistoryPath)));
        var team = File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "team.md"));
        Assert.DoesNotContain("Model routing:", team, StringComparison.Ordinal);
        Assert.DoesNotContain("| Model |", team, StringComparison.Ordinal);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
        Assert.Equal("model-newer", state.RootElement.GetProperty("PreviousManualModels").GetProperty("researcher").GetString());
        Assert.Contains(host.Approvals, approval => approval.Kind == SquadApprovalKind.ModelRouting);
    }

    [Fact]
    public async Task GivenPersistedPolicy_WhenCreatingAnotherHost_NullConfigurationInheritsIt()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using (var first = await host.CreateAsync())
        {
            var run = await first.RunAsync("Write documentation.");
            Assert.True(run.Status == SquadRunStatus.Completed, run.ResponseText);
        }
        host.Options.ModelRouting = null;
        host.QueueSuccess("technical-writer");
        using var second = await host.CreateAsync();

        var result = await second.RunAsync("Write documentation again.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.NotNull(evidence.ModelRouting));
        Assert.False(host.Client.Disposed);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unreported")]
    [InlineData("unverified")]
    public async Task GivenUnknownObservedIdentity_WhenRunning_DoesNotFabricateMismatch(string marker)
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var reporting = new ControlledClient(host.Client, _ => null, responseModel: marker);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>([new("model-newer", reporting)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.Null(evidence.ModelRouting!.ObservedModel));
        Assert.DoesNotContain("identity-mismatch:", result.ResponseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host-model+variant")]
    [InlineData("modèle-disponible")]
    public async Task GivenAdvertisedNonMetacharacterId_WhenManuallyTyped_UsesExactIdentifier(string id)
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", host.Client), new(id, host.Client)]);
        host.Options.ModelRouting = Manual("researcher");
        host.Options.AskAsync = (question, _) => ValueTask.FromResult<string?>(
            question.Role == "model-routing:researcher" ? id :
            question.Question.StartsWith("Choose one role", StringComparison.Ordinal) ? "1" : "3");
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(id, result.Evidence[0].Model);
        Assert.True(result.Evidence[0].ModelRouting!.Unevaluated);
    }

    [Fact]
    public async Task GivenClassSelection_WhenFloorsDiffer_OffersOnlyTheHighestFloorAndAcceptsNumericAnswers()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        SetFloor(host, "developer", "default");
        SetFloor(host, "technical-writer", "extended");
        host.Options.ModelRouting = Manual();
        host.Options.ModelRouting.Models.Remove("developer");
        host.Options.ModelRouting.Models.Remove("technical-writer");
        var menus = new List<SquadQuestion>();
        host.Options.AskAsync = (question, _) =>
        {
            menus.Add(question);
            return ValueTask.FromResult<string?>(
                question.Question.StartsWith("Choose models", StringComparison.Ordinal) ? "2" : "1");
        };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        var menu = Assert.Single(menus, question => question.Role == "model-routing:developer,technical-writer");
        Assert.Single(menu.Choices!);
        Assert.StartsWith("model-frontier -", menu.Choices![0], StringComparison.Ordinal);
        Assert.Equal("model-frontier", result.Evidence[2].Model);
    }

    [Fact]
    public async Task GivenAvailableSetChangingAfterResearch_WhenDispatching_ReRanksBeforeTheNextProvider()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            host.Client.Calls.Count > 1 ? [new("model-older", host.Client)] :
                [new("model-newer", host.Client), new("model-older", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("model-newer", result.Evidence[0].Model);
        Assert.Equal("model-older", result.Evidence[1].Model);
    }

    [Fact]
    public async Task GivenManualCellEditedBelowFloor_WhenReusingState_RefusesItRatherThanSilentlyOverwriting()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        SetFloor(host, "lead", "default");
        host.Options.ModelRouting = Manual();
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var first = await runtime.RunAsync("Write documentation.");
        Assert.True(first.Status == SquadRunStatus.Completed, first.ResponseText);
        var teamPath = Path.Combine(host.Project, ".copilot-tracking", "squad", "team.md");
        File.WriteAllText(teamPath, File.ReadAllText(teamPath).Replace("| default | model-newer |",
            "| default | model-light |", StringComparison.Ordinal));
        host.QueueSuccess("technical-writer");

        var next = await runtime.RunAsync("Write documentation again.");

        Assert.Equal(SquadRunStatus.InputRequired, next.Status);
        Assert.Single(next.Evidence);
        Assert.Contains(next.Diagnostics!, diagnostic => diagnostic.Contains("below the default floor", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Client.Calls, call => call.Options?.ModelId == "model-light");
    }

    [Fact]
    public async Task GivenSuspendedRankedRun_WhenRestartingWithOtherMode_RefusesChangedPolicyBeforeModelWork()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        using var cancellation = new CancellationTokenSource();
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.Plan)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(true);
        };
        host.QueueSuccess("technical-writer");
        string runId;
        using (var first = await host.CreateAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync("Write documentation.", cancellation.Token));
            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")));
            runId = state.RootElement.GetProperty("Checkpoint").GetProperty("RunId").GetString()!;
        }
        host.Options.ModelRouting = new() { Mode = "off" };
        var calls = host.Client.Calls.Count;
        var statePath = Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json");
        var before = File.ReadAllBytes(statePath);
        using var resumed = await host.CreateAsync();

        var result = await resumed.ResumeAsync(runId);

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("exact model policy changed", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(before, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task GivenUnconfirmedState_WhenUpdatingModelPolicy_DoesNotCreateNativeFiles()
    {
        using var host = new RuntimeTestHost();
        using var runtime = await host.CreateAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.UpdateModelRoutingAsync(new() { Mode = "ranked" }));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenInitializedState_WhenBuildingModelMenus_LeavesAllWriterBytesUnchanged()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Write documentation.");
        Assert.True(run.Status == SquadRunStatus.Completed, run.ResponseText);
        var root = Path.Combine(host.Project, ".copilot-tracking", "squad");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        _ = await runtime.GetModelRoutingChoicesAsync();

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public async Task GivenModelRejectionAfterToolExecution_WhenRunning_RefusesRetryAndRetainsPartialBytes()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var invalidRejection = new PostToolRejectingClient();
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", invalidRejection), new("model-older", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("not pre-execution", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(1, invalidRejection.Attempts);
        Assert.DoesNotContain(host.Client.Calls, call => call.Options?.ModelId == "model-older");
        Assert.Contains(Directory.GetFiles(host.Project, "*.md", SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("Actually written before rejection.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenModelRejectionAfterDirectRegisteredScript_WhenRunning_DoesNotRetryTheActualEffect()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Write("skills/apm-renamed-specialist/scripts/original.ps1", "Original synthetic resource; the registered host equivalent owns execution.");
        var roots = await host.Source().ResolveAsync();
        var binding = SquadSkillScriptBinding.Create(roots, "specialist-test", "scripts/original.ps1", "direct-host-fixture-v1");
        var writes = 0;
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(
            AIFunctionFactory.Create(() =>
            {
                writes++;
                File.WriteAllText(Path.Combine(host.Project, "implementation.txt"), "Actual direct registered-script effect.");
                return "implementation.txt";
            }, "registered_source", "Actual bounded source write through a canonical resource binding."),
            "execute/runInTerminal", ["developer"], SquadToolEffect.ProjectWrite,
            result => new([result is JsonElement path ? path.GetString()! : (string)result!]),
            ProjectWritePaths: _ => ["implementation.txt"], SkillScript: binding, ProjectReadPaths: _ => []));
        using var invalidRejection = new PostScriptRejectingClient(host.Client);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", invalidRejection), new("model-older", host.Client)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement a bounded source fixture.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("not pre-execution", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(1, invalidRejection.Attempts);
        Assert.Equal(1, writes);
        Assert.Single(result.Effects!);
        Assert.NotNull(Assert.Single(result.Effects!).Receipt.SkillScript);
        Assert.DoesNotContain(host.Client.Calls, call => call.Options?.ModelId == "model-older");
        Assert.Equal("Actual direct registered-script effect.", File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenDeclaredInputLimit_WhenDispatchInputExceedsIt_OnlyUsesConfirmedCapableBinding(bool longContext)
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", host.Client, ContextWindowTokens: 1,
                 LongContextWindowTokens: longContext ? 1_000_000 : null),
             new("model-older", host.Client, ContextWindowTokens: 1_000_000)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.Equal(longContext ? "model-newer" : "model-older", evidence.Model));
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("autonomous")]
    [InlineData("autopilot")]
    public async Task GivenHostCapabilityBelowFloor_WhenOnlyStaticFallbackExists_RefusesBeforeThatRoleModelExecutes(string mode)
    {
        using var host = new RuntimeTestHost();
        host.Options.Mode = mode;
        UseModels(host);
        SetFloor(host, "lead", "default");
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("unknown-light", host.Client, "fast-lightweight")]);
        host.Options.ModelSelector = (role, _) => role is null ? null : new(host.Client, "unknown-light");
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation.");

        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Single(result.Evidence);
        Assert.Contains("below floor", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Role == "lead");
    }

    [Fact]
    public async Task GivenDeclinedPolicyUpdate_WhenRequested_PreservesEveryWriterByte()
    {
        var reject = false;
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ApproveAsync = (_, _) => ValueTask.FromResult(!reject);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Write documentation.");
        Assert.True(run.Status == SquadRunStatus.Completed, run.ResponseText);
        var root = Path.Combine(host.Project, ".copilot-tracking", "squad");
        var bytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        reject = true;

        var update = await runtime.UpdateModelRoutingAsync(new() { Mode = "off" });

        Assert.Equal(SquadRunStatus.ApprovalDenied, update.Status);
        Assert.All(bytes, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public async Task GivenOriginalOptionsMutatedAfterCreation_WhenDispatching_PolicySnapshotRemainsUnchanged()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        host.Options.ModelRouting = Manual();
        host.QueueSuccess("technical-writer");
        using var runtime = await host.CreateAsync();
        host.Options.ModelRouting.Mode = "off";
        host.Options.ModelRouting.Models["researcher"] = "model-light";

        var result = await runtime.RunAsync("Write documentation.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.All(result.Evidence, evidence => Assert.Equal("model-newer", evidence.Model));
        Assert.All(result.Evidence, evidence => Assert.NotNull(evidence.ModelRouting));
    }

    [Fact]
    public async Task GivenSiblingToolExecutionDuringPreExecutionRejection_WhenCouncilRuns_ReranksOnlyRejectedCallWithoutFalseReplayDenial()
    {
        using var host = new RuntimeTestHost();
        UseModels(host);
        var observedSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new CouncilRoutingClient(host.Client, observedSibling, rejectArchitect: true);
        var replacement = new CouncilRoutingClient(host.Client, observedSibling, rejectArchitect: false);
        host.Options.AvailableModelsAsync = _ => ValueTask.FromResult<IReadOnlyList<SquadAvailableModel>>(
            [new("model-newer", primary), new("model-older", replacement)]);
        host.Options.ModelRouting = new() { Mode = "ranked" };
        host.QueuePipeline("technical-writer", council: true);
        host.Options.Council = new() { Roles = ["architect", "security", "cost-manager", "product-owner"] };
        host.QueueStage("researcher");
        host.QueueStage("lead");
        host.QueueStage("technical-writer");
        host.QueueStage("tester");
        host.Client.Text("Real parallel model routing result.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Write documentation with council.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(4, result.Evidence.Count(evidence => evidence.Stage == "council"));
        var architecture = result.Evidence.Single(evidence => evidence.Role == "architect");
        Assert.Equal("model-newer", architecture.ModelRouting!.RequestedModel);
        Assert.Equal("model-older", architecture.ModelRouting.EffectiveModel);
        Assert.True(observedSibling.Task.IsCompleted);
        Assert.DoesNotContain("not pre-execution", result.ResponseText, StringComparison.Ordinal);
    }

    private sealed class CouncilRoutingClient(IChatClient inner, TaskCompletionSource sibling, bool rejectArchitect) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            var assignment = snapshot.Select(message => message.Text).LastOrDefault(text =>
                text?.Contains("stage=council", StringComparison.Ordinal) == true);
            if (assignment is null)
            {
                return await base.GetResponseAsync(snapshot, options, cancellationToken);
            }
            var role = System.Text.RegularExpressions.Regex.Match(assignment, @"role=([^;]+);").Groups[1].Value;
            var results = snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            var calls = snapshot.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            var returnedNames = results.Select(result => calls.Single(call => call.CallId == result.CallId).Name).ToArray();
            if (role == "architect" && rejectArchitect && results.Length == 0)
            {
                await sibling.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                throw new SquadModelUnavailableException("model-newer", ["model-older"]);
            }
            if (results.Length == 0 && role != "cost-manager")
            {
                return RuntimeTestHost.Function("write_artifact", new() { ["content"] = role + " real independent findings." });
            }
            if (role == "security" && returnedNames.Contains("write_artifact", StringComparer.Ordinal))
            {
                sibling.TrySetResult();
            }
            if (returnedNames.Any(name => name is "complete_stage" or "complete_return"))
            {
                return new(new ChatMessage(ChatRole.Assistant, "Actual council lens completed."));
            }
            var arguments = new Dictionary<string, object?>
            {
                ["summary"] = role + " actual findings",
                ["verdict"] = "Approve",
                ["risk"] = "Low",
                ["blockingIssues"] = 0,
            };
            arguments["payload"] = new SquadCouncilFindings([], [], []);
            return RuntimeTestHost.Function(role == "cost-manager" ? "complete_return" : "complete_stage", arguments);
        }
    }

    private sealed class PostScriptRejectingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        internal int Attempts { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            if (snapshot.Any(message => message.Text?.Contains("stage=produce", StringComparison.Ordinal) == true))
            {
                Attempts++;
                var run = Assert.Single(options!.Tools!.OfType<AIFunction>(),
                    tool => tool.Name == AgentSkillsProvider.RunSkillScriptToolName);
                _ = await run.InvokeAsync(new AIFunctionArguments
                {
                    ["skillName"] = "specialist-test",
                    ["scriptName"] = "scripts/original.ps1",
                    ["arguments"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
                }, cancellationToken);
                throw new SquadModelUnavailableException("model-newer", ["model-older"]);
            }
            return await base.GetResponseAsync(snapshot, options, cancellationToken);
        }
    }

    private sealed class PostToolRejectingClient : IChatClient
    {
        internal int Attempts { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            var writer = Assert.Single(options!.Tools!.OfType<AIFunction>(), tool => tool.Name == "write_artifact");
            _ = await writer.InvokeAsync(new AIFunctionArguments { ["content"] = "Actually written before rejection." }, cancellationToken);
            throw new SquadModelUnavailableException("model-newer", ["model-older"]);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record ModelRow(string Id, string Family, string Generation, string Capability, decimal Blended, int[] Scores);

    private sealed class FixedClock(DateOnly date) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private sealed class ControlledClient(IChatClient inner, Func<ChatOptions?, Exception?> reject,
        string? responseModel = null) : DelegatingChatClient(inner)
    {
        internal int Attempts { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (reject(options) is { } error)
            {
                throw error;
            }
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.ModelId = responseModel;
            return response;
        }
    }
}
