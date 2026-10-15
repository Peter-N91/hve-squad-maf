using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Sample;

internal sealed record NativeHelperCommand(
    string Kind, string Operation, string Project, string SquadRoot,
    string Format = "md", string Period = "all", bool IncludePrivate = false, string? Request = null,
    string? OutputPath = null, string? Role = null, string? NotePath = null, string? Focus = null,
    string? Candidate = null, SquadLearningTarget? Target = null, string? SnapshotPath = null,
    string? PreparedPath = null, bool GitHubConnector = false)
{
    private static readonly string[] Selectors = ["--report", "--memory", "--learn"];

    internal static NativeHelperCommand Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Native helpers require distinct named options with nonempty values.");
            }
        }
        string Require(string key) => values.TryGetValue(key, out var value) ? value :
            throw new ArgumentException("Missing native helper option " + key + ".");
        var selectors = Selectors.Where(values.ContainsKey).ToArray();
        if (selectors.Length != 1)
        {
            throw new ArgumentException("Select exactly one native report, memory or human-only learning command.");
        }
        var kind = selectors[0];
        var operation = Require(kind);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { kind, "--project", "--squad-root" };
        var result = new NativeHelperCommand(kind, operation, Require("--project"),
            values.GetValueOrDefault("--squad-root") ?? ".copilot-tracking/squad");
        if (kind == "--report")
        {
            if (operation is not ("document" or "governance"))
            {
                throw new ArgumentException("Report operation must be document or governance.");
            }
            allowed.UnionWith(["--output", "--include-private"]);
            allowed.UnionWith(operation == "document" ? ["--request", "--format"] : ["--period"]);
            var format = values.GetValueOrDefault("--format") ?? "md";
            var period = values.GetValueOrDefault("--period") ?? "all";
            var includePrivate = values.GetValueOrDefault("--include-private") ?? "false";
            if (format is not ("md" or "html" or "pdf" or "docx") || period is not ("all" or "30d" or "7d") ||
                includePrivate is not ("true" or "false"))
            {
                throw new ArgumentException("Use format md/html/pdf/docx, period all/30d/7d and include-private true/false.");
            }
            result = result with
            {
                Request = operation == "document" ? Require("--request") : null,
                Format = format,
                Period = period,
                IncludePrivate = includePrivate == "true",
                OutputPath = values.GetValueOrDefault("--output"),
            };
        }
        else if (kind == "--memory")
        {
            if (operation is not ("read" or "remember"))
            {
                throw new ArgumentException("Memory operation must be read or remember.");
            }
            allowed.Add("--role");
            if (operation == "remember")
            {
                allowed.Add("--note");
            }
            result = result with { Role = Require("--role"), NotePath = operation == "remember" ? Require("--note") : null };
        }
        else
        {
            if (operation is not ("discover" or "prepare" or "promote" or "reconcile"))
            {
                throw new ArgumentException("Learning operation must be discover, prepare, promote or reconcile.");
            }
            if (operation == "discover")
            {
                allowed.Add("--focus");
                result = result with { Focus = values.GetValueOrDefault("--focus") };
            }
            else
            {
                allowed.UnionWith(["--target", "--repository", "--destination", "--prepared", "--learning-connector", "--fork-organization"]);
                var target = Require("--target");
                if (target is not ("upstream" or "tenant"))
                {
                    throw new ArgumentException("Learning target must be upstream or tenant.");
                }
                var connector = values.GetValueOrDefault("--learning-connector");
                if (connector is not (null or "github") || operation == "reconcile" && connector is null)
                {
                    throw new ArgumentException("Only the explicit github connector is implemented; reconciliation requires that original connector.");
                }
                var repository = Require("--repository").ToLowerInvariant();
                var forkOrganization = values.GetValueOrDefault("--fork-organization") ?? "false";
                if (forkOrganization is not ("true" or "false") ||
                    target != "upstream" && values.ContainsKey("--fork-organization"))
                {
                    throw new ArgumentException("Only upstream learning accepts --fork-organization true|false; it remains a separately authorized repository proposal.");
                }
                result = result with
                {
                    Target = new(target, repository, target == "upstream" ? Require("--destination").ToLowerInvariant() : repository,
                        ForkOrganization: forkOrganization == "true"),
                    PreparedPath = Require("--prepared"),
                    GitHubConnector = connector == "github",
                };
                if (target == "tenant" && values.ContainsKey("--destination"))
                {
                    throw new ArgumentException("Tenant learning uses the same exact private repository, not a fork destination.");
                }
                if (operation == "prepare")
                {
                    allowed.UnionWith(["--candidate", "--snapshot"]);
                    result = result with
                    {
                        Candidate = Require("--candidate"),
                        SnapshotPath = connector is null ? Require("--snapshot") : null,
                    };
                    if (connector is not null && values.ContainsKey("--snapshot"))
                    {
                        throw new ArgumentException("A registered connector must re-read the real target; it cannot substitute a supplied snapshot.");
                    }
                }
            }
        }
        var invalid = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (invalid is not null)
        {
            throw new ArgumentException("Unknown or inapplicable native helper option " + invalid + ". Capabilities cannot be granted by JSON or prose.");
        }
        return result;
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Native helpers: no IChatClient, release acquisition, initialization or state repair:");
        writer.WriteLine("  --report document --project <case> --request <focus> [--format md|html|pdf|docx] [--output <relative-path>]");
        writer.WriteLine("  --report governance --project <case> [--period all|30d|7d] [--include-private true|false]");
        writer.WriteLine("  --memory read --project <case> --role <installed-role>");
        writer.WriteLine("  --memory remember --project <case> --role <installed-role> --note <case-relative-note.json>");
        writer.WriteLine("  --learn discover --project <case> [--focus <descriptive-focus>]");
        writer.WriteLine("  --learn prepare --project <case> --candidate <discovered-id> --target tenant --repository <owner/repo> --snapshot <case-relative-snapshot.json> --prepared <new-case-relative-prepared.json>");
        writer.WriteLine("  --learn promote --project <case> --target tenant --repository <owner/repo> --prepared <original-prepared.json>");
        writer.WriteLine("  --learn reconcile --project <case> --target tenant --repository <owner/repo> --prepared <original-prepared.json> --learning-connector github");
        writer.WriteLine("All helpers accept --squad-root for an exact native member. Reports register real local OOXML/WinAnsi renderers; unsupported PDF content returns an explicit Markdown fallback.");
        writer.WriteLine("Each case/source/output/memory disclosure needs an actual console yes. Learning additionally requires explicit human initiation and separate sanitization/repository/action approvals.");
        writer.WriteLine("Human-only learning requires an interactive local console; redirected/piped approvals are denied before reading the case.");
        writer.WriteLine("No connector means prepared-only, never publication. For live GitHub, use --learning-connector github on BOTH prepare and promote/reconcile, with host-owned HVE_SQUAD_LEARNING_GITHUB_TOKEN.");
        writer.WriteLine("Upstream learning requires --target upstream --repository <source-owner/name> --destination <fork-owner/same-name>; --fork-organization true explicitly proposes an organization-owned fork.");
        writer.WriteLine("Remote effects remain individually approved; reconciliation is read-only and never replays.");
        writer.WriteLine("The process-account console is a local reference, not multi-tenant application authentication. Do not redirect private helper output into public CI logs.");
        writer.WriteLine("Exit codes: 0 verified/prepared, 1 stopped/failure, 2 invalid arguments, 3 explicit fallback/reconciliation required, 130 cancelled.");
    }
}
