using System.Globalization;
using HveSquad.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeConsumptionTests
{
    private static SquadCostDemandRow[] WorkedDemand(SquadConsumptionConfiguration configuration) =>
    [
        ConsumptionMath.Demand("coordinator", "coordinator", "orchestration", "Original Coordinator",
            ["model-fixed"], configuration, ConsumptionTestData.Today),
        ConsumptionMath.Demand("researcher", "research", "researcher", "Original Researcher",
            ["model-fixed"], configuration, ConsumptionTestData.Today),
        ConsumptionMath.Demand("scribe", "scribe", "scribe", "Illustrative model Scribe",
            ["model-scribe"], configuration, ConsumptionTestData.Today),
    ];

    private static SquadCostPreflight Evaluate(SquadCostDemandRow[] demand, decimal ceiling = 10m,
        decimal spend = .5m, decimal pending = 0, bool calibrated = true, CostApproval? approval = null) =>
        ConsumptionMath.Evaluate("run-fixed", "round-fixed", ceiling, spend, demand, ["researcher"],
            "pricing-fixed", "spend-fixed", true, calibrated, true, pending, approval);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e10")]
    [InlineData("unset ")]
    [InlineData("")]
    public void GivenInvalidCeiling_WhenValidating_RejectsNonfiniteNonpositiveOrAmbiguousInput(string ceiling) =>
        Assert.Throws<ArgumentException>(() => new SquadRunConfiguration(ceiling).Validate());

    [Theory]
    [InlineData("10", "10")]
    [InlineData("$0.0001", "0.0001")]
    [InlineData("unset", null)]
    public void GivenExplicitCeiling_WhenParsing_PreservesExactValueOrUnset(string input, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, CultureInfo.InvariantCulture), ConsumptionMath.ParseCeiling(input));

    [Theory]
    [InlineData(90, true)]
    [InlineData(91, false)]
    [InlineData(-1, false)]
    public void GivenDatedRates_WhenCheckingFreshness_UsesExactNinetyDayBoundary(int age, bool current)
    {
        var configuration = ConsumptionTestData.Configuration();
        configuration.ObservedOn = ConsumptionTestData.Today.AddDays(-age);
        Assert.Equal(current, ConsumptionMath.CurrentRates(configuration, ConsumptionTestData.Today));
    }

    [Fact]
    public void GivenWorkedReleasedInputs_WhenAdmitting_SumsUnroundedAppliesCalibrationOnceAndReserveThree()
    {
        var configuration = ConsumptionTestData.Configuration(factor: 1.2m);
        var demand = WorkedDemand(configuration);
        var result = Evaluate(demand);

        Assert.Equal(1.709112m, demand.Sum(row => row.ProjectedCostUsd));
        Assert.Equal(1.7091m, result.ProjectedCostUsd);
        Assert.Equal(5.1273m, result.AdmissionCostUsd);
        Assert.Equal(.5000m, result.EvaluatedSpendUsd);
        Assert.Equal(9.5000m, result.RemainingUsd);
        Assert.Equal("within-ceiling", result.Decision);
        Assert.Equal("medium", result.Confidence);
        Assert.Equal(["researcher"], result.PermittedSlots);
    }

    [Theory]
    [InlineData("5.627336", "within-ceiling")]
    [InlineData("5.627335", "over-ceiling")]
    public void GivenNearThresholdCeiling_WhenAdmitting_UsesUnroundedEqualityNotDisplayedTotals(string ceiling, string decision)
    {
        var result = Evaluate(WorkedDemand(ConsumptionTestData.Configuration(factor: 1.2m)),
            decimal.Parse(ceiling, CultureInfo.InvariantCulture));
        Assert.Equal(decision, result.Decision);
    }

    [Fact]
    public void GivenMissingCalibration_WhenAdmitting_CannotConfirmIsNeverApprovable()
    {
        var demand = WorkedDemand(ConsumptionTestData.Configuration());
        var approval = new CostApproval("run-fixed", 10, "pricing-fixed", demand, "old-round", "host-proof");
        var result = Evaluate(demand, calibrated: false, approval: approval);
        Assert.Equal("cannot-confirm", result.Decision);
        Assert.Empty(result.PermittedSlots);
    }

    [Theory]
    [InlineData(".5")]
    [InlineData(".5001")]
    public void GivenSpendAtOrAboveCeiling_WhenAdmitting_StopsEvenWithEarlierOvershootApproval(string spent)
    {
        var demand = WorkedDemand(ConsumptionTestData.Configuration());
        var approval = new CostApproval("run-fixed", .5m, "pricing-fixed", demand, "old-round", "host-proof");
        var result = Evaluate(demand, .5m, decimal.Parse(spent, CultureInfo.InvariantCulture), approval: approval);
        Assert.Equal("over-ceiling", result.Decision);
        Assert.NotNull(result.Diagnostic);
        Assert.Empty(result.PermittedSlots);
    }

    [Fact]
    public void GivenPendingHandoff_WhenAdmitting_AddsReservationWithoutSubtractingRecordedSpend()
    {
        var result = Evaluate(WorkedDemand(ConsumptionTestData.Configuration()), pending: .25m);
        Assert.Equal(.7500m, result.EvaluatedSpendUsd);
        Assert.Equal(9.2500m, result.RemainingUsd);
    }

    [Fact]
    public void GivenApprovedShrinkingManifest_WhenAdmitting_PermitsOnlyOneUnchangedSequentialSlot()
    {
        var demand = WorkedDemand(ConsumptionTestData.Configuration());
        var approval = new CostApproval("run-fixed", 1, "pricing-fixed", demand, "old-round", "host-proof");
        var result = Evaluate(demand.Where(row => row.Slot != "scribe").ToArray(), 1, approval: approval);
        Assert.Equal("approved-over-ceiling", result.Decision);
        Assert.Equal(["researcher"], result.PermittedSlots);
        Assert.Equal("old-round", result.ApprovedFrom);
        Assert.Equal("host-proof", result.ApprovalReference);
    }

    [Theory]
    [InlineData("pricing")]
    [InlineData("model")]
    [InlineData("ceiling")]
    [InlineData("expanded")]
    [InlineData("provenance")]
    public void GivenChangedApprovedInputs_WhenAdmitting_DoesNotReuseOvershoot(string change)
    {
        var demand = WorkedDemand(ConsumptionTestData.Configuration());
        var approval = new CostApproval("run-fixed", 1, change == "pricing" ? "different" : "pricing-fixed",
            change == "expanded" ? demand.Take(1).ToArray() : demand.ToArray(),
            "old-round", change == "provenance" ? "" : "host-proof");
        if (change == "model")
        {
            demand[1] = demand[1] with { PricingModels = ["different-model"] };
        }
        var result = Evaluate(demand, change == "ceiling" ? .9m : 1m, approval: approval);
        Assert.Equal("over-ceiling", result.Decision);
        Assert.Empty(result.PermittedSlots);
    }

    [Fact]
    public void GivenInnerAndMetaReservations_WhenAdmitting_DeduplicatesReferencesAndDoesNotApplyReserveTwice()
    {
        SquadCostDemandRow Row(string slot, string reservation, decimal admission) =>
            new(slot, "federation", "inner", "inner-run", 1, "already-admitted", [], 0, 0, 0, 0,
                admission / 3m, ReservationId: reservation, ReservedAdmissionUsd: admission);
        var inner = Row("product", "inner-product", 5.127336m);
        var meta = Row("meta", "meta-1", .9m);
        var result = ConsumptionMath.Evaluate("federation", "round", 12, 1,
            [inner, inner with { Slot = "duplicate-product" }, Row("azure", "inner-azure", 3.2m), meta, meta],
            ["product"], "pricing", "spend", true, true, true);
        Assert.Equal(9.2273m, result.AdmissionCostUsd);
        Assert.Equal(11m, result.RemainingUsd);
        Assert.Equal(3, result.Demand.Count);
        Assert.Equal("within-ceiling", result.Decision);
    }

    [Fact]
    public void GivenConflictingReservationReferences_WhenAdmitting_CannotConfirm()
    {
        var row = WorkedDemand(ConsumptionTestData.Configuration())[1] with { ReservationId = "same" };
        var result = Evaluate([row, row with { ProjectedCostUsd = row.ProjectedCostUsd + 1 }]);
        Assert.Equal("cannot-confirm", result.Decision);
        Assert.Empty(result.PermittedSlots);
    }

    [Fact]
    public void GivenMeasuredTokens_WhenPricing_UsesObservedIdentityAndAllFourRatesExactlyOnce()
    {
        var configuration = ConsumptionTestData.Configuration(factor: 1.2m);
        var call = ConsumptionTestData.Call() with
        {
            RequestedModel = "alias-not-a-tariff",
            ObservedModel = "model-fixed",
            InputTokens = 5_000,
            CachedInputTokens = 4_000,
            CacheWriteTokens = 1_500,
            OutputTokens = 1_000,
        };
        var charge = ConsumptionMath.Charge(call, configuration, ConsumptionTestData.Today);
        Assert.Equal("model-fixed", charge.PricedModel);
        Assert.Equal("provider-observed", charge.ModelSource);
        Assert.Equal(.024825m, charge.UncalibratedCostUsd);
        Assert.Equal(.029790m, charge.CostUsd);
        Assert.Equal("host-measured-token-split", charge.TokenBasis);
    }

    [Fact]
    public void GivenOnlyRequestedAlias_WhenPricing_DoesNotInventEffectiveIdentityOrZeroSpend()
    {
        var call = ConsumptionTestData.Call() with { EffectiveModel = null, RequestedModel = "model-fixed" };
        var charge = ConsumptionMath.Charge(call, ConsumptionTestData.Configuration(), ConsumptionTestData.Today);
        Assert.Null(charge.PricedModel);
        Assert.Null(charge.CostUsd);
        Assert.Equal("unresolved", charge.ModelSource);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public void GivenUnmeteredAttempt_WhenPricing_RecordsConservativeEstimateInsteadOfSuccessfulOrFreeWork(string status)
    {
        var call = ConsumptionTestData.Call() with { Status = status };
        var charge = ConsumptionMath.Charge(call, ConsumptionTestData.Configuration(), ConsumptionTestData.Today);
        Assert.True(charge.CostUsd > 0);
        Assert.Equal("estimated-class-floor-per-unmetered-attempt", charge.TokenBasis);
        Assert.Null(call.InputTokens);
        Assert.Equal(status, call.Status);
    }

    [Theory]
    [InlineData(5, "0.000015")]
    [InlineData(6, "0.000036")]
    public void GivenLongContextTariff_WhenPricing_ThresholdEqualityUsesStandard(long input, string expected)
    {
        var configuration = ConsumptionTestData.Configuration();
        configuration.Models["model-fixed"] = new()
        {
            Standard = new() { Input = 3, Cached = .3m, CacheWrite = 0, Output = 15 },
            LongContextThresholdTokens = 5,
            LongContext = new() { Input = 6, Cached = .6m, CacheWrite = 0, Output = 30 },
        };
        var call = ConsumptionTestData.Call() with { InputTokens = input, OutputTokens = 0, CachedInputTokens = 0, CacheWriteTokens = 0 };
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            ConsumptionMath.Charge(call, configuration, ConsumptionTestData.Today).CostUsd);
    }

    [Fact]
    public void GivenKnownSubtotalAndUnknownAttempt_WhenSummarizing_DoesNotLabelSubtotalAsRunTotal()
    {
        var configuration = ConsumptionTestData.Configuration();
        var known = ConsumptionMath.Charge(ConsumptionTestData.Call(), configuration, ConsumptionTestData.Today);
        var unknown = ConsumptionMath.Charge(ConsumptionTestData.Call() with { Id = "other", EffectiveModel = null },
            configuration, ConsumptionTestData.Today);
        var summary = ConsumptionMath.Summary("run-fixed", [known, unknown]);
        Assert.Null(summary.EstimatedCostUsd);
        Assert.True(summary.KnownCostUsd > 0);
        Assert.Equal(1, summary.UnpricedAttempts);
    }

    [Fact]
    public void GivenManySmallRows_WhenAdmitting_RoundsOnlyAfterFullPrecisionSummation()
    {
        var row = WorkedDemand(ConsumptionTestData.Configuration())[1];
        var rows = Enumerable.Range(0, 4).Select(index => row with
        {
            Slot = "small-" + index.ToString(CultureInfo.InvariantCulture),
            ProjectedCostUsd = .000024m,
        }).ToArray();
        var result = ConsumptionMath.Evaluate("run", "round", 1, 0, rows, ["small-0"], "pricing", "spend", true, true, true);
        Assert.Equal(.0001m, result.ProjectedCostUsd);
        Assert.Equal(.0003m, result.AdmissionCostUsd);
    }

    [Fact]
    public void GivenUnknownCacheSplitWithHigherCachedTariff_WhenPricing_UsesTheConservativeRate()
    {
        var configuration = ConsumptionTestData.Configuration();
        configuration.Models["model-fixed"].Standard = new() { Input = 1, Cached = 2, CacheWrite = 3, Output = 1 };
        var call = ConsumptionTestData.Call() with { InputTokens = 4, OutputTokens = 0 };
        var charge = ConsumptionMath.Charge(call, configuration, ConsumptionTestData.Today);
        Assert.Equal(.000020m, charge.CostUsd);
        Assert.Contains("unknown-cache-split", charge.TokenBasis, StringComparison.Ordinal);
        Assert.Null(call.CachedInputTokens);
    }

    [Fact]
    public void GivenHistoricalAttemptWithoutTariffSnapshot_WhenAccounting_DoesNotRetroactivelyPriceItAtCurrentRates()
    {
        var charge = ConsumptionMath.HistoricalUnknown(ConsumptionTestData.Call());
        Assert.Null(charge.CostUsd);
        Assert.Null(charge.UncalibratedCostUsd);
        Assert.Equal("unavailable", charge.PricingSha256);
        Assert.Equal("model-fixed", charge.PricedModel);
    }

    [Fact]
    public void GivenUnobservedLegacyDispatch_WhenSummarizing_DoesNotInventProviderAttemptsOrZeroTotal()
    {
        var result = ConsumptionMath.Summary("legacy", [], unobservedDispatches: 1);
        Assert.Equal(0, result.ProviderAttempts);
        Assert.Equal(1, result.UnobservedDispatches);
        Assert.Null(result.EstimatedCostUsd);
    }

    [Fact]
    public void GivenDeterministicNativeHandoff_WhenReserving_HasNoModelTokensOrModelPrice()
    {
        var row = ConsumptionMath.Demand("native", "scribe", "scribe", "Squad Scribe", [],
            null, ConsumptionTestData.Today, native: true);
        Assert.Equal(0m, row.ProjectedCostUsd);
        Assert.Equal(0, row.InternalTurns);
        Assert.Equal(0, row.BaseContext);
        Assert.True(row.DeterministicNative);
        Assert.Empty(row.PricingModels);
    }

    [Fact]
    public void GivenMissingLongContextConfirmation_WhenValidating_RejectsIncompleteTariffShape()
    {
        var configuration = ConsumptionTestData.Configuration();
        configuration.Models["model-fixed"].FlatRateConfirmed = false;
        Assert.Throws<ArgumentException>(configuration.Validate);
    }
}

internal static class ConsumptionTestData
{
    internal static readonly DateOnly Today = new(2026, 10, 11);

    internal static SquadConsumptionConfiguration Configuration(decimal factor = 1m, int observations = 1) => new()
    {
        ObservedOn = Today,
        Source = "original-synthetic-provider-tariff",
        Models = new Dictionary<string, SquadModelRate>
        {
            ["model-fixed"] = new()
            {
                Standard = new() { Input = 3, Cached = .3m, CacheWrite = 3.75m, Output = 15 },
                FlatRateConfirmed = true,
            },
            ["model-scribe"] = new()
            {
                Standard = new() { Input = 1, Cached = .1m, CacheWrite = 1.25m, Output = 5 },
                FlatRateConfirmed = true,
            },
        },
        Calibration = new()
        {
            Factor = factor,
            Observations = observations,
            LastReconciled = observations > 0 ? Today : null,
            Basis = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|2",
        },
    };

    internal static ModelCallObservation Call() =>
        new("call-fixed", "run-fixed", "dispatch-fixed", "Original Researcher", "researcher", "research",
            1, "returned", "model-fixed", null, EffectiveModel: "model-fixed");

    internal static void Configure(RuntimeTestHost host, string? ceiling = "10000", int observations = 1)
    {
        host.Options.TimeProvider = new Clock();
        host.Options.CostCeiling = ceiling;
        host.Options.Consumption = Configuration(observations: observations);
        host.Options.ModelSelector = (_, _) => new(host.Client, "model-fixed");
    }

    internal static void QueueResearch(RuntimeTestHost host)
    {
        host.Client.Call("run_focused", new()
        {
            ["stage"] = "research",
            ["inputPaths"] = Array.Empty<string>(),
            ["requiresCouncil"] = false,
            ["includesAiRisk"] = false,
        });
        host.QueueStage("researcher");
        host.Client.Text("Research-only result.");
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }
}
