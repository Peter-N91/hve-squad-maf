using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Artifacts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record RuntimeModelRoute(IChatClient Client, string? RequestedModel, string? ParameterModel,
    string Source, string AssignmentClass, string? Floor, string Rationale, bool Unevaluated, bool PolicyApplied)
{
    internal SquadDispatchModelIdentity Identity(string? observed) => new(RequestedModel,
        ParameterModel, observed, AssignmentClass, Floor, IdentityRationale(observed), Unevaluated);

    internal string IdentityRationale(string? observed)
    {
        if (RequestedModel is null)
        {
            return Rationale;
        }
        if (observed is not null && observed != RequestedModel)
        {
            return Rationale + $"; identity-mismatch: requested {RequestedModel}, observed {observed}";
        }
        return ParameterModel != RequestedModel
            ? Rationale + $"; identity-mismatch: requested {RequestedModel}, effective {ParameterModel}"
            : Rationale;
    }
}

public sealed partial class SquadRuntime
{
    private static readonly Action<ILogger, Exception?> LogModelRoutingWarning = LoggerMessage.Define(
        LogLevel.Warning, new EventId(5, "ModelRoutingWarning"),
        "Model routing used a fallback, refused a cell, or observed an identity mismatch. See the protected run diagnostics.");
    private SquadModelCatalog? _modelCatalog;
    private string? _modelCatalogDiagnostic;
    private bool _modelCatalogLoaded;

    /// <summary>Builds read-only menus from the installed catalog and actual host/client bindings.</summary>
    /// <param name="cancellationToken">Cancellation of host availability discovery.</param>
    /// <returns>At most eight ranked candidates per role, with all typed-eligible available IDs separate.</returns>
    public async Task<SquadModelRoutingChoices> GetModelRoutingChoicesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _scribe.Load();
            ValidateState(state);
            var policy = _options.ModelRouting ?? state?.ModelRouting ?? new();
            return await ModelChoicesAsync(policy, state?.PreviousManualModels, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Confirms a persisted policy change without recasting roles or resetting evidence.</summary>
    /// <param name="configuration">The exact proposed off, ranked or manual configuration.</param>
    /// <param name="cancellationToken">Cancellation while waiting for root ownership and approval.</param>
    /// <returns>The actual update status; missing manual picks prompt before the next dispatch.</returns>
    public async Task<SquadModelRoutingUpdateResult> UpdateModelRoutingAsync(
        SquadModelRoutingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var proposed = CopyModelRouting(configuration);
        ValidateModelRouting(proposed);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_scribe.Load() is null)
            {
                throw new InvalidDataException("Confirm initialization before updating model routing.");
            }
            using var diskLock = _scribe.AcquireLock();
            var state = _scribe.Load() ?? throw new InvalidDataException("Confirm initialization before updating model routing.");
            ValidateState(state);
            _ = _scribe.PrepareEvidence(state);
            ReadEffectiveRosterRoots();
            if (state.Checkpoint is not null || state.ActiveRun is not null)
            {
                return new(SquadRunStatus.Blocked, proposed,
                    "An active or suspended run owns its exact model policy; finish or reconcile it before updating.");
            }
            proposed = await ResolvePolicyCellsAsync(proposed, state, cancellationToken).ConfigureAwait(false);
            if (_options.ApproveAsync is null)
            {
                return new(SquadRunStatus.ApprovalRequired, proposed, "ModelRouting approval is required before changing policy.");
            }
            if (!await _options.ApproveAsync(new(SquadApprovalKind.ModelRouting,
                "Confirm this exact persisted model policy and cells. This never lowers roster floors or authorizes effects.",
                ModelRouting: proposed), cancellationToken).ConfigureAwait(false))
            {
                return new(SquadRunStatus.ApprovalDenied, proposed, "Model routing update declined; state is unchanged.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_resources.Fingerprint() != ArtifactFingerprint)
            {
                throw new InvalidDataException("Artifacts changed while model policy was awaiting approval.");
            }
            _scribe.SetModelRouting(state, Roster, Catalog.RoutingRules, proposed, "Confirmed model routing update.");
            _options.ModelRouting = CopyModelRouting(proposed);
            return new(SquadRunStatus.Completed, CopyModelRouting(proposed),
                "Model policy confirmed. Empty or refused manual cells require human selection before dispatch.");
        }
        finally
        {
            _runLock.Release();
        }
    }

    internal static SquadModelRoutingConfiguration CopyModelRouting(SquadModelRoutingConfiguration configuration) => new()
    {
        Mode = configuration.Mode,
        Models = new Dictionary<string, string>(configuration.Models, StringComparer.OrdinalIgnoreCase),
    };

    private static SquadModelRoutingConfiguration SafeModelRoutingCopy(SquadModelRoutingConfiguration configuration) => new()
    {
        Mode = configuration.Mode,
        Models = configuration.Mode == "manual"
            ? configuration.Models.Where(pair => SquadModelCatalog.IsModelId(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(),
    };

    private static void ValidateModelRouting(SquadModelRoutingConfiguration configuration)
    {
        if (configuration.Mode is not ("off" or "ranked" or "manual") || configuration.Models is null ||
            configuration.Models.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Model routing requires off, ranked or manual and nonempty role keys.", nameof(configuration));
        }
    }

    private static string ModelPolicyHash(SquadModelRoutingConfiguration configuration) =>
        ProjectFiles.Hash(JsonSerializer.Serialize(new
        {
            configuration.Mode,
            Models = configuration.Mode == "manual"
                ? configuration.Models.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new { Role = pair.Key.ToLowerInvariant(), Model = pair.Value }).ToArray()
                : [],
        }));

    private static bool SamePolicy(SquadModelRoutingConfiguration left, SquadModelRoutingConfiguration right) =>
        left.Mode == right.Mode && (left.Mode != "manual" ||
            left.Models.Count == right.Models.Count && left.Models.All(pair =>
                right.Models.TryGetValue(pair.Key, out var value) && value == pair.Value));

    private bool ResumeModelPolicyMatches(RuntimeState state, RunCheckpoint checkpoint) =>
        checkpoint.ModelRouting is { } bound
            ? state.ModelRouting is { } current && SamePolicy(bound, current) &&
                (_options.ModelRouting is null || checkpoint.ModelRoutingInputSha256 == ModelPolicyHash(_options.ModelRouting))
            : _options.ModelRouting?.Mode is null or "off";

    private SquadModelCatalog? ModelCatalog()
    {
        using (_scribe.SyncRoot.EnterScope())
        {
            if (_modelCatalogLoaded)
            {
                return _modelCatalog;
            }
            _modelCatalogLoaded = true;
            var catalog = _resources.OptionalReference("model-catalog");
            var routing = _resources.OptionalReference("model-routing");
            if (catalog is null || routing is null)
            {
                _modelCatalogDiagnostic = "stale-catalog fallback: installed model catalog/routing metadata is unavailable; use explicit host static-tier/default ownership, never invented model availability or pricing.";
                return null;
            }
            try
            {
                _modelCatalog = SquadModelCatalog.Parse(catalog, routing);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or OverflowException)
            {
                _modelCatalogDiagnostic = "stale-catalog fallback: " + ex.Message;
            }
            return _modelCatalog;
        }
    }

    private async ValueTask<SquadAvailableModel[]> AvailableModelsAsync(CancellationToken token)
    {
        if (_options.AvailableModelsAsync is null)
        {
            return [];
        }
        var models = await _options.AvailableModelsAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Host model availability returned null.");
        if (models.Count > 128 || models.Any(model => model is null || model.ChatClient is null ||
                !SquadModelCatalog.IsModelId(model.ModelId) ||
                model.CapabilityClass is not (null or "fast-lightweight" or "balanced" or "code-specialized" or "frontier-reasoning") ||
                model.ContextWindowTokens is <= 0 || model.LongContextWindowTokens is <= 0 ||
                model.ContextWindowTokens is { } standard && model.LongContextWindowTokens is { } extended && extended < standard) ||
            models.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Count() != models.Count)
        {
            throw new InvalidDataException("Host model availability must contain at most 128 unique safe IDs, actual clients and valid declared limits.");
        }
        return models.ToArray();
    }

    private string? CatalogDiagnostic(SquadModelCatalog? catalog)
    {
        if (catalog is null)
        {
            return _modelCatalogDiagnostic;
        }
        var today = DateOnly.FromDateTime(_options.TimeProvider.GetUtcNow().UtcDateTime);
        return catalog.IsCurrent(today) ? null :
            "stale-catalog fallback: Retrieved is over 90 days old or in the future; ranking/suggestions use host-owned static tiers, not stale fit/prices.";
    }

    private static string CellStatus(string? id, SquadRosterEntry role, SquadModelCatalog? catalog,
        IReadOnlyList<SquadAvailableModel> available)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "empty";
        }
        if (!SquadModelCatalog.IsModelId(id))
        {
            return "refused: metacharacter or invalid ID";
        }
        var offered = available.SingleOrDefault(model => model.ModelId == id);
        if (offered is null)
        {
            return "refused: not available on this host";
        }
        var capability = catalog?.Capabilities.GetValueOrDefault(id) ?? offered.CapabilityClass;
        if (capability is not null && !SquadModelCatalog.Admits(role.ModelTier, capability))
        {
            return "refused: below the " + role.ModelTier + " floor";
        }
        return catalog?.Capabilities.ContainsKey(id) == true ? "valid" : "valid: unevaluated";
    }

    private async Task<SquadModelRoutingChoices> ModelChoicesAsync(SquadModelRoutingConfiguration policy,
        Dictionary<string, string>? previous, CancellationToken token)
    {
        ValidateModelRouting(policy);
        var available = await AvailableModelsAsync(token).ConfigureAwait(false);
        var catalog = ModelCatalog();
        var diagnostic = CatalogDiagnostic(catalog);
        var choices = new List<SquadRoleModelChoice>();
        foreach (var role in Roster.Members.Where(role => role.Role != "scribe"))
        {
            var assignment = catalog?.AssignmentClass(role.Role, "ordinary") ?? "implementation";
            var ranked = diagnostic is null ? catalog!.Rank(available.Select(model => model.ModelId),
                assignment, role.ModelTier) : [];
            var offered = diagnostic is null ? catalog!.Rank(available.Select(model => model.ModelId),
                assignment, role.ModelTier, manual: true) : [];
            string? suggestion = ranked.Count > 0 ? ranked[0].Id : null;
            if (previous?.TryGetValue(role.Role, out var prior) == true &&
                CellStatus(prior, role, catalog, available).StartsWith("valid", StringComparison.Ordinal))
            {
                suggestion = prior;
            }
            if (suggestion is null && _options.ModelSelector?.Invoke(role, Catalog.ResolveAgent(role))?.ModelId is { } fallback &&
                catalog?.Models.Any(model => model.Id == fallback) == true &&
                CellStatus(fallback, role, catalog, available).StartsWith("valid", StringComparison.Ordinal))
            {
                suggestion = fallback;
            }
            var candidates = offered.Select(model => new SquadModelCandidate(model.Id, model.DisplayName,
                model.Fit[assignment], model.BlendedUsdPerMillion)).ToList();
            candidates.AddRange(available.Where(model => catalog?.Capabilities.ContainsKey(model.ModelId) != true &&
                    CellStatus(model.ModelId, role, catalog, available).StartsWith("valid", StringComparison.Ordinal))
                .Select(model => new SquadModelCandidate(model.ModelId, model.ModelId, null, null, Unevaluated: true)));
            if (suggestion is not null)
            {
                var index = candidates.FindIndex(candidate => candidate.ModelId == suggestion);
                if (index >= 0)
                {
                    var suggested = candidates[index];
                    candidates.RemoveAt(index);
                    candidates.Insert(0, suggested);
                }
                else
                {
                    var declared = catalog?.Models.SingleOrDefault(model => model.Id == suggestion);
                    candidates.Insert(0, new(suggestion, declared?.DisplayName ?? suggestion,
                        diagnostic is null ? declared?.Fit[assignment] : null, null, declared is null));
                }
            }
            policy.Models.TryGetValue(role.Role, out var cell);
            choices.Add(new(role.Role, assignment, role.ModelTier, suggestion, candidates.Take(8).ToArray(),
                CellStatus(cell, role, catalog, available), SquadModelCatalog.IsModelId(cell) ? cell : null));
        }
        return new(policy.Mode, choices.AsReadOnly(), available.Select(model => model.ModelId).ToArray(),
            diagnostic is null ? [] : [diagnostic]);
    }

    private async Task<SquadModelRoutingConfiguration> ResolvePolicyCellsAsync(
        SquadModelRoutingConfiguration proposed, RuntimeState state, CancellationToken token)
    {
        if (proposed.Models.Keys.Any(role => role == "scribe" || Roster.Resolve(role) is null))
        {
            throw new InvalidDataException("Model picks must name actual model-dispatched roster roles; the native Scribe has no model.");
        }
        if (proposed.Mode == "off")
        {
            return new();
        }
        var choices = await ModelChoicesAsync(proposed, state.PreviousManualModels, token).ConfigureAwait(false);
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices.Roles)
        {
            if (proposed.Mode == "ranked")
            {
                var catalog = ModelCatalog();
                var available = choices.AvailableModels;
                var ranked = choices.Diagnostics.Count == 0 ? catalog!.Rank(available, choice.AssignmentClass, choice.Floor) : [];
                if (ranked.Count > 0)
                {
                    models.Add(choice.Role, ranked[0].Id);
                }
            }
            else if (choice.CellStatus.StartsWith("valid", StringComparison.Ordinal))
            {
                models.Add(choice.Role, proposed.Models[choice.Role]);
            }
        }
        return new() { Mode = proposed.Mode, Models = models };
    }

    private sealed partial class RunContext
    {
        private readonly Dictionary<string, string> _advertisedCapabilities = new(StringComparer.Ordinal);

        private async Task PrepareModelRoutingAsync()
        {
            var desired = runtime._options.ModelRouting ?? state.ModelRouting;
            if (desired is null)
            {
                return;
            }
            ValidateModelRouting(desired);
            if (desired.Mode == "manual" && state.ModelRouting?.Mode == "manual" && !resuming)
            {
                var merged = CopyModelRouting(state.ModelRouting);
                foreach (var pair in desired.Models)
                {
                    merged.Models[pair.Key] = pair.Value;
                }
                desired = merged;
            }
            if (resuming && checkpoint.ModelRouting is { } bound &&
                (state.ModelRouting is null || !SamePolicy(bound, state.ModelRouting) ||
                 runtime._options.ModelRouting is { } configured &&
                 checkpoint.ModelRoutingInputSha256 != ModelPolicyHash(configured)))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "The suspended run's exact model policy changed; old gates cannot authorize it.");
            }
            if (checkpoint.ModelRouting is null)
            {
                checkpoint.ModelRoutingInputSha256 = ModelPolicyHash(runtime._options.ModelRouting ?? desired);
                var resolved = await runtime.ResolvePolicyCellsAsync(CopyModelRouting(desired), state, token).ConfigureAwait(false);
                if (runtime._options.ModelRouting is not null && state.ModelRouting is not null &&
                    !SamePolicy(state.ModelRouting, SafeModelRoutingCopy(desired)))
                {
                    await ApproveAsync(new(SquadApprovalKind.ModelRouting,
                        "Confirm the exact persisted model policy before this run dispatches. Effects still have separate gates.",
                        ModelRouting: CopyModelRouting(resolved))).ConfigureAwait(false);
                }
                if (desired.Mode == "off" && state.ModelRouting?.Mode is null or "off")
                {
                    checkpoint.ModelRouting = CopyModelRouting(desired);
                    runtime._scribe.Save(state);
                    runtime._options.ModelRouting = null;
                    return;
                }
                foreach (var (role, id) in desired.Models)
                {
                    if (desired.Mode == "manual" && !resolved.Models.ContainsKey(role))
                    {
                        ModelDiagnostic("Model cell for " + role + " refused: unavailable, invalid or below floor.");
                    }
                }
                if (resolved.Mode == "manual" && state.ModelRouting is { } current && SamePolicy(current, resolved))
                {
                    checkpoint.ModelRouting = CopyModelRouting(current);
                    runtime._scribe.Save(state);
                }
                else
                {
                    runtime._scribe.SetModelRouting(state, runtime.Roster, runtime.Catalog.RoutingRules, resolved,
                        "Prepared model routing for the current host and confirmed roster.");
                }
            }
            if (state.ModelRouting?.Mode == "manual")
            {
                var choices = await runtime.ModelChoicesAsync(state.ModelRouting, state.PreviousManualModels, token).ConfigureAwait(false);
                foreach (var diagnostic in choices.Diagnostics)
                {
                    ModelDiagnostic(diagnostic);
                }
                var needed = choices.Roles.Where(role => !role.CellStatus.StartsWith("valid", StringComparison.Ordinal)).ToArray();
                if (needed.Length > 0)
                {
                    if (runtime._options.WatchInvocation is not null)
                    {
                        ModelDiagnostic("Unattended Watch never prompts for a manual model. Invalid or empty cells use only the floor-admitted static-tier/default fallback.");
                    }
                    else
                    {
                        await SelectManualModelsAsync(needed, choices.AvailableModels).ConfigureAwait(false);
                    }
                }
            }
            runtime._options.ModelRouting = null;
        }

        private async Task SelectManualModelsAsync(IReadOnlyList<SquadRoleModelChoice> roles, IReadOnlyList<string> available)
        {
            var opening = await AskQuestionAsync(new("model-routing",
                "Choose models for the confirmed roles: " + string.Join("; ", roles.Select(role =>
                    role.Role + " -> " + (role.SuggestedModel ?? "no evaluated suggestion"))) +
                ". Only actual host-available IDs are usable; native Scribe work has no model.",
                ["Accept suggested models (Recommended)", "Customize by assignment class", "Customize individual roles"]),
                reuseConfirmed: true).ConfigureAwait(false);
            if (opening is "1" or "Accept suggested models (Recommended)")
            {
                foreach (var role in roles)
                {
                    if (role.SuggestedModel is null)
                    {
                        await PickManualModelAsync([role], available).ConfigureAwait(false);
                    }
                    else
                    {
                        SaveManualPick([role.Role], role.SuggestedModel);
                    }
                }
            }
            else if (opening is "2" or "Customize by assignment class")
            {
                foreach (var group in roles.GroupBy(role => role.AssignmentClass))
                {
                    await PickManualModelAsync(group.ToArray(), available).ConfigureAwait(false);
                }
            }
            else if (opening is "3" or "Customize individual roles")
            {
                foreach (var role in roles)
                {
                    await PickManualModelAsync([role], available).ConfigureAwait(false);
                }
            }
            else
            {
                throw _guard.Stop(SquadRunStatus.InputRequired, "Choose one of the three model-selection approaches; no model picks were invented.");
            }

            while (true)
            {
                var exception = await AskQuestionAsync(new("model-routing",
                    "Choose one role for a manual model exception, or keep the confirmed picks.",
                    ["Keep confirmed picks", .. runtime.Roster.Members.Where(role => role.Role != "scribe").Select(role => role.Role)]),
                    reuseConfirmed: false).ConfigureAwait(false);
                if (exception is "1" or "Keep confirmed picks")
                {
                    return;
                }
                var choices = await runtime.ModelChoicesAsync(state.ModelRouting!, state.PreviousManualModels, token).ConfigureAwait(false);
                var selected = choices.Roles.SingleOrDefault(role => role.Role.Equals(exception, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "A model exception must name one confirmed model-dispatched role.");
                }
                await PickManualModelAsync([selected], choices.AvailableModels).ConfigureAwait(false);
            }
        }

        private async Task PickManualModelAsync(IReadOnlyList<SquadRoleModelChoice> roles, IReadOnlyList<string> available)
        {
            var catalog = runtime.ModelCatalog();
            var floorRole = roles.OrderByDescending(role => role.Floor switch { "extended" => 3, "default" => 2, "fast" => 1, _ => 0 }).First();
            var admitted = runtime.CatalogDiagnostic(catalog) is null
                ? catalog!.Rank(available, floorRole.AssignmentClass, floorRole.Floor, manual: true)
                    .Select(model => new SquadModelCandidate(model.Id, model.DisplayName, model.Fit[floorRole.AssignmentClass],
                        model.BlendedUsdPerMillion)).ToList()
                : floorRole.Candidates.ToList();
            var suggestionIndex = admitted.FindIndex(candidate => candidate.ModelId == floorRole.SuggestedModel);
            if (suggestionIndex > 0)
            {
                var suggestion = admitted[suggestionIndex];
                admitted.RemoveAt(suggestionIndex);
                admitted.Insert(0, suggestion);
            }
            var currentBindings = await runtime.AvailableModelsAsync(token).ConfigureAwait(false);
            admitted.AddRange(available.Where(id => catalog?.Capabilities.ContainsKey(id) != true &&
                roles.All(role => CellStatus(id, runtime.Roster.Resolve(role.Role)!, catalog, currentBindings)
                    .StartsWith("valid", StringComparison.Ordinal)))
                .Where(id => admitted.All(candidate => candidate.ModelId != id))
                .Select(id => new SquadModelCandidate(id, id, null, null, Unevaluated: true)));
            var menu = admitted.Take(8).ToArray();
            var labels = menu.Select(candidate => candidate.ModelId + " - " + candidate.DisplayName +
                ", fit " + (candidate.Fit?.ToString(CultureInfo.InvariantCulture) ?? "unevaluated") +
                ", $" + (candidate.BlendedUsdPerMillion?.ToString(CultureInfo.InvariantCulture) ?? "unpriced") + "/1M").ToArray();
            var answer = await AskQuestionAsync(new("model-routing:" + string.Join(",", roles.Select(role => role.Role)),
                $"Choose an available model for {string.Join(", ", roles.Select(role => role.Role))}, floor {floorRole.Floor}. " +
                "The menu has at most eight ranked IDs; you may type any exact available ID. Advertised but uncatalogued IDs are unevaluated and unpriced.",
                labels), reuseConfirmed: false).ConfigureAwait(false);
            var labelIndex = Array.IndexOf(labels, answer);
            var id = labelIndex >= 0 ? menu[labelIndex].ModelId : answer;
            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 && index <= menu.Length)
            {
                id = menu[index - 1].ModelId;
            }
            var bindings = await runtime.AvailableModelsAsync(token).ConfigureAwait(false);
            if (checkpoint.ModelAvailability is { } narrowed)
            {
                bindings = bindings.Where(model => narrowed.Contains(model.ModelId, StringComparer.Ordinal)).ToArray();
            }
            if (roles.Any(role => !CellStatus(id, runtime.Roster.Resolve(role.Role)!, catalog, bindings).StartsWith("valid", StringComparison.Ordinal)))
            {
                ModelDiagnostic("Manual model selection refused: invalid, unavailable or below the confirmed floor.");
                throw _guard.Stop(SquadRunStatus.InputRequired, "The manual model was refused; select a current available floor-admitted ID.");
            }
            SaveManualPick(roles.Select(role => role.Role).ToArray(), id);
        }

        private void SaveManualPick(IReadOnlyList<string> roles, string id)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var policy = CopyModelRouting(state.ModelRouting!);
                foreach (var role in roles)
                {
                    policy.Models[role] = id;
                }
                runtime._scribe.SetModelRouting(state, runtime.Roster, runtime.Catalog.RoutingRules, policy,
                    "Confirmed manual model choices for " + string.Join(", ", roles) + ".");
            }
        }

        private async ValueTask<RuntimeModelRoute> ResolveModelAsync(SquadRosterEntry? role, AgentCharter charter,
            string stage, long estimatedInputTokens, IReadOnlyList<string>? narrowed = null)
        {
            var fallback = runtime._options.ModelSelector?.Invoke(role, charter);
            if (role is null || state.ModelRouting?.Mode is null or "off")
            {
                return new(fallback?.ChatClient ?? runtime._client, fallback?.ModelId, fallback?.ModelId,
                    fallback?.ModelId is null ? "unknown" : "host-configured", "implementation", role?.ModelTier,
                    "off: explicit host client/charter ownership; routing parameter omitted unless the legacy host selector supplies one.",
                    false, false);
            }
            var catalog = runtime.ModelCatalog();
            var diagnostic = runtime.CatalogDiagnostic(catalog);
            if (diagnostic is not null)
            {
                ModelDiagnostic(diagnostic);
            }
            var assignment = catalog?.AssignmentClass(role.Role, stage) ?? "implementation";
            var available = await runtime.AvailableModelsAsync(token).ConfigureAwait(false);
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                foreach (var model in available.Where(model => model.CapabilityClass is not null))
                {
                    _advertisedCapabilities[model.ModelId] = model.CapabilityClass!;
                }
            }
            var limited = narrowed ?? checkpoint.ModelAvailability;
            if (limited is not null)
            {
                available = available.Where(model => limited.Contains(model.ModelId, StringComparer.Ordinal)).ToArray();
            }
            available = available.Where(model => model.ContextWindowTokens is null ||
                estimatedInputTokens <= model.ContextWindowTokens ||
                model.LongContextWindowTokens is { } window && estimatedInputTokens <= window).ToArray();
            var mode = state.ModelRouting.Mode;
            string? id = null;
            string rationale;
            if (mode == "manual")
            {
                id = ReadModelCell(role);
                var status = CellStatus(id, role, catalog, available);
                if (!status.StartsWith("valid", StringComparison.Ordinal))
                {
                    if (_costPreview)
                    {
                        throw _guard.Stop(SquadRunStatus.InputRequired, "Confirm a current floor-admitted manual model before aggregate admission.");
                    }
                    ModelDiagnostic("Manual cell for " + role.Role + " " + status + ".");
                    if (runtime._options.WatchInvocation is not null)
                    {
                        id = null;
                    }
                    else
                    {
                        var choices = await runtime.ModelChoicesAsync(state.ModelRouting, state.PreviousManualModels, token).ConfigureAwait(false);
                        await PickManualModelAsync([choices.Roles.Single(choice => choice.Role == role.Role)],
                            available.Select(model => model.ModelId).ToArray()).ConfigureAwait(false);
                        id = state.ModelRouting.Models[role.Role];
                        if (!CellStatus(id, role, catalog, available).StartsWith("valid", StringComparison.Ordinal))
                        {
                            throw _guard.Stop(SquadRunStatus.InputRequired, "Host availability changed during manual selection; the pick must be confirmed again.");
                        }
                    }
                }
                if (id is null && runtime._options.WatchInvocation is null)
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "A manual model must have an exact available ID.");
                }
                rationale = id is null ? "unattended manual cell refused; static-tier/default fallback" :
                    $"manual; class {assignment}; floor {role.ModelTier}";
                if (id is not null && (!state.ModelRouting.Models.TryGetValue(role.Role, out var stored) || stored != id))
                {
                    if (runtime._options.WatchInvocation is not null)
                    {
                        id = null;
                        rationale = "unattended changed manual cell ignored; static-tier/default fallback";
                    }
                    else
                    {
                        if (_costPreview)
                        {
                            throw _guard.Stop(SquadRunStatus.InputRequired, "A changed manual model cell requires confirmation before aggregate admission.");
                        }
                        var edited = CopyModelRouting(state.ModelRouting);
                        edited.Models[role.Role] = id;
                        await ApproveAsync(new(SquadApprovalKind.ModelRouting,
                            "Confirm this changed manual Model cell before dispatch; repository text is not authorization.",
                            role.Role, ModelRouting: edited)).ConfigureAwait(false);
                        SaveManualPick([role.Role], id);
                    }
                }
            }
            else
            {
                var ranked = diagnostic is null ? catalog!.Rank(available.Select(model => model.ModelId), assignment, role.ModelTier) : [];
                id = ranked.Count > 0 ? ranked[0].Id : null;
                rationale = id is null ? (diagnostic is null ? "floor exhausted" : "stale-catalog fallback") :
                    $"ranked; class {assignment}; rank 1 of {ranked.Count}; fit {ranked[0].Fit[assignment]}; floor {role.ModelTier}";
            }
            if (id is not null)
            {
                var binding = available.Single(model => model.ModelId == id);
                var unevaluated = catalog?.Capabilities.ContainsKey(id) != true;
                if (unevaluated)
                {
                    rationale += "; unevaluated; model catalog supplies no tariff";
                }
                if (!_costPreview && (!state.ModelRouting.Models.TryGetValue(role.Role, out var current) || current != id ||
                    mode == "ranked" && ReadModelCell(role) != id))
                {
                    using (runtime._scribe.SyncRoot.EnterScope())
                    {
                        var updated = CopyModelRouting(state.ModelRouting);
                        updated.Models[role.Role] = id;
                        runtime._scribe.SetModelRouting(state, runtime.Roster, runtime.Catalog.RoutingRules, updated,
                            "Re-ranked the actual " + role.Role + " dispatch for its class, floor and current host availability.");
                    }
                }
                return new(binding.ChatClient, id, id, "host-advertised", assignment, role.ModelTier,
                    rationale, unevaluated, true);
            }
            ModelDiagnostic(rationale + ": " + role.Role + " uses explicit host static-tier/default ownership; no catalog ID or price was invented.");
            if (fallback?.ModelId is { } fallbackId)
            {
                var capability = catalog?.Capabilities.GetValueOrDefault(fallbackId) ?? fallback.CapabilityClass ??
                    available.SingleOrDefault(model => model.ModelId == fallbackId)?.CapabilityClass;
                if (capability is not null && !SquadModelCatalog.Admits(role.ModelTier, capability) ||
                    limited is not null && !limited.Contains(fallbackId, StringComparer.Ordinal) ||
                    runtime._options.AvailableModelsAsync is not null && !available.Any(model => model.ModelId == fallbackId))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "floor exhausted: even the host fallback is below floor or unavailable. Human escalation is required.");
                }
            }
            else if (runtime._options.AvailableModelsAsync is not null && available.Length == 0)
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "floor exhausted: this host advertises no admissible available default/client. Human escalation is required.");
            }
            return new(fallback?.ChatClient ?? runtime._client, fallback?.ModelId, fallback?.ModelId,
                fallback?.ModelId is null ? "unknown" : "host-configured", assignment, role.ModelTier,
                rationale + "; host static-tier/default fallback; floor identity " + (fallback?.ModelId is null ? "unverified" : "host-configured"),
                false, true);
        }

        private async ValueTask<RuntimeModelRoute> RejectModelAsync(SquadRosterEntry? role, AgentCharter charter,
            string stage, long inputTokens, RuntimeModelRoute prior, SquadModelUnavailableException rejection,
            IReadOnlySet<string> attempted, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prior.PolicyApplied || role is null || prior.ParameterModel != rejection.ModelId ||
                rejection.AvailableModels.Any(id => !SquadModelCatalog.IsModelId(id)))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "Provider model rejection was not a valid bound pre-execution routing refusal.");
            }
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                checkpoint.ModelAvailability = rejection.AvailableModels.Where(id => !attempted.Contains(id))
                    .Distinct(StringComparer.Ordinal).ToArray();
                runtime._scribe.Save(state);
            }
            ModelDiagnostic("Host refused the model for " + role.Role + "; current reported availability narrows this run.");
            var replacement = await ResolveModelAsync(role, charter, stage, inputTokens, checkpoint.ModelAvailability).ConfigureAwait(false);
            if (replacement.ParameterModel is null || attempted.Contains(replacement.ParameterModel))
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "floor exhausted after host rejection; no unverified/default retry was executed.");
            }
            return replacement with
            {
                RequestedModel = prior.RequestedModel,
                Rationale = replacement.Rationale + "; host rejected the prior pick; " +
                    (runtime._options.WatchInvocation is not null ? "unattended floor-admitted fallback" :
                        state.ModelRouting?.Mode == "manual" ? "human confirmed replacement" : "re-ranked substitution"),
            };
        }

        private void ValidateObservedModel(RuntimeModelRoute route, string? observed)
        {
            if (!route.PolicyApplied)
            {
                return;
            }
            var rationale = route.IdentityRationale(observed);
            if (rationale.Contains("identity-mismatch:", StringComparison.Ordinal))
            {
                ModelDiagnostic(rationale);
            }
            string? capability;
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                capability = observed is null ? null :
                    runtime.ModelCatalog()?.Capabilities.GetValueOrDefault(observed) ??
                    _advertisedCapabilities.GetValueOrDefault(observed);
            }
            if (capability is not null && !SquadModelCatalog.Admits(route.Floor, capability))
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "identity-mismatch: the concrete observed model is below the confirmed consequence floor; human escalation is required.");
            }
        }

        private void ModelDiagnostic(string diagnostic)
        {
            if (_costPreview)
            {
                return;
            }
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                state.ModelRoutingDiagnostics ??= [];
                if (!state.ModelRoutingDiagnostics.Contains(diagnostic, StringComparer.Ordinal))
                {
                    state.ModelRoutingDiagnostics.Add(diagnostic);
                    runtime._scribe.Decision(state, diagnostic);
                    runtime._scribe.Save(state);
                    if (runtime._options.LoggerFactory is { } factory)
                    {
                        LogModelRoutingWarning(factory.CreateLogger("HveSquad.AgentFramework.Runtime.Models"), null);
                    }
                }

            }
        }

        private string? ReadModelCell(SquadRosterEntry role)
        {
            using (runtime._scribe.SyncRoot.EnterScope())
            {
                var text = runtime._files.Read(runtime._files.SquadRoot + "/team.md");
                var mode = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^Model routing:\s*(ranked|manual)\s*$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                var tables = MarkdownTables.Parse(text).Where(table => table.Heading == "Members" &&
                    table.Headers.Contains("Role", StringComparer.Ordinal) &&
                    table.Headers.Contains("Model", StringComparer.Ordinal) &&
                    table.Headers.Contains("Model Tier", StringComparer.Ordinal)).ToArray();
                if (!mode.Success || mode.Groups[1].Value != state.ModelRouting!.Mode || tables.Length != 1)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "The replace-owned model roster mode/table changed; use confirmed native model-policy updates.");
                }
                var rows = tables[0].Rows.Where(row => row["Role"] == role.Role).ToArray();
                if (rows.Length != 1 || rows[0]["Model Tier"] != (role.ModelTier ?? ""))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, "The confirmed model owner/floor changed; repository text cannot lower a consequence floor.");
                }
                var cell = rows[0]["Model"].Trim('`', ' ');
                return cell is "" or "—" ? null : cell;
            }
        }
    }
}
