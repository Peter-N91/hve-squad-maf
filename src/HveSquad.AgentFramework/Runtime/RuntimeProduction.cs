using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

internal sealed partial class RuntimeScribe
{
    internal SquadProductionHandoff RecordProductionHandoff(RuntimeState state, SquadProductionPlan plan,
        string producer, string consumer, IReadOnlyList<SquadDispatchEvidence> evidence, DateTimeOffset recordedAt,
        int iteration = 0)
    {
        using (SyncRoot.EnterScope())
        {
            VerifyIntegrity(state);
            var runId = state.Checkpoint?.RunId ?? throw new InvalidDataException("A producing handoff requires an active run.");
            foreach (var source in evidence)
            {
                Verify(source, state);
            }
            var id = ProjectFiles.Hash(JsonSerializer.Serialize(new { runId, plan.PlanDispatchId, producer, consumer }));
            if (iteration > 1)
            {
                id = HumanActions.Hash(new { id, iteration });
            }
            state.ProductionHandoffs ??= [];
            var prior = state.ProductionHandoffs.SingleOrDefault(handoff => handoff.Id == id);
            var record = new SquadProductionHandoff(id, runId, plan.PlanDispatchId, producer, consumer,
                evidence.ToArray(), prior?.RecordedAt ?? recordedAt, iteration);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior, JsonOptions) != JsonSerializer.Serialize(record, JsonOptions))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: a producing handoff cannot replace its source evidence.");
                }
                return prior;
            }
            state.ProductionHandoffs.Add(record);
            RecordNativeOperation(state, "RecordProductionHandoff", runId, null, productionHandoff: record);
            Save(state);
            return record;
        }
    }

    private static void VerifyProductionHandoffs(RuntimeState state)
    {
        var handoffs = state.ProductionHandoffs ?? [];
        if (handoffs.Select(handoff => handoff.Id).Distinct(StringComparer.Ordinal).Count() != handoffs.Count ||
            state.NativeOperations.Count(operation => operation.ProductionHandoff is not null) != handoffs.Count)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: producing handoff identity/receipt index is incomplete.");
        }
        foreach (var handoff in handoffs)
        {
            var receipt = state.NativeOperations.SingleOrDefault(operation => operation.ProductionHandoff?.Id == handoff.Id);
            if (receipt is null || receipt.RunId != handoff.RunId ||
                JsonSerializer.Serialize(receipt.ProductionHandoff, JsonOptions) != JsonSerializer.Serialize(handoff, JsonOptions) ||
                handoff.Evidence.Count == 0 || handoff.Evidence.Any(source =>
                    state.DispatchRecords.Count(item => item.DispatchId == source.DispatchId) != 1 ||
                    JsonSerializer.Serialize(state.DispatchRecords.Single(item => item.DispatchId == source.DispatchId), JsonOptions) !=
                        JsonSerializer.Serialize(source, JsonOptions)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a producing handoff has no exact recorded source and immutable native receipt.");
            }
        }
    }
}
