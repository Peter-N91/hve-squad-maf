using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HveSquad.AgentFramework.Artifacts;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record FederationMove(string Source, string Destination, string Sha256, long Bytes);
internal sealed record FederationDirectoryMove(string Source, string Destination);
internal sealed record FederationPreparedFile(string Source, string Destination, string Sha256,
    string? BeforeSha256 = null);
internal sealed record NativeRelocation(string OperationId, string SourceRoot, string TargetRoot,
    string OriginalStatePath, string OriginalStateSha256, IReadOnlyList<FederationMove> Files, string? Seal = null);
internal sealed record NativeFederationMember(SquadFederationMember Entry, SquadRosterConfiguration Roster);
internal sealed record NativeFederationOutputOwner(string Member, string Path, string RunId, string DispatchId);

internal sealed class NativeFederation
{
    public required string ProjectBinding { get; init; }
    public string? NamingPolicy { get; init; }
    public long Revision { get; set; }
    public List<NativeFederationMember> Members { get; set; } = [];
    public List<SquadFederationRoute> Routes { get; set; } = [];
    public string RegistrySha256 { get; set; } = "";
    public string RoutingSha256 { get; set; } = "";
    public Dictionary<string, NativeFederationOutputOwner> OutputOwners { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class FederationLifecycle
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
    public required string HostBinding { get; init; }
    public required SquadFederationProposal Proposal { get; init; }
    public required string ProposalSha256 { get; init; }
    public required List<NativeFederationMember> Members { get; init; }
    public required List<FederationMove> Moves { get; init; }
    public List<FederationMove> ReadOnlyFiles { get; init; } = [];
    public required List<FederationDirectoryMove> Directories { get; init; }
    public required Dictionary<string, string> RootFiles { get; init; }
    public string Phase { get; set; } = "preparing";
    public int Attempt { get; set; }
    public List<FederationPreparedFile> Prepared { get; set; } = [];
    public List<string> PreparedDirectories { get; set; } = [];
    public string? PreviousSha256 { get; set; }
    [JsonIgnore]
    public string? SavedSha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WatchBootstrapAuthorization? WatchAuthorization { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WatchExistingMembers { get; set; }
}

internal sealed record FederationLifecycleEnvelope(FederationLifecycle Lifecycle, string Sha256);
internal sealed record FederationPending(string Id, string Fingerprint, string ProposalSha256);
internal sealed record FederationTree(IReadOnlyList<string> Directories, IReadOnlyList<(string Path, string Sha256, long Bytes)> Files);

internal static class FederationPersistence
{
    internal const int MaximumFiles = 4096;
    internal const long MaximumBytes = 512L * 1024 * 1024;
    internal const string PendingPath = ProjectFiles.StateRoot + "/federation.pending.json";
    internal const string MigrationRoot = ProjectFiles.StateRoot + "/migrations/federation";

    internal static string MemberRoot(string name) => ProjectFiles.StateRoot + "/members/" + name;
    internal static string JournalPath(string id) => MigrationRoot + "/" + id + "/transaction.json";

    internal static string ProjectBinding(string project)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        return HumanActions.Hash(new
        {
            Project = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root,
            SquadRoot = ProjectFiles.StateRoot,
        });
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            System.Text.RegularExpressions.Regex.IsMatch(name, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException("A sub-squad needs a portable lower-kebab-case name of at most 64 characters.", nameof(name));
        }
    }

    internal static FederationTree Enumerate(ProjectFiles files, string root,
        Func<string, bool>? exclude = null)
    {
        var directories = new List<string>();
        var entries = new List<(string Path, string Sha256, long Bytes)>();
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            var resolved = files.Resolve(directory);
            if (!Directory.Exists(resolved))
            {
                throw new DirectoryNotFoundException("A confirmed federation source directory is missing: " + directory);
            }
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(resolved).Order(StringComparer.Ordinal))
            {
                ProjectFiles.RejectLinks(entry);
                var relative = Path.GetRelativePath(files.ProjectRoot, entry).Replace('\\', '/');
                if (exclude?.Invoke(relative) == true)
                {
                    continue;
                }
                if (Directory.Exists(entry))
                {
                    pending.Push(relative);
                }
                else
                {
                    var length = new FileInfo(entry).Length;
                    bytes = checked(bytes + length);
                    if (bytes > MaximumBytes)
                    {
                        throw new InvalidDataException("Federation relocation exceeds the 512 MiB total byte limit.");
                    }
                    entries.Add((relative, files.HashFile(relative), length));
                }
                if (directories.Count + pending.Count + entries.Count > MaximumFiles)
                {
                    throw new InvalidDataException("Federation relocation exceeds the 4096-file/directory limit.");
                }
            }
        }
        if (entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
        {
            throw new InvalidDataException("Case-colliding source files cannot be relocated into a portable federation.");
        }
        return new(directories.Order(StringComparer.Ordinal).ToArray(), entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray());
    }

    internal static string RebaseRoot(string root, string memberRoot) =>
        ProjectFiles.IsWithin(root, ProjectFiles.StateRoot)
            ? memberRoot + root[ProjectFiles.StateRoot.Length..]
            : root.StartsWith(".copilot-tracking/", StringComparison.Ordinal)
                ? memberRoot + "/" + root[".copilot-tracking/".Length..] : root;

    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(HumanActions.Serialize(value))
        ?? throw new InvalidDataException("A federation configuration could not be copied.");

    internal static SquadFederationCost Cost(RuntimeState state)
    {
        var charges = state.ModelCallCharges ?? [];
        var known = charges.Sum(charge => charge.CostUsd ?? 0m);
        var unpriced = state.ModelCalls.Count(call => charges.All(charge => charge.CallId != call.Id)) +
            charges.Count(charge => charge.CostUsd is null);
        var unobserved = state.DispatchRecords.Count(dispatch => state.ModelCalls.All(call => call.DispatchId != dispatch.DispatchId));
        return new(unpriced == 0 && unobserved == 0 ? ConsumptionMath.Round(known) : null,
            ConsumptionMath.Round(known), state.ModelCalls.Count, unpriced, unobserved, 0m);
    }

    internal static void ClaimOutputs(ProjectFiles files, SquadRuntimeOptions options,
        string runId, string dispatchId, IEnumerable<string> paths, string fingerprint)
    {
        var shared = paths.Select(path => Path.GetRelativePath(files.ProjectRoot, files.Resolve(path)).Replace('\\', '/')).Where(path =>
            ProjectFiles.IsWithin(path, "docs") || ProjectFiles.IsWithin(path, "outputs")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!files.IsMemberRoot || shared.Length == 0 ||
            !File.Exists(Path.Combine(files.ProjectRoot, ".copilot-tracking", "squad", "federation.md")))
        {
            return;
        }
        var rootFiles = new ProjectFiles(files.ProjectRoot, options.MaxArtifactCharacters);
        var scribe = new RuntimeScribe(rootFiles, options.LoggerFactory?.CreateLogger("HveSquad.AgentFramework.Runtime.State"), options.TimeProvider);
        using var diskLock = scribe.AcquireLock();
        if (File.Exists(rootFiles.Resolve(PendingPath)))
        {
            throw new RuntimeHistoryIntegrityException("A federation lifecycle is pending; no shared output may be written.");
        }
        var state = scribe.Load(allowFederation: true)
            ?? throw new RuntimeHistoryIntegrityException("The registered federation lost its native state.");
        if (state.Fingerprint != fingerprint)
        {
            throw new RuntimeHistoryIntegrityException("Shared outputs remain bound to the federation's original acquired release.");
        }
        scribe.VerifyFederation(state);
        var name = files.SquadRoot[(ProjectFiles.StateRoot.Length + "/members/".Length)..];
        if (state.Federation!.Members.All(member => member.Entry.Name != name))
        {
            throw new RuntimeHistoryIntegrityException("An unregistered member cannot claim repository-wide outputs.");
        }
        foreach (var path in shared)
        {
            if (state.Federation.OutputOwners.TryGetValue(path, out var owner) && owner.Member != name)
            {
                throw new RuntimeHistoryIntegrityException("The shared output already belongs to another member; no second writer was invoked.");
            }
        }

        foreach (var path in shared.Where(path => !state.Federation.OutputOwners.ContainsKey(path)))
        {
            scribe.RecordFederationOutputOwner(state, new(name, path, runId, dispatchId));
        }
    }

    internal static void ValidateMember(ProjectFiles files, string fingerprint)
    {
        if (!files.IsMemberRoot ||
            !File.Exists(Path.Combine(files.ProjectRoot, ".copilot-tracking", "squad", "federation.md")))
        {
            return;
        }
        var rootFiles = new ProjectFiles(files.ProjectRoot, MaximumFiles * 1024);
        var scribe = new RuntimeScribe(rootFiles);
        if (scribe.ReadFederationPending() is not null)
        {
            throw new InvalidDataException("An incomplete federation lifecycle must be resumed before opening a member.");
        }
        var state = scribe.Load(allowFederation: true) ?? throw new InvalidDataException("The federation native state is missing.");
        scribe.VerifyFederation(state);
        var name = files.SquadRoot[(ProjectFiles.StateRoot.Length + "/members/".Length)..];
        if (state.Fingerprint != fingerprint || state.Federation!.Members.All(member => member.Entry.Name != name))
        {
            throw new InvalidDataException("Only an exact registered release-bound member may use this federation.");
        }
    }
}

internal sealed partial class RuntimeScribe
{
    private static readonly Action<ILogger, string, Exception?> LogFederationPhase = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(6, "FederationLifecyclePhase"),
        "Federation lifecycle reached {Phase}; source removal requires the exact verified prepared image.");

    internal Dictionary<string, string?> ReadRosterRoots(SquadRoster roster)
    {
        var text = files.Read(files.SquadRoot + "/team.md", MaximumStateCharacters);
        var tables = MarkdownTables.Parse(text).Where(table =>
            table.Headers.Contains("Role", StringComparer.OrdinalIgnoreCase) &&
            table.Headers.Contains("Deliverable Root", StringComparer.OrdinalIgnoreCase)).ToArray();
        if (tables.Length != 1 || tables[0].Rows.Count != roster.Members.Count)
        {
            throw new InvalidDataException("The native roster needs one complete Role/Deliverable Root table.");
        }
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tables[0].Rows)
        {
            var role = row["Role"];
            var member = roster.Resolve(role) ?? throw new InvalidDataException("The roster contains an unconfirmed role.");
            var root = row["Deliverable Root"];
            var value = string.IsNullOrWhiteSpace(root) || root is "—" or "-" ? null : root;
            if (role != "scribe")
            {
                if (value is not null)
                {
                    _ = files.ValidateDeliverableRoot(value.Replace("<deck-slug>", "proposed-deck", StringComparison.Ordinal));
                }
                else if (member.DeliverableRoot is not null)
                {
                    throw new InvalidDataException("A producing roster row lost its deliverable root.");
                }
            }
            if (!roots.TryAdd(role, value))
            {
                throw new InvalidDataException("The roster contains duplicate root owners.");
            }
        }
        return roots;
    }

    internal void VerifyPromotion(RuntimeState state)
    {
        if (state.Schema != RuntimeState.SchemaVersion || state.SquadRoot != ProjectFiles.StateRoot ||
            state.Checkpoint is not null || state.ActiveRun is not null || state.PendingHistoryAppend is not null ||
            HasUnresolvedLearning(state) ||
            File.Exists(files.Resolve(files.SquadRoot + "/state.pending.json")))
        {
            throw new InvalidDataException("Promotion requires a quiescent native v2 squad. Resume, stop or reconcile active/uncertain work, or explicitly migrate native v1 first.");
        }
        VerifyIntegrity(state);
        VerifyRecordedHistory(state);
        foreach (var evidence in state.DispatchRecords)
        {
            Verify(evidence, state);
        }
        if (!VerifyRates(state) || files.Read(files.SquadRoot + "/consumption.md", MaximumStateCharacters) != RenderLedger(state))
        {
            throw new InvalidDataException("Reconcile the existing tariff/consumption ledger before proposing promotion.");
        }
    }

    internal void VerifyNativeMember(RuntimeState state)
    {
        if (state.Schema != RuntimeState.SchemaVersion || state.SquadRoot != files.SquadRoot)
        {
            throw new InvalidDataException("The member does not have its exact native root/schema.");
        }
        VerifyIntegrity(state);
        VerifyRecordedHistory(state);
    }

    internal void VerifyFederation(RuntimeState state, bool allowMissingWatchTrees = false)
    {
        var federation = state.Federation;
        if (state.Schema != RuntimeState.FederationSchemaVersion || state.SquadRoot != ProjectFiles.StateRoot ||
            state.Binding != FederationPersistence.ProjectBinding(files.ProjectRoot) ||
            File.Exists(files.Resolve(files.SquadRoot + "/state.pending.json")) ||
            federation is null || federation.ProjectBinding != FederationPersistence.ProjectBinding(files.ProjectRoot) ||
            federation.Members.Count == 0 || federation.Members.Count > 64 ||
            federation.Members.Select(member => member.Entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != federation.Members.Count ||
            federation.RegistrySha256 != files.HashFile(files.SquadRoot + "/federation.md") ||
            federation.RoutingSha256 != files.HashFile(files.SquadRoot + "/meta-routing.md"))
        {
            throw new InvalidDataException("The federation registry, routing or native project binding changed or is incomplete.");
        }
        foreach (var member in federation.Members)
        {
            FederationPersistence.ValidateName(member.Entry.Name);
            if (member.Entry.Kind != "in-repo" || member.Entry.Location != "members/" + member.Entry.Name + "/" ||
                !(allowMissingWatchTrees && member.Entry.Owner == "watch-mode" &&
                    !Directory.Exists(files.Resolve(FederationPersistence.MemberRoot(member.Entry.Name)))) &&
                    (!File.Exists(files.Resolve(FederationPersistence.MemberRoot(member.Entry.Name) + "/team.md")) ||
                        !File.Exists(files.Resolve(FederationPersistence.MemberRoot(member.Entry.Name) + "/state.json"))))
            {
                throw new InvalidDataException("A registered member lacks its exact in-repository native tree.");
            }
        }
        var registry = MarkdownTables.Parse(files.Read(files.SquadRoot + "/federation.md", MaximumStateCharacters))
            .SingleOrDefault(table => table.Headers.SequenceEqual(
                ["Sub-squad", "Profile", "Kind", "Location", "Owner", "Description"], StringComparer.OrdinalIgnoreCase));
        var routes = MarkdownTables.Parse(files.Read(files.SquadRoot + "/meta-routing.md", MaximumStateCharacters))
            .SingleOrDefault(table => table.Headers.SequenceEqual(
                ["Pattern / Domain", "Sub-squad", "Parallel-Eligible"], StringComparer.OrdinalIgnoreCase));
        if (registry is null || routes is null || registry.Rows.Count != federation.Members.Count ||
            routes.Rows.Count != federation.Routes.Count ||
            registry.Rows.Any(row => !federation.Members.Any(member =>
                row["Sub-squad"] == member.Entry.Name && row["Profile"] == member.Entry.Profile &&
                row["Kind"] == member.Entry.Kind && row["Location"] == member.Entry.Location &&
                row["Owner"] == (member.Entry.Owner ?? "") && row["Description"] == member.Entry.Description)) ||
            routes.Rows.Any(row => !federation.Routes.Any(route =>
                row["Pattern / Domain"] == route.Pattern && row["Sub-squad"] == route.Member &&
                row["Parallel-Eligible"] == (route.ParallelEligible ? "yes" : "no"))) ||
            federation.Routes.Any(route => federation.Members.All(member => member.Entry.Name != route.Member)))
        {
            throw new InvalidDataException("The federation documents do not match their confirmed native rows.");
        }
        VerifyIntegrity(state);
        VerifyRecordedHistory(state);
        VerifyFederationTerminalOutcomes(state);
        var decisions = files.Read(files.SquadRoot + "/decisions.md", MaximumStateCharacters);
        if (state.NativeOperations.Count(operation => operation.Operation.StartsWith("ClaimFederationOutput:", StringComparison.Ordinal)) !=
            federation.OutputOwners.Count)
        {
            throw new RuntimeHistoryIntegrityException("A shared output ownership receipt was removed from its current index.");
        }
        foreach (var (path, owner) in federation.OutputOwners)
        {
            if (path != owner.Path || federation.Members.All(member => member.Entry.Name != owner.Member) ||
                state.NativeOperations.Count(operation => operation.Operation == "ClaimFederationOutput:" + HumanActions.Hash(owner)) != 1 ||
                !decisions.Contains(HumanActions.Serialize(owner), StringComparison.Ordinal))
            {
                throw new RuntimeHistoryIntegrityException("A shared output ownership index has no exact native receipt.");
            }
        }
    }

    internal void RecordFederationOutputOwner(RuntimeState state, NativeFederationOutputOwner owner)
    {
        var federation = state.Federation ?? throw new InvalidDataException("No federation owns the output.");
        federation.OutputOwners.Add(owner.Path, owner);
        federation.Revision++;
        Decision(state, "Confirmed repository output ownership:\n" + HumanActions.Serialize(owner));
        RecordNativeOperation(state, "ClaimFederationOutput:" + HumanActions.Hash(owner), owner.RunId, owner.DispatchId);
        Save(state);
    }

    internal void SeedFederation(RuntimeState state, FederationLifecycle lifecycle,
        IReadOnlyList<NativeFederationOutputOwner> inheritedOutputs)
    {
        if (state.Federation is null)
        {
            throw new InvalidDataException("The confirmed federation metadata is missing.");
        }
        files.Write(files.SquadRoot + "/federation.md",
            "---\ndescription: Confirmed native in-repository federation\n---\n# Federation\n\n## Sub-Squads\n\n" +
            "| Sub-squad | Profile | Kind | Location | Owner | Description |\n| --- | --- | --- | --- | --- | --- |\n" +
            string.Join("\n", state.Federation.Members.Select(member => RegistryRow(member.Entry))) + "\n",
            maximumCharacters: MaximumStateCharacters);
        files.Write(files.SquadRoot + "/meta-routing.md",
            "---\ndescription: Confirmed native sub-squad routes\n---\n# Meta-routing\n\n" +
            "| Pattern / Domain | Sub-squad | Parallel-Eligible |\n| --- | --- | --- |\n" +
            string.Join("\n", state.Federation.Routes.Select(RouteRow)) + "\n", maximumCharacters: MaximumStateCharacters);
        files.Write(files.SquadRoot + "/decisions.md", "# Native federation decisions\n\n");
        CaptureBaseline(state, files.SquadRoot + "/decisions.md");
        files.Write(files.SquadRoot + "/consumption-rates.md", RenderRates(state.Consumption));
        RecordFederationLifecycle(state, lifecycle, inheritedOutputs);
    }

    internal void ExpandFederation(RuntimeState state, FederationLifecycle lifecycle)
    {
        var federation = state.Federation ?? throw new InvalidDataException("Expansion requires native federation state.");
        federation.Members.AddRange(lifecycle.Members);
        var routes = lifecycle.Proposal.Members.SelectMany(member => member.Patterns.Select(pattern =>
            new SquadFederationRoute(pattern, member.Name, member.ParallelEligible))).ToArray();
        federation.Routes.AddRange(routes);
        AppendTableRows(files.SquadRoot + "/federation.md", "Sub-squad", lifecycle.Members.Select(member => RegistryRow(member.Entry)));
        AppendTableRows(files.SquadRoot + "/meta-routing.md", "Pattern / Domain", routes.Select(RouteRow));
        RecordFederationLifecycle(state, lifecycle, []);
    }

    private void RecordFederationLifecycle(RuntimeState state, FederationLifecycle lifecycle,
        IReadOnlyList<NativeFederationOutputOwner> inheritedOutputs)
    {
        var federation = state.Federation!;
        federation.Revision++;
        federation.RegistrySha256 = files.HashFile(files.SquadRoot + "/federation.md");
        federation.RoutingSha256 = files.HashFile(files.SquadRoot + "/meta-routing.md");
        if (state.FederationRun is { PlanConfirmed: false } unconfirmed)
        {
            unconfirmed.RegistrySha256 = federation.RegistrySha256;
            unconfirmed.RoutingSha256 = federation.RoutingSha256;
            unconfirmed.Plan = null;
            unconfirmed.Steps.Clear();
        }
        Decision(state, "Confirmed federation " + lifecycle.Proposal.Operation + ":\n" +
            HumanActions.Serialize(new { lifecycle.Id, lifecycle.Proposal, Moves = lifecycle.Moves }));
        foreach (var member in lifecycle.Members)
        {
            var path = files.SquadRoot + "/history/" + member.Entry.Name + ".md";
            if (File.Exists(files.Resolve(path)))
            {
                throw new InvalidDataException("A federation history destination already exists.");
            }
            Append(state, path, "# Sub-squad " + member.Entry.Name + "\n\nConfirmed lifecycle " + lifecycle.Id + ".\n");
        }
        RecordNativeOperation(state, "Federation" + lifecycle.Proposal.Operation, lifecycle.Id, null,
            watchRecord: lifecycle.WatchAuthorization is null ? null : JsonSerializer.SerializeToElement(lifecycle.WatchAuthorization));
        foreach (var owner in inheritedOutputs)
        {
            RecordFederationOutputOwner(state, owner);
        }
        Save(state);
    }

    private static string RegistryRow(SquadFederationMember member) =>
        $"| {member.Name} | {member.Profile} | {member.Kind} | {member.Location} | {member.Owner ?? ""} | {member.Description} |";

    private static string RouteRow(SquadFederationRoute route) =>
        $"| {route.Pattern} | {route.Member} | {(route.ParallelEligible ? "yes" : "no")} |";

    private void AppendTableRows(string path, string firstHeader, IEnumerable<string> rows)
    {
        var text = files.Read(path, MaximumStateCharacters);
        var lines = text.Split('\n');
        var header = Array.FindIndex(lines, line => line.TrimStart().StartsWith("| " + firstHeader + " |", StringComparison.Ordinal));
        if (header < 0)
        {
            throw new InvalidDataException("The confirmed federation table is missing.");
        }
        var end = header + 2;
        while (end < lines.Length && lines[end].TrimStart().StartsWith('|'))
        {
            end++;
        }
        var inserted = string.Join("\n", lines.Take(end)) + "\n" + string.Join("\n", rows) + "\n" +
            string.Join("\n", lines.Skip(end));
        files.Write(path, inserted, maximumCharacters: MaximumStateCharacters);
    }

    internal string RebaseRosterText(string text, SquadRoster roster, string targetRoot)
    {
        _ = ReadRosterRoots(roster);
        var lines = text.Split('\n');
        var column = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var cells = lines[index].Split('|');
            if (column < 0)
            {
                column = Array.FindIndex(cells, cell => cell.Trim().Equals("Deliverable Root", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (cells.Length <= column || roster.Members.All(member => member.Role != cells[1].Trim()))
            {
                continue;
            }
            var cell = cells[column];
            var offset = cell.IndexOf(".copilot-tracking/", StringComparison.Ordinal);
            if (offset >= 0 && cell[..offset].All(char.IsWhiteSpace))
            {
                var suffix = cell[offset..];
                cells[column] = cell[..offset] + FederationPersistence.RebaseRoot(suffix.TrimEnd(), targetRoot) +
                    suffix[suffix.TrimEnd().Length..];
                lines[index] = string.Join('|', cells);
            }
        }
        return string.Join('\n', lines);
    }

    internal void SaveRebasedState(RuntimeState state, SquadRuntimeOptions options, SquadRoster roster,
        NativeRelocation relocation)
    {
        state.SquadRoot = files.SquadRoot;
        state.Binding = SquadRuntime.RosterBinding(options, roster);
        state.Relocation = relocation;
        state.Initialization = state.Initialization! with
        {
            Scope = "member",
            DeliverableRoots = roster.Members.Where(member => member.DeliverableRoot is not null)
                .ToDictionary(member => member.Role, member => member.DeliverableRoot!),
            Roster = roster.Members,
            NotificationQuestion = null,
        };
        state.AppendOnlyFiles = state.AppendOnlyFiles.ToDictionary(
            entry => relocation.Files.Single(file => file.Source == entry.Key).Destination,
            entry => entry.Value, StringComparer.Ordinal);
        files.BindHistoricalPaths(relocation);
        Save(state);
        VerifyIntegrity(state);
        VerifyRecordedHistory(state);
        foreach (var evidence in state.DispatchRecords)
        {
            Verify(evidence, state);
        }
    }

    internal FederationPending? ReadFederationPending()
    {
        if (!File.Exists(files.Resolve(FederationPersistence.PendingPath)))
        {
            return null;
        }
        var pending = JsonSerializer.Deserialize<FederationPending>(
            files.Read(FederationPersistence.PendingPath, MaximumStateCharacters), JsonOptions)
            ?? throw new InvalidDataException("The federation pending operation is empty.");
        if (!Guid.TryParseExact(pending.Id, "N", out _) || !HumanActions.IsHash(pending.Fingerprint) ||
            !HumanActions.IsHash(pending.ProposalSha256))
        {
            throw new InvalidDataException("The federation pending operation has an invalid exact binding.");
        }
        return pending;
    }

    internal void StartFederationLifecycle(FederationLifecycle lifecycle)
    {
        if (ReadFederationPending() is not null)
        {
            throw new InvalidDataException("A prior confirmed federation operation must be resumed first.");
        }
        SaveFederationLifecycle(lifecycle);
        WriteExclusive(FederationPersistence.PendingPath, Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new FederationPending(lifecycle.Id, lifecycle.Fingerprint, lifecycle.ProposalSha256), JsonOptions)));
    }

    internal FederationLifecycle LoadFederationLifecycle(string id)
    {
        var pending = ReadFederationPending() ?? throw new InvalidDataException("No pending federation lifecycle exists.");
        if (pending.Id != id)
        {
            throw new InvalidDataException("Only the exact pending federation operation may resume.");
        }
        var path = FederationPersistence.JournalPath(id);
        var lifecycle = ReadLifecycle(path);
        var temporary = path + ".pending";
        if (File.Exists(files.Resolve(temporary)))
        {
            var candidate = ReadLifecycle(temporary);
            if (candidate.PreviousSha256 != files.HashFile(path) ||
                candidate.ProposalSha256 != lifecycle.ProposalSha256 || candidate.Id != lifecycle.Id)
            {
                throw new InvalidDataException("A partial federation journal does not match its previous exact snapshot.");
            }
            File.Move(files.Resolve(temporary), files.Resolve(path), overwrite: true);
            lifecycle = candidate;
        }
        if (lifecycle.Id != pending.Id || lifecycle.Fingerprint != pending.Fingerprint ||
            lifecycle.ProposalSha256 != pending.ProposalSha256)
        {
            throw new InvalidDataException("The federation journal lost its exact pending binding.");
        }
        lifecycle.SavedSha256 = files.HashFile(path);
        return lifecycle;
    }

    internal FederationLifecycle ReadCompletedFederationLifecycle(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("A completed native lifecycle needs its exact original identity.");
        }
        var path = FederationPersistence.JournalPath(id);
        if (File.Exists(files.Resolve(path + ".pending")))
        {
            throw new InvalidDataException("A completed lifecycle journal has an unexpected interrupted revision; reconcile it explicitly.");
        }
        var lifecycle = ReadLifecycle(path);
        if (lifecycle.Id != id || lifecycle.Phase != "completed")
        {
            throw new InvalidDataException("Only the exact completed seed image may be observed read-only for Watch recovery.");
        }
        return lifecycle;
    }

    private FederationLifecycle ReadLifecycle(string path)
    {
        var envelope = JsonSerializer.Deserialize<FederationLifecycleEnvelope>(
            files.Read(path, MaximumStateCharacters), JsonOptions)
            ?? throw new InvalidDataException("The protected federation journal is missing.");
        var lifecycle = envelope.Lifecycle;
        if (envelope.Sha256 != HumanActions.Hash(lifecycle) || lifecycle.ProposalSha256 != HumanActions.Hash(lifecycle.Proposal) ||
            lifecycle.Phase is not ("preparing" or "copying" or "removing" or "publishing" or "completed") ||
            lifecycle.Moves.Count + lifecycle.Prepared.Count > FederationPersistence.MaximumFiles * 2 ||
            lifecycle.Directories.Count > FederationPersistence.MaximumFiles ||
            lifecycle.Prepared.Any(file => !ProjectFiles.IsWithin(file.Source, FederationPersistence.MigrationRoot + "/" + lifecycle.Id) ||
                !ProjectFiles.IsWithin(file.Destination, ProjectFiles.StateRoot)))
        {
            throw new InvalidDataException("The protected federation journal is changed or outside its approved scope.");
        }
        return lifecycle;
    }

    internal void SaveFederationLifecycle(FederationLifecycle lifecycle)
    {
        var path = FederationPersistence.JournalPath(lifecycle.Id);
        var exists = File.Exists(files.Resolve(path));
        if (exists != (lifecycle.SavedSha256 is not null) ||
            exists && files.HashFile(path) != lifecycle.SavedSha256)
        {
            throw new InvalidDataException("The federation journal compare-and-swap failed.");
        }
        lifecycle.PreviousSha256 = lifecycle.SavedSha256;
        var envelope = new FederationLifecycleEnvelope(lifecycle, HumanActions.Hash(lifecycle));
        var temporary = path + ".pending";
        WriteExclusive(temporary, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)));
        File.Move(files.Resolve(temporary), files.Resolve(path), overwrite: exists);
        lifecycle.SavedSha256 = files.HashFile(path);
    }

    internal void PrepareFederationImage(FederationLifecycle lifecycle, string imageProject)
    {
        var image = new ProjectFiles(imageProject, MaximumStateCharacters);
        var tree = FederationPersistence.Enumerate(image, ProjectFiles.StateRoot,
            path => path.EndsWith("/native.lock", StringComparison.Ordinal));
        lifecycle.Prepared = tree.Files.Select(file =>
        {
            var source = Path.GetRelativePath(files.ProjectRoot, image.Resolve(file.Path)).Replace('\\', '/');
            return new FederationPreparedFile(source, file.Path, file.Sha256,
                lifecycle.RootFiles.GetValueOrDefault(file.Path));
        }).ToList();
        lifecycle.PreparedDirectories = tree.Directories.ToList();
        lifecycle.Phase = "copying";
        SaveFederationLifecycle(lifecycle);
        LogFederationProgress("copying");
    }

    internal void CommitFederationImage(FederationLifecycle lifecycle, CancellationToken token)
    {
        foreach (var prepared in lifecycle.Prepared)
        {
            if (files.HashFile(prepared.Source) != prepared.Sha256)
            {
                throw new InvalidDataException("The exact prepared federation image changed; no source may be removed.");
            }
        }
        var memberFiles = lifecycle.Prepared.Where(file =>
            ProjectFiles.IsWithin(file.Destination, ProjectFiles.StateRoot + "/members")).ToArray();
        var metaFiles = lifecycle.Prepared.Except(memberFiles).OrderBy(file =>
            file.Destination == ProjectFiles.StateRoot + "/federation.md" ? 2 :
            file.Destination == ProjectFiles.StateRoot + "/state.json" ? 1 : 0).ToArray();
        foreach (var member in lifecycle.Members)
        {
            var root = FederationPersistence.MemberRoot(member.Entry.Name);
            if (!Directory.Exists(files.Resolve(root)))
            {
                continue;
            }
            var existing = FederationPersistence.Enumerate(files, root, path => path == root + "/native.lock");
            if (existing.Directories.Any(directory => !lifecycle.PreparedDirectories.Contains(directory, StringComparer.Ordinal)) ||
                existing.Files.Any(file => !memberFiles.Any(prepared =>
                    file.Path == prepared.Destination && file.Sha256 == prepared.Sha256 ||
                    file.Path == prepared.Destination + ".federation-copy" && file.Sha256 == prepared.Sha256)))
            {
                throw new InvalidDataException("An unrelated partial member tree collided with the confirmed image; nothing may be merged or overwritten.");
            }
        }
        foreach (var directory in lifecycle.PreparedDirectories.Where(directory =>
            ProjectFiles.IsWithin(directory, ProjectFiles.StateRoot + "/members")))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(files.Resolve(directory));
        }
        foreach (var prepared in memberFiles)
        {
            token.ThrowIfCancellationRequested();
            CopyPrepared(prepared, replace: false);
        }
        foreach (var prepared in memberFiles)
        {
            if (files.HashFile(prepared.Destination) != prepared.Sha256)
            {
                throw new InvalidDataException("A destination copy could not be verified; all remaining sources are retained.");
            }
        }
        if (lifecycle.Phase == "copying")
        {
            lifecycle.Phase = "removing";
            SaveFederationLifecycle(lifecycle);
            LogFederationProgress("removing");
        }
        if (lifecycle.Phase == "removing")
        {
            VerifyRemainingFederationSources(lifecycle);
            foreach (var move in lifecycle.Moves)
            {
                token.ThrowIfCancellationRequested();
                if (lifecycle.Prepared.Any(file => file.Destination == move.Source))
                {
                    continue;
                }
                if (File.Exists(files.Resolve(move.Source)))
                {
                    if (files.HashFile(move.Source) != move.Sha256)
                    {
                        throw new InvalidDataException("A source changed after confirmation; it cannot be removed.");
                    }
                    File.Delete(files.Resolve(move.Source));
                }
            }
            foreach (var directory in lifecycle.Directories.Select(move => move.Source).OrderByDescending(path => path.Length))
            {
                var resolved = files.Resolve(directory);
                if (directory != ProjectFiles.StateRoot && Directory.Exists(resolved) &&
                    !Directory.EnumerateFileSystemEntries(resolved).Any())
                {
                    Directory.Delete(resolved, recursive: false);
                }
            }
            lifecycle.Phase = "publishing";
            SaveFederationLifecycle(lifecycle);
            LogFederationProgress("publishing");
        }
        var currentMeta = FederationPersistence.Enumerate(files, ProjectFiles.StateRoot, path =>
            ProjectFiles.IsWithin(path, ProjectFiles.StateRoot + "/members") ||
            ProjectFiles.IsWithin(path, FederationPersistence.MigrationRoot) ||
            path is ProjectFiles.StateRoot + "/native.lock" or ProjectFiles.StateRoot + "/federation.run.lock" or FederationPersistence.PendingPath);
        if (currentMeta.Files.Any(file => !metaFiles.Any(prepared =>
            file.Path == prepared.Destination && (file.Sha256 == prepared.BeforeSha256 || file.Sha256 == prepared.Sha256) ||
            file.Path == prepared.Destination + ".federation-copy" && file.Sha256 == prepared.Sha256)))
        {
            throw new InvalidDataException("The federation meta destination changed or retained unexpected source files; no activation is permitted.");
        }
        foreach (var prepared in metaFiles)
        {
            token.ThrowIfCancellationRequested();
            CopyPrepared(prepared, replace: true);
        }
        lifecycle.Phase = "completed";
        SaveFederationLifecycle(lifecycle);
        var pending = ReadFederationPending();
        if (pending?.Id != lifecycle.Id || pending.ProposalSha256 != lifecycle.ProposalSha256)
        {
            throw new InvalidDataException("The pending federation pointer changed before completion.");
        }
        File.Delete(files.Resolve(FederationPersistence.PendingPath));
        _savedStateSha256 = files.HashFile(StatePath);
    }

    private void CopyPrepared(FederationPreparedFile prepared, bool replace)
    {
        var destination = files.Resolve(prepared.Destination);
        var exists = File.Exists(destination);
        if (exists && files.HashFile(prepared.Destination) == prepared.Sha256)
        {
            return;
        }

        if (exists && (!replace || prepared.BeforeSha256 is null ||
            files.HashFile(prepared.Destination) != prepared.BeforeSha256) ||
            !exists && prepared.BeforeSha256 is not null)
        {
            throw new InvalidDataException("A federation destination collided or changed; nothing is overwritten.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = prepared.Destination + ".federation-copy";
        if (!File.Exists(files.Resolve(temporary)))
        {
            WriteExclusive(temporary, files.ReadBytes(prepared.Source));
        }
        if (files.HashFile(temporary) != prepared.Sha256)
        {
            throw new InvalidDataException("An incomplete destination copy differs from its exact prepared bytes.");
        }
        File.Move(files.Resolve(temporary), destination, overwrite: exists);
        if (files.HashFile(prepared.Destination) != prepared.Sha256)
        {
            throw new InvalidDataException("The copied federation destination did not preserve exact bytes.");
        }
    }

    private void VerifyRemainingFederationSources(FederationLifecycle lifecycle)
    {
        if (lifecycle.Proposal.Operation != "Promotion")
        {
            return;
        }
        var roots = lifecycle.Proposal.Deliverables.Where(candidate => candidate.Selected).Select(candidate => candidate.Path)
            .Prepend(ProjectFiles.StateRoot);
        foreach (var root in roots)
        {
            if (!Directory.Exists(files.Resolve(root)))
            {
                continue;
            }
            var tree = FederationPersistence.Enumerate(files, root, path =>
                ProjectFiles.IsWithin(path, ProjectFiles.StateRoot + "/members") ||
                ProjectFiles.IsWithin(path, FederationPersistence.MigrationRoot) ||
                path is ProjectFiles.StateRoot + "/native.lock" or ProjectFiles.StateRoot + "/federation.run.lock" or FederationPersistence.PendingPath);
            if (tree.Files.Any(file => !lifecycle.Moves.Any(move =>
                move.Source == file.Path && move.Sha256 == file.Sha256)) ||
                tree.Directories.Any(directory => directory != ProjectFiles.StateRoot &&
                    directory != ProjectFiles.StateRoot + "/migrations" &&
                    !lifecycle.Directories.Any(move => move.Source == directory)))
            {
                throw new InvalidDataException("A remaining source tree changed after preparation; no unconfirmed bytes may be removed.");
            }
        }
    }

    internal void WriteExclusive(string path, ReadOnlySpan<byte> bytes)
    {
        var resolved = files.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        using var stream = new FileStream(resolved, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void LogFederationProgress(string phase)
    {
        if (logger is not null)
        {
            LogFederationPhase(logger, phase, null);
        }
    }
}
