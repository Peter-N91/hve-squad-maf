using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record OutputRevisionSnapshot(string Id, string RunId, string SourceDispatchId,
    string ReplacementDispatchId, string? ValidationRecordId, SquadArtifactEvidence Output, string ArchivePath,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? HumanActionId = null);

internal sealed partial class RuntimeScribe
{
    private static readonly Action<ILogger, Exception?> LogPipelinedHandoff = LoggerMessage.Define(
        LogLevel.Debug, new EventId(5, "PipelinedHandoff"),
        "One deterministic Scribe handoff overlaps a safe role invocation; consuming barriers still require verified history.");

    internal void NotifyPipelinedHandoff()
    {
        if (logger is not null)
        {
            LogPipelinedHandoff(logger, null);
        }
    }

    internal void ArchiveOutputRevision(RuntimeState state, string sourceDispatchId, string replacementDispatchId,
        SquadArtifactEvidence output)
    {
        using (SyncRoot.EnterScope())
        {
            VerifyIntegrity(state);
            var checkpoint = state.Checkpoint ?? throw new InvalidDataException("An output revision requires its owning run.");
            var mode = checkpoint.Mode;
            if (mode is not { ProducingIteration: 2 } ||
                mode.ValidationRecords.Count != 1 && mode.ProducingChangeActionId is null ||
                !state.DispatchRecords.Any(source => source.DispatchId == sourceDispatchId &&
                    (source.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? [])
                        .Any(item => item.Path == output.Path && item.Sha256 == output.Sha256)) ||
                !checkpoint.DispatchStack.Any(dispatch => dispatch.Id == replacementDispatchId && dispatch.Stage == "produce"))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: only bounded, independently requested producing revisions may archive a prior output.");
            }
            var id = HumanActions.Hash(new { checkpoint.RunId, sourceDispatchId, replacementDispatchId, output });
            state.OutputRevisions ??= [];
            if (state.OutputRevisions.Any(item => item.Id == id))
            {
                return;
            }
            if (files.HashFile(output.Path) != output.Sha256)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: the prior output changed before its exact-byte archive.");
            }
            var path = files.SquadRoot + "/payloads/" + id + "-output.bin";
            var destination = files.Resolve(path);
            if (File.Exists(destination))
            {
                if (files.HashFile(path) != output.Sha256)
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: an interrupted output archive has different bytes.");
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, files.ReadBytes(output.Path));
            }
            if (files.HashFile(path) != output.Sha256)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: the exact-byte output archive could not be verified.");
            }
            var record = new OutputRevisionSnapshot(id, checkpoint.RunId, sourceDispatchId, replacementDispatchId,
                mode.ValidationRecords.FirstOrDefault(), output, path, mode.ProducingChangeActionId);
            state.OutputRevisions.Add(record);
            RecordNativeOperation(state, "ArchiveProducingOutput", checkpoint.RunId, replacementDispatchId, outputRevision: record);
            Save(state);
        }
    }

    internal bool HasOutputRevision(RuntimeState state, string firstDispatchId, string secondDispatchId, string path) =>
        (state.OutputRevisions ?? []).Any(item =>
            (item.SourceDispatchId == firstDispatchId && item.ReplacementDispatchId == secondDispatchId ||
                item.SourceDispatchId == secondDispatchId && item.ReplacementDispatchId == firstDispatchId) &&
            files.Resolve(item.Output.Path).Equals(files.Resolve(path), ProjectFiles.PathComparison));

    private bool HasArchivedOutput(RuntimeState? state, string dispatchId, SquadArtifactEvidence output) =>
        state?.OutputRevisions?.Any(item => item.SourceDispatchId == dispatchId &&
            item.Output.Sha256 == output.Sha256 &&
            files.Resolve(item.Output.Path).Equals(files.Resolve(output.Path), ProjectFiles.PathComparison) &&
            files.HashFile(item.ArchivePath) == output.Sha256) == true;

    private void VerifyOutputRevisions(RuntimeState state)
    {
        var snapshots = state.OutputRevisions ?? [];
        if (snapshots.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshots.Count ||
            state.NativeOperations.Count(item => item.OutputRevision is not null) != snapshots.Count)
        {
            throw new RuntimeHistoryIntegrityException("history-integrity: output revision receipt/index is incomplete.");
        }
        foreach (var snapshot in snapshots)
        {
            var operation = state.NativeOperations.SingleOrDefault(item => item.OutputRevision?.Id == snapshot.Id);
            var validation = (state.GateRecords ?? []).SingleOrDefault(item => item.Id == snapshot.ValidationRecordId);
            var human = (state.HumanDecisions ?? []).SingleOrDefault(item => item.Binding.Id == snapshot.HumanActionId);
            var validated = validation?.RunId == snapshot.RunId && validation.Kind == "Autonomous Validation Outcome";
            var changedByHuman = human?.Binding.RunId == snapshot.RunId && human.Decision == SquadHumanDecision.Changes &&
                human.Binding.Gate == "finaloutcome" && human.Binding.Mode == "autopilot";
            var federationChange = (state.GateRecords ?? []).FirstOrDefault(record =>
                record.Kind == "Federation Human Changes" &&
                record.Payload.TryGetProperty("Origin", out var origin) &&
                origin.TryGetProperty("Binding", out var binding) &&
                binding.GetProperty("Id").GetString() == snapshot.HumanActionId);
            if (federationChange is not null)
            {
                var revision = ReadinessContracts.Parse<FederationHumanRevision>(federationChange.Payload);
                FederationExecution.VerifyHumanRevision(files, state.Fingerprint, revision);
                changedByHuman |= revision.InnerRunId == snapshot.RunId && revision.Stage == "produce";
            }
            var replacement = state.DispatchRecords.Any(item => item.DispatchId == snapshot.ReplacementDispatchId &&
                item.Stage == "produce") || state.Checkpoint?.DispatchStack.Any(item =>
                item.Id == snapshot.ReplacementDispatchId && item.Stage == "produce") == true;
            if (operation is null || operation.RunId != snapshot.RunId ||
                operation.DispatchId != snapshot.ReplacementDispatchId ||
                JsonSerializer.Serialize(operation.OutputRevision, JsonOptions) != JsonSerializer.Serialize(snapshot, JsonOptions) ||
                !validated && !changedByHuman ||
                !replacement || !files.SameEvidencePath(snapshot.ArchivePath,
                    files.SquadRoot + "/payloads/" + snapshot.Id + "-output.bin") ||
                files.HashFile(snapshot.ArchivePath) != snapshot.Output.Sha256 ||
                !state.DispatchRecords.Any(item => item.DispatchId == snapshot.SourceDispatchId &&
                    (item.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? [])
                        .Any(output => output == snapshot.Output)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a producing revision lost its exact source, archive or independent validation authority.");
            }
        }
    }

    internal void RecordModeHistory(RuntimeState state, SquadGateRecord record, string mode)
    {
        using (SyncRoot.EnterScope())
        {
            var path = ModeHistoryPath(record, mode);
            var frame = ModeHistoryFrame(record);
            if (File.Exists(files.Resolve(path)) && files.Read(path, MaximumStateCharacters).Contains(frame, StringComparison.Ordinal))
            {
                VerifyBaseline(path, state.AppendOnlyFiles[path]);
                return;
            }
            Append(state, path, frame);
            Save(state);
        }
    }

    private string ModeHistoryPath(SquadGateRecord record, string mode) =>
        files.SquadRoot + "/history/" + (mode == "autopilot" ? "autopilot-run-" : "autonomous-loop-") + record.TopicId + ".md";

    private static string ModeHistoryFrame(SquadGateRecord record)
    {
        var result = ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload);
        return "\n## Run " + record.RunId + " " + record.Id + "\n\n* Opt-In: mode=" + result.Result.Mode +
            "\n* Outcome: " + result.Result.Outcome +
            "\n\n| Stage | Role | Dispatch Record | Result |\n| --- | --- | --- | --- |\n" +
            string.Join("\n", result.Stages.Select(stage =>
                $"| {stage.UnitSlot ?? stage.Stage} | {stage.Role} | {stage.HistoryPath}#{stage.DispatchId} | {stage.Verdict} |")) +
            "\n\n| Cycle | Verdict | Decision Ref |\n| --- | --- | --- |\n" +
            string.Join("\n", result.Result.Iterations.Select(iteration =>
                $"| {iteration.Cycle} | {iteration.Verdict} | {iteration.DecisionRef} |")) +
            "\n\n```json\n" + JsonSerializer.Serialize(record, JsonOptions) + "\n```\n";
    }

    private bool RecoverModeHistory(RuntimeState state)
    {
        var recovered = false;
        foreach (var record in (state.GateRecords ?? []).Where(item => item.Kind == "Mode Outcome"))
        {
            var outcome = ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload);
            var path = ModeHistoryPath(record, outcome.Result.Mode);
            if (File.Exists(files.Resolve(path)) &&
                files.Read(path, MaximumStateCharacters).Contains(ModeHistoryFrame(record), StringComparison.Ordinal))
            {
                continue;
            }
            if (record.RunId != state.Checkpoint?.RunId || recovered ||
                !state.NativeOperations.Any(operation => operation.GateRecord?.Id == record.Id))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a mode summary is missing outside one known interrupted handoff.");
            }
            RecordModeHistory(state, record, outcome.Result.Mode);
            recovered = true;
        }
        return recovered;
    }

    private void VerifyModeHistory(RuntimeState state)
    {
        foreach (var records in (state.GateRecords ?? []).Where(record => record.Kind == "Mode Outcome")
            .GroupBy(record => ModeHistoryPath(record, ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload).Result.Mode),
                StringComparer.Ordinal))
        {
            if (!File.Exists(files.Resolve(records.Key)))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: a recorded mode history file is missing.");
            }
            var text = files.Read(records.Key, MaximumStateCharacters);
            foreach (var record in records)
            {
                var outcome = ReadinessContracts.Parse<ModeHistoryOutcome>(record.Payload);
                if (!text.Contains(ModeHistoryFrame(record), StringComparison.Ordinal) ||
                    outcome.Stages.Any(stage => !state.DispatchRecords.Any(source =>
                        source.DispatchId == stage.DispatchId &&
                        JsonSerializer.Serialize(source, JsonOptions) == JsonSerializer.Serialize(stage, JsonOptions))) ||
                    outcome.Result.Outcome == "completed" &&
                        (!outcome.Stages.Any(stage => stage.Stage == "research") ||
                            !outcome.Stages.Any(stage => stage.Stage == "plan") ||
                            !outcome.Stages.Any(stage => stage.Stage == "produce") ||
                            !outcome.Stages.Any(stage => stage.Stage == "review") ||
                            outcome.Result.Mode == "autopilot" && !outcome.Result.FinalValidated))
                {
                    throw new RuntimeHistoryIntegrityException(
                        "history-integrity: a mode summary lost its exact dispatch records, final validation or append-only frame.");
                }
            }
        }
    }
}
