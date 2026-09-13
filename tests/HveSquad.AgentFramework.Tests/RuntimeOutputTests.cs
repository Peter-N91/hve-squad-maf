using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeOutputTests
{
    [Fact]
    public async Task IterativeHostWritesVerifyLatestBytesAndPreserveEveryReceipt()
    {
        using var host = new RuntimeTestHost();
        var writes = RegisterSourceWriter(host);
        QueueProduction(host);
        QueueWrite(host, "src//result.txt", "First version");
        var finalPath = OperatingSystem.IsWindows() ? "src\\RESULT.txt" : "src\\result.txt";
        QueueWrite(host, finalPath, "Final version");
        host.QueueStage("source-writer");
        host.QueueStage("tester");
        host.Client.Text("Done");
        using var runtime = await CreateRuntimeAsync(host);
        var result = await runtime.RunAsync("Implement a local source change.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, writes.Count);
        Assert.Equal(2, host.Approvals.Count(a => a.Kind == SquadApprovalKind.ConsequentialTool));
        var evidence = Assert.Single(result.Evidence, e => e.Stage == "produce");
        var receipts = evidence.ExecutedHostTools!;
        Assert.Equal(2, receipts.Count);
        Assert.Equal(ByteHash(Encoding.UTF8.GetBytes("First version")), Assert.Single(receipts[0].Outputs!).Sha256);
        Assert.Equal(ByteHash(Encoding.UTF8.GetBytes("Final version")), Assert.Single(receipts[1].Outputs!).Sha256);
        var history = File.ReadAllText(Path.Combine(host.Project, evidence.HistoryPath));
        Assert.All(receipts, r => Assert.Contains(Assert.Single(r.Outputs!).Sha256, history, StringComparison.Ordinal));
        var persisted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(host.Project, ".copilot-tracking", "squad", "state.json")))!;
        Assert.Equal(2, persisted["Runs"][0].GetProperty("Evidence").EnumerateArray()
            .Single(e => e.GetProperty("Stage").GetString() == "produce").GetProperty("ExecutedHostTools").GetArrayLength());
        Assert.Contains("Final", File.ReadAllText(Path.Combine(host.Project, "src", "result.txt")), StringComparison.Ordinal);
        Assert.Contains(Assert.Single(receipts[1].Outputs!).Path, result.ResponseText, StringComparison.Ordinal);
        Assert.Empty(host.Client.Steps);
    }

    [Theory]
    [InlineData("before-next-call", 1)]
    [InlineData("during-approval", 1)]
    [InlineData("after-last-call", 2)]
    [InlineData("after-review", 2)]
    public async Task IterativeReceiptsCannotHideExternalTampering(string when, int expectedWrites)
    {
        using var host = new RuntimeTestHost();
        var writes = RegisterSourceWriter(host);
        void Tamper() => File.WriteAllText(Path.Combine(host.Project, "result.txt"), "Unapproved edit");
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (when == "during-approval" && approval.Kind == SquadApprovalKind.ConsequentialTool && writes.Count == 1)
            {
                Tamper();
            }
            return ValueTask.FromResult(true);
        };
        QueueProduction(host);
        QueueWrite(host, "result.txt", "First");
        if (when == "before-next-call")
        {
            host.Client.Steps.Enqueue((_, _) =>
            {
                Tamper();
                return RuntimeTestHost.Function("write_source", new() { ["path"] = "result.txt", ["content"] = "Second" });
            });
        }
        else
        {
            QueueWrite(host, "result.txt", "Second");
        }
        if (when == "after-last-call")
        {
            host.Client.Steps.Enqueue((_, _) =>
            {
                Tamper();
                return RuntimeTestHost.Function("write_artifact", new() { ["content"] = "Change report" });
            });
        }
        host.QueueStage("source-writer");
        host.QueueStage("tester");
        host.Client.Steps.Enqueue((_, _) =>
        {
            if (when == "after-review")
            {
                Tamper();
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"));
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a local change.");

        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("Host output evidence", result.ResponseText, StringComparison.Ordinal);
        Assert.Equal(expectedWrites, writes.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task DistinctDispatchesCannotSupersedeEachOthersOutputs(bool parentFirst, bool overlap)
    {
        using var host = new RuntimeTestHost();
        RegisterSourceWriter(host);
        host.WriteCharter("developer", children: "Source Worker");
        host.Write("agents/source-worker.agent.md", "---\nname: Source Worker\nuser-invocable: false\n---\nSynthetic source worker.");
        QueueProduction(host);
        if (parentFirst)
        {
            QueueWrite(host, "parent.txt", "Parent edit");
        }
        host.Client.Call("dispatch_child", new() { ["agentName"] = "Source Worker", ["childTask"] = "Edit the assigned local source file." });
        QueueWrite(host, overlap ? "parent.txt" : "child.txt", "Child edit");
        host.QueueStage("source-worker");
        if (!parentFirst)
        {
            QueueWrite(host, "parent.txt", "Parent edit");
        }
        host.QueueStage("source-parent");
        host.QueueStage("tester");
        host.Client.Text("Done");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a local source change.");

        Assert.True(result.Status == (overlap ? SquadRunStatus.Failed : SquadRunStatus.Completed), result.ResponseText);
        if (overlap)
        {
            Assert.Contains("Overlapping edits across distinct dispatches are unsupported", result.ResponseText, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        }
        else
        {
            Assert.Equal(2, result.Evidence.Count(e => e.Stage == "produce"));
            Assert.Empty(host.Client.Steps);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryHostOutputsUseExactBytesNotLossyUtf8(bool tamper)
    {
        using var host = new RuntimeTestHost();
        byte[] first = [0xff, 0, 0xc0];
        byte[] second = [0xfe, 0, 0xc1];
        Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
        {
            File.WriteAllBytes(Path.Combine(host.Project, "first.bin"), first);
            File.WriteAllBytes(Path.Combine(host.Project, "second.bin"), second);
            return (string[])["first.bin", "second.bin"];
        }, "write_binary"), "edit/project", ["developer"], SquadToolEffect.ProjectWrite,
            result => new(JsonSerializer.Deserialize<string[]>(JsonSerializer.Serialize(result))!)));
        QueueProduction(host);
        host.Client.Call("write_binary", []);
        host.QueueStage("binary-writer");
        host.QueueStage("tester");
        host.Client.Steps.Enqueue((_, _) =>
        {
            if (tamper)
            {
                File.WriteAllBytes(Path.Combine(host.Project, "first.bin"), second);
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"));
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement local binary outputs.");

        Assert.True(result.Status == (tamper ? SquadRunStatus.Failed : SquadRunStatus.Completed), result.ResponseText);
        var outputs = Assert.Single(Assert.Single(result.Evidence, e => e.Stage == "produce").ExecutedHostTools!).Outputs!;
        Assert.Equal(ByteHash(first), outputs[0].Sha256);
        Assert.Equal(ByteHash(second), outputs[1].Sha256);
        Assert.NotEqual(outputs[0].Sha256, outputs[1].Sha256);
        Assert.Empty(host.Client.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostBinaryOutputHasAFinite64MiBLimitIndependentOfTextLimit(bool exceedsLimit)
    {
        using var host = new RuntimeTestHost();
        const int maximumBytes = 64 * 1024 * 1024;
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
        {
            using var stream = File.Create(Path.Combine(host.Project, "large.bin"));
            stream.SetLength(maximumBytes + (exceedsLimit ? 1 : 0));
            return "large.bin";
        }, "write_binary"), "edit/project", ["developer"], SquadToolEffect.ProjectWrite, OutputPath));
        QueueProduction(host);
        host.Client.Call("write_binary", []);
        host.QueueStage("binary-writer");
        host.QueueStage("tester");
        host.Client.Text("Done");
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement a local binary output.");

        Assert.True(result.Status == (exceedsLimit ? SquadRunStatus.Failed : SquadRunStatus.Completed), result.ResponseText);
        if (exceedsLimit)
        {
            Assert.Contains("64 MiB", result.ResponseText, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        }
    }

    [Theory]
    [InlineData("no-tools", SquadRunStatus.Unsupported)]
    [InlineData("unattested", SquadRunStatus.Unsupported)]
    [InlineData("markdown", SquadRunStatus.Blocked)]
    [InlineData("deck", SquadRunStatus.Completed)]
    [InlineData("tampered-deck", SquadRunStatus.Failed)]
    public async Task PresenterRequiresHostProducedPowerPointNotAMarkdownClaim(string scenario, SquadRunStatus expected)
    {
        using var host = new RuntimeTestHost();
        AddPresenter(host);
        host.Options.Tools.Clear();
        var calls = 0;
        if (scenario != "no-tools")
        {
            host.Options.Tools.Add(new(AIFunctionFactory.Create(() =>
            {
                calls++;
                var path = scenario == "markdown" ? "slides.md" : "slides.pptx";
                if (scenario == "markdown")
                {
                    File.WriteAllText(Path.Combine(host.Project, path), "A slide outline, not a deck.");
                }
                else
                {
                    WritePowerPointFixture(Path.Combine(host.Project, path));
                }
                return path;
            }, "produce_deck"), "edit/project", ["presenter"], SquadToolEffect.ProjectWrite,
                scenario == "unattested" ? null : OutputPath));
        }
        QueueProduction(host, "presenter");
        if (scenario is not ("no-tools" or "unattested"))
        {
            host.Client.Call("produce_deck", []);
        }
        host.QueueStage("presenter");
        host.QueueStage("tester");
        host.Client.Steps.Enqueue((_, _) =>
        {
            if (scenario == "tampered-deck")
            {
                File.AppendAllText(Path.Combine(host.Project, "slides.pptx"), "Unapproved edit");
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"));
        });
        using var runtime = await CreateRuntimeAsync(host, presenter: true);
        var result = await runtime.RunAsync("Produce a local PowerPoint deck.");

        Assert.True(result.Status == expected, result.ResponseText);
        if (scenario is "no-tools" or "unattested")
        {
            Assert.Equal(0, calls);
            Assert.Empty(result.Evidence);
            Assert.Contains("Native skill-script execution/rendering is unavailable", result.ResponseText, StringComparison.Ordinal);
        }
        else if (scenario == "markdown")
        {
            Assert.Contains(".pptx", result.ResponseText, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Evidence, e => e.Stage == "review");
        }
        else
        {
            Assert.Equal(1, calls);
            var produced = Assert.Single(result.Evidence, e => e.Stage == "produce");
            Assert.DoesNotContain("<deck-slug>", produced.ArtifactPath, StringComparison.Ordinal);
            Assert.Contains($"native-{result.RunId}", produced.ArtifactPath, StringComparison.Ordinal);
            var output = Assert.Single(Assert.Single(produced.ExecutedHostTools!).Outputs!);
            Assert.Equal("slides.pptx", output.Path);
            if (scenario == "deck")
            {
                Assert.Equal(ByteHash(File.ReadAllBytes(Path.Combine(host.Project, output.Path))), output.Sha256);
                using var package = ZipFile.OpenRead(Path.Combine(host.Project, output.Path));
                Assert.NotNull(package.GetEntry("ppt/slides/slide1.xml"));
                Assert.Contains(output.Path, result.ResponseText, StringComparison.Ordinal);
            }
        }
    }

    private static List<string> RegisterSourceWriter(RuntimeTestHost host)
    {
        var writes = new List<string>();
        host.Options.Tools.Clear();
        host.Options.Tools.Add(new(AIFunctionFactory.Create((string path, string content) =>
        {
            var absolute = Path.Combine(host.Project, path.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, content);
            writes.Add(path);
            return path;
        }, "write_source"), "edit/project", ["developer"], SquadToolEffect.ProjectWrite, OutputPath));
        return writes;
    }

    private static SquadToolOutput OutputPath(object? result) =>
        new([JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(result))!]);

    private static void QueueProduction(RuntimeTestHost host, string role = "developer")
    {
        host.QueuePipeline(role);
        host.QueueStage("researcher");
        host.QueueStage("lead");
    }

    private static void QueueWrite(RuntimeTestHost host, string path, string content) =>
        host.Client.Call("write_source", new() { ["path"] = path, ["content"] = content });

    private static Task<SquadRuntime> CreateRuntimeAsync(RuntimeTestHost host, bool presenter = false)
    {
        var release = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_ROOT");
        if (string.IsNullOrWhiteSpace(release))
        {
            return host.CreateAsync();
        }
        host.Options.Profile = presenter ? "full" : "default";
        return SquadRuntime.CreateAsync(host.Client, host.Options,
            new DirectoryArtifactSource(ProjectArtifactSource.ForProject(release).Roots));
    }

    private static void AddPresenter(RuntimeTestHost host)
    {
        host.WriteCharter("presenter", tools: "edit/*");
        var coordinator = Path.Combine(host.Artifacts, "agents", "coordinator.agent.md");
        File.WriteAllText(coordinator, File.ReadAllText(coordinator)
            .Replace("agents:\n", "agents:\n  - Test presenter\n", StringComparison.Ordinal));
        var roster = Path.Combine(host.Artifacts, "instructions", "squad", "squad-roster.instructions.md");
        File.WriteAllText(roster, File.ReadAllText(roster)
            .Replace("## Squad Profiles", "| presenter | Test presenter | — | test deck | .copilot-tracking/ppt/<date>/<deck-slug>/ |\n\n## Squad Profiles", StringComparison.Ordinal)
            .Replace("| default | ", "| default | presenter, ", StringComparison.Ordinal));
    }

    private static string ByteHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WritePowerPointFixture(string path)
    {
        // A synthetic Open XML package produced by the test host, not a native rendering integration.
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WritePart("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
            </Types>
            """);
        WritePart("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """);
        WritePart("ppt/presentation.xml", """
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>
              <p:sldSz cx="9144000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/>
            </p:presentation>
            """);
        WritePart("ppt/_rels/presentation.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
            </Relationships>
            """);
        WritePart("ppt/slides/slide1.xml", """
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld><p:spTree>
                <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
              </p:spTree></p:cSld>
            </p:sld>
            """);

        void WritePart(string name, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(xml);
        }
    }
}
