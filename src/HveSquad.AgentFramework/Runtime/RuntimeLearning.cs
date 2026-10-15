using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Runtime;

internal sealed partial class RuntimeScribe
{
    internal void RecordLearningOperation(RuntimeState state, LearningOperationRecord record)
    {
        using (SyncRoot.EnterScope())
        {
            var prior = state.NativeOperations.Select(operation => operation.LearningRecord)
                .LastOrDefault(item => item?.Mutation.Id == record.Mutation.Id);
            if (prior is null && record.Status != "started" || prior is not null &&
                (prior.Status == "applied" || record.Status == "started" ||
                    HumanActions.Hash(prior with { Status = record.Status, Receipt = record.Receipt }) != HumanActions.Hash(record)))
            {
                throw new RuntimeHistoryIntegrityException("learning-action: original mutation/approval changed or an attempted action would be replayed.");
            }
            ValidateLearningRecord(record);
            if (record.Status == "started")
            {
                foreach (var consent in record.PreparationConsents.Append(record.Approval))
                {
                    RecordReportHumanGate(state, new ReportHumanGate(consent.Binding.Id, consent.Binding.RunId,
                        consent.Binding.Gate, null, null, "human-only-learning", false, "approve", consent.Provider, consent.RecordedAt));
                }
            }
            RecordNativeOperation(state, "Learning" + record.Mutation.Kind + ":" + record.Status,
                record.PromotionId, null, learningRecord: record);
            _ = ReconcileLedger(state, expectedNewWork: true);
            Save(state);
        }
    }

    internal static bool HasUnresolvedLearning(RuntimeState state) =>
        state.NativeOperations.Select(operation => operation.LearningRecord).Where(record => record is not null)
            .Select(record => record!).GroupBy(record => record.Mutation.Id, StringComparer.Ordinal)
            .Any(group => group.Last().Status != "applied");

    private static void ValidateLearningRecord(LearningOperationRecord record)
    {
        var mutation = record.Mutation;
        if (!Guid.TryParseExact(record.PromotionId, "N", out _) || !HumanActions.IsHash(record.PreparedSeal) ||
            record.Status is not ("started" or "unverified" or "applied") ||
            !Guid.TryParseExact(mutation.Id, "N", out _) || mutation.Kind is not ("fork" or "branch" or "write" or "pull-request") ||
            mutation.PromotionId != record.PromotionId || mutation.Id != LearningContracts.ActionId(record.PromotionId, mutation.Kind) ||
            !WatchContracts.IsRepository(mutation.Repository) || !WatchContracts.IsRepository(mutation.OriginRepository) ||
            mutation.Path is not (LearningContracts.UpstreamPath or LearningContracts.TenantPath) ||
            !LearningContracts.IsBranch(mutation.Branch) || !mutation.Branch.StartsWith("squad-learning/", StringComparison.Ordinal) ||
            !LearningContracts.IsBranch(mutation.BaseBranch) || !LearningContracts.IsCommit(mutation.BaseCommitSha) ||
            !LearningContracts.IsCommit(mutation.FileSha) || mutation.ContentSha256 != ProjectFiles.Hash(mutation.Content) ||
            record.Approval.Binding.ActionSha256 != HumanActions.Hash(mutation) ||
            !HumanActions.IsHash(record.Approval.SubjectSha256) || !HumanActions.IsReference(record.Approval.Provider) ||
            !HumanActions.IsReference(record.Approval.ResponseId) || record.PreparationConsents.Count != 2 ||
            record.PreparationConsents.Any(consent => consent.SubjectSha256 != record.Approval.SubjectSha256 ||
                consent.Provider != record.Approval.Provider || consent.Binding.HostBinding != record.Approval.Binding.HostBinding ||
                consent.Binding.RootSha256 != record.Approval.Binding.RootSha256) ||
            record.PreparationConsents.Select(consent => consent.Binding.Gate)
                .SequenceEqual(["LearningSanitization", "LearningRepository"], StringComparer.Ordinal) != true ||
            record.Status == "applied" && (record.Receipt is null || record.Receipt.ActionSha256 != HumanActions.Hash(mutation)))
        {
            throw new RuntimeHistoryIntegrityException("learning-action: an original action, human confirmation or receipt lost its exact binding.");
        }
    }

    private static void VerifyLearningOperations(RuntimeState state)
    {
        foreach (var group in state.NativeOperations.Select(operation => operation.LearningRecord)
            .Where(record => record is not null).Select(record => record!)
            .GroupBy(record => record.Mutation.Id, StringComparer.Ordinal))
        {
            LearningOperationRecord? prior = null;
            foreach (var record in group)
            {
                ValidateLearningRecord(record);
                if (prior is null && record.Status != "started" || prior is not null &&
                    (prior.Status == "applied" || record.Status == "started" ||
                        HumanActions.Hash(prior with { Status = record.Status, Receipt = record.Receipt }) != HumanActions.Hash(record)))
                {
                    throw new RuntimeHistoryIntegrityException("learning-action: an immutable action was replayed, reordered or changed.");
                }
                prior = record;
            }
        }
    }

    internal SquadRoleMemoryEntry RememberRoleMemory(RuntimeState state, SquadMemoryNote note,
        string bindingSha256, DateTimeOffset at, int maximumEntries)
    {
        using (SyncRoot.EnterScope())
        {
            if ((state.RoleMemories ?? []).Count(entry => entry.Note.Role == note.Role) >= maximumEntries)
            {
                throw new InvalidOperationException("This role's memory limit is reached. Existing notes are retained, not silently evicted.");
            }
            var entry = new SquadRoleMemoryEntry(Guid.NewGuid().ToString("N"),
                MemoryContracts.PathFor(files.SquadRoot, note.Role), bindingSha256, note, at);
            state.RoleMemories ??= [];
            state.RoleMemories.Add(entry);
            var header = File.Exists(files.Resolve(entry.Path)) ? "" : "# Native role memory\n";
            Append(state, entry.Path, header + Frame("Memory", entry.Id, entry));
            RecordNativeOperation(state, "RecordRoleMemory", null, null, roleMemory: entry);
            _ = ReconcileLedger(state, expectedNewWork: true);
            Save(state);
            return entry;
        }
    }

    internal SquadRoleMemoryEntry[] ReadRoleMemory(RuntimeState state, string role, SquadMemoryConfiguration configuration)
    {
        VerifyRoleMemory(state);
        var result = (state.RoleMemories ?? []).Where(entry => entry.Note.Role == role).ToArray();
        if (result.Any(entry => entry.BindingSha256 != HumanActions.Hash(configuration.Binding)) ||
            result.Length > configuration.MaximumEntriesPerRole)
        {
            throw new UnauthorizedAccessException("Original role memory belongs to a different policy binding or exceeds this host's read limit.");
        }
        foreach (var entry in result)
        {
            MemoryContracts.ValidateNote(entry.Note, configuration);
        }
        return result;
    }

    private bool RecoverRoleMemoryReceipts(RuntimeState state)
    {
        var missing = (state.RoleMemories ?? []).Where(entry =>
            !state.NativeOperations.Any(operation => operation.RoleMemory?.Id == entry.Id)).ToArray();
        if (missing.Length == 0)
        {
            return false;
        }
        if (missing.Length != 1)
        {
            throw new RuntimeHistoryIntegrityException("memory-integrity: more than one original memory receipt is missing.");
        }
        VerifyRoleMemory(state, allowMissingReceipt: true);
        RecordNativeOperation(state, "RecordRoleMemory", null, null, roleMemory: missing[0]);
        Save(state);
        return true;
    }

    private void VerifyRoleMemory(RuntimeState state, bool allowMissingReceipt = false)
    {
        var entries = state.RoleMemories ?? [];
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new RuntimeHistoryIntegrityException("memory-integrity: duplicate note identities.");
        }
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(entry => entry.Note.Role, StringComparer.Ordinal))
        {
            var path = MemoryContracts.PathFor(files.SquadRoot, group.Key);
            if (!MemoryContracts.IsMemoryPath(files, path) || !state.AppendOnlyFiles.ContainsKey(path))
            {
                throw new RuntimeHistoryIntegrityException("memory-integrity: a note escaped its original protected role scope.");
            }
            expectedPaths.Add(path);
            var text = files.Read(path, MaximumStateCharacters);
            var offset = 0;
            foreach (var entry in group)
            {
                if (!Guid.TryParseExact(entry.Id, "N", out _) || !HumanActions.IsHash(entry.BindingSha256) ||
                    !files.SameEvidencePath(entry.Path, path))
                {
                    throw new RuntimeHistoryIntegrityException("memory-integrity: original note identity, policy or source changed.");
                }
                var frame = Frame("Memory", entry.Id, entry);
                var index = text.IndexOf(frame, offset, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new RuntimeHistoryIntegrityException("memory-integrity: notes were removed, changed or reordered.");
                }
                offset = index + frame.Length;
                var native = state.NativeOperations.Where(operation => operation.RoleMemory?.Id == entry.Id).ToArray();
                if (native.Length != 1 && !(allowMissingReceipt && native.Length == 0) ||
                    native.Length == 1 && HumanActions.Hash(native[0].RoleMemory) != HumanActions.Hash(entry))
                {
                    throw new RuntimeHistoryIntegrityException("memory-integrity: a note lacks its exact original Scribe receipt.");
                }
            }
            if (Regex.Count(text, @"(?m)^## Memory [a-f0-9]{32}$", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)) != group.Count())
            {
                throw new RuntimeHistoryIntegrityException("memory-integrity: unrecorded or duplicate memory frames.");
            }
        }
        if (state.NativeOperations.Any(operation => operation.RoleMemory is { } memory &&
            !entries.Any(entry => HumanActions.Hash(entry) == HumanActions.Hash(memory))))
        {
            throw new RuntimeHistoryIntegrityException("memory-integrity: an original note was removed from the state index.");
        }
        var root = files.SquadRoot + "/memory";
        if (Directory.Exists(files.Resolve(root)) &&
            !FederationPersistence.Enumerate(files, root).Files.Select(file => file.Path)
                .Order(StringComparer.Ordinal).SequenceEqual(expectedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new RuntimeHistoryIntegrityException("memory-integrity: unbound files are not consumer-local Scribe memory.");
        }
    }
}
