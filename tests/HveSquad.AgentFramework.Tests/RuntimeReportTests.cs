using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeReportTests
{
    private static readonly string[] Sections =
    [
        "1. Governance Gates", "2. Council Verdicts", "3. Cost Breakdown", "4. Role Dispatch Activity",
        "5. Risk & Compliance", "6. Activity Timeline", "7. Key Outcomes This Period",
    ];
    private static readonly string[] HeaderMetrics =
        ["Coordination Turns", "Role Dispatches", "Est. Cost", "Total Tokens", "Human Gates Fired", "Escalations"];
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static SquadReportConfiguration Configuration(bool privateContent = false, bool renderer = false) => new()
    {
        AuthorizeAsync = (_, _) => ValueTask.FromResult(true),
        IncludeCaseContent = privateContent,
        Renderers = renderer ? [new SquadLocalDocumentRenderer()] : [],
    };

    private static string PathFor(RuntimeTestHost host, string relative) =>
        Path.Combine(host.Project, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string MemberRoot(string member) => ".copilot-tracking/squad/members/" + member;
    private static RuntimeState State(RuntimeTestHost host) =>
        JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(PathFor(host, host.Options.SquadRoot + "/state.json")))!;
    private static Dictionary<string, string> Manifest(RuntimeTestHost host) =>
        Directory.EnumerateFiles(PathFor(host, ".copilot-tracking/squad"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(host.Project, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    private static void Unchanged(RuntimeTestHost host, Dictionary<string, string> before)
    {
        var after = Manifest(host);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }
    private static async Task<SquadRunResult> Seed(RuntimeTestHost host, bool pipeline = false, bool council = false)
    {
        host.UseCurrentPlanContract();
        if (pipeline)
        {
            if (council)
            {
                host.Options.Council = new() { Roles = ["architect"] };
            }
            host.QueuePipeline("technical-writer", council: council);
            host.QueueStage("researcher");
            host.QueueStage("lead");
            if (council)
            {
                host.QueueStage("architect", "Approve");
            }
            host.QueueStage("technical-writer");
            host.QueueStage("tester");
            host.Client.Text("Original completed fixture.");
        }
        else
        {
            ConsumptionTestData.QueueResearch(host);
        }
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Inspect the existing synthetic implementation.");
        Assert.True(result.Status == (pipeline ? SquadRunStatus.Completed : SquadRunStatus.ResearchCompleted), result.ResponseText);
        return result;
    }
    private static async Task<SquadFederation> Federation(RuntimeTestHost host)
    {
        host.UseCurrentPlanContract();
        var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        var result = await federation.InitializeAsync(
        [
            new() { Name = "product", Description = "Original product evidence", Patterns = ["product work"] },
            new() { Name = "platform", Description = "Original platform evidence", Patterns = ["platform work"] },
        ]);
        Assert.True(result.Status == SquadRunStatus.Completed, result.Diagnostic);
        return federation;
    }
    private static void LocalSource(RuntimeTestHost host, string relative, string content)
    {
        var path = PathFor(host, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private static XDocument Part(byte[] bytes, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        using var stream = (archive.GetEntry(name) ?? throw new InvalidDataException("Missing test OOXML part.")).Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public async Task GivenActualNativeEvidence_WhenExportingDefaultDocument_PreservesEveryStateByteAndWritesRequiredShape()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var before = Manifest(host);
        var calls = host.Client.Calls.Count;
        var reports = new SquadReports(host.Project, Configuration());

        var result = await reports.DocumentAsync(new("Current researcher roster and activity"));

        Assert.Equal("md", result.Format);
        Assert.False(result.Fallback);
        Assert.Equal(PathFor(host, "docs/squad-document-" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md"), result.Path);
        var text = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("Open Questions", text, StringComparison.Ordinal);
        Assert.Contains("none", text, StringComparison.Ordinal);
        Assert.Contains("Test researcher", text, StringComparison.Ordinal);
        Assert.Contains("SHA-256", text, StringComparison.Ordinal);
        Assert.NotEmpty(result.Grounding);
        Assert.Empty(result.OpenQuestions);
        Assert.Equal(calls, host.Client.Calls.Count);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenHtmlRequest_WhenExporting_EscapesRequestAndSourceInsteadOfExecutingThem()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/allowed.html", "<script>window.untrustedProbe=true</script> Approved margin is 17 percent.");
        var reports = new SquadReports(host.Project, Configuration(privateContent: true));

        var result = await reports.DocumentAsync(new("<script>margin</script>", "html"));

        var html = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("<main>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;margin&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;window.untrustedProbe=true&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>window.untrustedProbe", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)(?:src|href)\s*=\s*[""'](?:https?:)?//", html);
        Assert.Contains("--cp-bg:", html, StringComparison.Ordinal);
        Assert.Contains("scoutTheme", html, StringComparison.Ordinal);
        Assert.Contains("Approved margin is 17 percent.", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("docx")]
    [InlineData("pdf")]
    public async Task GivenUnavailableOrUnrecognizedFormat_WhenExporting_ReportsFallbackAndWritesActualMarkdownExtension(string format)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var before = Manifest(host);
        var reports = new SquadReports(host.Project, Configuration());

        var result = await reports.DocumentAsync(new("Current roster", format, "docs/explicit-name." + format));

        Assert.Equal("md", result.Format);
        Assert.Equal(format, result.RequestedFormat);
        Assert.True(result.Fallback);
        Assert.Contains("Markdown", result.Diagnostic, StringComparison.Ordinal);
        Assert.EndsWith(".md", result.Path, StringComparison.Ordinal);
        Assert.StartsWith("---", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.False(File.Exists(PathFor(host, "docs/explicit-name." + format)));
        Unchanged(host, before);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    public async Task GivenRegisteredLocalRenderer_WhenExporting_ProducesRealBytesWithoutModelOrStateMutation(string format)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var before = Manifest(host);
        var calls = host.Client.Calls.Count;
        var reports = new SquadReports(host.Project, Configuration(renderer: true));

        var result = await reports.DocumentAsync(new("Current roster", format));

        Assert.Equal(format, result.Format);
        Assert.False(result.Fallback);
        var bytes = await File.ReadAllBytesAsync(result.Path);
        ReportFormats.Verify(format, bytes);
        if (format == "pdf")
        {
            var pdf = Encoding.Latin1.GetString(bytes);
            Assert.StartsWith("%PDF-1.4", pdf, StringComparison.Ordinal);
            Assert.Contains("Open Questions", pdf, StringComparison.Ordinal);
            var xref = Regex.Match(pdf, @"startxref\n(\d+)\n%%EOF", RegexOptions.CultureInvariant);
            var offset = int.Parse(xref.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith("xref\n", pdf[offset..], StringComparison.Ordinal);
            var entries = Regex.Matches(pdf, @"(?m)^(\d{10}) 00000 n $", RegexOptions.CultureInvariant);
            Assert.NotEmpty(entries);
            for (var index = 0; index < entries.Count; index++)
            {
                var start = int.Parse(entries[index].Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.StartsWith((index + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n", pdf[start..], StringComparison.Ordinal);
            }
        }
        else
        {
            var document = Part(bytes, "word/document.xml");
            Assert.Equal(Word + "document", document.Root!.Name);
            Assert.Contains("Open Questions", string.Concat(document.Descendants(Word + "t").Select(text => text.Value)), StringComparison.Ordinal);
            var page = Assert.Single(document.Descendants(Word + "pgSz"));
            Assert.Equal("12240", (string?)page.Attribute(Word + "w"));
            Assert.Equal("15840", (string?)page.Attribute(Word + "h"));
            Assert.NotEmpty(Part(bytes, "word/styles.xml").Descendants(Word + "outlineLvl"));
        }
        Assert.Equal(calls, host.Client.Calls.Count);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenUnicodePrivateFacts_WhenUsingReferencePdf_FallsBackExplicitlyWhileWordRetainsUnicode()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/approved-facts.md", "Approved Unicode fact: \u4ea7\u4e1a\u8bc1\u636e.");
        var reports = new SquadReports(host.Project, Configuration(privateContent: true, renderer: true));

        var pdf = await reports.DocumentAsync(new("Unicode fact", "pdf", "docs/unicode.pdf"));
        var word = await reports.DocumentAsync(new("Unicode fact", "docx", "docs/unicode.docx"));

        Assert.True(pdf.Fallback);
        Assert.Equal("md", pdf.Format);
        Assert.Contains("Markdown", pdf.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("docx", word.Format);
        Assert.Contains("\u4ea7\u4e1a\u8bc1\u636e", string.Concat(Part(await File.ReadAllBytesAsync(word.Path),
            "word/document.xml").Descendants(Word + "t").Select(text => text.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenLocalRendererTablesAndLongText_WhenRendering_UsesRealFixedWidthTablesAndMultiplePdfPages()
    {
        var renderer = new SquadLocalDocumentRenderer();
        const string markdown = "---\ntitle: Probe\n---\n## Probe table\n\n| Name | Value |\n| --- | --- |\n| Alpha | 12 |\n\n## Open Questions\n\nnone";
        var word = await renderer.RenderAsync(new("docx", "Probe", markdown, "Probe", "", "original-input"), default);
        var table = Assert.Single(Part(word.Content!, "word/document.xml").Descendants(Word + "tbl"));
        Assert.Equal("9360", (string?)Assert.Single(table.Descendants(Word + "tblW")).Attribute(Word + "w"));
        Assert.Equal(9360, table.Descendants(Word + "gridCol").Sum(column => int.Parse(column.Attribute(Word + "w")!.Value, CultureInfo.InvariantCulture)));
        Assert.All(table.Descendants(Word + "tcW"), width => Assert.Equal("4680", (string?)width.Attribute(Word + "w")));
        var pdf = await renderer.RenderAsync(new("pdf", "Probe", "", string.Join("\n", Enumerable.Repeat("Actual paginated evidence", 120)),
            "", "original-input"), default);
        Assert.Contains("/Count 3", Encoding.Latin1.GetString(pdf.Content!), StringComparison.Ordinal);
        Assert.Equal(3, Regex.Count(Encoding.Latin1.GetString(pdf.Content!), @"/Type /Page ", RegexOptions.CultureInvariant));
    }

    [Theory]
    [InlineData("wrong-hash")]
    [InlineData("renamed-markdown")]
    [InlineData("no-reason")]
    public async Task GivenFalseRendererReceipt_WhenExporting_RejectsOutputRatherThanClaimingFormat(string failure)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var configuration = Configuration();
        configuration.Renderers = [new FaultyRenderer(failure)];
        var reports = new SquadReports(host.Project, configuration);
        var before = Manifest(host);

        await Assert.ThrowsAsync<InvalidDataException>(() => reports.DocumentAsync(new("Current roster", "pdf")));

        Assert.False(Directory.Exists(PathFor(host, "docs")));
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenRendererFailure_WhenExporting_SurfacesFailureWithoutSilentFallbackOrPartialFile()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var configuration = Configuration();
        configuration.Renderers = [new FaultyRenderer("throws")];
        var reports = new SquadReports(host.Project, configuration);

        await Assert.ThrowsAsync<IOException>(() => reports.DocumentAsync(new("Current roster", "pdf")));

        Assert.False(Directory.Exists(PathFor(host, "docs")));
    }

    [Fact]
    public async Task GivenNoDisclosureAuthority_WhenExporting_DeniesBeforeReadingStateOrWritingOutput()
    {
        using var host = new RuntimeTestHost();
        var reports = new SquadReports(host.Project, new());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reports.DocumentAsync(new("Current roster")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenAuthorizedButUninitializedCase_WhenExporting_ReportsMissingNativeState()
    {
        using var host = new RuntimeTestHost();
        var reports = new SquadReports(host.Project, Configuration());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => reports.GovernanceAsync());

        Assert.Contains("Initialize", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenExplicitPrivateCaseAuthority_WhenSearching_ExportsPermittedFactsButNotProtectedPayloadsOrContacts()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/private-case.md", "Authorized private margin: 17 percent for the local industrial case.");
        LocalSource(host, ".copilot-tracking/squad/payloads/private.json", "Unrelated protected margin: private-payload-never-export.");
        LocalSource(host, ".copilot-tracking/squad/watch-inputs/original.json", "Original event margin: raw-event-never-export.");
        var before = Manifest(host);
        var configuration = Configuration(privateContent: true);
        var reads = new List<string>();
        configuration.AuthorizeAsync = (access, _) =>
        {
            if (access.SourcePath is { } source)
            {
                reads.Add(source);
            }
            return ValueTask.FromResult(true);
        };
        var reports = new SquadReports(host.Project, configuration);

        var result = await reports.DocumentAsync(new("private margin"));

        var text = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("17 percent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-payload-never-export", text, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-event-never-export", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reads, path => path.Contains("/payloads/", StringComparison.Ordinal) || path.Contains("/watch-inputs/", StringComparison.Ordinal));
        Assert.Contains(result.Grounding, source => source.Path == "docs/private-case.md" && source.Kind == "authorized-local-text");
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenDefaultSafeView_WhenSearching_DoesNotReadOrExportPrivateDocumentContent()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/private-case.md", "Private contractual margin is 17 percent.");
        var configuration = Configuration();
        configuration.AuthorizeAsync = (access, _) =>
        {
            Assert.Null(access.SourcePath);
            return ValueTask.FromResult(true);
        };

        var result = await new SquadReports(host.Project, configuration).DocumentAsync(new("contractual margin"));

        Assert.DoesNotContain("17 percent", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.NotEmpty(result.OpenQuestions);
    }

    [Theory]
    [InlineData("Authorization: Bearer synthetic-do-not-export")]
    [InlineData("api_key=synthetic-do-not-export")]
    [InlineData("https://storage.invalid/object?sv=fixture&sig=synthetic-do-not-export")]
    [InlineData("https://account:synthetic-do-not-export@service.invalid")]
    [InlineData("github_pat_synthetic_do_not_export_123")]
    public async Task GivenSecretTransportInOtherwisePermittedSource_WhenSearching_WithholdsAndExplainsWithoutLeaking(string secret)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/credential-source.md", "Requested marker: signed-transport.\n" + secret);
        var reports = new SquadReports(host.Project, Configuration(privateContent: true));

        var result = await reports.DocumentAsync(new("signed-transport"));

        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.Contains(result.OpenQuestions, question => question.Contains("credential/secret transport", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Grounding, source => source.Path == "docs/credential-source.md");
    }

    [Fact]
    public async Task GivenHostKnownSecretAndDeniedCaseFile_WhenSearching_ExportsNeither()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/host-secret.md", "Requested marker host-sensitive: opaque-host-secret-fixture.");
        LocalSource(host, "docs/other-case.md", "Requested marker host-sensitive: unrelated-case-private-fact.");
        var configuration = Configuration(privateContent: true);
        configuration.SecretValues.Add("opaque-host-secret-fixture");
        configuration.AuthorizeAsync = (access, _) => ValueTask.FromResult(access.SourcePath != "docs/other-case.md");

        var result = await new SquadReports(host.Project, configuration).DocumentAsync(new("host-sensitive"));

        var text = await File.ReadAllTextAsync(result.Path);
        Assert.DoesNotContain("opaque-host-secret-fixture", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated-case-private-fact", text, StringComparison.Ordinal);
        Assert.Contains(result.OpenQuestions, question => question.Contains("withheld", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GivenEditedCurrentRosterRoot_WhenSearching_UsesTheActualCellInsteadOfOldCatalogOrHistoricalRoot()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var roster = PathFor(host, ".copilot-tracking/squad/team.md");
        File.WriteAllText(roster, File.ReadAllText(roster).Replace(".copilot-tracking/researcher/", ".copilot-tracking/current-research/", StringComparison.Ordinal));
        LocalSource(host, ".copilot-tracking/researcher/old.md", "Scoped-fact: obsolete unselected root.");
        LocalSource(host, ".copilot-tracking/current-research/allowed.md", "Scoped-fact: current authorized roster root.");
        var before = Manifest(host);

        var result = await new SquadReports(host.Project, Configuration(privateContent: true)).DocumentAsync(new("Scoped-fact"));

        var text = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("current authorized roster root", text, StringComparison.Ordinal);
        Assert.DoesNotContain("obsolete unselected root", text, StringComparison.Ordinal);
        Assert.Contains(result.Grounding, source => source.Path == ".copilot-tracking/current-research/allowed.md");
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenMemberScope_WhenHubAndSiblingAreUnreadable_ReadsOnlyTheOriginalMemberAndKeepsDefaultProjectOutput()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Federation(host);
        LocalSource(host, MemberRoot("product") + "/researcher/facts.md", "Member-bound-fact: authorized product evidence.");
        File.WriteAllText(PathFor(host, ".copilot-tracking/squad/state.json"), "unreadable unrelated hub fixture");
        File.WriteAllText(PathFor(host, MemberRoot("platform") + "/state.json"), "unreadable unrelated member fixture");
        var before = Manifest(host);
        var configuration = Configuration(privateContent: true);
        var reads = new List<SquadReportAccess>();
        configuration.AuthorizeAsync = (access, _) =>
        {
            reads.Add(access);
            return ValueTask.FromResult(access.Squad == "product" &&
                (access.SourcePath is null || access.SourcePath.StartsWith(MemberRoot("product") + "/", StringComparison.Ordinal)));
        };

        var result = await federation.CreateReports(configuration).DocumentAsync(new("Member-bound-fact", Squad: "product"));

        Assert.Contains("authorized product evidence", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.StartsWith(PathFor(host, "docs") + Path.DirectorySeparatorChar, result.Path, StringComparison.Ordinal);
        Assert.All(reads, access => Assert.Equal(MemberRoot("product"), access.SquadRoot));
        Assert.All(result.Grounding, source => Assert.StartsWith(MemberRoot("product") + "/", source.Path, StringComparison.Ordinal));
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenEmptyFederation_WhenReporting_ContainsAllSevenSectionsSixMetricsAndTruthfulZeroModelNativeWork()
    {
        using var host = new RuntimeTestHost();
        using var federation = await Federation(host);
        var before = Manifest(host);

        var result = await federation.CreateReports(Configuration()).GovernanceAsync();

        var metrics = Assert.IsType<SquadGovernanceMetrics>(result.Metrics);
        Assert.Equal(0, metrics.RoleDispatches);
        Assert.Equal(0, metrics.ProviderAttempts);
        Assert.Equal(0m, metrics.EstimatedCostUsd);
        Assert.Equal(0, metrics.TotalTokens);
        var html = await File.ReadAllTextAsync(result.Path);
        Assert.Equal(Sections, Regex.Matches(html, "<h2>(.*?)</h2>", RegexOptions.CultureInvariant)
            .Select(match => WebDecode(match.Groups[1].Value)));
        Assert.Equal(HeaderMetrics, Regex.Matches(html, "<dt>(.*?)</dt>", RegexOptions.CultureInvariant).Select(match => match.Groups[1].Value));
        Assert.Contains("No council reviews were triggered", html, StringComparison.Ordinal);
        Assert.Contains("Not activated", html, StringComparison.Ordinal);
        Assert.Contains("None", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)(?:src|href)\s*=\s*[""'](?:https?:)?//", html);
        Assert.Empty(host.Client.Calls);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenActualDispatchesAndPrices_WhenReporting_ReconcilesExactOriginalAttemptsWithoutRecalibrationOrReservations()
    {
        using var host = new RuntimeTestHost();
        ConsumptionTestData.Configure(host, ceiling: null);
        host.Options.Consumption!.Calibration.Factor = 1.2m;
        host.Options.TokenUsageSelector = _ => new(1000, 100, 0, 200);
        _ = await Seed(host, pipeline: true);
        var state = State(host);
        var before = Manifest(host);
        var calls = host.Client.Calls.Count;
        var expectedCost = ConsumptionMath.Round(state.ModelCallCharges!.Sum(charge => charge.CostUsd!.Value));
        var reports = new SquadReports(host.Project, Configuration());

        var result = await reports.GovernanceAsync();

        var metrics = Assert.IsType<SquadGovernanceMetrics>(result.Metrics);
        Assert.Equal(state.DispatchRecords.Count, metrics.RoleDispatches);
        Assert.Equal(state.ModelCalls.Count, metrics.ProviderAttempts);
        Assert.Equal(state.CoordinatorInvocations.Count, metrics.CoordinationTurns);
        Assert.Equal(expectedCost, metrics.EstimatedCostUsd);
        Assert.Equal(expectedCost, metrics.KnownCostUsd);
        Assert.Equal(state.ModelCalls.Sum(call => call.InputTokens!.Value + call.OutputTokens!.Value), metrics.TotalTokens);
        Assert.Equal(state.ModelCalls.Count, metrics.CalibratedAttempts);
        Assert.Equal(0, metrics.UncalibratedAttempts);
        Assert.Equal(0, metrics.UnpricedAttempts);
        Assert.Equal(calls, host.Client.Calls.Count);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenUnknownUsageAndPrices_WhenReporting_NeverTurnsPartialKnownSpendOrTokensIntoZeroTotals()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);

        var result = await new SquadReports(host.Project, Configuration()).GovernanceAsync();

        var metrics = Assert.IsType<SquadGovernanceMetrics>(result.Metrics);
        Assert.Null(metrics.EstimatedCostUsd);
        Assert.Null(metrics.TotalTokens);
        Assert.True(metrics.UnpricedAttempts > 0);
        Assert.True(metrics.UncalibratedAttempts > 0);
        var html = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("known partial", html, StringComparison.Ordinal);
        Assert.Contains("uncalibrated/pending", html, StringComparison.Ordinal);
        Assert.Contains("Credits: unknown", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all", 40, true)]
    [InlineData("30d", 8, true)]
    [InlineData("7d", 8, false)]
    [InlineData("30d", 31, false)]
    public async Task GivenFiniteWindow_WhenReporting_FiltersActualOriginalDatedEvidenceNotAllTimeTotals(string period, int days, bool included)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var state = State(host);
        var clock = new ReportClock(DateTimeOffset.UtcNow.AddDays(days));

        var result = await new SquadReports(host.Project, Configuration(), timeProvider: clock).GovernanceAsync(new(period));

        var metrics = Assert.IsType<SquadGovernanceMetrics>(result.Metrics);
        Assert.Equal(included ? state.DispatchRecords.Count : 0, metrics.RoleDispatches);
        Assert.Equal(included ? state.ModelCalls.Count : 0, metrics.ProviderAttempts);
        Assert.Equal(included ? state.CoordinatorInvocations.Count : 0, metrics.CoordinationTurns);
    }

    [Theory]
    [InlineData("7d", 7, -1, 1)]
    [InlineData("7d", 7, 0, 1)]
    [InlineData("7d", 7, 1, 0)]
    [InlineData("30d", 30, -1, 1)]
    [InlineData("30d", 30, 0, 1)]
    [InlineData("30d", 30, 1, 0)]
    public async Task GivenExactPeriodBoundary_WhenReporting_UsesInclusiveOriginalTimestampToTheTick(
        string period, int days, int ticks, int expected)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var recordedAt = Assert.Single(State(host).NativeOperations, operation => operation.Operation == "RecordDispatchEvidence").RecordedAt;
        var clock = new ReportClock(recordedAt.AddDays(days).AddTicks(ticks));

        var result = await new SquadReports(host.Project, Configuration(), timeProvider: clock).GovernanceAsync(new(period));

        Assert.Equal(expected, result.Metrics!.RoleDispatches);
    }

    [Fact]
    public async Task GivenPromotion_WhenReportingMember_ResolvesSealedHistoricalEvidenceWithoutReadingOriginalStateArchivesAsSources()
    {
        using var host = new RuntimeTestHost();
        var original = await Seed(host);
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        var promoted = await federation.PromoteAsync("product",
            federation.GetPromotionCandidates().Select(candidate => candidate.Path).ToArray());
        Assert.True(promoted.Status == SquadRunStatus.Completed, promoted.Diagnostic);
        var before = Manifest(host);

        var report = await federation.CreateReports(Configuration()).GovernanceAsync(new(Squad: "product"));
        var document = await federation.CreateReports(Configuration(privateContent: true))
            .DocumentAsync(new("researcher research", Squad: "product"));

        Assert.Equal(original.Evidence.Count, report.Metrics!.RoleDispatches);
        Assert.All(document.Grounding, source =>
        {
            Assert.StartsWith(MemberRoot("product") + "/", source.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("/migrations/", source.Path, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("original-state.json", await File.ReadAllTextAsync(document.Path), StringComparison.Ordinal);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenUnavailableRendererWithDeniedMarkdownDisclosure_WhenFallingBack_WritesNothingWithoutTheNewPathAuthority()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var configuration = Configuration(renderer: true);
        configuration.IncludeCaseContent = true;
        LocalSource(host, "docs/unicode-source.md", "Authorized fixture Unicode: \u4ea7\u4e1a.");
        configuration.AuthorizeAsync = (access, _) => ValueTask.FromResult(!access.OutputPath.EndsWith(".md", StringComparison.Ordinal));
        var before = Manifest(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new SquadReports(host.Project, configuration).DocumentAsync(new("Unicode", "pdf", "docs/fallback.pdf")));

        Assert.False(File.Exists(PathFor(host, "docs/fallback.pdf")));
        Assert.False(File.Exists(PathFor(host, "docs/fallback.md")));
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenSourceMutationDuringRendering_WhenPublishing_RejectsIncoherentSnapshotAndRetainsNoReport()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var configuration = Configuration();
        configuration.Renderers = [new MutatingRenderer(PathFor(host, ".copilot-tracking/squad/state.json"))];

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() =>
            new SquadReports(host.Project, configuration).DocumentAsync(new("Current roster", "docx", "docs/incoherent.docx")));

        Assert.False(File.Exists(PathFor(host, "docs/incoherent.docx")));
        Assert.False(Directory.Exists(PathFor(host, "docs")));
    }

    [Fact]
    public async Task GivenCancellationBeforeReading_WhenExporting_InvokesNoDisclosureCallbackOrWriter()
    {
        using var host = new RuntimeTestHost();
        var configuration = Configuration();
        configuration.AuthorizeAsync = (_, _) => throw new InvalidOperationException("Canceled work must not request disclosure.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SquadReports(host.Project, configuration).GovernanceAsync(cancellationToken: canceled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("rates")]
    public async Task GivenDerivedLedgerDefect_WhenReporting_UsesImmutableChargesAndWarnsWithoutRepair(string defect)
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        File.AppendAllText(PathFor(host, ".copilot-tracking/squad/" + (defect == "ledger" ? "consumption.md" : "consumption-rates.md")), "\nChanged derived fixture.");
        var before = Manifest(host);

        var result = await new SquadReports(host.Project, Configuration()).GovernanceAsync();

        Assert.Contains(result.OpenQuestions, question => question.Contains("did not repair", StringComparison.Ordinal));
        Assert.Equal(State(host).DispatchRecords.Count, result.Metrics!.RoleDispatches);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenHistoryIntegrityDefect_WhenReporting_StopsWithoutCorrectionOrDocument()
    {
        using var host = new RuntimeTestHost();
        var run = await Seed(host);
        File.AppendAllText(PathFor(host, run.Evidence[0].HistoryPath), "\nUnrecorded history fixture.");
        var before = Manifest(host);

        await Assert.ThrowsAsync<RuntimeHistoryIntegrityException>(() =>
            new SquadReports(host.Project, Configuration()).GovernanceAsync());

        Assert.False(Directory.Exists(PathFor(host, "docs")));
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenCurrentOutputDrift_WhenReporting_RecordsHistoricalActivityButDoesNotInventCurrentDelivery()
    {
        using var host = new RuntimeTestHost();
        var run = await Seed(host);
        File.AppendAllText(PathFor(host, run.Evidence[0].ArtifactPath), "\nChanged current research fixture.");
        var before = Manifest(host);

        var result = await new SquadReports(host.Project, Configuration()).GovernanceAsync();

        Assert.Equal(run.Evidence.Count, result.Metrics!.RoleDispatches);
        Assert.Contains(result.OpenQuestions, question => question.Contains("historical activity", StringComparison.Ordinal));
        Assert.Contains("0/1 current output sets verified", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenActualCouncilAndCallbackGates_WhenReporting_UsesImmutableTypedGateEvidenceAndNoPrivateApprovalDescriptions()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host, pipeline: true, council: true);
        var state = State(host);
        var gateIds = state.NativeOperations.Select(operation => operation.HumanGate).OfType<ReportHumanGate>()
            .Select(gate => gate.Id).Distinct(StringComparer.Ordinal).Count();
        var before = Manifest(host);

        var result = await new SquadReports(host.Project, Configuration()).GovernanceAsync();

        Assert.Equal(gateIds, result.Metrics!.HumanGatesFired);
        Assert.True(gateIds > 0);
        Assert.Equal(gateIds, result.Metrics.HumanGatesPassed);
        Assert.Contains("Council Verdicts", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.Contains("Go", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Approvals.Last().Description, await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenOriginalDurablePendingGate_WhenReporting_KeepsChallengeEvidenceAndPrivateDetailsUnchanged()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        using var channel = RuntimeModeTests.Durable(host);
        host.QueuePipeline("technical-writer");
        using var runtime = await host.CreateAsync();
        var run = await runtime.RunAsync("Authorized local documentation with private marker human-detail-never-export.");
        Assert.Equal(SquadRunStatus.ApprovalRequired, run.Status);
        var pending = Assert.Single(await runtime.GetPendingHumanActionsAsync());
        var before = Manifest(host);
        var details = await runtime.ReadHumanActionAsync(pending.Binding.Id);

        var result = await runtime.CreateReports(Configuration()).GovernanceAsync();

        Assert.Equal(1, result.Metrics!.HumanGatesFired);
        Assert.Equal(0, result.Metrics.HumanGatesPassed);
        Assert.DoesNotContain("human-detail-never-export", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Unchanged(host, before);
        Assert.Equal(details.Action.Binding, (await runtime.ReadHumanActionAsync(pending.Binding.Id)).Action.Binding);
        var submitted = await runtime.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(pending, "/approve", "report-accepted-original"));
        Assert.True(submitted.Accepted, submitted.Diagnostic);
    }

    [Fact]
    public async Task GivenNativeWatchMember_WhenReporting_UsesActualInnerCountsWithoutExportingOriginalEventOrMetaStageFiction()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        RuntimeWatchTests.QueueReportResearch(host);
        var original = RuntimeWatchTests.ReportEvent() with
        {
            Body = "/squad Research the permitted local event marker raw-watch-data-never-export.",
        };
        var watched = await federation.HandleWatchEventAsync(original, RuntimeWatchTests.ReportConfiguration());
        Assert.True(watched.Status == SquadWatchStatus.Prepared, watched.Diagnostic);
        var before = Manifest(host);
        var calls = host.Client.Calls.Count;

        var result = await federation.CreateReports(Configuration()).GovernanceAsync();

        Assert.Equal(watched.Run!.Evidence.Count, result.Metrics!.RoleDispatches);
        Assert.Equal(calls, result.Metrics.ProviderAttempts);
        Assert.DoesNotContain("raw-watch-data-never-export", await File.ReadAllTextAsync(result.Path), StringComparison.Ordinal);
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenVerifiedWatchIssue_WhenReporting_CountsOneOriginalProviderResourceWithoutDoubleCountingHubOrInnerRun()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        using var federation = await SquadFederation.CreateAsync(host.Client, host.Options, host.Source());
        RuntimeWatchTests.QueueReportResearch(host);
        var source = RuntimeWatchTests.ReportEvent() with
        {
            Kind = SquadWatchEventKind.Schedule,
            Action = null,
            Number = null,
            EventId = "workflow:901",
            WorkflowRunId = 901,
        };
        var delivered = await federation.HandleWatchEventAsync(source, RuntimeWatchTests.ReportConfiguration(terminal: true));
        Assert.True(delivered.Status == SquadWatchStatus.Delivered, delivered.Diagnostic);
        Assert.Equal(SquadWatchTerminalKind.Issue, delivered.Terminal!.Kind);
        var before = Manifest(host);

        var result = await federation.CreateReports(Configuration()).GovernanceAsync();

        var html = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("1 observed human-channel/Watch issues", html, StringComparison.Ordinal);
        Assert.Contains("Verified provider surface; not human acceptance", html, StringComparison.Ordinal);
        Assert.Equal(delivered.Run!.Evidence.Count, result.Metrics!.RoleDispatches);
        Unchanged(host, before);
    }

    [Theory]
    [InlineData("https://remote.invalid/report.html")]
    [InlineData("file://remote/report.html")]
    [InlineData("\\\\server\\share\\report.html")]
    [InlineData("..\\report.html")]
    [InlineData(".git\\report.html")]
    [InlineData(".github\\report.html")]
    [InlineData(".copilot-tracking\\squad\\history\\report.html")]
    public async Task GivenRemoteTraversalOrProtectedTarget_WhenReporting_RejectsBeforeStateOrOutputWork(string output)
    {
        using var host = new RuntimeTestHost();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SquadReports(host.Project, Configuration()).GovernanceAsync(new(OutputPath: output)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenAbsoluteLocalTarget_WhenInsideAuthorizedOutputDirectory_UsesActualFormatWithoutWritingState()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var configuration = Configuration();
        configuration.OutputDirectory = Path.Combine(host.Root, "authorized-exports");
        var output = Path.Combine(configuration.OutputDirectory, "report.wrong");
        var before = Manifest(host);

        var result = await new SquadReports(host.Project, configuration).GovernanceAsync(new(OutputPath: output));

        Assert.Equal(Path.ChangeExtension(output, "html"), result.Path);
        Assert.True(File.Exists(result.Path));
        Unchanged(host, before);
    }

    [Fact]
    public async Task GivenExistingTarget_WhenReporting_RefusesToOverwriteCaseEvidence()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        LocalSource(host, "docs/existing.html", "Original case evidence.");

        await Assert.ThrowsAsync<IOException>(() =>
            new SquadReports(host.Project, Configuration()).GovernanceAsync(new(OutputPath: "docs/existing.html")));

        Assert.Equal("Original case evidence.", await File.ReadAllTextAsync(PathFor(host, "docs/existing.html")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("90d")]
    [InlineData("ALL")]
    public async Task GivenInvalidPeriod_WhenReporting_SurfacesErrorInsteadOfAllTimeFallback(string period)
    {
        using var host = new RuntimeTestHost();
        await Assert.ThrowsAsync<ArgumentException>(() => new SquadReports(host.Project, Configuration()).GovernanceAsync(new(period)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Project));
    }

    [Fact]
    public async Task GivenReaderInDIWithoutChatClient_WhenReporting_DoesNotAcquireOrResolveModels()
    {
        using var host = new RuntimeTestHost();
        _ = await Seed(host);
        var services = new ServiceCollection();
        services.AddHveSquad(configureHost: options => options.ProjectPath = host.Project);
        using var provider = services.BuildServiceProvider();
        var before = Manifest(host);

        var result = await provider.GetRequiredService<SquadRuntimeFactory>().CreateReports(Configuration()).GovernanceAsync();

        Assert.True(File.Exists(result.Path));
        Unchanged(host, before);
    }

    [Trait("Category", "ReleaseCompatibility")]
    [Fact]
    public async Task GivenActualAcquiredRelease_WhenExporting_ReadsRealMafResearchEvidenceAndPreservesState()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION") ?? SupportedSquadRelease.Current.Tag;
        Assert.False(string.IsNullOrWhiteSpace(version));
        using var releaseClient = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        using var host = new RuntimeTestHost();
        ConsumptionTestData.QueueResearch(host);
        using var runtime = await SquadRuntime.CreateAsync(host.Client, host.Options,
            ReleaseTestHttpClient.Source(version, releaseClient));
        var run = await runtime.RunAsync("Inspect the existing synthetic implementation.");
        Assert.True(run.Status == SquadRunStatus.ResearchCompleted, run.ResponseText);
        var before = Manifest(host);
        var calls = host.Client.Calls.Count;

        var document = await runtime.CreateReports(Configuration(renderer: true)).DocumentAsync(new("researcher research", "docx"));
        var report = await runtime.CreateReports(Configuration()).GovernanceAsync();

        Assert.Equal("docx", document.Format);
        Assert.Equal(run.Evidence.Count, report.Metrics!.RoleDispatches);
        Assert.Equal(calls, report.Metrics.ProviderAttempts);
        Assert.Equal(version, runtime.Provenance.Release!.Tag);
        ReportFormats.Verify("docx", await File.ReadAllBytesAsync(document.Path));
        Unchanged(host, before);
    }

    private static string WebDecode(string value) => System.Net.WebUtility.HtmlDecode(value);

    private sealed class ReportClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FaultyRenderer(string failure) : ISquadDocumentRenderer
    {
        private static readonly string[] Supported = ["pdf"];
        public string Binding => "original-faulty-renderer";
        public IReadOnlyList<string> Formats => Supported;
        public ValueTask<SquadDocumentRendering> RenderAsync(SquadDocumentRenderingRequest request, CancellationToken cancellationToken) =>
            failure switch
            {
                "wrong-hash" => ValueTask.FromResult(new SquadDocumentRendering(Encoding.UTF8.GetBytes("wrong"), "wrong-input")),
                "renamed-markdown" => ValueTask.FromResult(new SquadDocumentRendering(Encoding.UTF8.GetBytes("# Not a PDF"), request.InputSha256)),
                "no-reason" => ValueTask.FromResult(new SquadDocumentRendering(null, request.InputSha256)),
                "throws" => throw new IOException("Original local renderer failure."),
                _ => throw new InvalidOperationException("Unrecognized test failure."),
            };
    }

    private sealed class MutatingRenderer(string statePath) : ISquadDocumentRenderer
    {
        private static readonly string[] Supported = ["docx"];
        public string Binding => "original-mutating-renderer-fixture";
        public IReadOnlyList<string> Formats => Supported;
        public ValueTask<SquadDocumentRendering> RenderAsync(SquadDocumentRenderingRequest request, CancellationToken cancellationToken)
        {
            File.AppendAllText(statePath, "\n ");
            return new SquadLocalDocumentRenderer().RenderAsync(request, cancellationToken);
        }
    }
}
