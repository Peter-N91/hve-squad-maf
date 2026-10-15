using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Explicit host billing rates, not the catalog's representative ranking prices.</summary>
public sealed class SquadConsumptionConfiguration
{
    /// <summary>Date on which this provider's rates were verified.</summary>
    public DateOnly ObservedOn { get; set; }
    /// <summary>Non-secret billing source or host tariff reference.</summary>
    public string Source { get; set; } = "";
    /// <summary>Revision of the released dispatch-size estimator.</summary>
    public int EstimatorRevision { get; set; } = 2;
    /// <summary>Exact executable model identities mapped to their verified rates.</summary>
    public IDictionary<string, SquadModelRate> Models { get; set; } = new Dictionary<string, SquadModelRate>();
    /// <summary>Reconciled estimate calibration, bound to the rate date and estimator revision.</summary>
    public SquadConsumptionCalibration Calibration { get; set; } = new();

    /// <summary>Validates the explicit tariff shape without acquiring artifacts or writing state.</summary>
    public void Validate() => ConsumptionMath.Validate(this);
}

/// <summary>USD per million tokens; cache writes are an additional input charge.</summary>
public sealed class SquadTokenRates
{
    /// <summary>Uncached input rate.</summary>
    public required decimal Input { get; set; }
    /// <summary>Cached input rate.</summary>
    public required decimal Cached { get; set; }
    /// <summary>Additional cache-write rate; explicit zero means no separate charge.</summary>
    public required decimal CacheWrite { get; set; }
    /// <summary>Output rate.</summary>
    public required decimal Output { get; set; }
}

/// <summary>A positively confirmed flat tariff or an explicit long-context threshold and tariff.</summary>
public sealed class SquadModelRate
{
    /// <summary>Standard-context rates.</summary>
    public required SquadTokenRates Standard { get; set; }
    /// <summary>True confirms that the provider has no separate long-context rate.</summary>
    public bool FlatRateConfirmed { get; set; }
    /// <summary>Input above this threshold uses LongContext; equality uses Standard.</summary>
    public long? LongContextThresholdTokens { get; set; }
    /// <summary>The complete above-threshold tariff, when one exists.</summary>
    public SquadTokenRates? LongContext { get; set; }
}

/// <summary>Running-mean reconciliation provenance. An unreconciled or stale basis cannot admit a ceiling.</summary>
public sealed class SquadConsumptionCalibration
{
    /// <summary>Running mean, bounded to 0.25 through 10.0.</summary>
    public decimal Factor { get; set; } = 1m;
    /// <summary>Number of reconciled runs contributing to the mean.</summary>
    public int Observations { get; set; }
    /// <summary>Last reconciliation date; null is unreconciled.</summary>
    public DateOnly? LastReconciled { get; set; }
    /// <summary>Exact observed-date and estimator-revision binding, joined by a pipe.</summary>
    public string Basis { get; set; } = "";
}

/// <summary>Optional per-invocation ceiling. Null inherits only when resuming the same run; "unset" removes it.</summary>
public sealed record SquadRunConfiguration(string? CostCeiling = null)
{
    /// <summary>Validates the finite positive ceiling or explicit unset operation.</summary>
    public void Validate() => _ = ConsumptionMath.ParseCeiling(CostCeiling);
}

/// <summary>Host-normalized token usage. InputTokens includes CachedTokens; CacheWriteTokens is additional.</summary>
public sealed record SquadTokenUsage(long InputTokens, long CachedTokens, long CacheWriteTokens, long OutputTokens);

/// <summary>A trusted host's billing observation for one completed recorded run, never model-supplied data.</summary>
public sealed record SquadConsumptionReconciliation(string RunId, decimal ObservedCostUsd, string EvidenceReference);

/// <summary>Actual calibration outcome; repeated identical observations do not calibrate twice.</summary>
public sealed record SquadConsumptionReconciliationResult(SquadRunStatus Status, decimal Factor, int Observations,
    string Diagnostic);

/// <summary>One complete remaining-demand row. ProjectedCostUsd retains unrounded calculation precision.</summary>
public sealed record SquadCostDemandRow(string Slot, string Stage, string Role, string Agent, int Count,
    string DispatchClass, IReadOnlyList<string> PricingModels, int InternalTurns, decimal BaseContext,
    decimal GrowthPerTurn, decimal OutputPerTurn, decimal? ProjectedCostUsd,
    bool DeterministicNative = false, string? ReservationId = null, decimal? ReservedAdmissionUsd = null);

/// <summary>A durable run/round decision. Totals are rounded only after unrounded row summation.</summary>
public sealed record SquadCostPreflight(string RunId, string RoundId, decimal? CeilingUsd, string Decision,
    string Confidence, decimal? EvaluatedSpendUsd, decimal? RemainingUsd, decimal? ProjectedCostUsd,
    decimal? AdmissionCostUsd, IReadOnlyList<SquadCostDemandRow> Demand, IReadOnlyList<string> PermittedSlots,
    string PricingSha256, string SpendSha256, string? ApprovedFrom = null, string? ApprovalReference = null,
    string? Diagnostic = null);

/// <summary>Reconciled per-run provider spend. A partial known sum is never presented as the total.</summary>
public sealed record SquadConsumptionSummary(string RunId, int ProviderAttempts, decimal? EstimatedCostUsd,
    decimal KnownCostUsd, int UnpricedAttempts, int EstimatedTokenAttempts, int UnobservedDispatches = 0);

internal sealed record DispatchSize(string Name, int Turns, decimal Context, decimal Growth, decimal Output)
{
    internal static DispatchSize? ForStage(string stage) => stage switch
    {
        "coordinator" => new("Lookup / single-file read", 3, 20_000, 3_000, 800),
        "research" or "discovery" => new("Research / file survey", 12, 40_000, 4_000, 1_250),
        "plan" or "remediation" => new("Plan / synthesis", 15, 60_000, 4_000, 2_000),
        "produce" or "implementation" => new("Implement / edit loop", 35, 60_000, 6_000, 2_000),
        "review" or "intake" or "validation" => new("Review / verification", 18, 50_000, 4_000, 1_500),
        "council" => new("Council member opinion", 10, 50_000, 4_000, 1_500),
        "scribe" => new("Scribe state write", 4, 15_000, 3_000, 800),
        _ => null,
    };
}

internal sealed record ModelCallCharge(string CallId, string RunId, string? DispatchId, string? PricedModel,
    string ModelSource, string TokenBasis, decimal? UncalibratedCostUsd, decimal? CostUsd,
    decimal CalibrationFactor, string PricingSha256, string TariffSha256, string Diagnostic,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ConsumptionPricingSnapshot? Pricing = null);

internal sealed record ConsumptionPricingSnapshot(DateOnly ObservedOn, string Source, int EstimatorRevision,
    SquadModelRate AppliedRates, SquadConsumptionCalibration Calibration);

internal sealed record CostApproval(string RunId, decimal CeilingUsd, string PricingSha256,
    IReadOnlyList<SquadCostDemandRow> Manifest, string FromRound, string Reference);

internal sealed class CostRunCheckpoint
{
    public required string RunId { get; init; }
    public decimal? CeilingUsd { get; set; }
    public long Revision { get; set; }
    public List<SquadCostPreflight> Rounds { get; set; } = [];
    public CostApproval? Approval { get; set; }
    public Dictionary<string, string> DispatchSlots { get; set; } = new(StringComparer.Ordinal);
}

internal static class ConsumptionMath
{
    internal const decimal ReserveMultiplier = 3m;
    private static readonly JsonSerializerOptions JsonOptions = new();

    internal static SquadConsumptionConfiguration Copy(SquadConsumptionConfiguration configuration) =>
        JsonSerializer.Deserialize<SquadConsumptionConfiguration>(JsonSerializer.Serialize(configuration, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("Empty consumption configuration.");

    internal static decimal? ParseCeiling(string? input)
    {
        if (input is null or "unset")
        {
            return null;
        }
        var value = input.StartsWith('$') ? input[1..] : input;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ceiling) ||
            ceiling <= 0m)
        {
            throw new ArgumentException("CostCeiling must be a finite positive USD decimal or the exact literal 'unset'.", nameof(input));
        }
        return ceiling;
    }

    internal static void Validate(SquadConsumptionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Models);
        if (configuration.ObservedOn == default || string.IsNullOrWhiteSpace(configuration.Source) ||
            configuration.Source.Length > 300 || configuration.Source.Any(char.IsControl) ||
            configuration.EstimatorRevision != 2 || configuration.Models.Count is < 1 or > 128 ||
            configuration.Models.Keys.Any(id => !SquadModelCatalog.IsModelId(id)) ||
            configuration.Calibration is not { Factor: >= .25m and <= 10m, Observations: >= 0 })
        {
            throw new ArgumentException("Consumption requires a dated source, estimator revision 2, exact model rates and bounded calibration.");
        }
        foreach (var rate in configuration.Models.Values)
        {
            if (rate is null || !ValidRates(rate.Standard) ||
                rate.FlatRateConfirmed && (rate.LongContext is not null || rate.LongContextThresholdTokens is not null) ||
                !rate.FlatRateConfirmed && (rate.LongContextThresholdTokens is not > 0 || !ValidRates(rate.LongContext)))
            {
                throw new ArgumentException("Each model needs all four nonnegative rates and a confirmed flat or complete long-context tariff.");
            }
        }
    }

    private static bool ValidRates(SquadTokenRates? rates) =>
        rates is { Input: >= 0, Cached: >= 0, CacheWrite: >= 0, Output: >= 0 };

    internal static string Basis(SquadConsumptionConfiguration configuration) =>
        configuration.ObservedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" +
        configuration.EstimatorRevision.ToString(CultureInfo.InvariantCulture);

    internal static bool EligibleCalibration(SquadConsumptionConfiguration? configuration, DateOnly today) =>
        configuration is not null && configuration.Calibration.Observations > 0 &&
        configuration.Calibration.LastReconciled is { } date && date <= today &&
        configuration.Calibration.Basis == Basis(configuration);

    internal static bool CurrentRates(SquadConsumptionConfiguration? configuration, DateOnly today) =>
        configuration is not null && configuration.ObservedOn <= today &&
        today.DayNumber - configuration.ObservedOn.DayNumber <= 90;

    internal static string TariffHash(SquadConsumptionConfiguration? configuration) =>
        ProjectFiles.Hash(JsonSerializer.Serialize(configuration is null ? null : new
        {
            configuration.ObservedOn,
            configuration.Source,
            configuration.EstimatorRevision,
            Models = configuration.Models.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }, JsonOptions));

    internal static string PricingHash(SquadConsumptionConfiguration? configuration) =>
        ProjectFiles.Hash(JsonSerializer.Serialize(new { Tariff = TariffHash(configuration), configuration?.Calibration }, JsonOptions));

    internal static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    internal static decimal Estimate(SquadModelRate model, DispatchSize size)
    {
        var rates = Tariff(model, size.Context + size.Growth * (size.Turns - 1));
        var gross = size.Turns * (size.Context + size.Growth * (size.Turns - 1) / 2m);
        var writes = rates.CacheWrite == 0 ? 0 : size.Context + size.Growth * (size.Turns - 1);
        return Price(rates, gross * .20m, gross * .80m, writes, size.Turns * size.Output);
    }

    private static SquadTokenRates Tariff(SquadModelRate model, decimal context) =>
        model.LongContextThresholdTokens is { } threshold && context > threshold
            ? model.LongContext ?? throw new InvalidDataException("Missing long-context tariff.")
            : model.Standard;

    private static decimal Price(SquadTokenRates rates, decimal input, decimal cached, decimal writes, decimal output) =>
        checked((input * rates.Input + cached * rates.Cached + writes * rates.CacheWrite + output * rates.Output) / 1_000_000m);

    internal static ModelCallCharge Charge(ModelCallObservation call, SquadConsumptionConfiguration? configuration, DateOnly today)
    {
        var model = KnownModel(call.ObservedModel) ? call.ObservedModel : KnownModel(call.EffectiveModel) ? call.EffectiveModel : null;
        var source = KnownModel(call.ObservedModel) ? "provider-observed" : model is null ? "unresolved" : "effective-passed";
        var hash = PricingHash(configuration);
        var factor = EligibleCalibration(configuration, today) ? configuration!.Calibration.Factor : 1m;
        ModelCallCharge Unknown(string reason) => new(call.Id, call.RunId, call.DispatchId, model, source,
            "unknown", null, null, factor, hash, TariffHash(configuration), reason);
        if (call.Status == "started")
        {
            return Unknown("Provider attempt is in flight; no completed usage is asserted.");
        }
        if (model is null || configuration is null || !configuration.Models.TryGetValue(model, out var rate))
        {
            return Unknown("Concrete model or exact rate is unknown; no zero-cost or tier-priced provider work is invented.");
        }
        if (!CurrentRates(configuration, today))
        {
            return Unknown("Rates are stale or future-dated; historical billed spend is not inferred.");
        }
        decimal raw;
        string basis;
        if (call.InputTokens is { } input && call.OutputTokens is { } output)
        {
            var rates = Tariff(rate, input);
            if (call.CachedInputTokens is { } cached && call.CacheWriteTokens is { } writes)
            {
                raw = Price(rates, input - cached, cached, writes, output);
                basis = "host-measured-token-split";
            }
            else
            {
                raw = checked((input * Math.Max(rates.Input, rates.Cached) +
                    (rates.CacheWrite > 0 ? input * rates.CacheWrite : 0) + output * rates.Output) / 1_000_000m);
                basis = "measured-totals; conservative-unknown-cache-split";
            }
        }
        else if (DispatchSize.ForStage(call.Stage) is { } size)
        {
            size = size with
            {
                Context = Math.Max(size.Context, Math.Max(call.EstimatedContextTokens ?? 0, call.InputTokens ?? 0)),
                Output = Math.Max(size.Output, call.OutputTokens ?? 0),
            };
            raw = Estimate(rate, size);
            basis = "estimated-class-floor-per-unmetered-attempt";
        }
        else
        {
            return Unknown("Unmapped dispatch class and incomplete token telemetry.");
        }
        var snapshot = Copy(configuration);
        return new(call.Id, call.RunId, call.DispatchId, model, source, basis, raw,
            checked(raw * factor), factor, hash, TariffHash(configuration),
            EligibleCalibration(configuration, today) ? "Estimated provider cost; calibration applied once." : "Uncalibrated estimated provider cost.",
            new(configuration.ObservedOn, configuration.Source, configuration.EstimatorRevision,
                snapshot.Models[model], snapshot.Calibration));
    }

    internal static bool KnownModel(string? model) => model is not null and not ("" or "unknown" or "unreported" or "unverified" or "auto");

    internal static ModelCallCharge HistoricalUnknown(ModelCallObservation call)
    {
        var model = KnownModel(call.ObservedModel) ? call.ObservedModel : KnownModel(call.EffectiveModel) ? call.EffectiveModel : null;
        return new(call.Id, call.RunId, call.DispatchId, model,
            KnownModel(call.ObservedModel) ? "provider-observed" : model is null ? "unresolved" : "effective-passed",
            "historical-missing-rate-provenance", null, null, 1m, "unavailable", "unavailable",
            "Historical provider work has no execution-time tariff snapshot. Current rates cannot retroactively invent its spend.");
    }

    internal static SquadCostDemandRow Demand(string slot, string stage, string role, string agent,
        IReadOnlyList<string> models, SquadConsumptionConfiguration? configuration, DateOnly today,
        bool native = false, decimal minimumContext = 0)
    {
        var size = DispatchSize.ForStage(stage);
        decimal? projected = native ? 0 : null;
        if (!native && size is not null && models.Count > 0 && models.All(KnownModel) &&
            CurrentRates(configuration, today) && EligibleCalibration(configuration, today) &&
            models.All(model => configuration!.Models.ContainsKey(model)))
        {
            size = size with { Context = Math.Max(size.Context, minimumContext) };
            projected = models.Max(model => Estimate(configuration!.Models[model], size)) * configuration!.Calibration.Factor;
        }
        return new(slot, stage, role, agent, 1, size?.Name ?? "unmapped", models,
            native ? 0 : size?.Turns ?? 0, native ? 0 : size?.Context ?? 0,
            native ? 0 : size?.Growth ?? 0, native ? 0 : size?.Output ?? 0, projected, native);
    }

    internal static SquadCostPreflight Evaluate(string runId, string roundId, decimal? ceiling, decimal spend,
        IReadOnlyList<SquadCostDemandRow> demand, IReadOnlyList<string> nextSlots, string pricingHash, string spendHash,
        bool complete, bool calibrationEligible, bool spendKnown, decimal pending = 0, CostApproval? approval = null)
    {
        var rows = demand.GroupBy(row => row.ReservationId ?? row.Slot, StringComparer.Ordinal).ToArray();
        var consistent = rows.All(group => group.All(row => RowHash(row) == RowHash(group.First())));
        var unique = rows.Select(group => group.First()).ToArray();
        var known = (ceiling is null or > 0) && consistent && complete && calibrationEligible && spendKnown &&
            unique.All(row => row.Count > 0 && row.ProjectedCostUsd is >= 0 &&
                row.ReservedAdmissionUsd is null or >= 0 &&
                (!row.DeterministicNative || row.ProjectedCostUsd == 0 && row.InternalTurns == 0 &&
                    row.BaseContext == 0 && row.GrowthPerTurn == 0 && row.OutputPerTurn == 0) &&
                (row.DeterministicNative || row.InternalTurns > 0 || row.ReservedAdmissionUsd is >= 0)) &&
            nextSlots.All(slot => unique.Any(row => row.Slot == slot));
        var projected = unique.Sum(row => (row.ProjectedCostUsd ?? 0) * row.Count);
        var admission = unique.Sum(row => row.ReservedAdmissionUsd ?? (row.ProjectedCostUsd ?? 0) * row.Count * ReserveMultiplier);
        var evaluated = checked(spend + pending);
        var remaining = Math.Max(0, (ceiling ?? 0) - evaluated);
        var decision = ceiling is null ? "not-requested" : !known ? "cannot-confirm" :
            evaluated >= ceiling || admission > remaining ? "over-ceiling" : "within-ceiling";
        var permits = decision is "not-requested" or "within-ceiling" ? nextSlots : [];
        string? approvedFrom = null;
        string? approvalReference = null;
        if (decision == "over-ceiling" && evaluated < ceiling && approval is not null &&
            approval.RunId == runId && approval.CeilingUsd == ceiling && approval.PricingSha256 == pricingHash &&
            !string.IsNullOrWhiteSpace(approval.Reference) && !string.IsNullOrWhiteSpace(approval.FromRound) &&
            unique.All(row => approval.Manifest.Any(prior => prior.Slot == row.Slot && RowHash(prior) == RowHash(row))))
        {
            decision = "approved-over-ceiling";
            permits = nextSlots.Take(1).ToArray();
            approvedFrom = approval.FromRound;
            approvalReference = approval.Reference;
        }
        var totalsKnown = consistent && unique.All(row => row.ProjectedCostUsd is not null);
        return new(runId, roundId, ceiling, decision, known && ceiling is not null ? "medium" : "low",
            spendKnown ? Round(evaluated) : null, ceiling is not null && spendKnown ? Round(remaining) : null,
            ceiling is not null && totalsKnown ? Round(projected) : null,
            ceiling is not null && totalsKnown ? Round(admission) : null, unique, permits,
            pricingHash, spendHash, approvedFrom, approvalReference,
            decision == "cannot-confirm" ? "Complete demand, current exact rates, eligible calibration and known spend are required; this state cannot be approved." :
                ceiling is not null && evaluated >= ceiling ? "Accumulated spend is at or above the ceiling; no later substantive unit may start." : null);
    }

    internal static string RowHash(SquadCostDemandRow row) =>
        ProjectFiles.Hash(JsonSerializer.Serialize(row with { Slot = "", ReservationId = null }, JsonOptions));

    internal static int UnobservedDispatches(string runId, RuntimeState state)
    {
        var evidence = state.Runs.Where(run => run.RunId == runId).SelectMany(run => run.Evidence)
            .Concat(state.Checkpoint is { } checkpoint && checkpoint.RunId == runId ? checkpoint.Evidence : [])
            .Concat(state.ActiveRun is { } active && active.RunId == runId ? active.Evidence : []);
        return evidence.Select(item => item.DispatchId).Distinct(StringComparer.Ordinal)
            .Count(id => !state.ModelCalls.Any(call => call.DispatchId == id));
    }

    internal static SquadConsumptionSummary Summary(string runId, IReadOnlyList<ModelCallCharge> charges, int unobservedDispatches = 0)
    {
        var calls = charges.Where(charge => charge.RunId == runId).ToArray();
        var known = calls.Sum(call => call.CostUsd ?? 0m);
        return new(runId, calls.Length, unobservedDispatches == 0 && calls.All(call => call.CostUsd is not null) ? Round(known) : null,
            Round(known), calls.Count(call => call.CostUsd is null),
            calls.Count(call => call.TokenBasis != "host-measured-token-split"), unobservedDispatches);
    }
}
