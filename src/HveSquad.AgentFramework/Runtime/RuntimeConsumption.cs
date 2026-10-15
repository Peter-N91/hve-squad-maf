using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed partial class RuntimeScribe
{
    private static readonly Action<ILogger, string, string, decimal?, decimal?, decimal?, Exception?> LogCostAdmission =
        LoggerMessage.Define<string, string, decimal?, decimal?, decimal?>(
            LogLevel.Information, new EventId(5, "CostAdmission"),
            "Cost admission {Decision} ({Confidence}); evaluated spend USD {EvaluatedSpendUsd}, projected USD {ProjectedCostUsd}, admission USD {AdmissionCostUsd}.");

    private void AssertSavedSnapshot()
    {
        var exists = File.Exists(files.Resolve(StatePath));
        if (exists != (_savedStateSha256 is not null) ||
            exists && files.HashFile(StatePath) != _savedStateSha256)
        {
            throw new RuntimeHistoryIntegrityException(
                "history-integrity: compare-and-swap found a changed or missing native snapshot; no pending work was authorized.");
        }
    }

    internal void ConfigureConsumption(RuntimeState state, SquadConsumptionConfiguration? supplied, DateOnly today,
        bool bindHostInput = true)
    {
        using (_writeLock.EnterScope())
        {
            var missing = state.ModelCalls.Where(call => state.ModelCallCharges?.Any(charge => charge.CallId == call.Id) != true).ToArray();
            if (missing.Length > 0)
            {
                state.ModelCallCharges ??= [];
                state.ModelCallCharges.AddRange(missing.Select(ConsumptionMath.HistoricalUnknown));
                RecordNativeOperation(state, "PreserveUnpricedHistoricalCalls", state.Checkpoint?.RunId, null);
            }
            var inputHash = supplied is null ? null : ConsumptionMath.PricingHash(supplied);
            var inherited = supplied is null || bindHostInput && state.ConsumptionInputSha256 == inputHash &&
                state.Consumption is not null;
            var configuration = inherited ? state.Consumption : ConsumptionMath.Copy(supplied!);
            if (configuration is not null)
            {
                ConsumptionMath.Validate(configuration);
                if (configuration.Calibration.Observations > 0 &&
                    (!ConsumptionMath.EligibleCalibration(configuration, today) ||
                     state.Consumption is { } prior && ConsumptionMath.TariffHash(prior) != ConsumptionMath.TariffHash(configuration) &&
                     JsonSerializer.Serialize(prior.Calibration, JsonOptions) == JsonSerializer.Serialize(configuration.Calibration, JsonOptions)))
                {
                    configuration = ConsumptionMath.Copy(configuration);
                    configuration.Calibration = new();
                }
            }
            var inputChanged = bindHostInput && inputHash is not null && state.ConsumptionInputSha256 != inputHash;
            if (inputChanged)
            {
                state.ConsumptionInputSha256 = inputHash;
            }
            if (ConsumptionMath.PricingHash(configuration) != ConsumptionMath.PricingHash(state.Consumption))
            {
                state.Consumption = configuration;
                files.Write(files.SquadRoot + "/consumption-rates.md", RenderRates(configuration));
                Decision(state, "Host consumption tariff/calibration changed. Prior charges retain their exact pricing provenance; future demand must be re-admitted.");
                RecordNativeOperation(state, "ConfigureConsumption", state.Checkpoint?.RunId, null);
                Save(state);
            }
            else if (inputChanged || missing.Length > 0)
            {
                Save(state);
            }
        }
    }

    internal bool VerifyRates(RuntimeState state) =>
        File.Exists(files.Resolve(files.SquadRoot + "/consumption-rates.md")) &&
        files.Read(files.SquadRoot + "/consumption-rates.md", MaximumStateCharacters) == RenderRates(state.Consumption);

    internal void CommitCostRound(RuntimeState state, long expectedRevision, SquadCostPreflight round, CostApproval? approval = null)
    {
        using (_writeLock.EnterScope())
        {
            AssertSavedSnapshot();
            var cost = state.Checkpoint?.Cost ?? throw new InvalidDataException("No active run owns cost admission.");
            if (cost.RunId != round.RunId || cost.Revision != expectedRevision ||
                cost.Rounds.Any(prior => prior.RoundId == round.RoundId) ||
                File.Exists(files.Resolve(files.SquadRoot + "/state.pending.json")))
            {
                throw new RuntimeHistoryIntegrityException(
                    "cost-admission: stale round, duplicate identity or partial state write; no slot may execute.");
            }
            if (round.Decision == "approved-over-ceiling" &&
                (round.Confidence != "medium" || round.CeilingUsd is null || round.ApprovalReference is null ||
                 round.ApprovedFrom is null || round.PermittedSlots.Count != 1 ||
                 !cost.Rounds.Any(prior => prior.RoundId == round.ApprovedFrom && prior.Decision == "over-ceiling" &&
                     prior.Confidence == "medium" && prior.Diagnostic is null)))
            {
                throw new RuntimeHistoryIntegrityException("cost-admission: non-approvable or unbound overshoot transition.");
            }
            cost.Rounds.Add(round);
            cost.Revision++;
            if (approval is not null)
            {
                cost.Approval = approval;
            }
            state.CostPreflights ??= [];
            state.CostPreflights.Add(round);
            Decision(state, RenderCostRound(round));
            RecordNativeOperation(state, "RecordCostPreflight", round.RunId, null);
            if (state.Federation is null)
            {
                _ = ReconcileLedger(state, expectedNewWork: true);
            }
            Save(state);
            if (logger is not null)
            {
                LogCostAdmission(logger, round.Decision, round.Confidence, round.EvaluatedSpendUsd,
                    round.ProjectedCostUsd, round.AdmissionCostUsd, null);
            }
        }
    }

    internal SquadConsumptionReconciliationResult ReconcileConsumption(RuntimeState state,
        SquadConsumptionReconciliation observation, DateOnly today)
    {
        using (_writeLock.EnterScope())
        {
            var configuration = state.Consumption ?? throw new InvalidDataException("No exact rates are configured.");
            var charges = state.ModelCallCharges?.Where(charge => charge.RunId == observation.RunId).ToArray() ?? [];
            if (charges.Length == 0 || charges.Any(charge => charge.UncalibratedCostUsd is null) ||
                charges.Any(charge => charge.TariffSha256 != ConsumptionMath.TariffHash(configuration)))
            {
                throw new InvalidDataException("A missing, unpriced or mixed-tariff run cannot supply a calibration denominator.");
            }
            var raw = charges.Sum(charge => charge.UncalibratedCostUsd!.Value);
            if (raw <= 0)
            {
                throw new InvalidDataException("Calibration requires a positive uncalibrated run estimate.");
            }
            return ApplyConsumptionReconciliation(state, observation, raw, today);
        }
    }

    internal SquadConsumptionReconciliationResult ApplyConsumptionReconciliation(RuntimeState state,
        SquadConsumptionReconciliation observation, decimal raw, DateOnly today)
    {
        using (_writeLock.EnterScope())
        {
            var configuration = state.Consumption ?? throw new InvalidDataException("No exact rates are configured.");
            if (raw <= 0 || observation.ObservedCostUsd < 0)
            {
                throw new InvalidDataException("Calibration requires a complete positive uncalibrated denominator and nonnegative billing.");
            }
            var current = ConsumptionMath.Copy(configuration);
            if (!ConsumptionMath.EligibleCalibration(current, today))
            {
                current.Calibration = new();
            }
            var calibration = current.Calibration;
            var ratio = observation.ObservedCostUsd / raw;
            calibration.Factor = Math.Clamp(
                (calibration.Factor * calibration.Observations + ratio) / (calibration.Observations + 1m), .25m, 10m);
            calibration.Observations = checked(calibration.Observations + 1);
            calibration.LastReconciled = today;
            calibration.Basis = ConsumptionMath.Basis(current);
            state.ConsumptionReconciliations ??= [];
            state.ConsumptionReconciliations.Add(observation);
            ConfigureConsumption(state, current, today, bindHostInput: false);
            Decision(state, "Confirmed consumption reconciliation:\n" + JsonSerializer.Serialize(observation, JsonOptions));
            RecordNativeOperation(state, "ReconcileConsumption", observation.RunId, null);
            if (state.Federation is null)
            {
                _ = ReconcileLedger(state, expectedNewWork: true);
            }
            Save(state);
            return new(SquadRunStatus.Completed, calibration.Factor, calibration.Observations,
                "Reconciled once against the uncalibrated estimate; historical charges were not multiplied again.");
        }
    }

    private static string RenderRates(SquadConsumptionConfiguration? configuration)
    {
        if (configuration is null)
        {
            return "# Consumption rates\n\nNo pricing configured. Costs and credits are unknown, not zero.\n";
        }
        var text = new StringBuilder("# Native consumption rates\n\n")
            .Append("Observed-on: ").AppendLine(configuration.ObservedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("Source: ").AppendLine(Cell(configuration.Source))
            .AppendLine("\nUSD per million tokens. These host tariffs are not catalog Blended prices or billed totals.")
            .AppendLine("\n| Model ID | Input | Cached | Cache write | Output | LC Threshold | LC Input | LC Cached | LC Cache write | LC Output |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |");
        foreach (var (id, rate) in configuration.Models.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(id)} | {rate.Standard.Input} | {rate.Standard.Cached} | {rate.Standard.CacheWrite} | {rate.Standard.Output} | " +
                $"{(rate.FlatRateConfirmed ? "confirmed flat" : rate.LongContextThresholdTokens?.ToString(CultureInfo.InvariantCulture))} | " +
                $"{rate.LongContext?.Input.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {rate.LongContext?.Cached.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{rate.LongContext?.CacheWrite.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {rate.LongContext?.Output.ToString(CultureInfo.InvariantCulture) ?? "n/a"} |");
        }
        text.AppendLine("\n## Tier fallback\n\nNo tier-priced identity is invented. Missing exact rates remain unknown.")
            .AppendLine("\n## Dispatch-size estimator\n\n| Class | Turns | Base context | Growth/turn | Output/turn |\n| --- | ---: | ---: | ---: | ---: |");
        foreach (var stage in new[] { "coordinator", "research", "plan", "produce", "review", "council", "scribe" })
        {
            var size = DispatchSize.ForStage(stage)!;
            text.AppendLine(CultureInfo.InvariantCulture, $"| {size.Name} | {size.Turns} | {size.Context} | {size.Growth} | {size.Output} |");
        }
        text.AppendLine("\n## Calibration\n\n```json")
            .AppendLine(JsonSerializer.Serialize(configuration.Calibration, JsonOptions))
            .AppendLine("```");
        return text.ToString();
    }

    private static string RenderCostRound(SquadCostPreflight round)
    {
        var text = new StringBuilder("\n## Cost Preflight ").Append(round.RunId).Append(' ').AppendLine(round.RoundId)
            .AppendLine("\n```json").AppendLine(JsonSerializer.Serialize(round, JsonOptions)).AppendLine("```")
            .AppendLine("\n| Slot | Stage | Role | Count | Dispatch Class | Pricing Basis | Internal Turns | Base Context | Growth/Turn | Output/Turn | Projected Cost |")
            .AppendLine("| --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in round.Demand)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(row.Slot)} | {Cell(row.Stage)} | {Cell(row.Role)} | {row.Count} | {Cell(row.DispatchClass)} | " +
                $"{(row.DeterministicNative ? "deterministic-native; no model" : Cell(string.Join(", ", row.PricingModels)))} | " +
                $"{row.InternalTurns} | {row.BaseContext} | {row.GrowthPerTurn} | {row.OutputPerTurn} | {Usd(row.ProjectedCostUsd)} |");
        }
        return text.ToString();
    }

    private static string RenderRoleTotals(RuntimeState state)
    {
        var roster = state.Initialization?.Roster ?? [];
        var order = (state.Initialization?.Roles ?? []).Concat(state.RemovedMembers.Select(member => member.Role))
            .Concat(state.ModelCalls.Select(call => call.Role).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var rows = order.Select(role => (Role: role, Calls: state.ModelCalls.Where(call => call.Role == role).ToArray()))
            .Where(row => row.Calls.Length != 0)
            .Append(("orchestration", state.ModelCalls.Where(call => call.Role is null).ToArray())).ToArray();
        var attribution = new StringBuilder("\n## Attribution\n\n")
            .AppendLine("| Role | Agents | Models | Identity sources | Tier | Provider attempts | Native writes |")
            .AppendLine("| --- | --- | --- | --- | --- | ---: | ---: |");
        var usage = new StringBuilder("\n## Usage & Cost\n\n")
            .AppendLine("Token counters are measured or unknown; cached/cache-write counters are input subsets, not extra total tokens. Credit equivalents are estimated USD / 0.01, not billed provider credits. Costs retain their execution-time calibration.")
            .AppendLine(state.Consumption?.Calibration.Observations is > 0
                ? "Calibration observations are recorded in the execution-time pricing receipts."
                : "Uncalibrated: no accepted billing observations; missing rates or counters remain unknown.")
            .AppendLine("\n| Role | Input | Cached | Cache write | Output | Est. USD | Est. credit equivalents |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        string Values(IEnumerable<string?> values) =>
            Cell(string.Join(", ", values.Select(value => value ?? "unknown").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        decimal? Cost(IReadOnlyList<ModelCallObservation> calls)
        {
            var ids = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            var charges = (state.ModelCallCharges ?? []).Where(charge => ids.Contains(charge.CallId)).ToArray();
            return charges.Length == calls.Count && charges.All(charge => charge.CostUsd is not null)
                ? charges.Sum(charge => charge.CostUsd!.Value) : null;
        }
        void Usage(string role, IReadOnlyList<ModelCallObservation> calls)
        {
            var cost = Cost(calls);
            usage.AppendLine(CultureInfo.InvariantCulture,
                $"| {role} | {Tokens(SumKnown(calls.Select(call => call.InputTokens)))} | " +
                $"{Tokens(SumKnown(calls.Select(call => call.CachedInputTokens)))} | " +
                $"{Tokens(SumKnown(calls.Select(call => call.CacheWriteTokens)))} | " +
                $"{Tokens(SumKnown(calls.Select(call => call.OutputTokens)))} | {Usd(cost)} | {Usd(cost / .01m)} |");
        }
        foreach (var (role, calls) in rows)
        {
            var ids = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            var tier = role == "orchestration" ? "mixed" : roster.FirstOrDefault(member => member.Role == role)?.ModelTier ?? "unknown";
            var sources = (state.ModelCallCharges ?? []).Where(charge => ids.Contains(charge.CallId)).Select(charge => charge.ModelSource);
            attribution.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(role)} | {Values(calls.Select(call => call.Agent))} | " +
                $"{Values(calls.Select(call => call.ObservedModel ?? call.EffectiveModel))} | {Values(sources)} | " +
                $"{Cell(tier)} | {calls.Length} | {(role == "orchestration" ? state.NativeOperations.Count : 0)} |");
            Usage(Cell(role), calls);
        }
        Usage("**Total**", state.ModelCalls);
        return attribution.Append(usage).ToString();
    }

    private static string RenderCharges(RuntimeState state)
    {
        if (state.ModelCallCharges is not { Count: > 0 } charges)
        {
            return "";
        }
        var text = new StringBuilder("\n## Full provider-call accounting\n\n")
            .AppendLine("Includes coordinator, failed/rejected attempts and removed members. Native operations invoke no model. Estimated tokens remain separate from measured counters.")
            .AppendLine("\n| Call | Run | Role | Status | Priced model | Identity source | Token basis | Est. USD | Calibration | Pricing hash |")
            .AppendLine("| --- | --- | --- | --- | --- | --- | --- | ---: | ---: | --- |");
        foreach (var charge in charges)
        {
            var matches = state.ModelCalls.Where(observation => observation.Id == charge.CallId).ToArray();
            if (matches.Length != 1)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a priced charge has no unique recorded provider attempt.");
            }
            var call = matches[0];
            var role = call.Role ?? "orchestration";
            var removed = state.RemovedMembers.Any(member => member.Role == role) ? " (removed)" : "";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {charge.CallId} | {charge.RunId} | {Cell(role)}{removed} | {call.Status} | {Cell(charge.PricedModel ?? "unknown")} | " +
                $"{charge.ModelSource} | {charge.TokenBasis} | {Usd(charge.CostUsd)} | {charge.CalibrationFactor} | {charge.PricingSha256} |");
        }
        text.AppendLine("\n## Per-run totals\n");
        foreach (var runId in charges.Select(charge => charge.RunId).Distinct(StringComparer.Ordinal))
        {
            var summary = ConsumptionMath.Summary(runId, charges, ConsumptionMath.UnobservedDispatches(runId, state));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Run `{runId}`: {summary.ProviderAttempts} actual provider attempts; estimated cost USD {Usd(summary.EstimatedCostUsd)}; " +
                $"known subtotal USD {summary.KnownCostUsd:F4}; unpriced attempts {summary.UnpricedAttempts}; " +
                $"dispatches without provider observations {summary.UnobservedDispatches}. Credits/billed spend are not inferred.");
        }
        return text.ToString();
    }

    private static string Usd(decimal? amount) => amount?.ToString("F4", CultureInfo.InvariantCulture) ?? "unknown";
}
