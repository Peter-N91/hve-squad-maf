using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>An untrusted reference to an actual human initiation event; a reference alone grants no authority.</summary>
public sealed record SquadLearningInvocation(string Reference);

/// <summary>Host authentication must verify human initiation of this exact helper, not merely a human-owned session.</summary>
public sealed record SquadLearningInvocationAccess(SquadLearningInvocation Invocation, string Operation,
    string SquadRoot, string RootSha256);

/// <summary>One exact local source proposed for learning disclosure, separate from report or Watch rights.</summary>
public sealed record SquadLearningSourceAccess(string SquadRoot, string Path, string Kind);

/// <summary>Verified local grounding. Paths and original hashes never travel to a learning connector.</summary>
public sealed record SquadLearningSource(string Path, string Sha256, string Kind);

/// <summary>Descriptive generalized fields; identity and date are assigned from the actual source playbook.</summary>
public sealed record SquadLearningDraft(string Scenario, string Rule, string Scope, string SourceContext);

/// <summary>A sanitized suggestion, not an authoritative playbook rule or publication approval.</summary>
public sealed record SquadLearningCandidate(string Id, string SquadRoot, SquadLearningDraft Draft,
    IReadOnlyList<SquadLearningSource> Sources);

/// <summary>Read-only candidate discovery and explicit gaps. Neither memory nor native state is written.</summary>
public sealed record SquadLearningDiscovery(IReadOnlyList<SquadLearningCandidate> Candidates, IReadOnlyList<string> Diagnostics);

/// <summary>Explicit public upstream fork or tenant repository; deployed local playbooks are never targets.</summary>
public sealed record SquadLearningTarget(string Kind, string Repository, string? DestinationRepository = null,
    bool ForkOrganization = false);

/// <summary>Exact source file at a commit. A connector re-reads provider state; a host snapshot yields prepared-only evidence.</summary>
public sealed record SquadLearningTargetSnapshot(string Repository, string Path, string BaseBranch,
    string CommitSha, string FileSha, string Content);

/// <summary>The six released row fields. Source context is sanitized and contains no local source paths.</summary>
public sealed record SquadLearningEntry(string Id, string Scenario, string Rule, string Scope, string SourceContext, string DateAdded);

/// <summary>An authorized local human preview. The channel notification receives only its safe action metadata.</summary>
public sealed record SquadLearningConfirmation(SquadPendingHumanAction Action, string Description,
    SquadLearningDraft Draft, SquadLearningTarget Target, IReadOnlyList<string> Checklist);

/// <summary>Original authenticated specific approval, not a string asserted by a model or a notification.</summary>
public sealed record SquadLearningConsent(SquadHumanActionBinding Binding, string Provider,
    string SubjectSha256, string ResponseId, DateTimeOffset RecordedAt);

/// <summary>Prepared immutable candidate, complete source-file diff and exact human approvals; no remote effect is implied.</summary>
public sealed record SquadLearningPrepared(string Id, string SquadRoot, string RootSha256, string HostBinding,
    string ReleaseFingerprint, SquadLearningTarget Target, SquadLearningTargetSnapshot Snapshot,
    SquadLearningEntry Entry, string Content, string Diff, string Branch,
    IReadOnlyList<SquadLearningSource> Sources, IReadOnlyList<SquadLearningConsent> Consents, string Seal);

/// <summary>One separately gated remote action. It contains sanitized target content, never local memory or source paths.</summary>
public sealed record SquadLearningMutation(string Id, string PromotionId, string Kind, string OriginRepository, string Repository,
    string Path, string Branch, string BaseBranch, string BaseCommitSha, string FileSha, string Content,
    string ContentSha256, string EntryId, string? HeadCommitSha = null, bool ForkOrganization = false);

/// <summary>Actual provider postconditions, independently verified again before a result is accepted.</summary>
public sealed record SquadLearningMutationReceipt(string ActionSha256, string ConnectorBinding, string Kind,
    string Repository, string ResourceId, string Reference, string? CommitSha = null, string? ContentSha256 = null);

/// <summary>
/// Trusted explicit remote capability. Each call requires a separate authenticated human approval.
/// Implementations must enforce their own tenant/resource rights and prove actual provider postconditions.
/// </summary>
public interface ISquadLearningConnector
{
    /// <summary>Stable non-secret provider/configuration/policy identity.</summary>
    string Binding { get; }
    /// <summary>Reads the exact explicit source repository and released learning file without mutation.</summary>
    ValueTask<SquadLearningTargetSnapshot> ReadTargetAsync(SquadLearningTarget target, CancellationToken cancellationToken);
    /// <summary>Checks independent host resource rights. This is not the human impactful-action approval.</summary>
    ValueTask<bool> AuthorizeAsync(SquadLearningMutation mutation, CancellationToken cancellationToken);
    /// <summary>Executes only this approved action and returns actual provider observations.</summary>
    ValueTask<SquadLearningMutationReceipt> ExecuteAsync(SquadLearningMutation mutation, CancellationToken cancellationToken);
    /// <summary>Independently re-reads the exact provider postconditions; a supplied URL is not proof.</summary>
    ValueTask<bool> VerifyAsync(SquadLearningMutation mutation, SquadLearningMutationReceipt receipt, CancellationToken cancellationToken);
    /// <summary>Read-only lookup after interruption. Null leaves uncertainty; this method must never replay a mutation.</summary>
    ValueTask<SquadLearningMutationReceipt?> ReconcileAsync(SquadLearningMutation mutation, CancellationToken cancellationToken);
}

/// <summary>Host-owned human initiation, source disclosure, sanitization and optional remote capabilities.</summary>
public sealed class SquadLearningConfiguration
{
    /// <summary>Opaque stable human-learning policy revision.</summary>
    public required string Binding { get; set; }
    /// <summary>Required actual human initiation authentication. Coordinator/child/session identity alone must be refused.</summary>
    public Func<SquadLearningInvocationAccess, CancellationToken, ValueTask<SquadHumanAuthorization?>>? AuthorizeInvocationAsync { get; set; }
    /// <summary>Required per-source permission to curate authorized local/public evidence.</summary>
    public Func<SquadLearningSourceAccess, CancellationToken, ValueTask<bool>>? AuthorizeSourceAsync { get; set; }
    /// <summary>Original authenticated response channel; report disclosure and generic approval callbacks are not substitutes.</summary>
    public ISquadHumanApprovalChannel? HumanApprovalChannel { get; set; }
    /// <summary>Displays the sanitized draft and exact action, then reads an actual response. Null withholds approval.</summary>
    public Func<SquadLearningConfirmation, CancellationToken, ValueTask<SquadHumanResponse?>>? ConfirmAsync { get; set; }
    /// <summary>Explicit trusted connector. Absence returns prepared diff/commands, never claimed publication.</summary>
    public ISquadLearningConnector? Connector { get; set; }
    /// <summary>Optional exact host-registered tenant repository. The provider must independently verify its private visibility.</summary>
    public string? TenantRepository { get; set; }
    /// <summary>Host-known secrets. Values are scrubbed locally and never persisted or logged.</summary>
    public IList<string> SecretValues { get; set; } = [];
    /// <summary>Customer, organization and individual names to remove in addition to conservative name-token filtering.</summary>
    public IList<string> SensitiveNames { get; set; } = [];
    /// <summary>Bound on candidate source files; incomplete scans are refused rather than silently truncated.</summary>
    public int MaximumSources { get; set; } = 256;
    /// <summary>Bound on descriptive fields in a candidate.</summary>
    public int MaximumDraftCharacters { get; set; } = 16_000;
    /// <summary>Bound on the original and proposed remote playbook.</summary>
    public int MaximumTargetCharacters { get; set; } = 200_000;
    /// <summary>Specific challenge lifetime; an expired response grants no permission.</summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Prepared, stopped, reconciliation-required or actual PR-created outcome. Created PRs still require human review.</summary>
public sealed record SquadLearningResult(string Status, string Diagnostic, SquadLearningPrepared? Prepared,
    IReadOnlyList<string> Commands, IReadOnlyList<SquadLearningMutationReceipt> Receipts,
    SquadPendingHumanAction? Pending = null);

internal sealed record LearningOperationRecord(string PromotionId, string PreparedSeal, string Status, SquadLearningMutation Mutation,
    SquadLearningConsent Approval, IReadOnlyList<SquadLearningConsent> PreparationConsents,
    SquadLearningMutationReceipt? Receipt = null);

/// <summary>
/// Human-only native Squad Learn, outside every model inventory. Discovery and preparation are state-read-only.
/// Specific remote attempts use original Scribe receipts; an interrupted mutation is never automatically replayed.
/// </summary>
public sealed partial class SquadLearning
{
    private static readonly string[] Checklist =
    [
        "Secrets, tokens, credentials and connection strings removed",
        "Customer, organization and individual names removed",
        "Absolute local paths and internal URLs removed",
        "Stack and environment specifics generalized",
        "Broad applicability confirmed; uncertain content must not be promoted",
        "Source playbook license notices preserved; final PR review remains required",
    ];
    private readonly ProjectFiles _files;
    private readonly SquadLearningConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly string _rootBinding;
    private readonly string _hostBinding;

    /// <summary>Creates a human-only service without acquiring artifacts, resolving a model or initializing state.</summary>
    /// <param name="projectPath">Existing isolated consumer project.</param>
    /// <param name="configuration">Explicit human authentication, source policy and optional remote capability.</param>
    /// <param name="squadRoot">One native case/member. All-federation and global IDE-memory scans are prohibited.</param>
    /// <param name="timeProvider">Trusted clock.</param>
    public SquadLearning(string projectPath, SquadLearningConfiguration configuration,
        string squadRoot = ".copilot-tracking/squad", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!HumanActions.IsReference(configuration.Binding) ||
            configuration.MaximumSources is < 1 or > FederationPersistence.MaximumFiles ||
            configuration.MaximumDraftCharacters is < 1 or > 200_000 ||
            configuration.MaximumTargetCharacters is < 1 or > 500_000 ||
            configuration.ApprovalTimeout < TimeSpan.FromMinutes(1) || configuration.ApprovalTimeout > TimeSpan.FromDays(1) ||
            configuration.SecretValues.Any(string.IsNullOrEmpty) || configuration.SensitiveNames.Any(string.IsNullOrWhiteSpace) ||
            configuration.TenantRepository is not null && !WatchContracts.IsRepository(configuration.TenantRepository) ||
            configuration.Connector is { } connector && !HumanActions.IsReference(connector.Binding))
        {
            throw new ArgumentException("Learning capabilities require non-secret policy bindings, bounded sources/text and explicit valid sanitization denies.", nameof(configuration));
        }
        _configuration = new()
        {
            Binding = configuration.Binding,
            AuthorizeInvocationAsync = configuration.AuthorizeInvocationAsync,
            AuthorizeSourceAsync = configuration.AuthorizeSourceAsync,
            HumanApprovalChannel = configuration.HumanApprovalChannel,
            ConfirmAsync = configuration.ConfirmAsync,
            Connector = configuration.Connector,
            TenantRepository = configuration.TenantRepository?.ToLowerInvariant(),
            SecretValues = configuration.SecretValues.ToArray(),
            SensitiveNames = configuration.SensitiveNames.ToArray(),
            MaximumSources = configuration.MaximumSources,
            MaximumDraftCharacters = configuration.MaximumDraftCharacters,
            MaximumTargetCharacters = configuration.MaximumTargetCharacters,
            ApprovalTimeout = configuration.ApprovalTimeout,
        };
        _files = new(projectPath, configuration.MaximumTargetCharacters, squadRoot);
        _clock = timeProvider ?? TimeProvider.System;
        _rootBinding = HumanActions.Hash(new
        {
            Project = OperatingSystem.IsWindows() ? _files.ProjectRoot.ToUpperInvariant() : _files.ProjectRoot,
            _files.SquadRoot,
        });
        _hostBinding = HumanActions.Hash(new
        {
            configuration.Binding,
            Channel = configuration.HumanApprovalChannel?.Binding,
            Connector = configuration.Connector?.Binding,
            _configuration.TenantRepository,
            configuration.ApprovalTimeout,
        });
    }

    /// <summary>Discovers sanitized durable notes and explicitly marked conventions from authorized original state.</summary>
    /// <param name="invocation">Reference to actual human initiation, authenticated by the host.</param>
    /// <param name="learning">Optional descriptive focus; it grants no control rights.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Read-only sanitized suggestions with exact local grounding and gaps.</returns>
    public async Task<SquadLearningDiscovery> DiscoverAsync(SquadLearningInvocation invocation, string? learning = null,
        CancellationToken cancellationToken = default)
    {
        _ = await AuthenticateAsync(invocation, "discover", cancellationToken).ConfigureAwait(false);
        using var snapshot = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var result = await DiscoverAsync(snapshot.Cases.Single(), learning, cancellationToken).ConfigureAwait(false);
        snapshot.AssertUnchanged();
        return result;
    }

    /// <summary>Confirms sanitization and the explicit repository, then reads existing rows and prepares the exact diff.</summary>
    /// <param name="invocation">Actual human initiation.</param>
    /// <param name="candidate">One originally grounded discovery candidate.</param>
    /// <param name="target">Explicit reach and source/fork repositories.</param>
    /// <param name="targetSnapshot">Required host-provided source snapshot when no connector is registered.</param>
    /// <param name="curatedDraft">Optional human rewrite; it is sanitized and specifically confirmed again.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Prepared-only or stopped outcome, with no native or remote writes.</returns>
    public async Task<SquadLearningResult> PrepareAsync(SquadLearningInvocation invocation, SquadLearningCandidate candidate,
        SquadLearningTarget target, SquadLearningTargetSnapshot? targetSnapshot = null, SquadLearningDraft? curatedDraft = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        target = LearningContracts.ValidateTarget(target);
        ValidateTargetPolicy(target);
        var actor = await AuthenticateAsync(invocation, "prepare", cancellationToken).ConfigureAwait(false);
        using var snapshot = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var item = snapshot.Cases.Single();
        var current = await DiscoverAsync(item, null, cancellationToken).ConfigureAwait(false);
        var selected = current.Candidates.SingleOrDefault(entry => entry.Id == candidate.Id);
        if (selected is null || HumanActions.Hash(selected) != HumanActions.Hash(candidate))
        {
            throw new RuntimeHistoryIntegrityException("learning-source: the selected original candidate changed or is outside this authorized case.");
        }
        var draft = Sanitize(curatedDraft ?? selected.Draft, item);
        if (draft is null)
        {
            snapshot.AssertUnchanged();
            return Stopped("Sanitization could not be established. Rewrite locally or leave the learning out; no target was read or changed.");
        }
        var sanitized = await ConfirmAsync(item.State, actor, invocation, candidate.Id, "LearningSanitization",
            draft, target, new { Draft = draft, candidate.Sources },
            "Confirm every sanitization checklist item and broad applicability of this exact redacted draft.", cancellationToken).ConfigureAwait(false);
        if (sanitized.Consent is null)
        {
            snapshot.AssertUnchanged();
            return Stopped("Sanitization was not authenticated and confirmed; learning promotion stopped without writes.", sanitized.Pending);
        }
        var repository = await ConfirmAsync(item.State, actor, invocation, candidate.Id, "LearningRepository",
            draft, target, new { Target = target, Path = LearningContracts.TargetPath(target) },
            "Confirm the exact source and destination repositories. Upstream reaches all public consumers; tenant stays in its authorized organization.",
            cancellationToken).ConfigureAwait(false);
        if (repository.Consent is null)
        {
            snapshot.AssertUnchanged();
            return Stopped("The explicit repository/target was not confirmed; no repository read or mutation occurred.", repository.Pending);
        }
        if (sanitized.Consent.ResponseId == repository.Consent.ResponseId)
        {
            throw new UnauthorizedAccessException("Sanitization and repository confirmation require separate actual human responses.");
        }
        var remote = _configuration.Connector is { } connector
            ? await connector.ReadTargetAsync(target, cancellationToken).ConfigureAwait(false)
            : targetSnapshot ?? throw new InvalidOperationException("Without a connector, provide the actual explicit target-file snapshot to prepare IDs and commands.");
        var appended = LearningContracts.Append(target, remote, draft, _clock.GetUtcNow(), _configuration.MaximumTargetCharacters);
        var id = HumanActions.Hash(new { _rootBinding, target, remote.CommitSha, Entry = appended.Entry })[..32].ToLowerInvariant();
        var prepared = new SquadLearningPrepared(id, _files.SquadRoot, _rootBinding, _hostBinding,
            item.State.Fingerprint, target, remote, appended.Entry, appended.Content, appended.Diff,
            "squad-learning/" + appended.Entry.Id.ToLowerInvariant() + "-" + id[..12],
            selected.Sources.ToArray(), [sanitized.Consent, repository.Consent], "");
        prepared = prepared with { Seal = HumanActions.Hash(prepared with { Seal = "" }) };
        snapshot.AssertUnchanged();
        return Result("prepared", "The sanitized row, next-free ID, exact diff and commands are prepared. Nothing was forked, branched, pushed or opened.",
            prepared, []);
    }

    /// <summary>Executes each exact fork/branch/write/PR only after its own authenticated approval and actual postcondition checks.</summary>
    /// <param name="invocation">New actual human initiation; original preparation belongs to the same authenticated actor.</param>
    /// <param name="prepared">Original prepared candidate.</param>
    /// <param name="cancellationToken">Cancellation. Started effects require read-only reconciliation, never replay.</param>
    /// <returns>Actual PR-created, prepared, stopped or reconciliation-required outcome.</returns>
    public async Task<SquadLearningResult> PromoteAsync(SquadLearningInvocation invocation, SquadLearningPrepared prepared,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthenticateAsync(invocation, "promote", cancellationToken).ConfigureAwait(false);
        ValidatePrepared(prepared, actor);
        if (_configuration.Connector is not { } connector)
        {
            using var snapshot = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            await VerifySourcesAsync(snapshot.Cases.Single(), prepared, cancellationToken).ConfigureAwait(false);
            snapshot.AssertUnchanged();
            return Result("prepared", "No connector is registered. Run the prepared commands only after separately approving each mutation; no publication is claimed.",
                prepared, []);
        }
        MemoryContracts.RejectHub(_files);
        var scribe = new RuntimeScribe(_files, timeProvider: _clock);
        using var diskLock = scribe.AcquireLock();
        var state = scribe.Load() ?? throw new InvalidDataException("The original native learning case is missing.");
        MemoryContracts.RequireQuiescent(state);
        _ = scribe.PrepareEvidence(state);
        var item = Case(scribe, state);
        await VerifySourcesAsync(item, prepared, cancellationToken).ConfigureAwait(false);
        var clean = Sanitize(Draft(prepared.Entry), item);
        if (clean != Draft(prepared.Entry))
        {
            return Result("stopped", "The final row is not safely generalized under the current policy. Reprepare and reconfirm; no mutation executed.", prepared, []);
        }
        var priorPreparation = state.NativeOperations.Select(operation => operation.LearningRecord)
            .FirstOrDefault(record => record?.PromotionId == prepared.Id);
        IReadOnlyList<SquadLearningConsent> executionConsents;
        if (priorPreparation is not null)
        {
            if (priorPreparation.PreparedSeal != prepared.Seal ||
                priorPreparation.PreparationConsents.Any(consent => consent.Provider != actor.Provider || consent.SubjectSha256 != actor.SubjectSha256))
            {
                throw new UnauthorizedAccessException("Original preparation and actual authenticated human confirmations cannot be replaced.");
            }
            executionConsents = priorPreparation.PreparationConsents;
        }
        else
        {
            var sanitized = await ConfirmAsync(state, actor, invocation, prepared.Id, "LearningSanitization",
                clean, prepared.Target, new { prepared.Entry, prepared.Sources },
                "Confirm all sanitization checklist items for the final exact row. A prepared object's hashes are not human authority.",
                cancellationToken).ConfigureAwait(false);
            if (sanitized.Consent is null)
            {
                return Result("stopped", "Final sanitization was not authenticated and confirmed; no remote mutation executed.",
                    prepared, [], sanitized.Pending);
            }
            var repository = await ConfirmAsync(state, actor, invocation, prepared.Id, "LearningRepository",
                clean, prepared.Target, new { prepared.Target, prepared.Snapshot.Path, prepared.Branch },
                "Reconfirm the exact source/fork repositories, target file and prepared branch before remote work.", cancellationToken).ConfigureAwait(false);
            if (repository.Consent is null)
            {
                return Result("stopped", "Final repository confirmation was withheld; no remote mutation executed.", prepared, [], repository.Pending);
            }
            if (repository.Consent.ResponseId == sanitized.Consent.ResponseId)
            {
                throw new UnauthorizedAccessException("Final sanitization and repository confirmation require separate actual human responses.");
            }
            executionConsents = [sanitized.Consent, repository.Consent];
        }
        var receipts = new List<SquadLearningMutationReceipt>();
        string? head = null;
        foreach (var kind in prepared.Target.Kind == "upstream"
            ? new[] { "fork", "branch", "write", "pull-request" }
            : ["branch", "write", "pull-request"])
        {
            var mutation = Mutation(prepared, kind, head);
            var records = state.NativeOperations.Select(operation => operation.LearningRecord)
                .Where(record => record?.PromotionId == prepared.Id && record.Mutation.Kind == kind)
                .Select(record => record!).ToArray();
            if (records.Length > 0)
            {
                var original = records[0];
                if (HumanActions.Hash(original.Mutation) != HumanActions.Hash(mutation) ||
                    original.Approval.SubjectSha256 != actor.SubjectSha256)
                {
                    throw new RuntimeHistoryIntegrityException("learning-action: original mutation/actor binding changed; old authority cannot be reused.");
                }
                var latest = records[^1];
                if (latest.Status != "applied" || latest.Receipt is not { } prior)
                {
                    return Result("reconciliation-required", "An original learning action started without verified completion. Use read-only ReconcileAsync; it will not replay the action.",
                        prepared, receipts);
                }
                if (!await VerifyReceiptAsync(connector, mutation, prior, cancellationToken).ConfigureAwait(false))
                {
                    return Result("reconciliation-required", "Original provider postconditions no longer verify; no later learning action executed.", prepared, receipts);
                }
                receipts.Add(prior);
                if (kind == "write")
                {
                    head = prior.CommitSha;
                }
                continue;
            }
            var current = await connector.ReadTargetAsync(prepared.Target, cancellationToken).ConfigureAwait(false);
            if (HumanActions.Hash(current) != HumanActions.Hash(prepared.Snapshot))
            {
                return Result("stopped", "The source playbook changed. Rediscover/reprepare and reconfirm the new row/ID; no new remote mutation executed.", prepared, receipts);
            }
            if (!await connector.AuthorizeAsync(mutation, cancellationToken).ConfigureAwait(false))
            {
                return Result("stopped", "The registered connector denied this exact resource/action. Host permissions are separate from human approval.", prepared, receipts);
            }
            var confirmation = await ConfirmAsync(state, actor, invocation, prepared.Id, "Learning" + kind,
                Draft(prepared.Entry), prepared.Target, mutation,
                $"Approve only {kind} to {mutation.Repository}, origin {mutation.OriginRepository}, branch {mutation.Branch}, " +
                $"path {mutation.Path}, base commit {mutation.BaseCommitSha}, content SHA-256 {mutation.ContentSha256}. " +
                "A write creates/pushes one commit; PR creation never merges or publishes a package.", cancellationToken).ConfigureAwait(false);
            if (confirmation.Consent is null)
            {
                return Result("stopped", "This specific impactful action was not approved. No subsequent fork, branch, push/write or PR executed.",
                    prepared, receipts, confirmation.Pending);
            }
            if (executionConsents.Any(consent => consent.ResponseId == confirmation.Consent.ResponseId))
            {
                throw new UnauthorizedAccessException("A preparation response cannot approve an impactful learning mutation.");
            }
            await VerifySourcesAsync(item, prepared, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            scribe.RecordLearningOperation(state, new(prepared.Id, prepared.Seal, "started", mutation, confirmation.Consent, executionConsents));
            var actual = await connector.ExecuteAsync(mutation, cancellationToken).ConfigureAwait(false);
            if (!await VerifyReceiptAsync(connector, mutation, actual, cancellationToken).ConfigureAwait(false))
            {
                scribe.RecordLearningOperation(state, new(prepared.Id, prepared.Seal, "unverified", mutation, confirmation.Consent,
                    executionConsents));
                return Result("reconciliation-required", "The connector response did not prove the exact provider postconditions. It is not publication, and no action will be replayed.",
                    prepared, receipts);
            }
            scribe.RecordLearningOperation(state, new(prepared.Id, prepared.Seal, "applied", mutation, confirmation.Consent,
                executionConsents, actual));
            receipts.Add(actual);
            if (kind == "write")
            {
                head = actual.CommitSha;
            }
        }
        return Result("pull-request-created", "The actual provider PR and exact head/content are verified. Human source-repository review remains the final gate; nothing was merged or deployed.",
            prepared, receipts);
    }

    /// <summary>Re-reads one uncertain original provider action. Absence keeps it blocked; it never re-executes that action.</summary>
    /// <param name="invocation">Actual human reconciliation initiation.</param>
    /// <param name="prepared">Original preparation, retained by the host.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Prepared-to-resume or still reconciliation-required status, never fabricated publication.</returns>
    public async Task<SquadLearningResult> ReconcileAsync(SquadLearningInvocation invocation, SquadLearningPrepared prepared,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthenticateAsync(invocation, "reconcile", cancellationToken).ConfigureAwait(false);
        ValidatePrepared(prepared, actor);
        var connector = _configuration.Connector ?? throw new InvalidOperationException("Read-only provider reconciliation requires the original registered connector.");
        var scribe = new RuntimeScribe(_files, timeProvider: _clock);
        using var diskLock = scribe.AcquireLock();
        var state = scribe.Load() ?? throw new InvalidDataException("Original learning receipts are missing.");
        MemoryContracts.RequireQuiescent(state);
        _ = scribe.PrepareEvidence(state);
        var attempts = state.NativeOperations.Select(operation => operation.LearningRecord)
            .Where(record => record?.PromotionId == prepared.Id).Select(record => record!)
            .GroupBy(record => record.Mutation.Kind, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var unresolved = attempts.Where(record => record.Status != "applied").ToArray();
        if (unresolved.Length != 1)
        {
            throw new InvalidOperationException("Exactly one started/unverified original learning action is required for reconciliation.");
        }
        var original = unresolved[0];
        if (original.Approval.SubjectSha256 != actor.SubjectSha256)
        {
            throw new UnauthorizedAccessException("Only the original authorized human can reconcile this learning action.");
        }
        var observed = await connector.ReconcileAsync(original.Mutation, cancellationToken).ConfigureAwait(false);
        if (observed is null || !await VerifyReceiptAsync(connector, original.Mutation, observed, cancellationToken).ConfigureAwait(false))
        {
            return Result("reconciliation-required", "No exact independently verified provider postcondition was found. Restore evidence or reconcile externally; automatic replay is forbidden.",
                prepared, attempts.Where(record => record.Status == "applied" && record.Receipt is not null).Select(record => record.Receipt!).ToArray());
        }
        scribe.RecordLearningOperation(state, original with { Status = "applied", Receipt = observed });
        return Result("prepared", "The original remote action is now independently verified and recorded once. PromoteAsync may continue at the next separately approved action.",
            prepared, [observed]);
    }

    private async ValueTask<SquadHumanAuthorization> AuthenticateAsync(SquadLearningInvocation invocation, string operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!HumanActions.IsReference(invocation.Reference) || _configuration.AuthorizeInvocationAsync is null)
        {
            throw new UnauthorizedAccessException("Squad Learn requires explicit authentication of an actual human-only initiation event.");
        }
        var actor = await _configuration.AuthorizeInvocationAsync(new(invocation, operation, _files.SquadRoot, _rootBinding), token).ConfigureAwait(false);
        if (actor is null || !HumanActions.IsReference(actor.Provider) || !HumanActions.IsHash(actor.SubjectSha256))
        {
            throw new UnauthorizedAccessException("Squad Learn denied non-human, coordinator, child or unauthorized initiation; no scoped state was read.");
        }
        token.ThrowIfCancellationRequested();
        return actor;
    }

    private Task<ReportSnapshot> CaptureAsync(CancellationToken token)
    {
        MemoryContracts.RejectHub(_files);
        return ReportSnapshot.CaptureAsync(_files, MemoryContracts.Reader(_files), "learning", null, "", token);
    }

    private async Task<SquadLearningDiscovery> DiscoverAsync(ReportCase item, string? focus, CancellationToken token)
    {
        var candidates = new List<SquadLearningCandidate>();
        var diagnostics = new List<string>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        async Task AddAsync(string id, string path, string kind, SquadLearningDraft draft)
        {
            if (sources.Add(path) && sources.Count > _configuration.MaximumSources)
            {
                throw new InvalidDataException("Learning discovery exceeds the exact source bound; no incomplete scan is presented.");
            }
            if (!await AuthorizeSourceAsync(path, kind, token).ConfigureAwait(false))
            {
                diagnostics.Add("A " + kind + " source was withheld by the explicit learning disclosure policy.");
                return;
            }
            var clean = Sanitize(draft, item);
            if (clean is null)
            {
                diagnostics.Add("A " + kind + " candidate could not be safely generalized; rewrite locally or leave it out.");
                return;
            }
            if (focus is not null && !focus.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => (clean.Scenario + " " + clean.Rule).Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            candidates.Add(new(id, _files.SquadRoot, clean, [new(path, item.Files.HashFile(path), kind)]));
        }
        foreach (var memory in item.State.RoleMemories ?? [])
        {
            await AddAsync(memory.Id, MemoryContracts.PathFor(_files.SquadRoot, memory.Note.Role), "role-memory",
                new(memory.Note.Scenario, memory.Note.Rule, memory.Note.Scope, memory.Note.SourceContext)).ConfigureAwait(false);
        }
        foreach (var dispatch in item.State.DispatchRecords)
        {
            var match = MarkedRule().Match(dispatch.Summary);
            if (!match.Success)
            {
                continue;
            }
            var path = Path.GetRelativePath(_files.ProjectRoot, item.Files.Resolve(dispatch.HistoryPath)).Replace('\\', '/');
            await AddAsync(dispatch.DispatchId, path, "history",
                new("When the same workflow correction recurs", match.Groups["rule"].Value,
                    "All consumers with the same scenario", "A reviewed local workflow correction")).ConfigureAwait(false);
        }
        var decisions = _files.SquadRoot + "/decisions.md";
        if (await AuthorizeSourceAsync(decisions, "decisions", token).ConfigureAwait(false))
        {
            foreach (Match match in MarkedRule().Matches(item.Files.Read(decisions, _configuration.MaximumTargetCharacters)))
            {
                await AddAsync(HumanActions.Hash(new { decisions, Rule = match.Value })[..32].ToLowerInvariant(),
                    decisions, "decisions", new("When the same convention applies", match.Groups["rule"].Value,
                        "All consumers with the same scenario", "A host-reviewed local convention")).ConfigureAwait(false);
            }
        }
        else
        {
            diagnostics.Add("Decision conventions were withheld by the explicit learning disclosure policy.");
        }
        if (candidates.Count == 0)
        {
            diagnostics.Add("No approved safely generalized durable candidate was found. Original memory and state remain unchanged.");
        }
        return new(candidates, diagnostics);
    }

    private SquadLearningDraft? Sanitize(SquadLearningDraft draft, ReportCase item) =>
        LearningContracts.Sanitize(draft, _configuration.SecretValues.ToArray(),
            _configuration.SensitiveNames.Concat(item.Roster.Members.Select(member => member.MemberName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)).ToArray(),
            _configuration.MaximumDraftCharacters);

    private ValueTask<bool> AuthorizeSourceAsync(string path, string kind, CancellationToken token) =>
        _configuration.AuthorizeSourceAsync is null ? ValueTask.FromResult(false) :
            _configuration.AuthorizeSourceAsync(new(_files.SquadRoot, path, kind), token);

    private async Task VerifySourcesAsync(ReportCase item, SquadLearningPrepared prepared, CancellationToken token)
    {
        if (item.State.Fingerprint != prepared.ReleaseFingerprint || prepared.Sources.Count is < 1 ||
            prepared.Sources.Count > _configuration.MaximumSources)
        {
            throw new RuntimeHistoryIntegrityException("learning-source: original release/grounding binding is missing or changed.");
        }
        foreach (var source in prepared.Sources)
        {
            if (!LearningContracts.AllowedSource(_files, source) ||
                !await AuthorizeSourceAsync(source.Path, source.Kind, token).ConfigureAwait(false) ||
                item.Files.HashFile(source.Path) != source.Sha256)
            {
                throw new RuntimeHistoryIntegrityException("learning-source: an original approved source changed or lost its disclosure permission.");
            }
        }
    }

    private async Task<(SquadLearningConsent? Consent, SquadPendingHumanAction Pending)> ConfirmAsync(RuntimeState state,
        SquadHumanAuthorization actor, SquadLearningInvocation invocation, string runId, string kind,
        SquadLearningDraft draft, SquadLearningTarget target, object payload, string description, CancellationToken token)
    {
        var channel = _configuration.HumanApprovalChannel;
        var now = _clock.GetUtcNow();
        var actionHash = HumanActions.Hash(payload);
        var evidenceHash = HumanActions.Hash(new { state.Fingerprint, Actor = actor.SubjectSha256, Target = target, Draft = draft });
        var actionId = HumanActions.Hash(new { invocation.Reference, runId, kind, actionHash, evidenceHash, _rootBinding, _hostBinding })[..32].ToLowerInvariant();
        var binding = new SquadHumanActionBinding(actionId, runId, _rootBinding, state.Fingerprint, _hostBinding,
            1, kind, "Squad Learn", null, null, null, actionHash, evidenceHash, actionHash, "human-only-learning");
        var pending = new SquadPendingHumanAction(binding, false, channel?.Name ?? "unregistered", now,
            now + _configuration.ApprovalTimeout, "pending");
        if (channel is null || _configuration.ConfirmAsync is null)
        {
            return (null, pending);
        }
        var response = await _configuration.ConfirmAsync(new(pending, description, draft, target, Checklist.ToArray()), token).ConfigureAwait(false);
        if (response is null || response.Decision is SquadHumanDecision.Stop or SquadHumanDecision.Changes)
        {
            return (null, pending);
        }
        var receivedAt = response.CreatedAt ?? _clock.GetUtcNow();
        if (response.Binding != binding || response.Decision != SquadHumanDecision.Approve || response.Input is not null ||
            !HumanActions.IsReference(response.ResponseId) || receivedAt < now || receivedAt > pending.ExpiresAt ||
            _clock.GetUtcNow() > pending.ExpiresAt)
        {
            throw new UnauthorizedAccessException("Learning confirmation differs from its exact action, payload, revision or expiry.");
        }
        var authorized = await channel.AuthorizeAsync(pending, response, token).ConfigureAwait(false);
        if (authorized != actor || (state.HumanDecisions ?? []).Any(decision =>
            decision.Provider == actor.Provider && decision.SubjectSha256 == actor.SubjectSha256 && decision.ResponseId == response.ResponseId) ||
            state.NativeOperations.Any(operation => operation.LearningRecord is { } record &&
                record.PreparationConsents.Append(record.Approval).Any(consent => consent.Provider == actor.Provider &&
                    consent.SubjectSha256 == actor.SubjectSha256 && consent.ResponseId == response.ResponseId)))
        {
            throw new UnauthorizedAccessException("Learning requires the actual initiating human's authorized response; consumed responses cannot authorize another action.");
        }
        token.ThrowIfCancellationRequested();
        return (new(binding, actor.Provider, actor.SubjectSha256, response.ResponseId, _clock.GetUtcNow()), pending);
    }

    private void ValidatePrepared(SquadLearningPrepared prepared, SquadHumanAuthorization actor)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!Guid.TryParseExact(prepared.Id, "N", out _) || prepared.SquadRoot != _files.SquadRoot ||
            prepared.RootSha256 != _rootBinding || prepared.HostBinding != _hostBinding ||
            prepared.Seal != HumanActions.Hash(prepared with { Seal = "" }) ||
            prepared.Consents.Count != 2 || prepared.Consents.Select(consent => consent.Binding.Gate)
                .SequenceEqual(["LearningSanitization", "LearningRepository"], StringComparer.Ordinal) != true ||
            prepared.Consents.Any(consent => consent.Provider != actor.Provider || consent.SubjectSha256 != actor.SubjectSha256 ||
                consent.Binding.HostBinding != _hostBinding || consent.Binding.RootSha256 != _rootBinding ||
                consent.Binding.ReleaseFingerprint != prepared.ReleaseFingerprint) ||
            !LearningContracts.IsBranch(prepared.Branch) || !prepared.Branch.StartsWith("squad-learning/", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Prepared learning lost its exact native root, host, release, sanitized content or authenticated human binding.");
        }
        _ = LearningContracts.ValidateTarget(prepared.Target);
        ValidateTargetPolicy(prepared.Target);
        var appended = LearningContracts.Append(prepared.Target, prepared.Snapshot, Draft(prepared.Entry),
            DateTimeOffset.ParseExact(prepared.Entry.DateAdded, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal), _configuration.MaximumTargetCharacters);
        if (appended.Entry != prepared.Entry || appended.Content != prepared.Content || appended.Diff != prepared.Diff)
        {
            throw new RuntimeHistoryIntegrityException("learning-candidate: prepared row/ID/diff differs from its original source snapshot.");
        }
    }

    private static SquadLearningDraft Draft(SquadLearningEntry entry) => new(entry.Scenario, entry.Rule, entry.Scope, entry.SourceContext);

    private static SquadLearningMutation Mutation(SquadLearningPrepared prepared, string kind, string? head) =>
        new(LearningContracts.ActionId(prepared.Id, kind), prepared.Id, kind,
            prepared.Target.Repository, prepared.Target.DestinationRepository!, prepared.Snapshot.Path,
            prepared.Branch, prepared.Snapshot.BaseBranch, prepared.Snapshot.CommitSha, prepared.Snapshot.FileSha,
            prepared.Content, ProjectFiles.Hash(prepared.Content), prepared.Entry.Id, head, prepared.Target.ForkOrganization);

    private void ValidateTargetPolicy(SquadLearningTarget target)
    {
        if (target.Kind == "upstream" &&
            !target.Repository.Equals(SupportedSquadRelease.Current.Repository, StringComparison.OrdinalIgnoreCase) ||
            target.Kind == "tenant" && target.Repository != _configuration.TenantRepository)
        {
            throw new InvalidOperationException("Public upstream must match the pinned package repository; tenant promotion needs its exact separately host-registered repository.");
        }
    }

    private async ValueTask<bool> VerifyReceiptAsync(ISquadLearningConnector connector,
        SquadLearningMutation mutation, SquadLearningMutationReceipt receipt, CancellationToken token) =>
        ReportSafety.IsSafe(HumanActions.Serialize(receipt), _configuration.SecretValues) &&
        receipt.ActionSha256 == HumanActions.Hash(mutation) && receipt.ConnectorBinding == connector.Binding &&
        receipt.Kind == mutation.Kind && receipt.Repository == mutation.Repository &&
        HumanActions.IsReference(receipt.ResourceId) && Uri.TryCreate(receipt.Reference, UriKind.Absolute, out var url) &&
        url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) &&
        string.IsNullOrEmpty(url.Fragment) &&
        (mutation.Kind != "write" || LearningContracts.IsCommit(receipt.CommitSha) && receipt.ContentSha256 == mutation.ContentSha256) &&
        (mutation.Kind != "pull-request" || receipt.CommitSha == mutation.HeadCommitSha) &&
        await connector.VerifyAsync(mutation, receipt, token).ConfigureAwait(false);

    private ReportCase Case(RuntimeScribe scribe, RuntimeState state)
    {
        _ = scribe.VerifyReportSnapshot(state);
        var roster = scribe.ReadReportRoster();
        return new(null, _files, scribe, state, roster, scribe.ReadRosterRoots(roster), true);
    }

    private static SquadLearningResult Stopped(string diagnostic, SquadPendingHumanAction? pending = null) =>
        new("stopped", diagnostic, null, [], [], pending);

    private static SquadLearningResult Result(string status, string diagnostic, SquadLearningPrepared prepared,
        IReadOnlyList<SquadLearningMutationReceipt> receipts, SquadPendingHumanAction? pending = null) =>
        new(status, diagnostic, prepared, Commands(prepared), receipts, pending);

    private static string[] Commands(SquadLearningPrepared prepared)
    {
        var directory = "squad-learning-" + prepared.Id[..12];
        var target = prepared.Target;
        var commands = new List<string>();
        if (target.Kind == "upstream")
        {
            if (!target.ForkOrganization)
            {
                commands.Add("if ((gh api user --jq .login) -ne '" + target.DestinationRepository!.Split('/')[0] +
                    "') { throw 'The authenticated account is not the approved fork owner.' }");
            }
            commands.Add("gh repo fork " + target.Repository + " --clone=false" +
                (target.ForkOrganization ? " --org " + target.DestinationRepository!.Split('/')[0] : ""));
        }
        commands.AddRange(
        [
            "git clone https://github.com/" + target.DestinationRepository + ".git " + directory,
            "git -C " + directory + " fetch origin " + prepared.Snapshot.CommitSha,
            "git -C " + directory + " switch -c " + prepared.Branch + " " + prepared.Snapshot.CommitSha,
            "git -C " + directory + " apply learning.patch",
            "git -C " + directory + " add -- " + prepared.Snapshot.Path,
            "git -C " + directory + " commit -m \"feat(learnings): add " + prepared.Entry.Id + "\"",
            "git -C " + directory + " push origin HEAD:refs/heads/" + prepared.Branch,
            "gh pr create --repo " + target.Repository + " --head " +
                target.DestinationRepository!.Split('/')[0] + ":" + prepared.Branch + " --base " +
                prepared.Snapshot.BaseBranch + " --title \"feat(learnings): add " + prepared.Entry.Id +
                "\" --body \"Human-reviewed generalized learning. Verify secrets, names, absolute paths and internal URLs are removed; broad applicability and original license notices are preserved. Review before merge.\"",
        ]);
        return commands.ToArray();
    }

    [GeneratedRegex(@"(?im)^(?:[-*]\s*)?(?:learning|convention|correction|routing rule)\s*:\s*(?<rule>[^\r\n]+)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MarkedRule();
}
