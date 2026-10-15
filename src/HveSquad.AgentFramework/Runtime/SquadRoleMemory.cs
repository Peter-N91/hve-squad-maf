using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>Exact consumer-local memory access. It grants neither another case nor learning promotion.</summary>
public sealed record SquadMemoryAccess(string Operation, string SquadRoot, string Role, string? NoteSha256 = null);

/// <summary>Code-only host authorization for the explicit native memory directory under one squad root.</summary>
public sealed class SquadMemoryConfiguration
{
    /// <summary>Stable non-secret case memory policy identity; credentials are never part of this value.</summary>
    public required string Binding { get; set; }
    /// <summary>Required exact case/role read or write authorization, including any model-context disclosure.</summary>
    public Func<SquadMemoryAccess, CancellationToken, ValueTask<bool>>? AuthorizeAsync { get; set; }
    /// <summary>Additional host-known credentials to refuse, never persisted or logged.</summary>
    public IList<string> SecretValues { get; set; } = [];
    /// <summary>Maximum durable notes for each role. Existing notes are never silently evicted.</summary>
    public int MaximumEntriesPerRole { get; set; } = 64;
    /// <summary>Maximum total characters in one note's descriptive fields.</summary>
    public int MaximumNoteCharacters { get; set; } = 16_000;
}

/// <summary>A local convention or correction. These fields are untrusted case data, never tool authority.</summary>
public sealed record SquadMemoryNote(string Role, string Scenario, string Rule,
    string Scope = "This consumer", string SourceContext = "Host-curated local convention");

/// <summary>Original Scribe-owned memory with an immutable identity, source path and policy binding.</summary>
public sealed record SquadRoleMemoryEntry(string Id, string Path, string BindingSha256,
    SquadMemoryNote Note, DateTimeOffset CreatedAt);

/// <summary>
/// Working consumer-local role memory, not global IDE memory. Only the deterministic Scribe writes it.
/// Reading is state-read-only; writing requires a quiescent native case and preserves every prior note.
/// </summary>
public sealed class SquadRoleMemory
{
    private readonly ProjectFiles _files;
    private readonly SquadMemoryConfiguration _configuration;
    private readonly TimeProvider _clock;

    /// <summary>Registers an explicit isolated native memory store without a model or artifact acquisition.</summary>
    /// <param name="projectPath">Existing consumer project.</param>
    /// <param name="configuration">Host case policy, disclosure rights and bounds.</param>
    /// <param name="squadRoot">One contained native root, including an explicitly selected federation member.</param>
    /// <param name="timeProvider">Trusted clock.</param>
    public SquadRoleMemory(string projectPath, SquadMemoryConfiguration configuration,
        string squadRoot = ".copilot-tracking/squad", TimeProvider? timeProvider = null)
    {
        _configuration = MemoryContracts.Copy(configuration);
        _files = new(projectPath, configuration.MaximumNoteCharacters, squadRoot);
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Reads only the authorized role's original notes, with no initialization or state repair.</summary>
    /// <param name="role">An installed role or coordinator.</param>
    /// <param name="cancellationToken">Cancellation, not authority.</param>
    /// <returns>Verified notes in their original append order.</returns>
    public async Task<IReadOnlyList<SquadRoleMemoryEntry>> ReadAsync(string role, CancellationToken cancellationToken = default)
    {
        MemoryContracts.ValidateRole(role);
        await MemoryContracts.AuthorizeAsync(_configuration, new("read", _files.SquadRoot, role), cancellationToken).ConfigureAwait(false);
        MemoryContracts.RejectHub(_files);
        using var snapshot = await ReportSnapshot.CaptureAsync(_files, MemoryContracts.Reader(_files),
            "memory", null, "", cancellationToken).ConfigureAwait(false);
        var item = snapshot.Cases.Single();
        MemoryContracts.CheckRole(item.Roster, role);
        var result = item.Scribe.ReadRoleMemory(item.State, role, _configuration);
        snapshot.AssertUnchanged();
        return result;
    }

    /// <summary>Appends a bounded convention through the existing single-writer Scribe, never a model tool.</summary>
    /// <param name="note">Host-curated local data; credentials are refused.</param>
    /// <param name="cancellationToken">Cancellation before persistence.</param>
    /// <returns>The actually stored original note.</returns>
    public async Task<SquadRoleMemoryEntry> RememberAsync(SquadMemoryNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        MemoryContracts.ValidateNote(note, _configuration);
        await MemoryContracts.AuthorizeAsync(_configuration,
            new("write", _files.SquadRoot, note.Role, HumanActions.Hash(note)), cancellationToken).ConfigureAwait(false);
        MemoryContracts.RejectHub(_files);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_files.Resolve(_files.SquadRoot + "/state.json")))
        {
            throw new InvalidDataException("Initialize this native case before writing role memory.");
        }
        var scribe = new RuntimeScribe(_files, timeProvider: _clock);
        using var diskLock = scribe.AcquireLock();
        var state = scribe.Load() ?? throw new InvalidDataException("Initialize this native case before writing role memory.");
        MemoryContracts.RequireQuiescent(state);
        MemoryContracts.CheckRole(scribe.ReadReportRoster(), note.Role);
        _ = scribe.PrepareEvidence(state);
        _ = scribe.ReadRoleMemory(state, note.Role, _configuration);
        return scribe.RememberRoleMemory(state, note, HumanActions.Hash(_configuration.Binding), _clock.GetUtcNow(),
            _configuration.MaximumEntriesPerRole);
    }
}

internal static class MemoryContracts
{
    internal static SquadMemoryConfiguration Copy(SquadMemoryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!HumanActions.IsReference(configuration.Binding) ||
            configuration.MaximumEntriesPerRole is < 1 or > 1024 ||
            configuration.MaximumNoteCharacters is < 1 or > 200_000 ||
            configuration.SecretValues.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Memory needs a non-secret policy binding, bounded limits and nonempty credential deny values.", nameof(configuration));
        }
        return new()
        {
            Binding = configuration.Binding,
            AuthorizeAsync = configuration.AuthorizeAsync,
            SecretValues = configuration.SecretValues.ToArray(),
            MaximumEntriesPerRole = configuration.MaximumEntriesPerRole,
            MaximumNoteCharacters = configuration.MaximumNoteCharacters,
        };
    }

    internal static async ValueTask AuthorizeAsync(SquadMemoryConfiguration configuration, SquadMemoryAccess access, CancellationToken token)
    {
        if (configuration.AuthorizeAsync is null || !await configuration.AuthorizeAsync(access, token).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Consumer-local memory requires this exact case/role host authorization.");
        }
    }

    internal static void ValidateRole(string role) => FederationPersistence.ValidateName(role);

    internal static void CheckRole(SquadRoster roster, string role)
    {
        ValidateRole(role);
        if (role != "coordinator" && !roster.Members.Any(member => member.Role == role))
        {
            throw new InvalidOperationException("Only an installed role or the coordinator owns memory in this case.");
        }
    }

    internal static void ValidateNote(SquadMemoryNote note, SquadMemoryConfiguration configuration)
    {
        ValidateRole(note.Role);
        var fields = new[] { note.Scenario, note.Rule, note.Scope, note.SourceContext };
        if (fields.Any(string.IsNullOrWhiteSpace) || fields.Sum(field => (long)field.Length) > configuration.MaximumNoteCharacters ||
            fields.Any(field => !ReportSafety.IsSafe(field, configuration.SecretValues)))
        {
            throw new ArgumentException("A memory note needs bounded nonempty descriptive fields without credentials or secret transport values.", nameof(note));
        }
    }

    internal static string PathFor(string root, string role) => root + "/memory/squad-" + role + ".md";

    internal static bool IsMemoryPath(ProjectFiles files, string path)
    {
        var prefix = files.SquadRoot + "/memory/squad-";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.Ordinal))
        {
            return false;
        }
        var role = path[prefix.Length..^3];
        return System.Text.RegularExpressions.Regex.IsMatch(role, "^[a-z0-9]+(?:-[a-z0-9]+)*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    internal static void RejectHub(ProjectFiles files)
    {
        if (files.SquadRoot == ProjectFiles.StateRoot && File.Exists(files.Resolve(files.SquadRoot + "/federation.md")))
        {
            throw new InvalidOperationException("Choose one explicit federation member for memory or learning; hub/sibling discovery is not allowed.");
        }
    }

    internal static void RequireQuiescent(RuntimeState state)
    {
        if (state.Schema != RuntimeState.SchemaVersion || state.Federation is not null ||
            state.Checkpoint is not null || state.ActiveRun is not null)
        {
            throw new InvalidOperationException("Memory or learning mutation requires a quiescent, verified native v2 case.");
        }
    }

    internal static SquadReportConfiguration Reader(ProjectFiles files) => new()
    {
        AuthorizeAsync = (access, _) => ValueTask.FromResult(access.SquadRoot == files.SquadRoot),
        MaximumSourceCharacters = 200_000,
    };
}
