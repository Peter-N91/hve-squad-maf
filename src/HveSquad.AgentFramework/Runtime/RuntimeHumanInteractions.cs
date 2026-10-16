using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

internal sealed partial class RuntimeScribe
{
    internal string StoreHumanPayload(string actionId, object payload, string kind)
    {
        if (!Guid.TryParseExact(actionId, "N", out _) || kind is not ("action" or "input"))
        {
            throw new InvalidDataException("Invalid protected human-action payload identity.");
        }
        var path = $"{files.SquadRoot}/payloads/{actionId}-human-{kind}.json";
        if (File.Exists(files.Resolve(path)))
        {
            throw new RuntimeHistoryIntegrityException("human-action: an immutable protected payload already exists.");
        }
        files.Write(path, JsonSerializer.Serialize(payload, JsonOptions), maximumCharacters: MaximumStateCharacters);
        return path;
    }

    internal HumanActionPayload ReadHumanPayload(HumanActionCheckpoint action)
    {
        var path = $"{files.SquadRoot}/payloads/{action.Action.Binding.Id}-human-action.json";
        if (action.PayloadPath != path || files.HashFile(path) != action.Action.Binding.PayloadSha256)
        {
            throw new RuntimeHistoryIntegrityException("human-action: protected pending payload changed; the old response cannot authorize it.");
        }
        var payload = JsonSerializer.Deserialize<HumanActionPayload>(files.Read(path, MaximumStateCharacters), JsonOptions)
            ?? throw new RuntimeHistoryIntegrityException("human-action: protected pending payload is missing.");
        if ((payload.Approval is null) == (payload.Question is null) ||
            HumanActions.Hash(payload) != action.Action.Binding.ActionSha256)
        {
            throw new RuntimeHistoryIntegrityException("human-action: pending action content differs from its exact binding.");
        }
        return payload;
    }

    internal string ReadHumanInput(string actionId, string path, string digest)
    {
        if (!files.SameEvidencePath(path, $"{files.SquadRoot}/payloads/{actionId}-human-input.json") ||
            files.HashFile(path) != digest)
        {
            throw new RuntimeHistoryIntegrityException("human-action: protected human input changed.");
        }
        return JsonSerializer.Deserialize<string>(files.Read(path, MaximumStateCharacters), JsonOptions)
            ?? throw new RuntimeHistoryIntegrityException("human-action: protected human input is missing.");
    }

    internal void RecordHumanDecision(RuntimeState state, HumanActionCheckpoint pending, HumanDecisionReceipt receipt)
    {
        using (SyncRoot.EnterScope())
        {
            if (pending.Decision is not null ||
                (state.HumanDecisions ?? []).Any(record => record.Binding.Id == receipt.Binding.Id))
            {
                throw new RuntimeHistoryIntegrityException("human-action: a decision cannot be replayed or replaced.");
            }
            _ = ReadHumanPayload(pending);
            state.HumanDecisions ??= [];
            state.HumanDecisions.Add(receipt);
            pending.Decision = receipt;
            pending.Action = pending.Action with { Status = receipt.Decision.ToString().ToLowerInvariant() };
            RecordReportHumanGate(state, pending.Action);
            RecordNativeOperation(state, "RecordHumanDecision", receipt.Binding.RunId,
                receipt.Binding.DispatchId, humanDecision: receipt);
            Notification(state, $"Human action {receipt.Binding.Id}: {receipt.Decision}; authenticated {receipt.Provider} actor hash recorded.");
            Save(state);
        }
    }

    internal void RestoreHumanAnswer(RuntimeState state, HumanActionCheckpoint pending)
    {
        var receipt = pending.Decision;
        if (receipt?.Decision != SquadHumanDecision.Answer)
        {
            return;
        }
        var question = ReadHumanPayload(pending).Question
            ?? throw new RuntimeHistoryIntegrityException("human-action: an answer has no exact protected question.");
        var input = ReadHumanInput(receipt.Binding.Id,
            receipt.InputPath ?? throw new RuntimeHistoryIntegrityException("human-action: answer input is missing."),
            receipt.InputSha256 ?? throw new RuntimeHistoryIntegrityException("human-action: answer input digest is missing."));
        var answer = new SquadConfirmedAnswer(receipt.Binding.RunId, question.Role, question.Question, input,
            question.DispatchId, receipt.Binding.Id);
        var prior = state.ConfirmedAnswers.Where(item => item.HumanActionId == receipt.Binding.Id).ToArray();
        if (prior.Length > 1 || prior.Length == 1 && prior[0] != answer)
        {
            throw new RuntimeHistoryIntegrityException("human-action: a confirmed human answer cannot be replaced.");
        }
        if (prior.Length == 0)
        {
            state.ConfirmedAnswers.Add(answer);
        }
        state.OpenQuestions.RemoveAll(item => item.Role == question.Role && item.Question == question.Question &&
            item.DispatchId == question.DispatchId);
    }

    internal void VerifyHumanDecisions(RuntimeState state)
    {
        var decisions = state.HumanDecisions ?? [];
        if (decisions.Select(record => record.Binding.Id).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
        {
            throw new RuntimeHistoryIntegrityException("human-action: duplicate human decision identities.");
        }
        foreach (var decision in decisions)
        {
            var native = state.NativeOperations.Where(operation => operation.HumanDecision?.Binding.Id == decision.Binding.Id).ToArray();
            if (native.Length != 1 || HumanActions.Serialize(native[0].HumanDecision) != HumanActions.Serialize(decision))
            {
                throw new RuntimeHistoryIntegrityException("human-action: a human decision has no exact immutable native receipt.");
            }
            if (decision.InputPath is not null)
            {
                _ = ReadHumanInput(decision.Binding.Id, decision.InputPath,
                    decision.InputSha256 ?? throw new RuntimeHistoryIntegrityException("human-action: human input has no digest."));
            }
        }
        foreach (var operation in state.NativeOperations.Where(operation => operation.HumanDecision is not null))
        {
            if (!decisions.Any(decision => HumanActions.Serialize(decision) == HumanActions.Serialize(operation.HumanDecision)))
            {
                throw new RuntimeHistoryIntegrityException("human-action: a recorded human decision was removed or altered.");
            }
        }
        foreach (var pending in state.Checkpoint?.HumanActions ?? [])
        {
            _ = ReadHumanPayload(pending);
            if (pending.Decision is { } receipt &&
                !decisions.Any(decision => HumanActions.Serialize(decision) == HumanActions.Serialize(receipt)))
            {
                throw new RuntimeHistoryIntegrityException("human-action: pending decision differs from its immutable receipt.");
            }
        }
        foreach (var answer in state.ConfirmedAnswers.Where(answer => answer.HumanActionId is not null))
        {
            var receipt = decisions.SingleOrDefault(decision => decision.Binding.Id == answer.HumanActionId);
            if (receipt?.Decision != SquadHumanDecision.Answer || receipt.Binding.RunId != answer.RunId ||
                receipt.InputPath is null || receipt.InputSha256 is null)
            {
                throw new RuntimeHistoryIntegrityException("human-action: an authenticated answer has no exact human-only receipt.");
            }
            var payload = ReadHumanPayload(new()
            {
                Action = new(receipt.Binding, true, receipt.Provider, receipt.RecordedAt, receipt.RecordedAt, "consumed"),
                Scope = "historical-answer",
                PayloadPath = $"{files.SquadRoot}/payloads/{receipt.Binding.Id}-human-action.json",
            });
            if (payload.Question is not { } question || question.Role != answer.Role ||
                question.Question != answer.Question || question.DispatchId != answer.DispatchId ||
                ReadHumanInput(receipt.Binding.Id, receipt.InputPath, receipt.InputSha256) != answer.Answer)
            {
                throw new RuntimeHistoryIntegrityException("human-action: a confirmed answer was altered after authenticated submission.");
            }
        }
    }
}
