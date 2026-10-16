using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Released in-repository triggers. Fork events and pull_request_target are deliberately absent.</summary>
public enum SquadWatchEventKind
{
    Issue,
    PullRequest,
    Comment,
    Schedule,
    Dispatch,
    Push,
}

/// <summary>A structural event hint. An authority must re-read or authenticate it before any state or model work.</summary>
public sealed record SquadWatchEvent
{
    /// <summary>The event-to-intent selector, never derived from task prose.</summary>
    public required SquadWatchEventKind Kind { get; init; }
    /// <summary>Explicit owner/repository, bound to the host's authorized case.</summary>
    public required string Repository { get; init; }
    /// <summary>Provider delivery, comment or workflow identity, not a task title.</summary>
    public required string EventId { get; init; }
    /// <summary>Provider event action, such as opened, labeled, synchronize or created.</summary>
    public string? Action { get; init; }
    /// <summary>Positive issue or PR number when the event has a thread.</summary>
    public long? Number { get; init; }
    /// <summary>Whether a comment or numbered manual dispatch refers to a PR.</summary>
    public bool IsPullRequest { get; init; }
    /// <summary>Fork origin is always ignored, even with otherwise valid authorization.</summary>
    public bool ForkOrigin { get; init; }
    /// <summary>Structural branch reference, used only for push identity and workflow filtering.</summary>
    public string? Ref { get; init; }
    /// <summary>Actual source head commit; never an executable command.</summary>
    public string? CommitSha { get; init; }
    /// <summary>Positive platform workflow run identity for committed-workflow gates and disambiguation.</summary>
    public long? WorkflowRunId { get; init; }
    /// <summary>Provider UTC timestamp, including the UTC day of a scheduled sweep.</summary>
    public DateTimeOffset ReceivedAt { get; init; }
    /// <summary>Provider actor hint. The authority, not this value, establishes rights.</summary>
    public string? Actor { get; init; }
    /// <summary>Verified current provider labels. A matching string alone grants no permission.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
    /// <summary>Task title, retained as protected case data and never used for paths or controls.</summary>
    public string? Title { get; init; }
    /// <summary>Task or command body. Only the exact /squad opt-in keyword is recognized as a signal.</summary>
    public string? Body { get; init; }
    /// <summary>Optional provider-retrieved diff/context. It remains untrusted descriptive data.</summary>
    public JsonElement? Context { get; init; }
}

/// <summary>Authenticated provider observations, not an authorization supplied by the event author.</summary>
/// <param name="Event">Canonical re-read event; the task body is still data.</param>
/// <param name="ActorId">Actual non-secret initiator; persisted as a hash.</param>
/// <param name="ProofReference">Opaque provider evidence reference, never a credential.</param>
/// <param name="WriteCollaborator">The signal's actor has current repository write/maintain/admin rights.</param>
/// <param name="RegisteredApprover">The command's actor is the explicitly registered human approver.</param>
/// <param name="CommittedWorkflowSha256">Exact reviewed workflow content digest for workflow-level opt-ins.</param>
public sealed record SquadWatchAuthorization(SquadWatchEvent Event, string ActorId, string ProofReference,
    bool WriteCollaborator = false, bool RegisteredApprover = false, string? CommittedWorkflowSha256 = null);

/// <summary>Host/provider authentication is separate from privileged terminal connector permission.</summary>
public interface ISquadWatchEventAuthority
{
    /// <summary>Stable non-secret repository/source/policy binding; change it when that authority changes.</summary>
    string Binding { get; }
    /// <summary>Authenticates structural provenance and re-reads the signal. Null explicitly means unauthorized.</summary>
    /// <param name="source">Untrusted structural hint from an authenticated host transport.</param>
    /// <param name="cancellationToken">Cancellation for bounded provider verification.</param>
    /// <returns>Canonical authorized metadata or null, never permission to mutate a provider.</returns>
    ValueTask<SquadWatchAuthorization?> AuthorizeAsync(SquadWatchEvent source, CancellationToken cancellationToken);
}

/// <summary>The only terminal mutation shapes available to the native Watch connector.</summary>
public enum SquadWatchTerminalKind
{
    DraftPullRequest,
    PullRequestReview,
    SourceComment,
    Issue,
    CommitComment,
}

/// <summary>Trusted Watch policy. None of these controls is parsed from issue, PR or comment prose.</summary>
public sealed class SquadWatchConfiguration
{
    /// <summary>Explicit authorized owner/repository for this isolated host case.</summary>
    public required string Repository { get; init; }
    /// <summary>Registered authenticated event authority.</summary>
    public required ISquadWatchEventAuthority Authority { get; init; }
    /// <summary>Optional independently authorized terminal integration. Omission produces Prepared, not a URL.</summary>
    public ISquadWatchTerminalConnector? TerminalConnector { get; init; }
    /// <summary>Optional host-selected roster. Otherwise a unique durable meta-route's roster or the host default is copied.</summary>
    public SquadRosterConfiguration? Roster { get; init; }
    /// <summary>Trusted recognized command/manual target. An absent registered name blocks; prose cannot set it.</summary>
    public string? Target { get; init; }
    /// <summary>Host command/manual mode. Issues always use autopilot; PR review and push validation are focused.</summary>
    public string RoutedMode { get; init; } = "interactive";
    /// <summary>Host scheduled-sweep mode, autopilot by default.</summary>
    public string ScheduleMode { get; init; } = "autopilot";
    /// <summary>Scheduled work fixed in the reviewed host policy, not supplied by an issue author.</summary>
    public string ScheduleRequest { get; init; } = "Perform a maintenance sweep of this repository.";
    /// <summary>Draft PR or issue for a scheduled sweep.</summary>
    public SquadWatchTerminalKind ScheduleTerminal { get; init; } = SquadWatchTerminalKind.DraftPullRequest;
}

/// <summary>Safe native provenance. Actor and authority evidence are hashes; task prose lives in the exact input file.</summary>
public sealed record SquadWatchTrigger(string Source, string Ref, string EventId, string ActorSha256,
    DateTimeOffset ReceivedAt, string RunId, string WatchRunId, string Member, string SourceKey,
    string AuthorityBinding, string AuthorizationSha256, string InputPath, string InputSha256,
    string Mode, SquadRunScope? RequiredScope, SquadWatchTerminalKind TerminalKind, string PolicySha256,
    string ProjectBinding, bool ExplicitTarget = false);

/// <summary>Exact terminal effect arguments built from verified native evidence, not from a model's proposed URL.</summary>
public sealed record SquadWatchTerminalRequest(string OperationId, string RunId, string SquadRoot,
    string Repository, string SourceRef, string EventId, long? Number, string? CommitSha,
    SquadWatchTerminalKind Kind, SquadRunStatus Outcome, IReadOnlyList<SquadArtifactEvidence> Artifacts,
    IReadOnlyList<SquadArtifactEvidence> ProjectOutputs, IReadOnlyList<string> BlockingFindings,
    SquadWatchTerminalReceipt? Previous = null)
{
    /// <summary>Exact original action/evidence binding used by authorization and provider reconciliation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Sha256 => HumanActions.Hash(this);
}

/// <summary>An actual provider postcondition returned and re-read by the trusted connector.</summary>
public sealed record SquadWatchTerminalReceipt(string OperationId, string RequestSha256, string ConnectorBinding,
    string Repository, string SourceRef, SquadWatchTerminalKind Kind, string ResourceId, string Reference,
    string ProviderObservationSha256, bool Draft = false,
    SquadToolOutputStatus Status = SquadToolOutputStatus.Succeeded,
    SquadToolReceiptKind ReceiptKind = SquadToolReceiptKind.HostObservation);

/// <summary>Provider reconciliation of the original terminal operation, never authorization to replay it.</summary>
public sealed record SquadWatchTerminalReconciliation(SquadEffectDisposition Disposition,
    SquadWatchTerminalReceipt? Receipt = null, string? Diagnostic = null);

/// <summary>Fixed terminal capability, never a generic shell, push, merge or deploy interface.</summary>
public interface ISquadWatchTerminalConnector
{
    /// <summary>Stable non-secret implementation/repository/output-policy binding.</summary>
    string Binding { get; }
    /// <summary>Authorizes the exact bounded terminal action separately from trigger validation.</summary>
    /// <param name="request">Actual original action and verified evidence.</param>
    /// <param name="cancellationToken">Cancellation for resource authorization.</param>
    /// <returns>Whether this connector may execute this exact terminal action.</returns>
    ValueTask<bool> AuthorizeAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken);
    /// <summary>Executes and verifies the actual provider postcondition. Process exit alone is insufficient.</summary>
    /// <param name="request">Original immutable action; use OperationId as the remote idempotency marker.</param>
    /// <param name="cancellationToken">Cancellation for the provider action.</param>
    /// <returns>Verified actual resource evidence, never a drafted link.</returns>
    ValueTask<SquadWatchTerminalReceipt> ExecuteAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken);
    /// <summary>Re-reads the exact original provider action without re-executing it.</summary>
    /// <param name="request">Original persisted arguments, including operation identity.</param>
    /// <param name="cancellationToken">Cancellation for provider observation.</param>
    /// <returns>Executed, proven NotExecuted, or honestly CannotConfirm.</returns>
    ValueTask<SquadWatchTerminalReconciliation> ReconcileAsync(SquadWatchTerminalRequest request, CancellationToken cancellationToken);
}

/// <summary>Delivery is distinct from native pipeline success and human acceptance.</summary>
public enum SquadWatchStatus
{
    Ignored,
    Prepared,
    Pending,
    Delivered,
    Blocked,
    ReconciliationRequired,
}

/// <summary>Actual event-scoped outcome. A pending/blocked run may have a provider-attested escalation, never a fake success.</summary>
public sealed record SquadWatchRunResult(SquadWatchStatus Status, string Diagnostic,
    SquadWatchTrigger? Trigger = null, SquadRunResult? Run = null, SquadWatchTerminalReceipt? Terminal = null);

public sealed partial class SquadFederation
{
    private SquadWatchConfiguration? _registeredWatch;
    private static readonly Action<ILogger, Exception?> LogWatchIgnored = LoggerMessage.Define(
        LogLevel.Information, new EventId(9, "WatchIgnored"),
        "Watch ignored an unmatched, unauthorized or out-of-scope event. No model or terminal action was performed.");
    private static readonly Action<ILogger, Exception?> LogWatchBlocked = LoggerMessage.Define(
        LogLevel.Warning, new EventId(10, "WatchBlocked"),
        "Watch is blocked at its original state, authority or provider evidence barrier. No uncertain action was replayed.");

    /// <summary>Authenticates one opted-in event, safely bootstraps its member, then runs the existing inner engine.</summary>
    /// <param name="source">Untrusted provider event hint; task prose is never configuration.</param>
    /// <param name="configuration">Trusted source, modes, roster and independently authorized terminal capability.</param>
    /// <param name="cancellationToken">Cancellation for the bounded native handler.</param>
    /// <returns>Explicit ignored, prepared, pending, delivered or blocked evidence.</returns>
    public async Task<SquadWatchRunResult> HandleWatchEventAsync(SquadWatchEvent source,
        SquadWatchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        RegisterWatch(configuration);
        configuration = _registeredWatch!;
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RunTimeout);
        var token = timeout.Token;
        WatchRunCheckpoint? watch = null;
        try
        {
            WatchContracts.ValidateEvent(source, _options.MaxArtifactCharacters);
            if (!source.Repository.Equals(configuration.Repository, StringComparison.OrdinalIgnoreCase) || source.ForkOrigin)
            {
                return WatchIgnored("Repository mismatch or fork origin is outside the authorized Watch scope.");
            }
            using var activity = _trace?.StartActivity("consume.watch", ActivityKind.Consumer);
            SquadWatchAuthorization? authorization;
            using (var verification = _trace?.StartActivity("get.watch.authorization", ActivityKind.Client))
            {
                authorization = await configuration.Authority.AuthorizeAsync(source, token).ConfigureAwait(false);
            }
            if (authorization is null)
            {
                return WatchIgnored("The provider did not establish an authorized opt-in signal.");
            }
            var verified = FederationPersistence.Copy(authorization.Event);
            WatchContracts.ValidateEvent(verified, _options.MaxArtifactCharacters);
            if (WatchContracts.StructuralIdentity(source) != WatchContracts.StructuralIdentity(verified) ||
                !WatchContracts.OptedIn(verified, authorization))
            {
                return WatchIgnored("The authenticated event does not match its structural identity or opt-in requirements.");
            }
            Directory.CreateDirectory(_files.Resolve(ProjectFiles.StateRoot));
            using var entryLock = new FileStream(_files.Resolve(ProjectFiles.StateRoot + "/federation.run.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            watch = await PrepareWatchAsync(verified, authorization, configuration, token).ConfigureAwait(false);
            if (watch.Result is { Status: SquadWatchStatus.Delivered } delivered &&
                watch.EventIds.Contains(verified.EventId, StringComparer.Ordinal))
            {
                var (_, scribe, completed) = FederationExecution.ReadMember(_files, watch.Trigger.Member, ArtifactFingerprint);
                foreach (var evidence in delivered.Run?.Evidence ?? [])
                {
                    scribe.Verify(evidence, completed);
                }
                if (delivered.Terminal is not { } originalTerminal ||
                    completed.Runs.SingleOrDefault(run => run.RunId == watch.Trigger.RunId)?.WatchTerminal != originalTerminal)
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: the cached Watch delivery lost its original inner/provider outcome.");
                }
                WatchContracts.ReadTerminalEvidence(new(_files.ProjectRoot, _options.MaxArtifactCharacters,
                    FederationPersistence.MemberRoot(watch.Trigger.Member)), scribe, completed, originalTerminal);
                return delivered;
            }
            using var runtime = await CreateWatchRuntimeAsync(watch, configuration, token).ConfigureAwait(false);
            var (_, _, inner) = FederationExecution.ReadMember(_files, watch.Trigger.Member, ArtifactFingerprint);
            if (inner.Checkpoint?.Effects.Any(effect => effect.Status == "in-flight") == true)
            {
                return SaveWatchResult(watch, new(SquadWatchStatus.ReconciliationRequired,
                    "The original effect is uncertain. ReconcileWatchAsync observes it without replay or model work.",
                    watch.Trigger, inner.ActiveRun));
            }
            if (inner.Checkpoint is { } active && active.RunId != watch.Trigger.RunId)
            {
                throw new InvalidDataException("A different member run is active; this event cannot overwrite it.");
            }
            SquadRunResult run;
            if (inner.ActiveRun is { } prepared && FederationExecution.Successful(prepared))
            {
                run = prepared;
            }
            else if (inner.Runs.SingleOrDefault(result => result.RunId == watch.Trigger.RunId) is { } completed)
            {
                run = completed;
            }
            else
            {
                watch.Started = true;
                SaveWatchCheckpoint(watch, "WatchStarted");
                run = inner.Checkpoint is null
                    ? await runtime.RunAsync(WatchContracts.Request(watch.Event, configuration), token).ConfigureAwait(false)
                    : await runtime.ResumeAsync(watch.Trigger.RunId, token).ConfigureAwait(false);
            }
            var terminal = await runtime.CompleteWatchTerminalAsync(watch.Trigger, watch.Event, run,
                configuration.TerminalConnector, token).ConfigureAwait(false);
            return SaveWatchResult(watch, terminal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            SquadHumanTransportException or RuntimeStopException or RuntimeHistoryIntegrityException)
        {
            if (_logger is not null)
            {
                LogWatchBlocked(_logger, null);
            }
            return new(SquadWatchStatus.Blocked,
                exception is SquadHumanTransportException
                    ? "Watch provider verification failed; inspect host transport permissions without logging private data."
                    : exception.Message, watch?.Trigger, watch?.Result?.Run, watch?.Result?.Terminal);
        }
    }

    /// <summary>Observes original uncertain terminal or inner connector effects. It does not implicitly resume the run.</summary>
    /// <param name="watchRunId">Original Watch identity returned in the trigger.</param>
    /// <param name="configuration">Original trusted authority and exact connector implementation.</param>
    /// <param name="cancellationToken">Cancellation for bounded provider reconciliation.</param>
    /// <returns>Recorded reconciliation only; explicitly re-handle the authorized source event to continue.</returns>
    public async Task<SquadWatchRunResult> ReconcileWatchAsync(string watchRunId,
        SquadWatchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        RegisterWatch(configuration);
        configuration = _registeredWatch!;
        var state = LoadWatchFederation();
        var watch = state.WatchRuns?.SingleOrDefault(item => item.Trigger.WatchRunId == watchRunId)
            ?? throw new InvalidDataException("No exact native Watch identity exists.");
        using var runtime = await CreateWatchRuntimeAsync(watch, configuration, cancellationToken).ConfigureAwait(false);
        var run = await runtime.ReconcileAsync(watch.Trigger.RunId, cancellationToken).ConfigureAwait(false);
        return SaveWatchResult(watch, new(
            run.Status == SquadRunStatus.ReconciliationRequired ? SquadWatchStatus.ReconciliationRequired : SquadWatchStatus.Prepared,
            run.ResponseText, watch.Trigger, run, watch.Result?.Terminal));
    }

    /// <summary>
    /// Registers the trusted Watch policy without writing state or calling a model. Register the
    /// same policy after restart before using the ordinary federation submit/poll/renew gate APIs.
    /// </summary>
    /// <param name="configuration">Explicit host authority and independently scoped terminal capability.</param>
    public void RegisterWatch(SquadWatchConfiguration configuration)
    {
        ValidateWatchConfiguration(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registeredWatch is { } prior && WatchContracts.Policy(prior) != WatchContracts.Policy(configuration))
        {
            throw new InvalidOperationException("A controller's registered Watch policy is fixed. Recreate it explicitly for a different host policy.");
        }
        _registeredWatch = new()
        {
            Repository = configuration.Repository,
            Authority = configuration.Authority,
            TerminalConnector = configuration.TerminalConnector,
            Roster = configuration.Roster is null ? null : FederationPersistence.Copy(configuration.Roster),
            Target = configuration.Target,
            RoutedMode = configuration.RoutedMode,
            ScheduleMode = configuration.ScheduleMode,
            ScheduleRequest = configuration.ScheduleRequest,
            ScheduleTerminal = configuration.ScheduleTerminal,
        };
    }

    internal async Task<WatchRunCheckpoint> PrepareWatchAsync(SquadWatchEvent source,
        SquadWatchAuthorization authorization, SquadWatchConfiguration configuration, CancellationToken token)
    {
        var key = WatchContracts.SourceKey(source);
        var pending = _scribe.ReadFederationPending();
        if (pending is not null)
        {
            var lifecycle = _scribe.LoadFederationLifecycle(pending.Id);
            if (lifecycle.WatchAuthorization?.SourceKey != key ||
                lifecycle.WatchAuthorization.AuthorityBinding != configuration.Authority.Binding)
            {
                throw new InvalidDataException("Another exact pending lifecycle must be explicitly reconciled; Watch cannot adopt its permission.");
            }
            var resumed = await ResumeLifecycleAsync(pending.Id, token).ConfigureAwait(false);
            if (resumed.Status != SquadRunStatus.Completed)
            {
                throw new InvalidDataException(resumed.Diagnostic);
            }
        }
        RepairWatchMembers(source, configuration, token);
        var snapshot = GetSnapshot();
        var approval = new WatchBootstrapAuthorization(key, configuration.Authority.Binding,
            HumanActions.Hash(authorization), source.EventId);
        if (snapshot.Topology == SquadTopology.SingleSquad)
        {
            var original = _scribe.Load() ?? throw new InvalidDataException("A top-level roster without native state cannot be auto-promoted.");
            var profile = original.Initialization?.Profile.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] ?? "default";
            var promotedName = WatchContracts.ProfileName(profile);
            var owned = GetPromotionCandidates().Where(candidate => original.DispatchRecords.Any(dispatch =>
                new[] { dispatch.ArtifactPath }.Concat(dispatch.Artifacts?.Select(item => item.Path) ?? [])
                    .Concat((dispatch.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? []).Select(item => item.Path))
                    .Any(path => ProjectFiles.IsWithin(path, candidate.Path))) ||
                (original.Initialization?.DeliverableRoots?.Values ?? []).Any(root =>
                    root is not null && ProjectFiles.IsWithin(root.Replace('\\', '/'), candidate.Path)))
                .Select(candidate => candidate.Path).ToArray();
            var result = await ExecuteLifecycleAsync(null, new()
            {
                Name = promotedName,
                Description = "Promoted existing native " + promotedName + " squad",
                Patterns = ["*"],
                ParallelEligible = false,
            }, owned, token, approval).ConfigureAwait(false);
            if (result.Status != SquadRunStatus.Completed)
            {
                throw new InvalidDataException(result.Diagnostic);
            }
            snapshot = GetSnapshot();
        }
        RuntimeState? hub = snapshot.Topology == SquadTopology.Federation ? LoadWatchFederation() : null;
        if (hub?.WatchRuns?.Any(item => !item.Event.Repository.Equals(source.Repository, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new InvalidDataException("Watch is in-repository only. Another repository already owns this host case's Watch authority.");
        }
        var prior = hub?.WatchRuns?.LastOrDefault(item => item.EventIds.Contains(source.EventId, StringComparer.Ordinal) &&
            item.Trigger.SourceKey == key);
        prior ??= hub?.WatchRuns?.LastOrDefault(item => item.Trigger.SourceKey == key &&
            item.Result?.Status != SquadWatchStatus.Delivered);
        if (prior is not null)
        {
            VerifyWatchPolicy(prior, configuration);
            WatchContracts.VerifyInput(_files, prior.Trigger);
            if (!prior.EventIds.Contains(source.EventId, StringComparer.Ordinal))
            {
                prior.EventIds.Add(source.EventId);
                SaveWatchCheckpoint(prior, "WatchRetriggerRetainedOriginalInput");
            }
            return prior;
        }
        var name = configuration.Target ?? WatchContracts.MemberName(source);
        NativeFederationMember? member = hub?.Federation?.Members.SingleOrDefault(item => item.Entry.Name == name);
        if (configuration.Target is not null && source.Kind is not (SquadWatchEventKind.Comment or SquadWatchEventKind.Dispatch))
        {
            throw new InvalidDataException("Only a trusted recognized command/manual target can bypass event-member creation.");
        }
        if (configuration.Target is not null && member is null)
        {
            throw new InvalidDataException("The explicit Watch target is not registered. No member was created by that name.");
        }
        if (configuration.Target is null && member is not null)
        {
            if (member.Entry.Owner != "watch-mode")
            {
                throw new InvalidDataException("The structural event name belongs to a human-created member. Watch did not write into it.");
            }
            var (_, _, existing) = FederationExecution.ReadMember(_files, name, ArtifactFingerprint);
            if (existing.Trigger is null && existing.Runs.Count == 0 && existing.Checkpoint is null &&
                FindWatchLifecycle(hub!, name, key, configuration.Authority.Binding) is not null)
            {
                member = hub!.Federation!.Members.Single(item => item.Entry.Name == name);
            }
            else if (existing.Trigger is not { } trigger || trigger.SourceKey != key)
            {
                name = WatchContracts.Disambiguate(name, source.WorkflowRunId);
                member = hub!.Federation!.Members.SingleOrDefault(item => item.Entry.Name == name);
                if (member is not null)
                {
                    throw new InvalidDataException("The once-disambiguated event name is also occupied; choose an explicit human remediation.");
                }
            }
        }
        if (member is null && Directory.Exists(_files.Resolve(FederationPersistence.MemberRoot(name))))
        {
            var (orphanFiles, orphanScribe, orphan) = FederationExecution.ReadMember(_files, name, ArtifactFingerprint);
            if (orphan.Trigger is not { } trigger || trigger.SourceKey != key || trigger.EventId != source.EventId ||
                trigger.AuthorityBinding != configuration.Authority.Binding ||
                trigger.ProjectBinding != FederationPersistence.ProjectBinding(_files.ProjectRoot))
            {
                throw new InvalidDataException("An unregistered member directory has no matching native Watch provenance. It was not overwritten or adopted.");
            }
            WatchContracts.VerifyInput(orphanFiles, trigger);
            var roster = RestoreWatchRoster(orphan, name);
            member = new(new(name, orphan.Initialization!.Profile, "in-repo", "members/" + name + "/", "watch-mode",
                "Watch " + trigger.Ref + " -> " + trigger.TerminalKind), roster);
            if (hub is null)
            {
                var adoptedHub = await ExecuteLifecycleAsync([new()
                {
                    Name = name,
                    Description = member.Entry.Description,
                    Owner = "watch-mode",
                    Roster = roster,
                    Patterns = ["watch:" + key[..24]],
                    ParallelEligible = false,
                }], null, null, token, approval, [name]).ConfigureAwait(false);
                if (adoptedHub.Status != SquadRunStatus.Completed)
                {
                    throw new InvalidDataException(adoptedHub.Diagnostic);
                }
            }
            else
            {
                using (_scribe.AcquireLock())
                {
                    hub = LoadWatchFederation();
                    _scribe.RegisterWatchMember(hub, member, trigger);
                }
            }
            var originalEvent = JsonSerializer.Deserialize<SquadWatchEvent>(orphanFiles.Read(trigger.InputPath, _options.MaxArtifactCharacters))
                ?? throw new InvalidDataException("The original Watch input is missing.");
            var adopted = new WatchRunCheckpoint
            {
                Trigger = trigger,
                Event = originalEvent,
                EventIds = [source.EventId],
                Started = orphan.Checkpoint is not null || orphan.Runs.Any(run => run.RunId == trigger.RunId),
                Result = orphan.Runs.SingleOrDefault(run => run.RunId == trigger.RunId && run.WatchTerminal is not null) is { } completed
                    ? new(SquadWatchStatus.Delivered, "Proven original Watch delivery re-registered without replay.",
                        trigger, completed, completed.WatchTerminal) : null,
            };
            VerifyWatchPolicy(adopted, configuration);
            SaveWatchCheckpoint(adopted, "WatchReregistered");
            _ = orphanScribe;
            return adopted;
        }
        if (member is null)
        {
            var roster = configuration.Roster is null ? null : FederationPersistence.Copy(configuration.Roster);
            if (roster is null && hub is not null)
            {
                var routed = RoutingProposal(hub, WatchContracts.Request(source, configuration), null);
                if (!routed.NeedsHumanChoice && routed.Members.Count == 1)
                {
                    var original = hub.Federation!.Members.Single(item => item.Entry.Name == routed.Members[0]);
                    roster = FederationPersistence.Copy(original.Roster);
                    var originalRoot = FederationPersistence.MemberRoot(original.Entry.Name);
                    roster.DeliverableRoots = roster.DeliverableRoots.ToDictionary(pair => pair.Key, pair =>
                        ProjectFiles.IsWithin(pair.Value, originalRoot)
                            ? ProjectFiles.StateRoot + pair.Value[originalRoot.Length..] : pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                }
            }
            var initialized = await ExecuteLifecycleAsync([new()
            {
                Name = name,
                Description = "Watch " + WatchContracts.SourceRef(source) + " -> " + WatchContracts.Terminal(source, configuration),
                Patterns = ["watch:" + HumanActions.Hash(key)[..24]],
                Owner = "watch-mode",
                Roster = roster,
                ParallelEligible = false,
            }], null, null, token, approval).ConfigureAwait(false);
            if (initialized.Status != SquadRunStatus.Completed)
            {
                throw new InvalidDataException(initialized.Diagnostic);
            }
        }
        hub = LoadWatchFederation();
        member = hub.Federation!.Members.Single(item => item.Entry.Name == name);
        var (files, scribe, inner) = FederationExecution.ReadMember(_files, name, ArtifactFingerprint);
        if (inner.Checkpoint is not null || inner.ActiveRun is not null)
        {
            throw new InvalidDataException("A prior owning-member run is active. No competing event run was started.");
        }
        var id = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var input = files.SquadRoot + "/watch-inputs/" + id + ".json";
        using (scribe.AcquireLock())
        {
            inner = scribe.Load()!;
            if (File.Exists(files.Resolve(input)))
            {
                throw new InvalidDataException("The exact original Watch input destination is occupied.");
            }
            files.Write(input, JsonSerializer.Serialize(source));
            var trigger = new SquadWatchTrigger(WatchContracts.Source(source), WatchContracts.SourceRef(source),
                source.EventId, HumanActions.Hash(authorization.ActorId), source.ReceivedAt, runId, id, name, key,
                configuration.Authority.Binding, HumanActions.Hash(authorization), input, files.HashFile(input),
                WatchContracts.Mode(source, configuration), WatchContracts.Scope(source),
                WatchContracts.Terminal(source, configuration), WatchContracts.Policy(configuration),
                FederationPersistence.ProjectBinding(_files.ProjectRoot), configuration.Target is not null);
            scribe.RecordWatchTrigger(inner, trigger);
            var created = new WatchRunCheckpoint { Trigger = trigger, Event = source, EventIds = [source.EventId] };
            SaveWatchCheckpoint(created, "WatchPrepared");
            return created;
        }
    }

    private async Task<SquadRuntime> CreateWatchRuntimeAsync(WatchRunCheckpoint watch,
        SquadWatchConfiguration configuration, CancellationToken token)
    {
        VerifyWatchPolicy(watch, configuration);
        var hub = LoadWatchFederation();
        var member = hub.Federation!.Members.Single(item => item.Entry.Name == watch.Trigger.Member);
        if (!watch.Trigger.ExplicitTarget && member.Entry.Owner != "watch-mode")
        {
            throw new InvalidDataException("The original Watch-owned registry row changed owner.");
        }
        var (_, _, inner) = FederationExecution.ReadMember(_files, member.Entry.Name, ArtifactFingerprint);
        if (inner.Checkpoint?.WatchTerminal is { } terminal &&
            terminal.ConnectorBinding != configuration.TerminalConnector?.Binding)
        {
            throw new InvalidDataException("An original terminal operation requires its exact registered connector binding.");
        }
        if (inner.Trigger != watch.Trigger)
        {
            throw new InvalidDataException("The member's original Watch provenance differs from its immutable hub authority.");
        }
        WatchContracts.VerifyInput(_files, watch.Trigger);
        var options = RestoreMemberOptions(inner, member);
        options.Mode = watch.Trigger.Mode;
        options.HumanPresent = false;
        options.Discovery = "skip";
        options.AskAsync = null;
        options.ApproveAsync = null;
        options.SelectCouncilAsync = null;
        options.InputPaths = _options.InputPaths.Append(watch.Trigger.InputPath).Distinct(StringComparer.Ordinal).ToArray();
        options.RequireCouncil = watch.Event.Kind == SquadWatchEventKind.Push;
        options.WatchInvocation = new(watch.Trigger, configuration.TerminalConnector);
        options.Tools = options.Tools.Append(WatchContracts.TerminalRegistration(options.WatchInvocation)).ToArray();
        return await SquadRuntime.CreateAsync(_client, options, _source, token).ConfigureAwait(false);
    }

    private RuntimeState LoadWatchFederation()
    {
        var state = _scribe.Load(allowFederation: true) ?? throw new InvalidDataException("Watch has no native federation state.");
        if (state.Fingerprint != ArtifactFingerprint || _scribe.ReadFederationPending() is not null)
        {
            throw new InvalidDataException("Watch requires the original acquired release and a completed exact lifecycle.");
        }
        _scribe.VerifyFederation(state);
        return state;
    }

    private WatchRunCheckpoint SaveWatchCheckpoint(WatchRunCheckpoint watch, string operation)
    {
        using var diskLock = _scribe.AcquireLock();
        var state = LoadWatchFederation();
        _scribe.RecordWatchRun(state, watch, operation);
        return watch;
    }

    private SquadWatchRunResult SaveWatchResult(WatchRunCheckpoint watch, SquadWatchRunResult result)
    {
        watch.Result = result;
        SaveWatchCheckpoint(watch, "WatchOutcome");
        return result;
    }

    private SquadWatchRunResult WatchIgnored(string diagnostic)
    {
        if (_logger is not null)
        {
            LogWatchIgnored(_logger, null);
        }
        return new(SquadWatchStatus.Ignored, diagnostic);
    }

    private static void ValidateWatchConfiguration(SquadWatchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Authority);
        if (!WatchContracts.IsRepository(configuration.Repository) ||
            !HumanActions.IsReference(configuration.Authority.Binding) ||
            configuration.TerminalConnector is { } connector && !HumanActions.IsReference(connector.Binding) ||
            configuration.RoutedMode is not ("interactive" or "autonomous" or "autopilot") ||
            configuration.ScheduleMode is not ("interactive" or "autonomous" or "autopilot") ||
            configuration.ScheduleTerminal is not (SquadWatchTerminalKind.DraftPullRequest or SquadWatchTerminalKind.Issue) ||
            string.IsNullOrWhiteSpace(configuration.ScheduleRequest) || configuration.ScheduleRequest.Length > 8192)
        {
            throw new ArgumentException("Watch needs an explicit repository, stable non-secret authority/capability bindings and valid trusted modes.", nameof(configuration));
        }
        if (configuration.Target is not null)
        {
            FederationPersistence.ValidateName(configuration.Target);
        }
    }

    private static void VerifyWatchPolicy(WatchRunCheckpoint watch, SquadWatchConfiguration configuration)
    {
        if (!watch.Event.Repository.Equals(configuration.Repository, StringComparison.OrdinalIgnoreCase) ||
            watch.Trigger.AuthorityBinding != configuration.Authority.Binding ||
            watch.Trigger.PolicySha256 != WatchContracts.Policy(configuration))
        {
            throw new InvalidDataException("The original event/root/mode/roster policy changed. Restore that exact authority before continuation.");
        }
    }

    private SquadRosterConfiguration RestoreWatchRoster(RuntimeState state, string name)
    {
        var initialization = state.Initialization ?? throw new InvalidDataException("An orphan lacks its confirmed native roster.");
        var roster = new SquadRosterConfiguration
        {
            Profile = initialization.Profile.StartsWith("custom:", StringComparison.Ordinal) ? initialization.Profile["custom:".Length..] : initialization.Profile,
            Packs = initialization.Packs.ToArray(),
            Roles = initialization.Roles.ToArray(),
            NamingPolicy = initialization.NamingPolicy,
            MemberNames = initialization.NamingPolicy == "assigned" ? new Dictionary<string, string>() :
                new Dictionary<string, string>(initialization.MemberNames, StringComparer.OrdinalIgnoreCase),
            DeliverableRoots = (initialization.DeliverableRoots ?? new Dictionary<string, string>())
                .Where(pair => pair.Key != "scribe").ToDictionary(pair => pair.Key, pair => pair.Value),
            Notifications = state.Notify,
        };
        _ = MemberOptions(roster, name, state.Notify, initialization.NamingPolicy, rebase: false);
        return roster;
    }

    private FederationLifecycle? FindWatchLifecycle(RuntimeState hub, string name, string key, string authority)
    {
        foreach (var operation in hub.NativeOperations.Where(item =>
            item.Operation is "FederationInitialization" or "FederationExpansion").Reverse())
        {
            var lifecycle = _scribe.ReadCompletedFederationLifecycle(operation.RunId!);
            if (lifecycle.Fingerprint == ArtifactFingerprint && lifecycle.HostBinding == _template.HostBinding &&
                lifecycle.WatchAuthorization is { } authorization && authorization.SourceKey == key &&
                authorization.AuthorityBinding == authority && lifecycle.Members.Any(member => member.Entry.Name == name &&
                    member.Entry.Owner == "watch-mode") && operation.WatchRecord is { } proof &&
                JsonElement.DeepEquals(proof, JsonSerializer.SerializeToElement(authorization)))
            {
                return lifecycle;
            }
        }
        return null;
    }

    private void RepairWatchMembers(SquadWatchEvent source, SquadWatchConfiguration configuration, CancellationToken token)
    {
        if (!File.Exists(_files.Resolve(ProjectFiles.StateRoot + "/federation.md")))
        {
            return;
        }
        var hub = _scribe.Load(allowFederation: true) ?? throw new InvalidDataException("The registry has no native federation evidence.");
        _scribe.VerifyFederation(hub, allowMissingWatchTrees: true);
        var key = WatchContracts.SourceKey(source);
        foreach (var member in hub.Federation!.Members.Where(member =>
            !Directory.Exists(_files.Resolve(FederationPersistence.MemberRoot(member.Entry.Name)))).ToArray())
        {
            var history = (hub.WatchRuns ?? []).Where(watch => watch.Trigger.Member == member.Entry.Name).ToArray();
            if (member.Entry.Owner != "watch-mode" || history.Any(watch => watch.Started) ||
                history.Any(watch => watch.Trigger.SourceKey != key))
            {
                throw new InvalidDataException("A missing started or unrelated member tree must be restored from its original evidence. Watch cannot reconstruct or replay lost effects/spend.");
            }
            var lifecycle = FindWatchLifecycle(hub, member.Entry.Name, key, configuration.Authority.Binding)
                ?? throw new InvalidDataException("The missing tree has no exact original authorized Watch seed image.");
            var root = FederationPersistence.MemberRoot(member.Entry.Name);
            using (var diskLock = _scribe.AcquireLock())
            {
                foreach (var file in lifecycle.Prepared.Where(file => ProjectFiles.IsWithin(file.Destination, root)))
                {
                    token.ThrowIfCancellationRequested();
                    if (_files.HashFile(file.Source) != file.Sha256)
                    {
                        throw new InvalidDataException("The exact original Watch seed image changed; no blank replacement was created.");
                    }
                    _scribe.WriteExclusive(file.Destination, _files.ReadBytes(file.Source));
                }
            }
            var (files, scribe, restored) = FederationExecution.ReadMember(_files, member.Entry.Name, ArtifactFingerprint);
            if (history.LastOrDefault() is { } pending)
            {
                using var memberLock = scribe.AcquireLock();
                files.Write(pending.Trigger.InputPath, JsonSerializer.Serialize(pending.Event));
                WatchContracts.VerifyInput(files, pending.Trigger);
                scribe.RecordWatchTrigger(restored, pending.Trigger);
            }
            using (_scribe.AcquireLock())
            {
                hub = _scribe.Load(allowFederation: true)!;
                _scribe.RecordWatchRepair(hub, member.Entry.Name, source.EventId);
            }
        }
    }
}
