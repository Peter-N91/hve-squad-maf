using System.Globalization;
using System.Text;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>A host-owned case disclosure decision, not permission obtained from document prose.</summary>
public sealed record SquadReportAccess(string Kind, string SquadRoot, string? Squad, string OutputPath,
    bool IncludeCaseContent, string? SourcePath = null);

/// <summary>Explicit local disclosure and rendering capabilities. No model or remote connector is used.</summary>
public sealed class SquadReportConfiguration
{
    /// <summary>Required case/tenant authorization, called before scoped state or document content is read.</summary>
    public Func<SquadReportAccess, CancellationToken, ValueTask<bool>>? AuthorizeAsync { get; set; }
    /// <summary>Opt-in to authorized local source excerpts and case summaries; protected payloads are always excluded.</summary>
    public bool IncludeCaseContent { get; set; }
    /// <summary>Optional explicitly permitted local output directory. The project directory is the default.</summary>
    public string? OutputDirectory { get; set; }
    /// <summary>Explicit trusted local PDF/Word capabilities. An absent capability produces a declared Markdown fallback.</summary>
    public IList<ISquadDocumentRenderer> Renderers { get; set; } = [];
    /// <summary>Additional host-known secret values to withhold. These values are never persisted or logged.</summary>
    public IList<string> SecretValues { get; set; } = [];
    /// <summary>Maximum characters read from any ordinary local document.</summary>
    public int MaximumSourceCharacters { get; set; } = 200_000;
    /// <summary>Maximum candidate text files searched across the authorized cases.</summary>
    public int MaximumSourceFiles { get; set; } = 256;
}

/// <summary>A descriptive search request. It cannot change disclosure, roots, roles, models or permissions.</summary>
public sealed record SquadDocumentRequest(string Request, string Format = "md", string? OutputPath = null, string? Squad = null);

/// <summary>A governance observation window: all, 30d or 7d. The result is always self-contained HTML.</summary>
public sealed record SquadGovernanceRequest(string Period = "all", string? OutputPath = null, string? Squad = null);

/// <summary>One exact local source of a safe projection or explicitly authorized excerpt.</summary>
public sealed record SquadReportGrounding(string? Squad, string Path, string Sha256, string Kind);

/// <summary>Six required header metrics and their honest partial/unknown/calibration annotations.</summary>
public sealed record SquadGovernanceMetrics(int CoordinationTurns, int RoleDispatches, decimal? EstimatedCostUsd,
    decimal KnownCostUsd, long? TotalTokens, long KnownTokens, int HumanGatesFired, int HumanGatesPassed,
    int Escalations, int ProviderAttempts, int UnpricedAttempts, int UnobservedDispatches, int UnknownTokenAttempts,
    int CalibratedAttempts, int UncalibratedAttempts);

/// <summary>Actual local output, actual format, verified grounding and explicit gaps; never a publication receipt.</summary>
public sealed record SquadReportResult(string Path, string Format, string RequestedFormat, bool Fallback,
    string? Diagnostic, IReadOnlyList<SquadReportGrounding> Grounding, IReadOnlyList<string> OpenQuestions,
    SquadGovernanceMetrics? Metrics = null);

/// <summary>Authorized in-memory content only. A renderer receives no state path, credentials or filesystem capability.</summary>
public sealed record SquadDocumentRenderingRequest(string Format, string Title, string Markdown,
    string PlainText, string Html, string InputSha256);

/// <summary>Real bytes bound to the exact input, or an explicit unavailable reason. Exceptions do not become success.</summary>
public sealed record SquadDocumentRendering(byte[]? Content, string InputSha256, string? UnavailableReason = null);

/// <summary>
/// Trusted local host capability. Hosts must isolate their implementation and prohibit network/disclosure effects.
/// Registering it does not expose a model tool or create a role dispatch or charge.
/// </summary>
public interface ISquadDocumentRenderer
{
    /// <summary>Stable non-secret implementation/configuration identity.</summary>
    string Binding { get; }
    /// <summary>Supported real byte formats: pdf and/or docx.</summary>
    IReadOnlyList<string> Formats { get; }
    /// <summary>Produces real local document bytes, or explicitly reports unavailable tooling/content support.</summary>
    ValueTask<SquadDocumentRendering> RenderAsync(SquadDocumentRenderingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// State-read-only native helpers, independent of MAF clients and the producing-role dispatcher.
/// State/history are verified without repair. Every case and optional private source needs host disclosure authority.
/// A selected federation member reads no hub or sibling file; shared project documents require a wider authorized report.
/// </summary>
public sealed class SquadReports
{
    private readonly ProjectFiles _files;
    private readonly SquadReportConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a read-only helper without acquiring releases, initializing state or resolving a model.</summary>
    /// <param name="projectPath">Existing isolated native case/project directory.</param>
    /// <param name="configuration">Host-owned disclosure, limits and optional local renderer registrations.</param>
    /// <param name="squadRoot">Contained native state root; defaults to the single/federation hub root.</param>
    /// <param name="timeProvider">Trusted clock for output dates and period boundaries.</param>
    /// <exception cref="ArgumentException">Configuration or a capability registration is invalid.</exception>
    public SquadReports(string projectPath, SquadReportConfiguration configuration,
        string squadRoot = ".copilot-tracking/squad", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.MaximumSourceCharacters is < 1 or > 16_000_000 ||
            configuration.MaximumSourceFiles is < 1 or > FederationPersistence.MaximumFiles ||
            configuration.SecretValues.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Report source limits and secret deny values must be explicit and bounded.", nameof(configuration));
        }
        _configuration = new()
        {
            AuthorizeAsync = configuration.AuthorizeAsync,
            IncludeCaseContent = configuration.IncludeCaseContent,
            OutputDirectory = configuration.OutputDirectory,
            Renderers = configuration.Renderers.ToArray(),
            SecretValues = configuration.SecretValues.ToArray(),
            MaximumSourceCharacters = configuration.MaximumSourceCharacters,
            MaximumSourceFiles = configuration.MaximumSourceFiles,
        };
        var formats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renderer in _configuration.Renderers)
        {
            if (!HumanActions.IsReference(renderer.Binding) || renderer.Formats.Count == 0 ||
                renderer.Formats.Any(format => format is not ("pdf" or "docx") || !formats.Add(format)))
            {
                throw new ArgumentException("Each local renderer needs a non-secret binding and unique pdf/docx formats.", nameof(configuration));
            }
        }
        _files = new(projectPath, configuration.MaximumSourceCharacters, squadRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ = ReportPaths.OutputRoot(_files, _configuration);
    }

    /// <summary>Searches safe native projections and permitted local text, then writes a grounded focused document.</summary>
    /// <param name="request">Descriptive request, requested format and optional local path/member.</param>
    /// <param name="cancellationToken">Cancellation; never approval.</param>
    /// <returns>The real file, actual format/fallback, exact source hashes and Open Questions.</returns>
    /// <exception cref="UnauthorizedAccessException">The host denied the case or a required native scope.</exception>
    /// <exception cref="InvalidDataException">State, renderer bytes or source integrity cannot be established.</exception>
    public async Task<SquadReportResult> DocumentAsync(SquadDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Request);
        if (request.Request.Length > _configuration.MaximumSourceCharacters ||
            !ReportSafety.IsSafe(request.Request, _configuration.SecretValues))
        {
            throw new ArgumentException("A document request must be bounded descriptive text without credentials or secret transport values.", nameof(request));
        }
        var now = _timeProvider.GetUtcNow();
        var requested = request.Format;
        var format = requested is "md" or "html" or "pdf" or "docx" ? requested : "md";
        var diagnostic = format != requested ? "Unrecognized format; the document was written as Markdown instead." : null;
        var renderer = _configuration.Renderers.SingleOrDefault(item => item.Formats.Contains(format, StringComparer.Ordinal));
        if (format is "pdf" or "docx" && renderer is null)
        {
            diagnostic = format.ToUpperInvariant() + " generation is not available in this environment. The document was written as Markdown instead.";
            format = "md";
        }
        var output = ReportPaths.Output(_files, _configuration, request.OutputPath,
            "squad-document-" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), format);
        using var snapshot = await ReportSnapshot.CaptureAsync(_files, _configuration, "document", request.Squad,
            output, cancellationToken).ConfigureAwait(false);
        var observations = ReportObservation.Read(snapshot.Cases, null, now);
        var sources = ReportContent.Projections(observations, snapshot.Grounding, _configuration);
        var questions = new List<string>(observations.Diagnostics);
        if (_configuration.IncludeCaseContent)
        {
            sources.AddRange(await snapshot.SearchSourcesAsync(_configuration, output, questions, cancellationToken).ConfigureAwait(false));
        }
        var document = ReportContent.Document(request.Request, snapshot.Mode, sources, questions);
        var markdown = document.Markdown();
        var html = document.Html();
        var bytes = Encoding.UTF8.GetBytes(format == "html" ? html : markdown);
        if (format is "pdf" or "docx")
        {
            var inputHash = HumanActions.Hash(new { Format = format, document.Title, Markdown = markdown });
            var rendering = await renderer!.RenderAsync(new(format, document.Title, markdown, document.PlainText(), html, inputHash),
                cancellationToken).ConfigureAwait(false);
            if (rendering.InputSha256 != inputHash)
            {
                throw new InvalidDataException("The renderer did not attest the original authorized document input.");
            }
            if (rendering.Content is null)
            {
                if (string.IsNullOrWhiteSpace(rendering.UnavailableReason))
                {
                    throw new InvalidDataException("An unavailable renderer must return an explicit safe reason.");
                }
                diagnostic = "Registered " + format.ToUpperInvariant() +
                    " renderer reported unavailable content/tooling: " +
                    ReportContent.Safe(rendering.UnavailableReason, _configuration) +
                    " The document was written as Markdown instead.";
                format = "md";
                output = ReportPaths.Output(_files, _configuration, request.OutputPath,
                    "squad-document-" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), format);
                await snapshot.AuthorizeOutputAsync(_configuration, "document", output, cancellationToken).ConfigureAwait(false);
                bytes = Encoding.UTF8.GetBytes(markdown);
            }
            else
            {
                if (rendering.UnavailableReason is not null)
                {
                    throw new InvalidDataException("A renderer cannot claim both real output and unavailable generation.");
                }
                bytes = rendering.Content;
                ReportFormats.Verify(format, bytes);
            }
        }
        await snapshot.PublishAsync(output, bytes, cancellationToken).ConfigureAwait(false);
        return new(output, format, requested, format != requested, diagnostic, document.Grounding.ToArray(),
            document.OpenQuestions.ToArray());
    }

    /// <summary>Writes all seven governance sections and six metrics from actual selected-period native evidence.</summary>
    /// <param name="request">Period all/30d/7d and optional local output/member selector.</param>
    /// <param name="cancellationToken">Cancellation for authorized reading and local output.</param>
    /// <returns>Real self-contained HTML plus mechanically reconciled metrics and grounding.</returns>
    /// <exception cref="ArgumentException">The period or local path is invalid.</exception>
    public async Task<SquadReportResult> GovernanceAsync(SquadGovernanceRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? since = request.Period switch
        {
            "all" => null,
            "30d" => now.AddDays(-30),
            "7d" => now.AddDays(-7),
            _ => throw new ArgumentException("Governance period must be all, 30d or 7d.", nameof(request)),
        };
        var output = ReportPaths.Output(_files, _configuration, request.OutputPath,
            "squad-governance-report-" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "html");
        using var snapshot = await ReportSnapshot.CaptureAsync(_files, _configuration, "governance", request.Squad,
            output, cancellationToken).ConfigureAwait(false);
        var observations = ReportObservation.Read(snapshot.Cases, since, now);
        var metrics = observations.Metrics();
        var document = ReportContent.Governance(observations, metrics, snapshot.Mode, request.Period,
            since, now, snapshot.Grounding, _configuration);
        await snapshot.PublishAsync(output, Encoding.UTF8.GetBytes(document.Html()), cancellationToken).ConfigureAwait(false);
        return new(output, "html", "html", false, null, document.Grounding.ToArray(), document.OpenQuestions.ToArray(), metrics);
    }
}

internal static class ReportPaths
{
    internal static string OutputRoot(ProjectFiles files, SquadReportConfiguration configuration)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration.OutputDirectory ?? files.ProjectRoot));
        EnsureLocal(root);
        ProjectFiles.RejectLinks(root);
        return root;
    }

    internal static string Output(ProjectFiles files, SquadReportConfiguration configuration, string? requested,
        string stem, string format)
    {
        var root = OutputRoot(files, configuration);
        var relative = requested ?? Path.Combine("docs", stem + "." + format);
        EnsureLocal(relative);
        if (relative.Split(['/', '\\']).Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')) ||
            !Path.IsPathFullyQualified(relative) && relative.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Report output cannot use traversal or ambiguous path components.", nameof(requested));
        }
        var output = Path.ChangeExtension(Path.GetFullPath(relative, root), format);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, ProjectFiles.PathComparison))
        {
            throw new ArgumentException("Report output must stay within the host-authorized local output directory.", nameof(requested));
        }
        EnsureLocal(output);
        ProjectFiles.RejectLinks(output);
        foreach (var protectedRoot in new[] { files.SquadRoot, ProjectFiles.StateRoot, ".git", ".github", ".agents" })
        {
            var protectedPath = files.Resolve(protectedRoot);
            if (output.Equals(protectedPath, ProjectFiles.PathComparison) ||
                output.StartsWith(protectedPath + Path.DirectorySeparatorChar, ProjectFiles.PathComparison))
            {
                throw new ArgumentException("A report cannot write protected/native state, artifact registrations or repository metadata.", nameof(requested));
            }
        }
        if (File.Exists(output))
        {
            throw new IOException("The report target already exists. Select a new local output path; existing case evidence will not be overwritten.");
        }
        return output;
    }

    private static void EnsureLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Remote URLs, UNC paths and shared drives are not report output targets.", nameof(path));
        }
        if (Path.IsPathFullyQualified(path) && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
        {
            throw new ArgumentException("A shared/network drive is not a local report target.", nameof(path));
        }
    }
}

internal sealed class ReportSnapshot : IDisposable
{
    private readonly List<FileStream> _locks = [];
    private readonly List<(ProjectFiles Files, string Root, FederationTree Tree)> _trees = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly ProjectFiles _parent;
    internal List<ReportCase> Cases { get; } = [];
    internal List<SquadReportGrounding> Grounding { get; } = [];
    internal string Mode { get; private set; } = "single";
    private ReportSnapshot(ProjectFiles parent) => _parent = parent;

    internal static async Task<ReportSnapshot> CaptureAsync(ProjectFiles parent, SquadReportConfiguration configuration,
        string kind, string? squad, string output, CancellationToken token)
    {
        var result = new ReportSnapshot(parent);
        var complete = false;
        try
        {
            var federation = parent.SquadRoot == ProjectFiles.StateRoot &&
                File.Exists(parent.Resolve(ProjectFiles.StateRoot + "/federation.md"));
            if (federation && squad is not null)
            {
                FederationPersistence.ValidateName(squad);
                result.Mode = "federation member " + squad;
                await result.AddCaseAsync(FederationPersistence.MemberRoot(squad), squad, configuration, kind, output, token).ConfigureAwait(false);
            }
            else if (federation)
            {
                result.Mode = "all federation members";
                await AuthorizeAsync(configuration, new(kind, parent.SquadRoot, null, output,
                    configuration.IncludeCaseContent), token).ConfigureAwait(false);
                result.ReadLock(parent, parent.SquadRoot + "/federation.run.lock");
                var hub = result.ReadCase(parent.SquadRoot, null, configuration, true);
                var registry = hub.State.Federation ?? throw new InvalidDataException("No confirmed native federation registry exists.");
                foreach (var member in registry.Members.OrderBy(member => member.Entry.Name, StringComparer.Ordinal))
                {
                    FederationPersistence.ValidateName(member.Entry.Name);
                    await result.AddCaseAsync(FederationPersistence.MemberRoot(member.Entry.Name), member.Entry.Name,
                        configuration, kind, output, token).ConfigureAwait(false);
                }
                hub.Scribe.VerifyFederation(hub.State);
                result.Cases.Insert(0, hub);
            }
            else
            {
                await result.AddCaseAsync(parent.SquadRoot, parent.IsMemberRoot ? Path.GetFileName(parent.Resolve(parent.SquadRoot)) : null,
                    configuration, kind, output, token).ConfigureAwait(false);
            }
            result.AssertUnchanged();
            complete = true;
            return result;
        }
        finally
        {
            if (!complete)
            {
                result.Dispose();
            }
        }
    }

    internal async Task AuthorizeOutputAsync(SquadReportConfiguration configuration, string kind, string output, CancellationToken token)
    {
        foreach (var item in Cases)
        {
            await AuthorizeAsync(configuration, new(kind, item.Files.SquadRoot, item.Name, output,
                configuration.IncludeCaseContent), token).ConfigureAwait(false);
        }
    }

    internal async Task<List<ReportSource>> SearchSourcesAsync(SquadReportConfiguration configuration, string output,
        List<string> questions, CancellationToken token)
    {
        var sources = new List<ReportSource>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = 0;
        foreach (var item in Cases.Where(item => item.State.Federation is null))
        {
            var roots = item.Roots.Where(pair => pair.Key != "scribe" && pair.Value is not null)
                .Select(pair => Prefix(pair.Value!)).Distinct(StringComparer.Ordinal).ToList();
            if (Mode is "single" or "all federation members")
            {
                roots.Add("docs");
            }
            foreach (var root in roots.Distinct(StringComparer.Ordinal))
            {
                if (item.Files.IsMemberRoot && !ProjectFiles.IsWithin(root, item.Files.SquadRoot))
                {
                    if (Mode != "all federation members")
                    {
                        questions.Add("Shared project documentation is outside the selected member. Use a separately authorized wider report to read it.");
                        continue;
                    }
                    if (root is not ("docs" or "outputs") && !ProjectFiles.IsWithin(root, "docs") && !ProjectFiles.IsWithin(root, "outputs"))
                    {
                        throw new InvalidDataException("A roster document root escapes its native member.");
                    }
                }
                if (item.Files.IsProtectedStatePath(root) ||
                    ProjectFiles.IsWithin(root, ProjectFiles.StateRoot + "/members") &&
                    !ProjectFiles.IsWithin(root, item.Files.SquadRoot))
                {
                    throw new InvalidDataException("A roster document root crosses protected or unrelated native case state.");
                }
                var directory = item.Files.Resolve(root);
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                var pending = new Stack<string>();
                pending.Push(directory);
                while (pending.TryPop(out var current))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                    {
                        ProjectFiles.RejectLinks(path);
                        if (Directory.Exists(path))
                        {
                            pending.Push(path);
                            continue;
                        }
                        if (!ReportSafety.TextExtension(Path.GetExtension(path)))
                        {
                            continue;
                        }
                        var relative = Path.GetRelativePath(_parent.ProjectRoot, path).Replace('\\', '/');
                        var shared = item.Files.IsMemberRoot && !ProjectFiles.IsWithin(relative, item.Files.SquadRoot);
                        var hub = Cases.SingleOrDefault(candidate => candidate.State.Federation is not null);
                        var owner = shared ? hub?.State.Federation?.OutputOwners.GetValueOrDefault(relative)?.Member : item.Name;
                        if (shared && owner is not null && owner != item.Name || !seen.Add(path))
                        {
                            continue;
                        }
                        if (++candidates > configuration.MaximumSourceFiles)
                        {
                            questions.Add("The bounded source-file search limit was reached; remaining files were not read.");
                            return sources;
                        }
                        if (ReportSafety.ProtectedSource(item.Files, relative) ||
                            path.Equals(output, ProjectFiles.PathComparison))
                        {
                            continue;
                        }
                        if (!ReportSafety.IsSafe(relative, configuration.SecretValues) ||
                            !await configuration.AuthorizeAsync!(new("document-source",
                            shared && owner is null ? _parent.SquadRoot : item.Files.SquadRoot, owner,
                            output, true, relative), token).ConfigureAwait(false))
                        {
                            questions.Add("A candidate local source was withheld by the host's case disclosure policy.");
                            continue;
                        }
                        if (new FileInfo(path).Length > (long)configuration.MaximumSourceCharacters * 4)
                        {
                            questions.Add("A candidate local source exceeds the bounded text limit and was not exported.");
                            continue;
                        }
                        var bytes = item.Files.ReadBytes(relative);
                        string content;
                        try
                        {
                            content = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                        }
                        catch (DecoderFallbackException)
                        {
                            questions.Add("A candidate source is not valid UTF-8 text and was withheld rather than exporting replacement bytes.");
                            continue;
                        }
                        if (content.Length > configuration.MaximumSourceCharacters ||
                            !ReportSafety.IsSafe(content, configuration.SecretValues))
                        {
                            questions.Add("A candidate source was withheld because it exceeds the text limit or contains credential/secret transport material.");
                            continue;
                        }
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                        _sources[path] = hash;
                        sources.Add(new(relative, content, new(owner, relative, hash, "authorized-local-text")));
                    }
                }
            }
        }
        return sources;
    }

    internal async Task PublishAsync(string output, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is 0 or > ProjectFiles.MaximumHostOutputBytes || _sources.ContainsKey(output))
        {
            throw new InvalidDataException("Report output is empty, oversized or would overwrite a grounding source.");
        }
        token.ThrowIfCancellationRequested();
        AssertUnchanged();
        var directory = Path.GetDirectoryName(output)!;
        ProjectFiles.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var temporary = output + ".pending-" + Guid.NewGuid().ToString("N");
        var published = false;
        var complete = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            AssertUnchanged();
            ProjectFiles.RejectLinks(output);
            File.Move(temporary, output, false);
            published = true;
            AssertUnchanged();
            complete = true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            if (published && !complete)
            {
                File.Delete(output);
            }
        }
    }

    public void Dispose()
    {
        foreach (var stream in _locks)
        {
            stream.Dispose();
        }
    }

    private async Task AddCaseAsync(string root, string? name, SquadReportConfiguration configuration, string kind,
        string output, CancellationToken token)
    {
        await AuthorizeAsync(configuration, new(kind, root, name, output, configuration.IncludeCaseContent), token).ConfigureAwait(false);
        Cases.Add(ReadCase(root, name, configuration, false));
    }

    private ReportCase ReadCase(string root, string? name, SquadReportConfiguration configuration, bool hub)
    {
        var files = new ProjectFiles(_parent.ProjectRoot, configuration.MaximumSourceCharacters, root);
        if (!File.Exists(files.Resolve(root + "/state.json")) || !hub && !File.Exists(files.Resolve(root + "/team.md")))
        {
            throw new InvalidDataException("No native squad state found. Initialize the native squad before generating a report.");
        }
        ReadLock(files, root + "/native.lock");
        var tree = FederationPersistence.Enumerate(files, root, path =>
            hub && ProjectFiles.IsWithin(path, root + "/members"));
        _trees.Add((files, root, tree));
        var scribe = new RuntimeScribe(files);
        var state = scribe.Load(allowFederation: hub) ?? throw new InvalidDataException("No native report snapshot exists.");
        if (!hub && state.Schema != RuntimeState.SchemaVersion)
        {
            throw new InvalidDataException("Reporting requires explicitly migrated native v2 state.");
        }
        var ledgerVerified = scribe.VerifyReportSnapshot(state);
        var roster = hub ? SquadRoster.Empty : scribe.ReadReportRoster();
        IReadOnlyDictionary<string, string?> roots = hub ? new Dictionary<string, string?>() : scribe.ReadRosterRoots(roster);
        foreach (var file in hub
            ? new[] { "state.json", "federation.md", "meta-routing.md", "decisions.md" }
            : new[] { "state.json", "team.md", "routing.md", "decisions.md", "consumption.md", "notifications.md" })
        {
            var path = root + "/" + file;
            var resolved = files.Resolve(path);
            var hash = files.HashFile(path);
            _sources[resolved] = hash;
            Grounding.Add(new(name, path, hash, "verified-native-projection"));
        }
        return new(name, files, scribe, state, roster, roots, ledgerVerified);
    }

    private void ReadLock(ProjectFiles files, string path)
    {
        var resolved = files.Resolve(path);
        if (File.Exists(resolved))
        {
            _locks.Add(new(resolved, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
    }

    private static string Prefix(string root)
    {
        var marker = root.IndexOf('<');
        return (marker < 0 ? root : root[..marker]).Replace('\\', '/').TrimEnd('/');
    }

    private static async ValueTask AuthorizeAsync(SquadReportConfiguration configuration, SquadReportAccess access, CancellationToken token)
    {
        if (configuration.AuthorizeAsync is null || !await configuration.AuthorizeAsync(access, token).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The host must authorize this exact case scope and local report disclosure before reading state.");
        }
    }

    internal void AssertUnchanged()
    {
        if (!SnapshotMatches())
        {
            throw new RuntimeHistoryIntegrityException("report-snapshot: scoped state or a grounding source changed during observation; no coherent export is available.");
        }
    }

    private bool SnapshotMatches()
    {
        foreach (var (files, root, before) in _trees)
        {
            var after = FederationPersistence.Enumerate(files, root, path =>
                Cases.Any(item => item.Files == files && item.State.Federation is not null) &&
                ProjectFiles.IsWithin(path, root + "/members"));
            if (!before.Directories.SequenceEqual(after.Directories, StringComparer.Ordinal) ||
                !before.Files.SequenceEqual(after.Files))
            {
                return false;
            }
        }
        return _sources.All(pair => File.Exists(pair.Key) &&
            HashPath(pair.Key) == pair.Value);
    }

    private static string HashPath(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
