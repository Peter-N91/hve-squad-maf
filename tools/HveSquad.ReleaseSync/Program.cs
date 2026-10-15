using System.Net.Http.Headers;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal static class Program
{
    internal static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0 || arguments is ["--help"])
            {
                Console.WriteLine("HveSquad.ReleaseSync: target | inspect | capture-contracts | capture-observations | verify-evidence | verify-conformance | verify | propose | status");
                Console.WriteLine("Options: --workspace PATH --output PATH --head SHA --repository OWNER/REPO --report PATH --trx PATH --observations PATH --package-proof PATH");
                Console.WriteLine("Flags: --evaluate-current --require-candidate --allow-write. Only propose --allow-write can mutate one candidate branch/PR.");
                return 0;
            }
            var command = arguments[0];
            if (command is not ("target" or "inspect" or "capture-contracts" or "capture-observations" or "verify-evidence" or "verify-conformance" or "verify" or "propose" or "status"))
            {
                throw new ArgumentException("Unknown release-sync command. Use --help.");
            }
            var options = Options.Parse(arguments.AsSpan(1));
            var workspace = Path.GetFullPath(options.Get("workspace") ?? Environment.CurrentDirectory);
            ProjectFiles.RejectLinks(workspace);
            var output = options.Get("output") ?? Path.Combine(workspace, "artifacts", "release-sync", "report.json");
            var candidatePath = Path.Combine(workspace, "compatibility", "candidate-release.json");
            var contractPath = Path.Combine(workspace, "compatibility", "hve-squad-contracts.json");
            var observationPath = options.Get("observations") ?? Path.Combine(workspace, "artifacts", "release-sync", "observations");
            var priorRelease = File.Exists(candidatePath)
                ? ReleaseEvaluation.ReadJson<ReleaseCandidate>(candidatePath) : null;
            if (priorRelease is not null)
            {
                ReleaseProposals.ValidateDescriptor(priorRelease);
            }
            var goldenRelease = priorRelease?.Baseline ??
                new SquadRelease(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha);
            var goldenPath = Path.Combine(workspace, "compatibility", "observations", goldenRelease.Tag + ".json");
            var head = options.Get("head");
            if (options.Flag("allow-write") && command != "propose")
            {
                throw new ArgumentException("--allow-write is valid only for the explicit propose command.");
            }
            if (command == "target")
            {
                var target = Target(candidatePath, options.Flag("require-candidate"));
                Write(output, target);
                Console.WriteLine("Exact validation target: " + target.Tag + " at " + target.CommitSha);
                return 0;
            }
            if (command == "capture-observations")
            {
                var observations = ReleaseEvaluation.ReadObservations(observationPath);
                if (observations.Length == 0)
                {
                    throw new InvalidDataException("No actual fixture observations are available to capture.");
                }
                Write(output, ReleaseObservations.Latest(observations).Values.OrderBy(item => item.ScenarioId, StringComparer.Ordinal).ToArray());
                Console.WriteLine("Captured actual native fixture observations; no answer was judged.");
                return 0;
            }
            if (command == "verify-evidence")
            {
                ReleaseEvaluation.VerifyEvidence(Target(candidatePath, options.Flag("require-candidate")),
                    ReleaseEvaluation.ReadObservations(observationPath), options.Required("trx"));
                Console.WriteLine("Saved actual release evidence is complete and identity-bound. This does not verify a clean candidate head, reviewed contracts or promotion permission.");
                return 0;
            }
            if (command == "verify-conformance")
            {
                if (head is not null)
                {
                    await ReleaseEvaluation.AssertCheckoutAsync(workspace, head, default).ConfigureAwait(false);
                }
                var report = ReleaseConformance.Verify(workspace, Target(candidatePath, options.Flag("require-candidate")),
                    ReleaseEvaluation.ReadObservations(observationPath), options.Required("trx"), options.Required("package-proof"), head);
                if (head is not null)
                {
                    await ReleaseEvaluation.AssertCheckoutAsync(workspace, head, default).ConfigureAwait(false);
                }
                Write(output, report);
                Console.WriteLine("Every individually mapped native assertion and the actual isolated package consumer passed. Answer quality, human review and deployment remain separate.");
                return 0;
            }
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            var token = command is "propose" or "status"
                ? Environment.GetEnvironmentVariable("GH_TOKEN")
                : Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var evaluation = new ReleaseEvaluation(client);
            if (command is "propose" or "status")
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new UnauthorizedAccessException("Set a job-scoped GitHub token with the explicit repository rights; credentials are not provisioned by this tool.");
                }
                var report = ReleaseEvaluation.ReadJson<ReleaseEvaluationReport>(options.Required("report"));
                var proposals = new ReleaseProposals(client, options.Required("repository"));
                var result = command == "propose"
                    ? await proposals.ProposeAsync(report, options.Flag("allow-write"), default).ConfigureAwait(false)
                    : await proposals.StatusAsync(report, default).ConfigureAwait(false);
                Write(output, result);
                Console.WriteLine("Release proposal: " + result.Status + ". " + result.Diagnostic);
                return result.Status is "checks-failed" or "checks-unavailable" or "proposal-missing" or
                    "closed-requires-maintainer" or "merged-baseline-unpromoted" ? 1 : 0;
            }
            if (command == "verify")
            {
                await ReleaseEvaluation.AssertCheckoutAsync(workspace, options.Required("head"), default).ConfigureAwait(false);
            }
            var golden = File.Exists(contractPath) ? ReleaseEvaluation.ReadJson<ReleaseContractSnapshot>(contractPath) : null;
            var requested = command == "verify" ? Target(candidatePath, options.Flag("require-candidate"))
                : command == "capture-contracts" ? new SquadRelease(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha) : null;
            var observed = ReleaseEvaluation.ReadObservations(observationPath);
            var baselineObserved = ReleaseEvaluation.ReadObservations(goldenPath);
            if (baselineObserved.Any(observation => observation.ReleaseTag != goldenRelease.Tag ||
                observation.ReleaseCommitSha != goldenRelease.CommitSha))
            {
                throw new InvalidDataException("The golden observations do not belong to the exact prior last-good release.");
            }
            var inspected = await evaluation.InspectAsync(command == "capture-contracts" ? null : golden, head,
                options.Flag("evaluate-current"), requested, observed, baselineObserved).ConfigureAwait(false);
            if (command == "verify")
            {
                await ReleaseEvaluation.AssertCheckoutAsync(workspace, options.Required("head"), default).ConfigureAwait(false);
            }
            if (command == "capture-contracts")
            {
                Write(output, inspected.Contracts ?? throw new InvalidDataException("No acquired contract snapshot was produced."));
                Console.WriteLine("Captured the exact supported artifact/dependency/assertion contract. Human review is still required.");
                return 0;
            }
            Write(output, inspected);
            Console.WriteLine("Release evaluation: " + inspected.Action + "; promotion blockers: " + inspected.PromotionBlockers.Count +
                "; observations: " + inspected.ObservationStatus + "; answer: " + inspected.AnswerStatus + ".");
            if (command == "verify")
            {
                ReleaseEvaluation.VerifyEvidence(inspected.Candidate, observed, options.Required("trx"));
                _ = ReleaseConformance.Verify(workspace, inspected.Candidate, observed,
                    options.Required("trx"), options.Required("package-proof"), head);
                if (inspected.PromotionBlockers.Count != 0)
                {
                    foreach (var blocker in inspected.PromotionBlockers)
                    {
                        Console.Error.WriteLine(blocker);
                    }
                    return 1;
                }
                Console.WriteLine("Exact candidate-head mechanical checks passed. Semantic drift is advisory; human review/merge remains separate.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or JsonException or SquadArtifactsNotFoundException or SquadHumanTransportException)
        {
            Console.Error.WriteLine("Release sync failed: " + exception.Message);
            return 1;
        }
    }

    internal static SquadRelease Target(string path, bool requireCandidate)
    {
        if (!File.Exists(path))
        {
            if (requireCandidate)
            {
                throw new InvalidDataException("The automation candidate branch is missing its exact proposed release file; a baseline run cannot substitute.");
            }
            return new(SupportedSquadRelease.Current.Tag, SupportedSquadRelease.Current.CommitSha);
        }
        var descriptor = ReleaseEvaluation.ReadJson<ReleaseCandidate>(path);
        ReleaseProposals.ValidateDescriptor(descriptor);
        return descriptor.Release;
    }

    internal static void Write<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        if (Path.GetExtension(path) != ".json")
        {
            throw new ArgumentException("Generated release-sync artifacts must have an explicit JSON output path.", nameof(path));
        }
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("Output needs a parent directory.", nameof(path));
        for (var current = parent; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                ProjectFiles.RejectLinks(current);
            }
        }
        if (File.Exists(path))
        {
            ProjectFiles.RejectLinks(path);
        }
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, JsonSerializer.Serialize(value, ReleaseContracts.Json) + "\n");
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        internal string? Get(string key) => _values.GetValueOrDefault(key);
        internal bool Flag(string key) => _values.ContainsKey(key);
        internal string Required(string key) => Get(key) ?? throw new ArgumentException("Missing --" + key + ".");

        internal static Options Parse(ReadOnlySpan<string> arguments)
        {
            var result = new Options();
            for (var index = 0; index < arguments.Length; index++)
            {
                var argument = arguments[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Release-sync options must be explicit structural arguments.");
                }
                var key = argument[2..];
                if (key is "evaluate-current" or "allow-write" or "require-candidate")
                {
                    if (!result._values.TryAdd(key, null))
                    {
                        throw new ArgumentException("A release-sync flag was repeated.");
                    }
                }
                else if (key is "workspace" or "output" or "head" or "repository" or "report" or "trx" or "observations" or "package-proof")
                {
                    if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) ||
                        arguments[index].StartsWith("--", StringComparison.Ordinal) || !result._values.TryAdd(key, arguments[index]))
                    {
                        throw new ArgumentException("A release-sync option has a missing or repeated value.");
                    }
                }
                else
                {
                    throw new ArgumentException("Unknown release-sync option. Use --help.");
                }
            }
            return result;
        }
    }
}
