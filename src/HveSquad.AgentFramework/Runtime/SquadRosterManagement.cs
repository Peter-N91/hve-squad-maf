using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private const string NotificationQuestion =
        "Do you want remote notifications, or will you remain in-chat? Choose in-chat, github-issue " +
        "(bidirectional remote approval), or webhook (outbound only), with optional courtesy email. " +
        "Declining keeps in-chat; never supply a secret webhook URL.";
    private static readonly string[] MethodologyRoles = ["researcher", "lead", "developer", "tester"];

    /// <summary>Returns a defensive copy of the current confirmed roster/contact settings for a future host instance.</summary>
    public SquadRosterConfiguration RosterConfiguration
    {
        get
        {
            var configuration = ConfigurationFromOptions(_options);
            if (File.Exists(_files.Resolve(_files.SquadRoot + "/team.md")))
            {
                foreach (var (role, root) in _scribe.ReadRosterRoots(Roster))
                {
                    if (role != "scribe" && root is not null)
                    {
                        configuration.DeliverableRoots[role] = root;
                    }
                }
            }
            return configuration;
        }
    }

    /// <summary>Offers canonical topology/profile/pack/custom/naming/contact choices without creating state.</summary>
    /// <param name="request">The initial request, used as data for proposed domain packs and council extensions.</param>
    /// <returns>Read-only project observations and explicit choices; no proposal is implicitly authorized.</returns>
    public SquadInitializationChoices GetInitializationChoices(string request = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var discovery = DiscoverProject(_options);
        var proposed = ConfigurationFromOptions(_options);
        if (_options.Profile == "auto")
        {
            proposed.Profile = RecommendProfile(Catalog, discovery);
        }
        if (proposed.Packs.Count == 0)
        {
            foreach (var pack in Catalog.Packs.Where(pack => PackMatches(pack.Name, discovery, request)))
            {
                proposed.Packs.Add(pack.Name);
            }
        }
        var menu = Catalog.Roles.Select(role =>
        {
            try
            {
                var entry = Catalog.ResolveRoles([role.Name])[0];
                var charter = Catalog.ResolveAgent(entry);
                return new SquadRoleChoice(role, string.IsNullOrWhiteSpace(charter.Description)
                    ? role.SelectionCue ?? role.Name : charter.Description, true, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                return new SquadRoleChoice(role, role.SelectionCue ?? role.Name, false, ex.Message);
            }
        }).ToArray();
        var selected = Roster.Members.Select(e => e.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = new List<string>();
        var lenses = CouncilLenses(request, null, _options.IncludeRaiInCouncil);
        if (_options.RequireCouncil || lenses.Count(lens => lens.Needed) >= 2 || lenses.Any(lens => lens.Role == "rai" && lens.Needed) ||
            Regex.IsMatch(request, @"(?i)\b(council|cross-check|pre-implementation review)\b",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            foreach (var role in lenses.Where(lens => lens.Needed).Select(lens => lens.Role)
                .Concat(_options.Council?.Roles ?? []).Distinct(StringComparer.Ordinal))
            {
                if (!selected.Contains(role) && menu.Any(choice => choice.Role.Name == role && choice.Selectable))
                {
                    extensions.Add(role);
                }
            }
        }
        return new(proposed, Catalog.Profiles, Catalog.Packs, menu,
            ["single-squad", "federation"], ["provided", "assigned", "mixed", "skip"],
            ["in-chat", "github-issue", "webhook"], discovery, extensions);
    }

    /// <summary>Proposes and confirms a roster refresh without altering existing outputs or historical spend.</summary>
    /// <param name="configuration">Desired full roster configuration; surviving names and root overrides are retained.</param>
    /// <param name="cancellationToken">Cancellation for the root-exclusive operation and human confirmation.</param>
    /// <returns>The actual confirmation outcome and added/removed roles.</returns>
    public async Task<SquadRosterUpdateResult> UpdateRosterAsync(
        SquadRosterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var selected = OptionsFor(configuration);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = _scribe.AcquireLock();
            var state = _scribe.Load() ?? throw new InvalidDataException("Initialize and confirm a native squad before changing its roster.");
            ValidateState(state);
            _ = _scribe.PrepareEvidence(state);
            var desiredRoles = SelectedEntries(Catalog, selected).Select(e => e.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentRoots = _scribe.ReadRosterRoots(Roster);
            foreach (var member in Roster.Members.Where(member => !desiredRoles.Contains(member.Role)))
            {
                selected.MemberNames.Remove(member.Role);
                selected.DeliverableRoots.Remove(member.Role);
            }
            foreach (var member in Roster.Members.Where(member => desiredRoles.Contains(member.Role)))
            {
                if (selected.NamingPolicy != "skip" && member.MemberName is not null && !selected.MemberNames.ContainsKey(member.Role))
                {
                    selected.MemberNames[member.Role] = member.MemberName;
                }
                var root = currentRoots.GetValueOrDefault(member.Role);
                if (member.Role != "scribe" && root is not null &&
                    !selected.DeliverableRoots.ContainsKey(member.Role))
                {
                    selected.DeliverableRoots[member.Role] = root;
                }
            }
            if (selected.MemberNames.Count > 0 && selected.NamingPolicy is null)
            {
                selected.NamingPolicy = _options.NamingPolicy is "assigned" or "mixed" ? "mixed" : "provided";
            }
            if (selected.NamingPolicy == "assigned" && selected.MemberNames.Count > 0)
            {
                selected.NamingPolicy = "mixed";
            }
            var roster = BuildRoster(Catalog, Artifacts, selected);
            var proposal = InitializationProposal(selected, roster);
            var currentRoles = Roster.Members.Select(e => e.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = roster.Members.Where(member => !currentRoles.Contains(member.Role)).Select(member => member.Role).ToArray();
            var removed = Roster.Members.Where(member => !desiredRoles.Contains(member.Role)).Select(member => member.Role).ToArray();
            if (state.Checkpoint is not null || state.ActiveRun is not null)
            {
                return new(SquadRunStatus.Blocked, proposal, added, removed,
                    "An active/suspended run is bound to the current roster. Resume or stop/reconcile it before roster changes.");
            }
            if (selected.InitializationScope == "federation")
            {
                return new(SquadRunStatus.Unsupported, proposal, added, removed,
                    "Roster refresh cannot silently promote a single squad into a federation; use confirmed federation promotion.");
            }
            if (_options.ApproveAsync is null)
            {
                return new(SquadRunStatus.ApprovalRequired, proposal, added, removed, "A RosterUpdate approval is required.");
            }
            var approved = await _options.ApproveAsync(new(SquadApprovalKind.RosterUpdate,
                "Confirm this exact roster refresh, added/removed roles, surviving names, root overrides and contact choices. " +
                "Existing output files and all recorded history/spend remain untouched.", Initialization: proposal),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
            {
                return new(SquadRunStatus.ApprovalDenied, proposal, added, removed, "Roster update declined; state and outputs are unchanged.");
            }
            if (_resources.Fingerprint() != ArtifactFingerprint)
            {
                throw new InvalidDataException("Released artifacts changed while roster confirmation was pending.");
            }
            var removedSnapshot = Roster.Members.Where(member => removed.Contains(member.Role, StringComparer.OrdinalIgnoreCase)).ToArray();
            ApplyRosterOptions(selected, roster);
            state.Initialization = proposal;
            state.Notify = CopyNotifications(selected.Notifications);
            state.Binding = _binding;
            foreach (var member in removedSnapshot)
            {
                if (!state.RemovedMembers.Any(previous => previous.Key == member.Key))
                {
                    state.RemovedMembers.Add(member);
                }
            }
            state.RemovedMembers.RemoveAll(member => desiredRoles.Contains(member.Role));
            _scribe.RefreshRoster(state, roster, added, removed, Catalog.RoutingRules);
            return new(SquadRunStatus.Completed, proposal, added, removed,
                "The confirmed roster was refreshed; prior output bytes and historical spend are retained.");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private SquadRuntimeOptions OptionsFor(SquadRosterConfiguration configuration)
    {
        var options = Snapshot(_options);
        options.Profile = configuration.Profile;
        options.Packs = configuration.Packs.ToArray();
        options.Roles = configuration.Roles.ToArray();
        options.AdditionalRoles = configuration.AdditionalRoles.ToArray();
        options.NamingPolicy = configuration.NamingPolicy;
        options.MemberNames = new Dictionary<string, string>(configuration.MemberNames, StringComparer.OrdinalIgnoreCase);
        options.DeliverableRoots = new Dictionary<string, string>(configuration.DeliverableRoots, StringComparer.OrdinalIgnoreCase);
        options.InitializationScope = configuration.Scope;
        options.Notifications = CopyNotifications(configuration.Notifications ?? _options.Notifications);
        ValidateOptions(options);
        return options;
    }

    internal static SquadRosterConfiguration ConfigurationFromOptions(SquadRuntimeOptions options) => new()
    {
        Profile = options.Profile,
        Packs = options.Packs.ToArray(),
        Roles = options.Roles.ToArray(),
        AdditionalRoles = options.AdditionalRoles.ToArray(),
        NamingPolicy = options.NamingPolicy,
        MemberNames = new Dictionary<string, string>(options.MemberNames, StringComparer.OrdinalIgnoreCase),
        DeliverableRoots = new Dictionary<string, string>(options.DeliverableRoots, StringComparer.OrdinalIgnoreCase),
        Scope = options.InitializationScope,
        Notifications = CopyNotifications(options.Notifications),
    };

    internal static SquadRoster BuildRoster(SquadCatalog catalog, SquadArtifacts artifacts, SquadRuntimeOptions options)
    {
        if (options.Profile == "auto")
        {
            options.Profile = RecommendProfile(catalog, DiscoverProject(options));
        }
        var entries = SelectedEntries(catalog, options);
        catalog.ValidateRoster(entries);
        var names = new Dictionary<string, string>(options.MemberNames, StringComparer.OrdinalIgnoreCase);
        if (names.Keys.Any(role => !entries.Any(entry => entry.Role.Equals(role, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("A supplied member name targets an off-roster role.");
        }
        if (options.DeliverableRoots.Keys.Any(role => !entries.Any(entry => entry.Role.Equals(role, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("A deliverable-root override targets an off-roster role.");
        }
        foreach (var value in names.Values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || c == '|') || value.Length > 100)
            {
                throw new InvalidDataException("Member names must be nonempty, single-line bounded display names.");
            }
        }
        if (options.NamingPolicy is "assigned" or "mixed")
        {
            var source = artifacts.Instructions.Single(i => i.FileName.Equals("squad-roster.instructions.md", StringComparison.OrdinalIgnoreCase)).Body;
            var match = Regex.Match(source, @"(?m)^#### Deterministic Alias Wordlist\s*$[\s\S]*?```text\s*(?<names>[^`]+)```",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success)
            {
                throw new InvalidDataException("Assigned/mixed naming requires the canonical deterministic alias wordlist; no invented aliases are used.");
            }
            var aliases = match.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (aliases.Length == 0 || aliases.Any(alias => !Regex.IsMatch(alias, "^[A-Za-z]+$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) ||
                aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length)
            {
                throw new InvalidDataException("The canonical deterministic alias wordlist is malformed.");
            }
            var used = names.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var entry in entries.Where(entry => !names.ContainsKey(entry.Role)))
            {
                string alias;
                do
                {
                    var cycle = index / aliases.Length + 1;
                    alias = aliases[index++ % aliases.Length] + (cycle == 1 ? "" : "-" + cycle.ToString(CultureInfo.InvariantCulture));
                } while (!used.Add(alias));
                names.Add(entry.Role, alias);
            }
        }
        var files = new ProjectFiles(options.ProjectPath, options.MaxArtifactCharacters, options.SquadRoot);
        var members = entries.Select(entry =>
        {
            var root = ResolveDeliverableRoot(options, entry);
            if (root is not null && entry.Role != "scribe")
            {
                _ = files.ValidateDeliverableRoot(root.Replace("<deck-slug>", "proposed-deck", StringComparison.Ordinal));
            }
            return new SquadRosterEntry
            {
                Role = entry.Role,
                MemberName = names.GetValueOrDefault(entry.Role),
                PrimaryAgent = entry.PrimaryAgent,
                AlternateAgents = entry.AlternateAgents,
                SelectionCue = entry.SelectionCue,
                Invocation = entry.Invocation,
                ModelTier = entry.ModelTier,
                DeliverableRoot = root,
            };
        }).ToArray();
        return new() { Members = members };
    }

    private static IReadOnlyList<SquadRosterEntry> SelectedEntries(SquadCatalog catalog, SquadRuntimeOptions options)
    {
        var baseEntries = catalog.ResolveRoster(options.Profile, options.Packs);
        var roles = options.Roles.Count == 0 ? baseEntries.Select(entry => entry.Role).ToList() : options.Roles.ToList();
        if (options.Roles.Count > 0)
        {
            foreach (var pack in options.Packs)
            {
                roles.AddRange(catalog.Packs.Single(p => p.Name.Equals(pack, StringComparison.OrdinalIgnoreCase)).Roles);
            }
        }
        roles.AddRange(options.AdditionalRoles);
        if (!roles.Contains("scribe", StringComparer.OrdinalIgnoreCase))
        {
            roles.Add("scribe");
        }
        return catalog.ResolveRoles(roles.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private SquadInitializationProposal InitializationProposal(SquadRuntimeOptions options, SquadRoster roster) => new(
        options.Roles.Count > 0 || options.AdditionalRoles.Count > 0 ? "custom:" + options.Profile : options.Profile,
        options.Packs.ToArray(), options.NamingPolicy ?? (options.MemberNames.Count == 0 ? "skip" : "provided"),
        roster.Members.Where(member => member.MemberName is not null).ToDictionary(member => member.Role, member => member.MemberName!),
        options.Notifications.ApprovalChannel, roster.Members.Select(member => member.Role).ToArray(),
        _files.IsMemberRoot ? "member" : options.InitializationScope,
        roster.Members.Where(member => member.DeliverableRoot is not null).ToDictionary(member => member.Role, member => member.DeliverableRoot!),
        CopyNotifications(options.Notifications), NotificationQuestion,
        MethodologyRoles.Where(role => roster.Resolve(role) is null).ToArray(), roster.Members,
        options.ModelRouting is null ? null : SafeModelRoutingCopy(options.ModelRouting));

    private void ApplyRosterOptions(SquadRuntimeOptions selected, SquadRoster roster)
    {
        _options.Profile = selected.Profile;
        _options.Packs = selected.Packs.ToArray();
        _options.Roles = selected.Roles.ToArray();
        _options.AdditionalRoles = selected.AdditionalRoles.ToArray();
        _options.NamingPolicy = selected.NamingPolicy;
        _options.MemberNames = new Dictionary<string, string>(selected.MemberNames, StringComparer.OrdinalIgnoreCase);
        _options.DeliverableRoots = new Dictionary<string, string>(selected.DeliverableRoots, StringComparer.OrdinalIgnoreCase);
        _options.InitializationScope = selected.InitializationScope;
        _options.Notifications = CopyNotifications(selected.Notifications);
        Roster = roster;
        _binding = RosterBinding(_options, roster);
    }

    internal static string RosterBinding(SquadRuntimeOptions options, SquadRoster roster) =>
        ProjectFiles.Hash(JsonSerializer.Serialize(new
        {
            options.Profile,
            options.Packs,
            options.NamingPolicy,
            options.MemberNames,
            options.ApprovalChannel,
            Roster = roster.Members,
        }));

    internal SquadRuntimeOptions ConfigurationSnapshot => Snapshot(_options);
    internal string HostBinding => _hostBinding;
    internal SquadInitializationProposal MemberInitialization => InitializationProposal(_options, Roster) with
    {
        NotificationQuestion = null,
    };

    internal RuntimeState CreateInitialState(SquadInitializationProposal proposal) => new()
    {
        Fingerprint = ArtifactFingerprint,
        Binding = _binding,
        Initialization = proposal,
        ReleaseTag = Provenance.Release?.Tag,
        ReleaseCommitSha = Provenance.Release?.CommitSha,
        ArtifactOrigin = Provenance.Origin,
        SquadRoot = _files.SquadRoot,
        Notify = CopyNotifications(_options.Notifications),
        ModelRouting = _options.ModelRouting is null ? null : SafeModelRoutingCopy(_options.ModelRouting),
        Consumption = _options.Consumption is null ? null : ConsumptionMath.Copy(_options.Consumption),
    };

    internal void InitializeConfirmedMember()
    {
        if (!_files.IsMemberRoot || _scribe.Load() is not null)
        {
            throw new InvalidDataException("A confirmed federation seed requires a new isolated member root.");
        }
        using var diskLock = _scribe.AcquireLock();
        _scribe.Initialize(CreateInitialState(MemberInitialization), Roster, Catalog.RoutingRules);
    }

    private void ReadEffectiveRosterRoots()
    {
        var roots = _scribe.ReadRosterRoots(Roster);
        Roster = new()
        {
            Members = Roster.Members.Select(member => WithRoot(member, roots[member.Role])).ToArray(),
        };
    }

    internal static SquadRosterEntry WithRoot(SquadRosterEntry member, string? root) => new()
    {
        Role = member.Role,
        MemberName = member.MemberName,
        PrimaryAgent = member.PrimaryAgent,
        AlternateAgents = member.AlternateAgents,
        SelectionCue = member.SelectionCue,
        Invocation = member.Invocation,
        ModelTier = member.ModelTier,
        DeliverableRoot = root,
    };

    internal void VerifyAcquiredArtifacts()
    {
        if (_resources.Fingerprint() != ArtifactFingerprint)
        {
            throw new InvalidDataException("The acquired release/dependencies changed; no federation lifecycle may proceed.");
        }
    }

    internal static SquadNotificationConfiguration CopyNotifications(SquadNotificationConfiguration value) => new()
    {
        ApprovalChannel = value.ApprovalChannel,
        Enabled = value.Enabled,
        Email = string.IsNullOrWhiteSpace(value.Email) ? null : value.Email,
        GitHubHandle = string.IsNullOrWhiteSpace(value.GitHubHandle) ? null : value.GitHubHandle,
        GitHubRepository = string.IsNullOrWhiteSpace(value.GitHubRepository) ? null : value.GitHubRepository,
        WebhookBinding = string.IsNullOrWhiteSpace(value.WebhookBinding) ? null : value.WebhookBinding,
    };

    private static void ValidateRosterOptions(SquadRuntimeOptions options)
    {
        if (options.InitializationScope is not ("single-squad" or "federation") ||
            options.Roles.Any(string.IsNullOrWhiteSpace) || options.AdditionalRoles.Any(string.IsNullOrWhiteSpace) ||
            options.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Roles.Count ||
            options.AdditionalRoles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.AdditionalRoles.Count)
        {
            throw new ArgumentException("Topology and custom/additional role identifiers must be explicit and valid.", nameof(options));
        }
        var value = options.Notifications;
        if (value.ApprovalChannel is not ("in-chat" or "github-issue" or "webhook") ||
            value.Enabled != (value.ApprovalChannel != "in-chat") ||
            value.Email is not null && !MailAddress.TryCreate(value.Email, out _) ||
            value.GitHubHandle is not null && !Regex.IsMatch(value.GitHubHandle, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            value.GitHubRepository is not null && !Regex.IsMatch(value.GitHubRepository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            value.ApprovalChannel == "github-issue" && (value.GitHubHandle is null || value.GitHubRepository is null) ||
            value.WebhookBinding is not null && !Regex.IsMatch(value.WebhookBinding, @"^[A-Za-z0-9_-]{1,64}$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            value.ApprovalChannel == "webhook" && value.WebhookBinding is null)
        {
            throw new ArgumentException("Notification choices require an explicit opt-in and valid non-secret contacts/binding; never persist a webhook URL.", nameof(options));
        }
    }

    private static string[] DiscoverProject(SquadRuntimeOptions options)
    {
        var files = new ProjectFiles(options.ProjectPath, options.MaxArtifactCharacters, options.SquadRoot);
        var pending = new Queue<string>();
        pending.Enqueue(".");
        var paths = new List<string>();
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".github", ".agents", ".copilot-tracking", "node_modules", "bin", "obj", "dist", "_site",
        };
        while (pending.TryDequeue(out var directory))
        {
            foreach (var path in files.List(directory).Order(StringComparer.Ordinal))
            {
                if (paths.Count >= 2000)
                {
                    return [.. paths, "Discovery bounded at 2000 entries; unresolved signals require explicit profile selection."];
                }
                if (ignore.Contains(Path.GetFileName(path)))
                {
                    continue;
                }
                if (Directory.Exists(files.Resolve(path)))
                {
                    pending.Enqueue(path);
                }
                else
                {
                    paths.Add(path);
                    var name = Path.GetFileName(path);
                    if (name is "main.tsp" or "template.yaml" or "template.yml" ||
                        Path.GetExtension(path) == ".tf")
                    {
                        var text = files.Read(path);
                        foreach (var signal in new[] { "AWS::Serverless", "AWSTemplateFormatVersion", "hashicorp/aws", "@microsoft/typespec-m365-copilot" })
                        {
                            if (text.Contains(signal, StringComparison.Ordinal))
                            {
                                paths.Add(path + ": " + signal);
                            }
                        }
                    }
                }
            }
        }
        return paths.ToArray();
    }

    private static string RecommendProfile(SquadCatalog catalog, IReadOnlyList<string> discovery)
    {
        var candidates = new List<string>();
        bool Match(string pattern) => discovery.Any(path => Regex.IsMatch(path, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        if (Match(@"(auth|threat|privacy|secret|llm|machine-learning)"))
        {
            candidates.Add("security");
        }
        if (Match(@"(react|vue|svelte|angular|\.css$)"))
        {
            candidates.Add("design");
        }
        if (Match(@"(wcag|aria|vpat|accessibility|section-508)"))
        {
            candidates.Remove("design");
            candidates.Add("accessibility");
        }
        if (Match(@"\.bicep$") && Match(@"(cost|budget|finops|landing-zone)"))
        {
            candidates.Add("azure");
        }
        else if (Match(@"(\.bicep$|\.tf$|architecture|component-diagram)"))
        {
            candidates.Add("architecture");
        }
        if (Match(@"(migration|replatform|\.NET-Framework|legacy)"))
        {
            candidates.Add("modernization");
        }
        if (Match(@"(audit|attestation|regulatory|conformance-questionnaire)"))
        {
            candidates.Add("compliance");
        }
        if (Match(@"(runbook|incident|postmortem|slo|error-budget|otel-collector)"))
        {
            candidates.Add("operations");
        }
        if (Match(@"(requirements|roadmap|prd|brd|design-thinking)") ||
            !Match(@"\.(cs|js|ts|py|go|java|rs|cpp|c|bicep|tf)$"))
        {
            candidates.Add("product");
        }
        var known = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => catalog.Profiles.Any(profile => profile.Name == name)).ToArray();
        return known.Length == 1 ? known[0] : "default";
    }

    private static bool PackMatches(string pack, IReadOnlyList<string> discovery, string request)
    {
        var text = string.Join('\n', discovery) + "\n" + request;
        var pattern = pack switch
        {
            "power-platform" => @"(?i)(power platform|power automate|power apps|dataverse|\.cdsproj|solution\.xml|\.msapp)",
            "m365-copilot" => @"(?i)(m365|microsoft 365|declarativeAgent\.json|m365agents\.yml|teamsapp\.yml|appPackage|typespec-m365-copilot)",
            "aws" => @"(?i)(\bAWS\b|AWS::|hashicorp/aws|cdk\.json|samconfig\.toml|serverless\.yml)",
            _ => null,
        };
        return pattern is not null && Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
