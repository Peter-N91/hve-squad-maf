using System.Diagnostics;
using System.Globalization;
using System.Text;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Filesystem detection precedence, not permission to initialize or promote a case.</summary>
public enum SquadTopology
{
    /// <summary>No federation registry or single-squad roster exists.</summary>
    Initialization,
    /// <summary>A top-level roster exists without a federation registry.</summary>
    SingleSquad,
    /// <summary>The federation registry takes precedence, even over a stray top-level roster.</summary>
    Federation,
}

/// <summary>A named, host-proposed independent outcome. Selection never grants tool authority.</summary>
public sealed class SquadFederationMemberConfiguration
{
    /// <summary>Required unique, portable lower-kebab-case selector and directory name.</summary>
    public required string Name { get; set; }
    /// <summary>Required bounded one-line purpose, retained only in the authorized local registry.</summary>
    public required string Description { get; set; }
    /// <summary>Explicit one-line request patterns. Empty selects the description as its proposed route.</summary>
    public IList<string> Patterns { get; set; } = [];
    /// <summary>Optional human/team label; not an authentication identity.</summary>
    public string? Owner { get; set; }
    /// <summary>Optional independent roster; omission inherits the parent's roster settings.</summary>
    public SquadRosterConfiguration? Roster { get; set; }
    /// <summary>Whether independent work may run concurrently; dependencies still prohibit overlap.</summary>
    public bool ParallelEligible { get; set; } = true;
}

/// <summary>One confirmed native registry row; external repositories are not supported.</summary>
public sealed record SquadFederationMember(string Name, string Profile, string Kind, string Location,
    string? Owner, string Description);

/// <summary>A confirmed route to a registered member, not a cross-root read grant.</summary>
public sealed record SquadFederationRoute(string Pattern, string Member, bool ParallelEligible);

/// <summary>Exact proposed member initialization and its independent routing metadata.</summary>
public sealed record SquadFederationProposedMember(string Name, SquadInitializationProposal Initialization,
    string? Owner, string Description, IReadOnlyList<string> Patterns, bool ParallelEligible);

/// <summary>A disk-enumerated directory, including ambiguous/non-squad workflows for an explicit keep/move choice.</summary>
public sealed record SquadFederationDeliverableCandidate(string Path, int Files, long Bytes, string Sha256,
    bool Selected, string Diagnostic);

/// <summary>Honest recorded inner spend at lifecycle completion. No second ledger or fabricated model cost.</summary>
public sealed record SquadFederationCost(decimal? EstimatedCostUsd, decimal KnownCostUsd, int ProviderAttempts,
    int UnpricedAttempts, int UnobservedDispatches, decimal OrchestrationCostUsd);

/// <summary>The complete local confirmation payload; not suitable for notifications or public reports.</summary>
public sealed record SquadFederationProposal(string Operation, IReadOnlyList<SquadFederationProposedMember> Members,
    IReadOnlyList<SquadFederationDeliverableCandidate> Deliverables, SquadNotificationConfiguration Notifications,
    string? NamingPolicy, SquadRelease? Release, string Fingerprint, SquadFederationCost? PreservedCost = null);

/// <summary>Read-only host observations. Pending migration or stray state must be reconciled explicitly.</summary>
public sealed record SquadFederationSnapshot(SquadTopology Topology, IReadOnlyList<SquadFederationMember> Members,
    IReadOnlyList<SquadFederationRoute> Routes, SquadFederationCost? PreservedCost, string? PendingOperationId,
    IReadOnlyList<string> Diagnostics);

/// <summary>Actual lifecycle outcome only; completion does not mean an IndustryIQ outcome was produced.</summary>
public sealed record SquadFederationLifecycleResult(SquadRunStatus Status, string? OperationId,
    SquadFederationProposal? Proposal, string Diagnostic);

/// <summary>
/// Confirmed in-repository federation lifecycle over the existing native inner engine.
/// The deterministic Scribe owns both levels; supplied clients, callbacks and connectors remain host-owned.
/// Promotion relocates verified native v2 evidence, never Copilot state or active effect authority.
/// </summary>
public sealed partial class SquadFederation : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogLifecycleBlocked = LoggerMessage.Define(
        LogLevel.Warning, new EventId(7, "FederationLifecycleBlocked"),
        "Federation lifecycle is blocked. Original evidence and any exact pending operation are retained for host reconciliation.");
    private readonly IChatClient _client;
    private readonly SquadRuntimeOptions _options;
    private readonly SquadRuntime _template;
    private readonly SquadArtifactSource _source;
    private readonly ProjectFiles _files;
    private readonly RuntimeScribe _scribe;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ILogger? _logger;
    private readonly ActivitySource? _trace;
    private bool _disposed;

    private SquadFederation(IChatClient client, SquadRuntimeOptions options, SquadRuntime template)
    {
        _client = client;
        _options = options;
        _template = template;
        _source = new ResolvedSource(template.Provenance);
        _files = template.FederationFiles;
        _logger = options.LoggerFactory?.CreateLogger("HveSquad.AgentFramework.Runtime.Federation");
        _scribe = template.FederationScribe;
        template.EnableFederationControlPlane();
        _trace = options.EnableOpenTelemetry ? new(options.OpenTelemetrySourceName) : null;
    }

    /// <summary>Exact acquisition provenance; directory sources are never advertised as official releases.</summary>
    public SquadArtifactRoots Provenance => _template.Provenance;
    /// <summary>Fingerprint of the actual acquired release/dependencies, fixed for this controller.</summary>
    public string ArtifactFingerprint => _template.ArtifactFingerprint;

    /// <summary>Creates member-scoped or all-federation read-only helpers; registry presence does not grant disclosure consent.</summary>
    /// <param name="configuration">Explicit case/tenant authorization and optional trusted local rendering.</param>
    /// <returns>A helper independent of federation dispatch, Watch and model clients.</returns>
    public SquadReports CreateReports(SquadReportConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(_files.ProjectRoot, configuration, _files.SquadRoot, _options.TimeProvider);
    }

    /// <summary>Creates isolated consumer-local memory for one explicitly named member, reading no hub or sibling state.</summary>
    /// <param name="configuration">Exact host member/role memory rights.</param>
    /// <param name="member">Portable explicit member name.</param>
    /// <returns>A Scribe-owned member-local store.</returns>
    public SquadRoleMemory CreateMemory(SquadMemoryConfiguration configuration, string member)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FederationPersistence.ValidateName(member);
        return new(_files.ProjectRoot, configuration, FederationPersistence.MemberRoot(member), _options.TimeProvider);
    }

    /// <summary>Creates authenticated human-only learning for one explicitly named member, never an aggregate memory scan.</summary>
    /// <param name="configuration">Separate actual human initiation, local-source disclosure and mutation rights.</param>
    /// <param name="member">Portable explicit member name.</param>
    /// <returns>A member-isolated learning helper.</returns>
    public SquadLearning CreateLearning(SquadLearningConfiguration configuration, string member)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FederationPersistence.ValidateName(member);
        return new(_files.ProjectRoot, configuration, FederationPersistence.MemberRoot(member), _options.TimeProvider);
    }

    /// <summary>Acquires and validates the release without creating any squad or federation state.</summary>
    /// <param name="chatClient">Caller-owned client used only by subsequently opened inner runtimes.</param>
    /// <param name="options">Trusted parent limits, capabilities, approval and inherited policy.</param>
    /// <param name="source">Optional truthful installed-artifact source.</param>
    /// <param name="cancellationToken">Cancellation for artifact acquisition.</param>
    /// <returns>A caller-owned lifecycle controller, not initialized state.</returns>
    public static async Task<SquadFederation> CreateAsync(IChatClient chatClient, SquadRuntimeOptions options,
        SquadArtifactSource? source = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = SquadRuntime.Snapshot(options);
        if (options.SquadRoot.Replace('\\', '/').TrimEnd('/') != ProjectFiles.StateRoot)
        {
            throw new ArgumentException("An in-repository federation is controlled only at the default native hub root.", nameof(options));
        }
        var template = await SquadRuntime.CreateCoreAsync(chatClient, options, source, false, cancellationToken).ConfigureAwait(false);
        return new(chatClient, template.ConfigurationSnapshot, template);
    }

    /// <summary>Detects registry, then single roster, then Init; reads no private model sessions.</summary>
    /// <returns>Verified registry metadata when available, with explicit partial/stray-state diagnostics.</returns>
    public SquadFederationSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _template.VerifyAcquiredArtifacts();
        var pending = _scribe.ReadFederationPending();
        var registry = File.Exists(_files.Resolve(ProjectFiles.StateRoot + "/federation.md"));
        var roster = File.Exists(_files.Resolve(ProjectFiles.StateRoot + "/team.md"));
        var diagnostics = new List<string>();
        if (pending is not null)
        {
            diagnostics.Add("A confirmed lifecycle is incomplete; resume its exact operation before using any affected root.");
        }
        if (registry && roster)
        {
            diagnostics.Add("Federation takes precedence over the stray top-level team.md; the host must reconcile that file.");
        }
        if (!registry || pending is not null || roster)
        {
            return new(registry ? SquadTopology.Federation : roster ? SquadTopology.SingleSquad : SquadTopology.Initialization,
                [], [], null, pending?.Id, diagnostics);
        }
        var state = LoadFederation();
        return new(SquadTopology.Federation, state.Federation!.Members.Select(member => member.Entry).ToArray(),
            state.Federation.Routes.ToArray(), state.FederationCurrentRun, null, diagnostics);
    }

    /// <summary>Lists every actual immediate tracking directory except squad, without selecting any for movement.</summary>
    /// <returns>Bounded original-file manifests for a host's explicit keep/move choice.</returns>
    public IReadOnlyList<SquadFederationDeliverableCandidate> GetPromotionCandidates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return PromotionCandidates([]).Candidates;
    }

    /// <summary>Confirms and seeds named members on a fresh case, or additively expands an existing federation.</summary>
    /// <param name="members">Complete proposed independent outcomes and roster settings.</param>
    /// <param name="cancellationToken">Cancellation for confirmation and bounded file preparation/commit.</param>
    /// <returns>The actual lifecycle result; a single squad requires explicit Promotion instead.</returns>
    public Task<SquadFederationLifecycleResult> InitializeAsync(
        IReadOnlyList<SquadFederationMemberConfiguration> members, CancellationToken cancellationToken = default) =>
        ExecuteLifecycleAsync(members, null, null, cancellationToken);

    /// <summary>Adopts a verified quiescent single squad, preserving its original bytes, aliases, roots and spend.</summary>
    /// <param name="name">Required unique name for the existing squad.</param>
    /// <param name="deliverableDirectories">Explicit disk-enumerated directory paths to move; all other candidates are shown as kept.</param>
    /// <param name="owner">Optional local human/team label.</param>
    /// <param name="description">Optional bounded local purpose, otherwise the confirmed existing profile is used.</param>
    /// <param name="cancellationToken">Cancellation for confirmation and bounded relocation.</param>
    /// <returns>The actual confirmed promotion, or an explicit denied/blocked/pending result.</returns>
    public Task<SquadFederationLifecycleResult> PromoteAsync(string name, IReadOnlyList<string> deliverableDirectories,
        string? owner = null, string? description = null, CancellationToken cancellationToken = default)
    {
        FederationPersistence.ValidateName(name);
        ArgumentNullException.ThrowIfNull(deliverableDirectories);
        return ExecuteLifecycleAsync(null, new()
        {
            Name = name,
            Owner = owner,
            Description = description ?? "Promoted existing native squad",
            Patterns = ["*"],
            ParallelEligible = false,
        }, deliverableDirectories, cancellationToken);
    }

    /// <summary>Resumes only the same original confirmed lifecycle; it never replays project/remote effects.</summary>
    /// <param name="operationId">Exact pending operation returned by the lifecycle or read-only snapshot.</param>
    /// <param name="cancellationToken">Cancellation for remaining preparation/copy/removal/publication.</param>
    /// <returns>The actual continuation outcome with the same identity.</returns>
    public async Task<SquadFederationLifecycleResult> ResumeLifecycleAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            throw new ArgumentException("A federation operation ID must be an exact native GUID.", nameof(operationId));
        }
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RunTimeout);
        FederationLifecycle? lifecycle = null;
        try
        {
            using var diskLock = _scribe.AcquireLock();
            lifecycle = _scribe.LoadFederationLifecycle(operationId);
            if (lifecycle.Fingerprint != ArtifactFingerprint || lifecycle.HostBinding != _template.HostBinding)
            {
                throw new InvalidDataException("The original release/host binding is required to resume this federation lifecycle.");
            }
            await CompleteLifecycleAsync(lifecycle, timeout.Token).ConfigureAwait(false);
            return new(SquadRunStatus.Completed, lifecycle.Id, lifecycle.Proposal, "The exact confirmed federation lifecycle completed.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            return Blocked(lifecycle, operationId, exception);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Opens the same native MAF engine at a verified registered member root.</summary>
    /// <param name="name">Exact registered lower-kebab-case selector.</param>
    /// <param name="readOnlyInputs">Additional explicit host-authorized input files, never writable handoffs.</param>
    /// <param name="cancellationToken">Cancellation for creation.</param>
    /// <returns>An independently caller-owned runtime. It retains all inner human, cost, risk and tool gates.</returns>
    public async Task<SquadRuntime> CreateMemberRuntimeAsync(string name, IReadOnlyList<string>? readOnlyInputs = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FederationPersistence.ValidateName(name);
        var state = LoadFederation();
        var member = state.Federation!.Members.SingleOrDefault(member => member.Entry.Name == name)
            ?? throw new InvalidDataException("The requested sub-squad is not registered.");
        var memberFiles = new ProjectFiles(_files.ProjectRoot, _options.MaxArtifactCharacters, FederationPersistence.MemberRoot(name));
        var memberScribe = new RuntimeScribe(memberFiles);
        var memberState = memberScribe.Load() ?? throw new InvalidDataException("The registered member lost its native state.");
        memberScribe.VerifyNativeMember(memberState);
        if (memberState.Trigger is { } trigger && memberState.Checkpoint?.RunId == trigger.RunId)
        {
            var configuration = _registeredWatch ?? throw new InvalidDataException(
                "Register the exact trusted Watch policy after restart before opening its original human gates.");
            var watch = state.WatchRuns?.SingleOrDefault(item => item.Trigger.WatchRunId == trigger.WatchRunId)
                ?? throw new InvalidDataException("The active Watch member has no original hub authority.");
            return await CreateWatchRuntimeAsync(watch, configuration, cancellationToken).ConfigureAwait(false);
        }
        var options = RestoreMemberOptions(memberState, member);
        options.InputPaths = _options.InputPaths.Concat(readOnlyInputs ?? []).Distinct(StringComparer.Ordinal).ToArray();
        return await SquadRuntime.CreateAsync(_client, options, _source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposes controller synchronization and its own trace source, never host clients/connectors.</summary>
    public void Dispose()
    {
        _disposed = true;
        _template.Dispose();
        _trace?.Dispose();
        _metaGate.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task<SquadFederationLifecycleResult> ExecuteLifecycleAsync(
        IReadOnlyList<SquadFederationMemberConfiguration>? members, SquadFederationMemberConfiguration? promotion,
        IReadOnlyList<string>? selectedDirectories, CancellationToken cancellationToken,
        WatchBootstrapAuthorization? watchAuthorization = null, IReadOnlyList<string>? watchExistingMembers = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(members ?? (object?)promotion);
        if (watchExistingMembers is not null && watchAuthorization is null)
        {
            throw new InvalidOperationException("Only an authenticated structural Watch bootstrap may retain a proven existing event member.");
        }
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RunTimeout);
        FederationLifecycle? lifecycle = null;
        Activity? activity = null;
        try
        {
            var snapshot = GetSnapshot();
            var watchMixed = watchAuthorization is not null && snapshot.Topology == SquadTopology.Federation &&
                snapshot.PendingOperationId is null && File.Exists(_files.Resolve(ProjectFiles.StateRoot + "/team.md"));
            if (snapshot.PendingOperationId is not null || snapshot.Diagnostics.Count != 0 && !watchMixed)
            {
                throw new InvalidDataException("Reconcile the exact pending or stray federation state before another lifecycle.");
            }
            var operation = promotion is not null ? "Promotion" :
                snapshot.Topology == SquadTopology.Federation ? "Expansion" : "Initialization";
            activity = _trace?.StartActivity(operation switch
            {
                "Promotion" => "promote.federation",
                "Expansion" => "expand.federation",
                _ => "initialize.federation",
            }, ActivityKind.Internal);
            if (promotion is null && snapshot.Topology == SquadTopology.SingleSquad ||
                promotion is not null && snapshot.Topology != SquadTopology.SingleSquad)
            {
                throw new InvalidDataException("Init expands only a federation or initializes a fresh case; Promotion requires a single roster without a registry.");
            }
            var existing = snapshot.Topology == SquadTopology.Federation
                ? watchAuthorization is null ? LoadFederation() : LoadWatchFederation() : _scribe.Load();
            if (existing?.FederationRun is { } metaRun &&
                (metaRun.PlanConfirmed || metaRun.Steps.Any(step => step.Started)))
            {
                throw new InvalidDataException("Resume, stop or reconcile the exact active meta-run before changing its federation topology.");
            }
            if (snapshot.Topology == SquadTopology.Initialization && existing is not null)
            {
                throw new InvalidDataException("Partial native state cannot be overwritten by federation Init.");
            }
            var configurations = (promotion is null ? members! : [promotion]).Select(FederationPersistence.Copy).ToArray();
            ValidateConfigurations(configurations);
            ValidateNames(configurations.Select(member => member.Name), existing?.Federation, watchExistingMembers);
            var candidates = promotion is null ? (Candidates: Array.Empty<SquadFederationDeliverableCandidate>(),
                Trees: new Dictionary<string, FederationTree>(StringComparer.Ordinal)) : PromotionCandidates(selectedDirectories!);
            var proposals = new List<SquadFederationProposedMember>();
            var nativeMembers = new List<NativeFederationMember>();
            foreach (var configuration in configurations)
            {
                var options = promotion is null
                    ? MemberOptions(configuration.Roster ?? SquadRuntime.ConfigurationFromOptions(_options), configuration.Name,
                        existing?.Notify ?? _options.Notifications, existing?.Federation?.NamingPolicy ?? _options.NamingPolicy, rebase: true)
                    : PromotionOptions(existing!, configuration.Name);
                using var preview = await SquadRuntime.CreateCoreAsync(_client, options, _source, false, timeout.Token).ConfigureAwait(false);
                var initialization = preview.MemberInitialization;
                if (promotion is not null)
                {
                    initialization = initialization with { Profile = existing!.Initialization!.Profile };
                }
                var profile = initialization.Profile + string.Concat(initialization.Packs.Select(pack => " +" + pack));
                proposals.Add(new(configuration.Name, initialization, configuration.Owner, configuration.Description,
                    configuration.Patterns.Count == 0 ? [configuration.Description] : configuration.Patterns.ToArray(),
                    configuration.ParallelEligible));
                nativeMembers.Add(new(new(configuration.Name, profile, "in-repo", "members/" + configuration.Name + "/",
                    configuration.Owner, configuration.Description), SquadRuntime.ConfigurationFromOptions(preview.ConfigurationSnapshot)));
            }
            var proposal = new SquadFederationProposal(operation, proposals, candidates.Candidates,
                SquadRuntime.CopyNotifications(existing?.Notify ?? _options.Notifications),
                existing?.Federation?.NamingPolicy ?? existing?.Initialization?.NamingPolicy ?? _options.NamingPolicy,
                Provenance.Release, ArtifactFingerprint,
                promotion is null ? existing?.FederationCurrentRun : FederationPersistence.Cost(existing!));
            lifecycle = CreateLifecycle(proposal, nativeMembers, candidates.Trees, existing, promotion is not null);
            lifecycle.WatchAuthorization = watchAuthorization;
            lifecycle.WatchExistingMembers = watchExistingMembers;
            foreach (var name in watchExistingMembers ?? [])
            {
                var tree = FederationPersistence.Enumerate(_files, FederationPersistence.MemberRoot(name),
                    path => path.EndsWith("/native.lock", StringComparison.Ordinal));
                lifecycle.ReadOnlyFiles.AddRange(tree.Files.Select(file =>
                    new FederationMove(file.Path, file.Path, file.Sha256, file.Bytes)));
            }
            if (watchAuthorization is null && _options.ApproveAsync is null)
            {
                return new(SquadRunStatus.ApprovalRequired, null, proposal,
                    "A trusted host must confirm these exact named members, inherited contacts/naming and disk-enumerated move/keep choices.");
            }
            var kind = operation switch
            {
                "Promotion" => SquadApprovalKind.FederationPromotion,
                "Expansion" => SquadApprovalKind.FederationExpansion,
                _ => SquadApprovalKind.FederationInitialization,
            };
            if (watchAuthorization is null && !await _options.ApproveAsync!(new(kind,
                "Confirm this exact federation lifecycle. Ambiguous tracking directories are explicitly kept or moved; " +
                "all members retain their own gates. No deployment, remote mutation or cross-member write is authorized.",
                Federation: proposal), timeout.Token).ConfigureAwait(false))
            {
                return new(SquadRunStatus.ApprovalDenied, null, proposal, "Federation confirmation declined; no files or model calls were created.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (HumanActions.Hash(lifecycle.Proposal) != lifecycle.ProposalSha256)
            {
                throw new InvalidDataException("The exact federation proposal changed during confirmation; no files were created.");
            }
            using var diskLock = _scribe.AcquireLock();
            ValidateNames(configurations.Select(member => member.Name), existing?.Federation, watchExistingMembers);
            AssertOriginalFiles(lifecycle);
            if (GetSnapshot().Topology != snapshot.Topology || _scribe.ReadFederationPending() is not null)
            {
                throw new InvalidDataException("Federation detection changed while confirmation was pending; no stale lifecycle may commit.");
            }
            _scribe.StartFederationLifecycle(lifecycle);
            await CompleteLifecycleAsync(lifecycle, timeout.Token).ConfigureAwait(false);
            return new(SquadRunStatus.Completed, lifecycle.Id, proposal,
                "The confirmed " + operation.ToLowerInvariant() + " completed with verified isolated native trees; no delivery or deployment is claimed.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Federation lifecycle did not complete");
            return Blocked(lifecycle, null, exception);
        }
        finally
        {
            activity?.Dispose();
            _lifecycleLock.Release();
        }
    }

    private SquadFederationLifecycleResult Blocked(FederationLifecycle? lifecycle, string? requestedId, Exception exception)
    {
        if (_logger is not null)
        {
            LogLifecycleBlocked(_logger, null);
        }
        var pending = _scribe.ReadFederationPending();
        return new(SquadRunStatus.Blocked,
            pending?.Id ?? requestedId, lifecycle?.Proposal,
            exception.Message + (pending is null ? " No lifecycle commit is pending." : " Resume operation " + pending.Id + " after resolving the cause."));
    }

    private RuntimeState LoadFederation()
    {
        if (_scribe.ReadFederationPending() is not null ||
            File.Exists(_files.Resolve(ProjectFiles.StateRoot + "/team.md")))
        {
            throw new InvalidDataException("Pending migration or a stray top-level roster blocks federation use.");
        }
        var state = _scribe.Load(allowFederation: true) ?? throw new InvalidDataException("Initialize a confirmed federation first.");
        if (state.Fingerprint != ArtifactFingerprint)
        {
            throw new InvalidDataException("The federation remains bound to its original acquired release and dependencies.");
        }
        _scribe.VerifyFederation(state);
        return state;
    }

    private SquadRuntimeOptions MemberOptions(SquadRosterConfiguration roster, string name,
        SquadNotificationConfiguration? inheritedNotifications, string? inheritedNaming, bool rebase)
    {
        roster = FederationPersistence.Copy(roster);
        var options = SquadRuntime.Snapshot(_options);
        options.SquadRoot = FederationPersistence.MemberRoot(name);
        options.InitializationScope = "single-squad";
        options.SelectInitializationAsync = null;
        options.Profile = roster.Profile;
        options.Packs = roster.Packs.ToArray();
        options.Roles = roster.Roles.ToArray();
        options.AdditionalRoles = roster.AdditionalRoles.ToArray();
        options.NamingPolicy = roster.NamingPolicy ?? inheritedNaming;
        options.MemberNames = new Dictionary<string, string>(roster.MemberNames, StringComparer.OrdinalIgnoreCase);
        options.Notifications = SquadRuntime.CopyNotifications(roster.Notifications ?? inheritedNotifications ?? _options.Notifications);
        options.DeliverableRoots = roster.DeliverableRoots.ToDictionary(entry => entry.Key, entry =>
        {
            var path = entry.Value.Replace('\\', '/');
            if (ProjectFiles.IsWithin(path, ProjectFiles.StateRoot + "/members") &&
                !ProjectFiles.IsWithin(path, options.SquadRoot))
            {
                throw new InvalidDataException("A member root override cannot target another sub-squad.");
            }
            return rebase && !ProjectFiles.IsWithin(path, options.SquadRoot)
                ? FederationPersistence.RebaseRoot(path, options.SquadRoot) : path;
        }, StringComparer.OrdinalIgnoreCase);
        SquadRuntime.ValidateOptions(options);
        return options;
    }

    private SquadRuntimeOptions PromotionOptions(RuntimeState state, string name)
    {
        if (state.Fingerprint != ArtifactFingerprint || state.Initialization is not { Roster: not null } initialization)
        {
            throw new InvalidDataException("Promotion requires the exact native confirmed roster and acquired release.");
        }
        _scribe.VerifyPromotion(state);
        var original = new SquadRoster { Members = initialization.Roster };
        var roots = _scribe.ReadRosterRoots(original);
        var roster = new SquadRosterConfiguration
        {
            Profile = initialization.Profile.StartsWith("custom:", StringComparison.Ordinal)
                ? initialization.Profile["custom:".Length..] : initialization.Profile,
            Packs = initialization.Packs.ToArray(),
            Roles = initialization.Roles.ToArray(),
            NamingPolicy = initialization.NamingPolicy,
            MemberNames = initialization.NamingPolicy == "assigned" ? new Dictionary<string, string>() :
                new Dictionary<string, string>(initialization.MemberNames, StringComparer.OrdinalIgnoreCase),
            DeliverableRoots = roots.Where(entry => entry.Key != "scribe" && entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.OrdinalIgnoreCase),
            Notifications = SquadRuntime.CopyNotifications(state.Notify ?? initialization.Notifications ?? _options.Notifications),
        };
        var options = MemberOptions(roster, name, state.Notify, initialization.NamingPolicy, rebase: true);
        options.ModelRouting = state.ModelRouting is null ? null : SquadRuntime.CopyModelRouting(state.ModelRouting);
        options.Consumption = state.Consumption is null ? null : ConsumptionMath.Copy(state.Consumption);
        var rebased = SquadRuntime.BuildRoster(_template.Catalog, _template.Artifacts, options);
        if (original.Members.Any(member => rebased.Resolve(member.Role)?.MemberName != member.MemberName ||
            rebased.Resolve(member.Role)?.PrimaryAgent != member.PrimaryAgent))
        {
            throw new InvalidDataException("The acquired cast cannot preserve the existing literal roster identity.");
        }
        return options;
    }

    private (SquadFederationDeliverableCandidate[] Candidates, Dictionary<string, FederationTree> Trees)
        PromotionCandidates(IReadOnlyList<string> selected)
    {
        var root = _files.Resolve(".copilot-tracking");
        if (!Directory.Exists(root))
        {
            if (selected.Count != 0)
            {
                throw new InvalidDataException("A selected promotion directory does not exist.");
            }
            return ([], new(StringComparer.Ordinal));
        }
        var selection = selected.ToHashSet(StringComparer.Ordinal);
        if (selection.Count != selected.Count)
        {
            throw new ArgumentException("Promotion directory choices must be unique.", nameof(selected));
        }
        var trees = new Dictionary<string, FederationTree>(StringComparer.Ordinal);
        var candidates = new List<SquadFederationDeliverableCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ProjectFiles.RejectLinks(directory);
            var path = Path.GetRelativePath(_files.ProjectRoot, directory).Replace('\\', '/');
            if (path.Equals(ProjectFiles.StateRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Path.GetFileName(directory).Equals(".copilot-tracking", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A duplicated tracking-root directory requires host reconciliation, not another relocation.");
            }
            var tree = FederationPersistence.Enumerate(_files, path);
            trees.Add(path, tree);
            candidates.Add(new(path, tree.Files.Count, tree.Files.Sum(file => file.Bytes),
                HumanActions.Hash(new { tree.Directories, Files = tree.Files.Select(file => new { file.Path, file.Sha256, file.Bytes }) }),
                selection.Contains(path), "Disk-enumerated. Ownership/age is not inferred; choose keep or move explicitly, including unrelated earlier workflows."));
        }
        if (selection.Any(path => !trees.ContainsKey(path)))
        {
            throw new InvalidDataException("A selected deliverable directory was not enumerated from the actual tracking root.");
        }
        return (candidates.ToArray(), trees);
    }

    private FederationLifecycle CreateLifecycle(SquadFederationProposal proposal, List<NativeFederationMember> members,
        Dictionary<string, FederationTree> trees, RuntimeState? state, bool promotion)
    {
        var moves = new List<FederationMove>();
        var directories = new List<FederationDirectoryMove>();
        var rootFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(_files.Resolve(ProjectFiles.StateRoot)))
        {
            var root = FederationPersistence.Enumerate(_files, ProjectFiles.StateRoot, path =>
                ProjectFiles.IsWithin(path, ProjectFiles.StateRoot + "/members") ||
                ProjectFiles.IsWithin(path, FederationPersistence.MigrationRoot) ||
                path is ProjectFiles.StateRoot + "/native.lock" or ProjectFiles.StateRoot + "/federation.run.lock");
            foreach (var file in root.Files)
            {
                rootFiles.Add(file.Path, file.Sha256);
            }
            if (promotion)
            {
                var target = FederationPersistence.MemberRoot(members[0].Entry.Name);
                moves.AddRange(root.Files.Select(file => new FederationMove(file.Path,
                    target + file.Path[ProjectFiles.StateRoot.Length..], file.Sha256, file.Bytes)));
                directories.AddRange(root.Directories.Where(path => path != ProjectFiles.StateRoot).Select(path =>
                    new FederationDirectoryMove(path, target + path[ProjectFiles.StateRoot.Length..])));
            }
        }
        if (promotion)
        {
            var target = FederationPersistence.MemberRoot(members[0].Entry.Name);
            foreach (var candidate in proposal.Deliverables.Where(candidate => candidate.Selected))
            {
                var tree = trees[candidate.Path];
                var scoped = new ProjectFiles(_files.ProjectRoot, _options.MaxArtifactCharacters, target);
                _ = scoped.ValidateDeliverableRoot(FederationPersistence.RebaseRoot(candidate.Path, target));
                moves.AddRange(tree.Files.Select(file => new FederationMove(file.Path,
                    FederationPersistence.RebaseRoot(file.Path, target), file.Sha256, file.Bytes)));
                directories.AddRange(tree.Directories.Select(path => new FederationDirectoryMove(path,
                    FederationPersistence.RebaseRoot(path, target))));
            }
            var outputs = state!.DispatchRecords.SelectMany(dispatch =>
                new[] { dispatch.ArtifactPath }.Concat(dispatch.Artifacts?.Select(artifact => artifact.Path) ?? [])
                    .Concat((dispatch.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? []).Select(output => output.Path))).ToArray();
            foreach (var candidate in proposal.Deliverables.Where(candidate => !candidate.Selected))
            {
                if (outputs.Any(path => ProjectFiles.IsWithin(path, candidate.Path)))
                {
                    throw new InvalidDataException("A directory containing verified squad output was kept in place; select that actual directory before promotion.");
                }
            }
        }
        if (moves.Count > FederationPersistence.MaximumFiles || moves.Sum(move => move.Bytes) > FederationPersistence.MaximumBytes ||
            moves.Select(move => move.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != moves.Count ||
            directories.Select(directory => directory.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Count)
        {
            throw new InvalidDataException("The complete relocation is oversized or has colliding deliverable destinations.");
        }
        if (proposal.Operation == "Initialization" && rootFiles.Count != 0)
        {
            throw new InvalidDataException("Partial native files without a roster cannot be overwritten by federation Init.");
        }
        var readOnly = promotion ? state!.DispatchRecords.SelectMany(dispatch =>
            new[] { dispatch.ArtifactPath }.Concat(dispatch.Artifacts?.Select(artifact => artifact.Path) ?? [])
                .Concat((dispatch.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? []).Select(output => output.Path)))
            .Distinct(StringComparer.Ordinal).Where(path => moves.All(move => move.Source != path))
            .Select(path => new FederationMove(path, path, _files.HashFile(path), new FileInfo(_files.Resolve(path)).Length)).ToList() : [];
        return new()
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Fingerprint = ArtifactFingerprint,
            HostBinding = _template.HostBinding,
            Proposal = proposal,
            ProposalSha256 = HumanActions.Hash(proposal),
            Members = members,
            Moves = moves,
            ReadOnlyFiles = readOnly,
            Directories = directories,
            RootFiles = rootFiles,
        };
    }

    private async Task CompleteLifecycleAsync(FederationLifecycle lifecycle, CancellationToken token)
    {
        _template.VerifyAcquiredArtifacts();
        if (lifecycle.Phase == "preparing")
        {
            AssertOriginalFiles(lifecycle);
            lifecycle.Attempt++;
            _scribe.SaveFederationLifecycle(lifecycle);
            var imageProject = _files.Resolve(FederationPersistence.MigrationRoot + "/" + lifecycle.Id + "/image-" +
                lifecycle.Attempt.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(imageProject);
            var image = new ProjectFiles(imageProject, _options.MaxArtifactCharacters);
            var imageScribe = new RuntimeScribe(image, timeProvider: _options.TimeProvider);
            foreach (var directory in lifecycle.Directories)
            {
                Directory.CreateDirectory(image.Resolve(directory.Destination));
            }
            foreach (var move in lifecycle.Moves)
            {
                token.ThrowIfCancellationRequested();
                imageScribe.WriteExclusive(move.Destination, _files.ReadBytes(move.Source));
                if (image.HashFile(move.Destination) != move.Sha256)
                {
                    throw new InvalidDataException("An original-byte prepared copy could not be verified.");
                }
            }
            RuntimeState? original = null;
            if (lifecycle.Proposal.Operation == "Promotion")
            {
                original = _scribe.Load() ?? throw new InvalidDataException("The original single squad is missing.");
                foreach (var readOnly in lifecycle.ReadOnlyFiles)
                {
                    token.ThrowIfCancellationRequested();
                    imageScribe.WriteExclusive(readOnly.Destination, _files.ReadBytes(readOnly.Source));
                    if (image.HashFile(readOnly.Destination) != readOnly.Sha256)
                    {
                        throw new InvalidDataException("An original read-only output changed during relocation preparation.");
                    }
                }
                PreparePromotedMember(lifecycle, imageProject, original, token);
            }
            else
            {
                foreach (var member in lifecycle.Members)
                {
                    token.ThrowIfCancellationRequested();
                    if (lifecycle.WatchExistingMembers?.Contains(member.Entry.Name, StringComparer.Ordinal) == true)
                    {
                        foreach (var file in lifecycle.ReadOnlyFiles.Where(file =>
                            ProjectFiles.IsWithin(file.Source, FederationPersistence.MemberRoot(member.Entry.Name))))
                        {
                            imageScribe.WriteExclusive(file.Destination, _files.ReadBytes(file.Source));
                            if (image.HashFile(file.Destination) != file.Sha256)
                            {
                                throw new InvalidDataException("The proven original orphan bytes changed during preparation.");
                            }
                        }
                        continue;
                    }
                    var options = MemberOptions(member.Roster, member.Entry.Name, lifecycle.Proposal.Notifications,
                        lifecycle.Proposal.NamingPolicy, rebase: false);
                    options.ProjectPath = imageProject;
                    using var runtime = await SquadRuntime.CreateCoreAsync(_client, options, _source, false, token).ConfigureAwait(false);
                    runtime.InitializeConfirmedMember();
                }
            }
            if (lifecycle.Proposal.Operation == "Expansion")
            {
                foreach (var file in lifecycle.RootFiles.Where(file =>
                    !ProjectFiles.IsWithin(file.Key, FederationPersistence.MigrationRoot)))
                {
                    imageScribe.WriteExclusive(file.Key, _files.ReadBytes(file.Key));
                }
                var state = imageScribe.Load(allowFederation: true) ?? throw new InvalidDataException("The original federation state is missing.");
                imageScribe.ExpandFederation(state, lifecycle);
            }
            else
            {
                var state = new RuntimeState
                {
                    Schema = RuntimeState.FederationSchemaVersion,
                    Fingerprint = ArtifactFingerprint,
                    Binding = FederationPersistence.ProjectBinding(_files.ProjectRoot),
                    SquadRoot = ProjectFiles.StateRoot,
                    ReleaseTag = Provenance.Release?.Tag,
                    ReleaseCommitSha = Provenance.Release?.CommitSha,
                    ArtifactOrigin = Provenance.Origin,
                    Notify = SquadRuntime.CopyNotifications(lifecycle.Proposal.Notifications),
                    ModelRouting = original?.ModelRouting ?? _options.ModelRouting,
                    Consumption = original?.Consumption ?? _options.Consumption,
                    Federation = new()
                    {
                        ProjectBinding = FederationPersistence.ProjectBinding(_files.ProjectRoot),
                        NamingPolicy = lifecycle.Proposal.NamingPolicy,
                        Members = lifecycle.Members,
                        Routes = lifecycle.Proposal.Members.SelectMany(member => member.Patterns.Select(pattern =>
                            new SquadFederationRoute(pattern, member.Name, member.ParallelEligible))).ToList(),
                    },
                    FederationCurrentRun = lifecycle.Proposal.PreservedCost,
                };
                var outputs = original is null ? [] : OriginalSharedOutputs(original, lifecycle.Members[0].Entry.Name);
                imageScribe.SeedFederation(state, lifecycle, outputs);
            }
            _scribe.PrepareFederationImage(lifecycle, imageProject);
        }
        token.ThrowIfCancellationRequested();
        _scribe.CommitFederationImage(lifecycle, token);
    }

    private void PreparePromotedMember(FederationLifecycle lifecycle, string imageProject,
        RuntimeState original, CancellationToken token)
    {
        var member = lifecycle.Members[0];
        var options = MemberOptions(member.Roster, member.Entry.Name, lifecycle.Proposal.Notifications,
            lifecycle.Proposal.NamingPolicy, rebase: false);
        options.ProjectPath = imageProject;
        var root = options.SquadRoot;
        var image = new ProjectFiles(imageProject, _options.MaxArtifactCharacters, root);
        var scribe = new RuntimeScribe(image, timeProvider: _options.TimeProvider);
        var sourceState = _files.ReadBytes(ProjectFiles.StateRoot + "/state.json");
        var originalStatePath = root + "/migrations/federation-" + lifecycle.Id + "/original-state.json";
        scribe.WriteExclusive(originalStatePath, sourceState);
        scribe.WriteExclusive(root + "/migrations/federation-" + lifecycle.Id + "/original-team.md",
            _files.ReadBytes(ProjectFiles.StateRoot + "/team.md"));
        var originalRoster = new SquadRoster { Members = original.Initialization!.Roster! };
        var rebasedText = _scribe.RebaseRosterText(Encoding.UTF8.GetString(
            _files.ReadBytes(ProjectFiles.StateRoot + "/team.md")), originalRoster, root);
        image.Write(root + "/team.md", rebasedText, maximumCharacters: ProjectFiles.MaximumHostOutputBytes);
        var state = scribe.Load() ?? throw new InvalidDataException("The exact original native state was not copied.");
        var relocation = new NativeRelocation(lifecycle.Id, ProjectFiles.StateRoot, root, originalStatePath,
            _files.HashFile(ProjectFiles.StateRoot + "/state.json"), lifecycle.Moves);
        relocation = relocation with { Seal = HumanActions.Hash(relocation) };
        var roster = SquadRuntime.BuildRoster(_template.Catalog, _template.Artifacts, options);
        scribe.SaveRebasedState(state, options, roster, relocation);
        token.ThrowIfCancellationRequested();
    }

    private static NativeFederationOutputOwner[] OriginalSharedOutputs(RuntimeState state, string member) =>
        state.DispatchRecords.SelectMany(dispatch => new[] { new SquadArtifactEvidence("primary", dispatch.ArtifactPath, dispatch.ArtifactSha256) }
            .Concat(dispatch.Artifacts ?? []).Concat((dispatch.ExecutedHostTools ?? []).SelectMany(tool => tool.Outputs ?? []))
            .Where(output => ProjectFiles.IsWithin(output.Path, "docs") || ProjectFiles.IsWithin(output.Path, "outputs"))
            .Select(output => new NativeFederationOutputOwner(member, output.Path,
                state.Runs.Single(run => run.Evidence.Any(evidence => evidence.DispatchId == dispatch.DispatchId)).RunId, dispatch.DispatchId)))
            .DistinctBy(owner => owner.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    private void AssertOriginalFiles(FederationLifecycle lifecycle)
    {
        _template.VerifyAcquiredArtifacts();
        foreach (var move in lifecycle.Moves.Concat(lifecycle.ReadOnlyFiles))
        {
            if (_files.HashFile(move.Source) != move.Sha256)
            {
                throw new InvalidDataException("An enumerated promotion source changed while confirmation was pending.");
            }
        }
        foreach (var file in lifecycle.RootFiles)
        {
            if (_files.HashFile(file.Key) != file.Value)
            {
                throw new InvalidDataException("The original native root changed while confirmation was pending.");
            }
        }
        if (Directory.Exists(_files.Resolve(ProjectFiles.StateRoot)))
        {
            var root = FederationPersistence.Enumerate(_files, ProjectFiles.StateRoot, path =>
                ProjectFiles.IsWithin(path, ProjectFiles.StateRoot + "/members") ||
                ProjectFiles.IsWithin(path, FederationPersistence.MigrationRoot) ||
                path is ProjectFiles.StateRoot + "/native.lock" or ProjectFiles.StateRoot + "/federation.run.lock" or FederationPersistence.PendingPath);
            if (!root.Files.Select(file => file.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(lifecycle.RootFiles.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new InvalidDataException("The original native file inventory changed while confirmation was pending.");
            }
        }
        foreach (var candidate in lifecycle.Proposal.Deliverables.Where(candidate => candidate.Selected))
        {
            var current = FederationPersistence.Enumerate(_files, candidate.Path);
            if (!current.Files.Select(file => file.Path).Order(StringComparer.Ordinal).SequenceEqual(
                lifecycle.Moves.Where(move => ProjectFiles.IsWithin(move.Source, candidate.Path))
                    .Select(move => move.Source).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new InvalidDataException("The selected deliverable file inventory changed after confirmation.");
            }
        }
    }

    private SquadRuntimeOptions RestoreMemberOptions(RuntimeState state, NativeFederationMember member)
    {
        if (state.Fingerprint != ArtifactFingerprint || state.Initialization is not { } initialization)
        {
            throw new InvalidDataException("A member must retain its exact acquired release and confirmed initialization.");
        }
        var roster = new SquadRosterConfiguration
        {
            Profile = initialization.Profile.StartsWith("custom:", StringComparison.Ordinal)
                ? initialization.Profile["custom:".Length..] : initialization.Profile,
            Packs = initialization.Packs.ToArray(),
            Roles = initialization.Roles.ToArray(),
            NamingPolicy = initialization.NamingPolicy == "skip" && member.Roster.NamingPolicy is null
                ? null : initialization.NamingPolicy,
            MemberNames = initialization.NamingPolicy == "assigned" ? new Dictionary<string, string>() :
                new Dictionary<string, string>(initialization.MemberNames, StringComparer.OrdinalIgnoreCase),
            DeliverableRoots = new Dictionary<string, string>(initialization.DeliverableRoots ??
                throw new InvalidDataException("The confirmed member root index is missing."), StringComparer.OrdinalIgnoreCase),
            Notifications = state.Notify,
        };
        roster.DeliverableRoots.Remove("scribe");
        var options = MemberOptions(roster, member.Entry.Name, state.Notify, _options.NamingPolicy, rebase: false);
        var built = SquadRuntime.BuildRoster(_template.Catalog, _template.Artifacts, options);
        if (SquadRuntime.RosterBinding(options, built) == state.Binding)
        {
            return options;
        }
        options = MemberOptions(member.Roster, member.Entry.Name, state.Notify, _options.NamingPolicy, rebase: false);
        built = SquadRuntime.BuildRoster(_template.Catalog, _template.Artifacts, options);
        if (SquadRuntime.RosterBinding(options, built) != state.Binding)
        {
            throw new InvalidDataException("The member's current confirmed roster cannot be reconstructed from its native evidence.");
        }
        return options;
    }

    private void ValidateNames(IEnumerable<string> names, NativeFederation? federation, IReadOnlyList<string>? watchExistingMembers = null)
    {
        var proposed = names.ToArray();
        var memberDirectory = _files.Resolve(ProjectFiles.StateRoot + "/members");
        var existing = Directory.Exists(memberDirectory)
            ? Directory.EnumerateDirectories(memberDirectory).Select(Path.GetFileName).ToArray() : [];
        if ((federation?.Members.Count ?? 0) + proposed.Length > 64)
        {
            throw new InvalidDataException("The complete federation is limited to 64 registered members; expansion cannot create an unusable over-limit registry.");
        }
        if (proposed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != proposed.Length ||
            proposed.Any(name => federation?.Members.Any(member =>
                member.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true ||
                File.Exists(_files.Resolve(FederationPersistence.MemberRoot(name))) ||
                existing.Any(directory => directory?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                    !(watchExistingMembers?.Contains(name, StringComparer.Ordinal) == true && directory == name))))
        {
            throw new InvalidDataException("A sub-squad name or directory already exists. Choose another name; no auto-suffix or overwrite is permitted.");
        }
    }

    private static void ValidateConfigurations(SquadFederationMemberConfiguration[] members)
    {
        if (members.Length is < 1 or > 64)
        {
            throw new ArgumentException("Propose between one and 64 named sub-squads.", nameof(members));
        }
        foreach (var member in members)
        {
            FederationPersistence.ValidateName(member.Name);
            if (member.Roster?.Scope is not (null or "single-squad") ||
                string.IsNullOrWhiteSpace(member.Description) || member.Description.Length > 512 ||
                member.Owner?.Length > 100 || member.Patterns.Count > 32 ||
                new[] { member.Description, member.Owner }.Concat(member.Patterns).Any(value =>
                    value?.Any(character => char.IsControl(character) || character == '|') == true) ||
                member.Patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512) ||
                member.Patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != member.Patterns.Count)
            {
                throw new ArgumentException("Federation descriptions, owners and routes must be bounded, unique, single-line table values.", nameof(members));
            }
        }
    }

    private sealed class ResolvedSource(SquadArtifactRoots roots) : SquadArtifactSource
    {
        public override ValueTask<SquadArtifactRoots> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(roots);
        }
    }
}
