using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal sealed record ReleaseCandidate(int SchemaVersion, string Producer, SquadRelease Baseline,
    SquadRelease Release, string HveCoreCommitSha, string ContractFingerprint, string EvaluationHead,
    IReadOnlyDictionary<string, int> DriftSummary, IReadOnlyList<string> PromotionBlockers,
    string ObservationStatus, string AnswerStatus = "not judged");

internal sealed record ReleaseProposalResult(string Status, string Diagnostic, SquadRelease Release,
    string? Head = null, long? PullRequest = null, string? Url = null);

internal sealed partial class ReleaseProposals
{
    internal const string CandidatePath = "compatibility/candidate-release.json";
    internal const string Producer = "hve-squad-release-sync/v1";
    internal const string WorkflowPath = ".github/workflows/release-candidate.yml";
    private readonly HumanChannelHttp _http;
    private readonly HttpClient _client;
    private readonly string _repository;
    private readonly string _owner;

    internal ReleaseProposals(HttpClient client, string repository)
    {
        ValidateRepository(repository);
        _repository = repository;
        _owner = repository.Split('/')[0];
        _client = client;
        _http = new(client, new Uri("https://api.github.com/"));
    }

    internal async Task<ReleaseProposalResult> ProposeAsync(ReleaseEvaluationReport report, bool allowWrite, CancellationToken token)
    {
        ValidateReport(report);
        if (report.Candidate == report.Baseline)
        {
            return new("up-to-date", "The verified production pin is unchanged; no branch or PR was written.", report.Candidate);
        }
        if (!allowWrite)
        {
            throw new UnauthorizedAccessException("Opening a proposal requires explicit propose --allow-write and repository contents/pull-request write rights.");
        }
        var descriptor = Descriptor(report);
        ValidateDescriptor(descriptor);
        var content = JsonSerializer.Serialize(descriptor, ReleaseContracts.Json) + "\n";
        await AssertOfficialIdentityAsync(report.Candidate, token).ConfigureAwait(false);
        var repository = await RequiredAsync(HttpMethod.Get, "repos/" + _repository, null, token).ConfigureAwait(false);
        if (Text(repository, "full_name") != _repository || Boolean(repository, "archived") || Boolean(repository, "disabled"))
        {
            throw new InvalidDataException("The proposal repository is unavailable or has a different provider identity.");
        }
        var baseBranch = Text(repository, "default_branch");
        var baseHead = await RefAsync(baseBranch, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The proposal repository has no exact default-branch head.");
        if (baseHead != report.ImplementationHead)
        {
            throw new InvalidDataException("The default branch moved after read-only evaluation. Rerun the trusted evaluation; no stale proposal was written.");
        }
        var branch = Branch(report.Candidate);
        var existing = await PullsAsync(branch, baseBranch, token).ConfigureAwait(false);
        if (existing.Length != 0)
        {
            return await ExistingAsync(existing.Single(), report, token).ConfigureAwait(false);
        }
        var head = await RefAsync(branch, token).ConfigureAwait(false);
        if (head is null)
        {
            var created = await RequiredAsync(HttpMethod.Post, "repos/" + _repository + "/git/refs",
                new { @ref = "refs/heads/" + branch, sha = baseHead }, token).ConfigureAwait(false);
            if (Text(created, "ref") != "refs/heads/" + branch || Text(Object(created, "object"), "sha") != baseHead)
            {
                throw new InvalidDataException("The provider did not prove the exact new candidate branch.");
            }
            head = await RefAsync(branch, token).ConfigureAwait(false);
            if (head != baseHead)
            {
                throw new InvalidDataException("The created candidate branch has an unverified head.");
            }
        }
        var observed = await _http.SendAsync(HttpMethod.Get, "repos/" + _repository + "/contents/" +
            CandidatePath + "?ref=" + head, null, token, allowNotFound: true).ConfigureAwait(false);
        if (head != baseHead)
        {
            if (observed is null)
            {
                throw new InvalidDataException("The deterministic candidate branch collides with unrelated work; no overwrite or auto-suffix is permitted.");
            }
            ValidateExistingDescriptor(ReleaseEvaluation.DecodeFile(observed.Value, CandidatePath), report);
            await VerifyCommitAsync(head, baseHead, ReleaseEvaluation.DecodeFile(observed.Value, CandidatePath),
                report.Candidate, token).ConfigureAwait(false);
        }
        else
        {
            head = await WriteAsync(branch, baseHead, content, report.Candidate, token).ConfigureAwait(false);
        }
        var createdPr = await RequiredAsync(HttpMethod.Post, "repos/" + _repository + "/pulls", new
        {
            title = "chore(compatibility): evaluate HVE Squad " + report.Candidate.Tag,
            head = branch,
            @base = baseBranch,
            body = Body(descriptor),
            draft = true,
            maintainer_can_modify = true,
        }, token).ConfigureAwait(false);
        var identity = ReadPull(createdPr, branch, baseBranch);
        if (identity.Head != head || identity.State != "open")
        {
            throw new InvalidDataException("The provider did not prove an open proposal at the exact candidate head.");
        }
        var readBack = await RequiredAsync(HttpMethod.Get, "repos/" + _repository + "/pulls/" +
            identity.Number.ToString(CultureInfo.InvariantCulture), null, token).ConfigureAwait(false);
        var verified = ReadPull(readBack, branch, baseBranch);
        if (verified != identity)
        {
            throw new InvalidDataException("The new candidate PR changed before independent verification; inspect the provider outcome without replaying mutation.");
        }
        return await ChecksAsync(identity, report.Candidate, token).ConfigureAwait(false);
    }

    internal async Task<ReleaseProposalResult> StatusAsync(ReleaseEvaluationReport report, CancellationToken token)
    {
        ValidateReport(report);
        if (report.Candidate == report.Baseline)
        {
            return new("up-to-date", "No upgrade proposal is required. Conformance remains a separate acceptance status.", report.Candidate);
        }
        await AssertOfficialIdentityAsync(report.Candidate, token).ConfigureAwait(false);
        var repository = await RequiredAsync(HttpMethod.Get, "repos/" + _repository, null, token).ConfigureAwait(false);
        var existing = await PullsAsync(Branch(report.Candidate), Text(repository, "default_branch"), token).ConfigureAwait(false);
        return existing.Length == 0
            ? new("proposal-missing", "No candidate PR exists. Explicit write permission/setup is required; no checks were inferred.", report.Candidate)
            : await ExistingAsync(existing.Single(), report, token).ConfigureAwait(false);
    }

    internal static ReleaseCandidate Descriptor(ReleaseEvaluationReport report)
    {
        ValidateReport(report);
        var contracts = report.Contracts ?? throw new InvalidDataException("A proposal needs actual acquired candidate contracts, not release notes.");
        return new(1, Producer, report.Baseline, report.Candidate, contracts.HveCoreCommitSha,
            contracts.Fingerprint, report.ImplementationHead ?? throw new InvalidDataException("A proposal needs the trusted default-branch code head."),
            report.Drift.GroupBy(delta => delta.Category, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            report.PromotionBlockers.ToArray(), report.ObservationStatus);
    }

    internal static void ValidateDescriptor(ReleaseCandidate descriptor)
    {
        ReleaseContracts.ValidateIdentity(descriptor.Baseline);
        ReleaseContracts.ValidateIdentity(descriptor.Release);
        if (descriptor.SchemaVersion != 1 || descriptor.Producer != Producer ||
            !ReleaseArtifactSource.IsCommit(descriptor.HveCoreCommitSha) ||
            !ReleaseArtifactSource.IsCommit(descriptor.EvaluationHead) || !Hash().IsMatch(descriptor.ContractFingerprint) ||
            descriptor.DriftSummary is null || descriptor.DriftSummary.Any(pair =>
                pair.Key is not ("dependency" or "resource" or "role" or "mode" or "schema" or "assertion") || pair.Value < 0) ||
            descriptor.PromotionBlockers is null || descriptor.PromotionBlockers.Count > 32 ||
            descriptor.PromotionBlockers.Any(value => value.Length is 0 or > 512 || value.Any(char.IsControl)) ||
            descriptor.ObservationStatus is not ("not observed" or "unbaselined" or "compared") || descriptor.AnswerStatus != "not judged")
        {
            throw new InvalidDataException("The candidate descriptor has an unknown identity, schema or unsafe status.");
        }
    }

    internal static void ValidateRepository(string repository)
    {
        if (string.IsNullOrEmpty(repository) || !RepositoryName().IsMatch(repository))
        {
            throw new ArgumentException("A repository must be an explicit structural owner/name.", nameof(repository));
        }
    }

    private async Task<ReleaseProposalResult> ExistingAsync(JsonElement value, ReleaseEvaluationReport report, CancellationToken token)
    {
        var identity = ReadPull(value, Branch(report.Candidate), Text(Object(value, "base"), "ref"));
        var file = await RequiredAsync(HttpMethod.Get, "repos/" + _repository + "/contents/" + CandidatePath +
            "?ref=" + identity.Head, null, token).ConfigureAwait(false);
        ValidateExistingDescriptor(ReleaseEvaluation.DecodeFile(file, CandidatePath), report);
        if (identity.State == "closed")
        {
            return new(identity.Merged ? "merged-baseline-unpromoted" : "closed-requires-maintainer",
                "This release already has a closed proposal. Review its disposition; automation will not duplicate, reopen or change the production pin.",
                report.Candidate, identity.Head, identity.Number, identity.Url);
        }
        return await ChecksAsync(identity, report.Candidate, token).ConfigureAwait(false);
    }

    private async Task<ReleaseProposalResult> ChecksAsync(PullIdentity pull, SquadRelease release, CancellationToken token)
    {
        ReleaseProposalResult Result(string status, string diagnostic) => new(status, diagnostic, release, pull.Head, pull.Number, pull.Url);
        var runs = await _http.SendAsync(HttpMethod.Get, "repos/" + _repository +
            "/actions/workflows/release-candidate.yml/runs?head_sha=" + pull.Head + "&event=pull_request&per_page=100",
            null, token, allowNotFound: true).ConfigureAwait(false);
        if (runs is null)
        {
            return Result("checks-unavailable", "Install/enable the candidate workflow and grant read access to Actions. Promotion is blocked.");
        }
        var allRuns = Array(runs.Value, "workflow_runs");
        if (!runs.Value.TryGetProperty("total_count", out var count) || !count.TryGetInt32(out var total) ||
            total < 0 || total > 100 || allRuns.Length != total)
        {
            throw new InvalidDataException("Candidate workflow runs exceed the complete bounded query; partial status cannot permit promotion.");
        }
        var matching = allRuns.Where(run => HumanChannelHttp.Text(run, "head_sha") == pull.Head &&
            HumanChannelHttp.Text(run, "event") == "pull_request" && HumanChannelHttp.Text(run, "path") == WorkflowPath &&
            Array(run, "pull_requests").Any(pr => HumanChannelHttp.Id(pr, "number") == pull.Number))
            .OrderByDescending(run => HumanChannelHttp.Id(run) ?? 0).ToArray();
        if (matching.Length == 0)
        {
            return Result("checks-missing", "No run belongs to this exact candidate head/PR. For GITHUB_TOKEN bot PRs select Approve workflows to run in the merge box; base-branch green runs do not count.");
        }
        var latest = matching[0];
        var status = Text(latest, "status");
        var conclusion = HumanChannelHttp.Text(latest, "conclusion");
        if (status == "waiting" || conclusion == "action_required")
        {
            return Result("approval-pending", "A repository writer must select Approve workflows to run. Pending approval is not compatibility success.");
        }
        if (status != "completed")
        {
            return Result("checks-running", "Candidate checks are queued or executing; promotion remains blocked until all required jobs complete.");
        }
        if (conclusion != "success")
        {
            return Result("checks-failed", "The exact candidate workflow failed, was cancelled or skipped. Fix/re-run it; no production promotion occurred.");
        }
        var runId = HumanChannelHttp.Id(latest) ?? throw new InvalidDataException("An actual candidate workflow needs a provider run ID.");
        var jobs = await RequiredAsync(HttpMethod.Get, "repos/" + _repository + "/actions/runs/" +
            runId.ToString(CultureInfo.InvariantCulture) + "/jobs?filter=latest&per_page=100", null, token).ConfigureAwait(false);
        var rows = Array(jobs, "jobs");
        if (!jobs.TryGetProperty("total_count", out var jobCount) || !jobCount.TryGetInt32(out var totalJobs) ||
            totalJobs != rows.Length || totalJobs > 100)
        {
            throw new InvalidDataException("The candidate job inventory is incomplete.");
        }
        foreach (var name in new[] { "candidate-linux", "candidate-windows" })
        {
            var required = rows.Where(job => HumanChannelHttp.Text(job, "name") == name).ToArray();
            if (required.Length != 1 || Text(required[0], "status") != "completed" ||
                HumanChannelHttp.Text(required[0], "conclusion") != "success" ||
                HumanChannelHttp.Text(required[0], "head_sha") != pull.Head)
            {
                return Result("checks-failed", "Both Windows and Linux jobs must prove successful execution at the exact candidate head; missing/mismatched jobs do not count.");
            }
            foreach (var stepName in new[]
            {
                "Build the exact checked-out head",
                "Run candidate release contracts",
                "Preserve exact legacy behavior",
                "Execute the isolated actual-package consumer",
                "Verify every individual native assertion",
                "Verify candidate evidence and reviewed contracts",
            })
            {
                var steps = Array(required[0], "steps").Where(step => HumanChannelHttp.Text(step, "name") == stepName).ToArray();
                if (steps.Length != 1 || HumanChannelHttp.Text(steps[0], "status") != "completed" ||
                    HumanChannelHttp.Text(steps[0], "conclusion") != "success")
                {
                    return Result("checks-failed", "A required parity/evidence step was unexecuted, skipped or failed; a green surrounding job cannot substitute.");
                }
            }
        }
        return Result("checks-passed", "Actual candidate-head Windows/Linux parity and reviewed-contract gates passed. Semantic drift remains advisory; human review/merge is still required. No auto-merge, publication or deployment is performed.");
    }

    private async Task<string> WriteAsync(string branch, string expectedHead, string content, SquadRelease release, CancellationToken token)
    {
        var message = "chore(compatibility): propose HVE Squad " + release.Tag;
        var input = new
        {
            branch = new { repositoryNameWithOwner = _repository, branchName = branch },
            expectedHeadOid = expectedHead,
            clientMutationId = "hve-squad-release-" + release.Tag,
            message = new
            {
                headline = message,
                body = "\U0001f4e6 - Generated by Copilot\n\nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>",
            },
            fileChanges = new { additions = new[] { new { path = CandidatePath, contents = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) } } },
        };
        var result = await RequiredAsync(HttpMethod.Post, "graphql", new
        {
            query = "mutation($input:CreateCommitOnBranchInput!){createCommitOnBranch(input:$input){commit{oid}}}",
            variables = new { input },
        }, token).ConfigureAwait(false);
        if (result.TryGetProperty("errors", out _))
        {
            throw new InvalidDataException("GitHub did not accept the exact expected-head candidate commit; inspect branch/permissions without replaying an uncertain write.");
        }
        var head = Text(Object(Object(Object(result, "data"), "createCommitOnBranch"), "commit"), "oid");
        if (!ReleaseArtifactSource.IsCommit(head))
        {
            throw new InvalidDataException("GitHub returned an invalid candidate commit.");
        }
        await VerifyCommitAsync(head, expectedHead, content, release, token).ConfigureAwait(false);
        return head;
    }

    private async Task VerifyCommitAsync(string head, string expectedHead, string content, SquadRelease release, CancellationToken token)
    {
        var message = "chore(compatibility): propose HVE Squad " + release.Tag;
        var commit = await RequiredAsync(HttpMethod.Get, "repos/" + _repository + "/commits/" + head, null, token).ConfigureAwait(false);
        var parents = Array(commit, "parents");
        var files = Array(commit, "files");
        if (Text(commit, "sha") != head || parents.Length != 1 || Text(parents[0], "sha") != expectedHead ||
            Text(Object(commit, "commit"), "message").Split('\n')[0] != message ||
            files.Length != 1 || Text(files[0], "filename") != CandidatePath ||
            Text(files[0], "status") is not ("added" or "modified"))
        {
            throw new InvalidDataException("The candidate commit does not prove one original parent and the sole permitted metadata file.");
        }
        var file = await RequiredAsync(HttpMethod.Get, "repos/" + _repository + "/contents/" + CandidatePath +
            "?ref=" + head, null, token).ConfigureAwait(false);
        if (ReleaseEvaluation.DecodeFile(file, CandidatePath) != content || await RefAsync(Branch(release), token).ConfigureAwait(false) != head)
        {
            throw new InvalidDataException("The provider did not prove the exact candidate file bytes and branch head.");
        }
    }

    private async Task<JsonElement[]> PullsAsync(string branch, string baseBranch, CancellationToken token)
    {
        var result = await _http.ListAsync("repos/" + _repository + "/pulls?state=all&head=" +
            Uri.EscapeDataString(_owner + ":" + branch) + "&base=" + Uri.EscapeDataString(baseBranch), token).ConfigureAwait(false);
        if (result.Length > 1)
        {
            throw new InvalidDataException("More than one proposal owns this release/update branch; resolve duplicates manually.");
        }
        foreach (var pull in result)
        {
            _ = ReadPull(pull, branch, baseBranch);
        }
        return result;
    }

    private async Task<string?> RefAsync(string branch, CancellationToken token)
    {
        var result = await _http.SendAsync(HttpMethod.Get, "repos/" + _repository + "/git/ref/heads/" +
            Uri.EscapeDataString(branch), null, token, allowNotFound: true).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        var head = Text(Object(result.Value, "object"), "sha");
        if (Text(result.Value, "ref") != "refs/heads/" + branch || !ReleaseArtifactSource.IsCommit(head))
        {
            throw new InvalidDataException("A proposal branch ref has a different identity or invalid commit.");
        }
        return head;
    }

    private PullIdentity ReadPull(JsonElement value, string branch, string baseBranch)
    {
        var number = HumanChannelHttp.Id(value, "number") ?? throw new InvalidDataException("A real proposal needs a provider PR number.");
        var head = Object(value, "head");
        var @base = Object(value, "base");
        var sha = Text(head, "sha");
        var url = Text(value, "html_url");
        var state = Text(value, "state");
        if (Text(head, "ref") != branch || Text(@base, "ref") != baseBranch ||
            Text(Object(head, "repo"), "full_name") != _repository || Text(Object(@base, "repo"), "full_name") != _repository ||
            !ReleaseArtifactSource.IsCommit(sha) || state is not ("open" or "closed") ||
            url != "https://github.com/" + _repository + "/pull/" + number.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException("The proposal provider head/base/repository/URL identity is invalid.");
        }
        return new(number, sha, state, url, HumanChannelHttp.Time(value, "merged_at") is not null);
    }

    private async Task AssertOfficialIdentityAsync(SquadRelease release, CancellationToken token)
    {
        var actual = await new ReleaseArtifactSource(release.Tag, httpClient: _client).ResolveReleaseAsync(token).ConfigureAwait(false);
        if (actual != release)
        {
            throw new InvalidDataException("The proposed official tag moved. No proposal mutation or promotion is permitted.");
        }
    }

    private async Task<JsonElement> RequiredAsync(HttpMethod method, string path, object? body, CancellationToken token) =>
        await _http.SendAsync(method, path, body, token).ConfigureAwait(false)
        ?? throw new InvalidDataException("GitHub omitted required release-proposal evidence.");

    private static void ValidateReport(ReleaseEvaluationReport report)
    {
        ReleaseContracts.ValidateIdentity(report.Baseline);
        ReleaseContracts.ValidateIdentity(report.Candidate);
        if (report.SchemaVersion != 1 || !SupportedSquadRelease.Current.Matches(report.Baseline) ||
            report.Action is not ("up-to-date" or "current-evaluated" or "candidate") ||
            report.ImplementationHead is not null && !ReleaseArtifactSource.IsCommit(report.ImplementationHead) ||
            report.PromotionBlockers is null || report.Drift is null || report.Observations is null ||
            report.ObservationStatus is not ("not observed" or "unbaselined" or "compared") || report.AnswerStatus != "not judged" ||
            report.Candidate != report.Baseline && (report.Contracts is null || report.ImplementationHead is null ||
                !Version.TryParse(report.Candidate.Tag.AsSpan(1), out var candidateVersion) ||
                !Version.TryParse(report.Baseline.Tag.AsSpan(1), out var baselineVersion) || candidateVersion <= baselineVersion))
        {
            throw new InvalidDataException("The evaluation report is not bound to the trusted tool's exact production baseline/code head.");
        }
        if (report.Contracts is { } contracts)
        {
            ReleaseContracts.Validate(contracts);
            if (contracts.Release != report.Candidate)
            {
                throw new InvalidDataException("Candidate contract evidence belongs to a different release.");
            }
        }
    }

    private static void ValidateExistingDescriptor(string content, ReleaseEvaluationReport report)
    {
        var descriptor = JsonSerializer.Deserialize<ReleaseCandidate>(content, ReleaseContracts.Json)
            ?? throw new InvalidDataException("The candidate branch has no typed descriptor.");
        ValidateDescriptor(descriptor);
        if (descriptor.Release != report.Candidate || descriptor.Baseline != report.Baseline ||
            report.Contracts is null || descriptor.ContractFingerprint != report.Contracts.Fingerprint ||
            descriptor.HveCoreCommitSha != report.Contracts.HveCoreCommitSha)
        {
            throw new InvalidDataException("The candidate branch belongs to different source/baseline contracts; no overwrite, duplicate or moved-tag update is permitted.");
        }
    }

    private static string Branch(SquadRelease release) => "automation/hve-squad/" + release.Tag;
    private static string Body(ReleaseCandidate descriptor)
    {
        ValidateDescriptor(descriptor);
        var summary = string.Join("\n", descriptor.DriftSummary.Select(pair =>
            "* " + pair.Key + " drift: " + pair.Value.ToString(CultureInfo.InvariantCulture)));
        return "## Official stable candidate\n\n" +
            "* Squad: `" + descriptor.Release.Tag + "` at `" + descriptor.Release.CommitSha + "`\n" +
            "* HVE Core: `" + descriptor.HveCoreCommitSha + "`\n" +
            "* Production pin remains `" + descriptor.Baseline.Tag + "` until human-reviewed promotion.\n" +
            "* Candidate contract fingerprint: `" + descriptor.ContractFingerprint + "`\n\n" +
            "## Drift and acceptance\n\n" + (summary.Length == 0 ? "* No observed contract delta.\n" : summary + "\n") +
            "* Observation comparison: " + descriptor.ObservationStatus + " (advisory); answer: not judged.\n" +
            "* " + descriptor.PromotionBlockers.Count.ToString(CultureInfo.InvariantCulture) + " mechanical/review blockers; see the release-alignment run artifact and candidate checks.\n\n" +
            "This is a draft proposal, not an upgrade or compatibility pass. Review every contract change, update executable ownership/adapters, the supported pin/API mirror and reviewed snapshot in this branch. " +
            "Both `candidate-linux` and `candidate-windows` must finish against this exact PR head. For bot-created PRs select **Approve workflows to run** in the merge box. " +
            "A base-branch green run, absent/skipped tests, missing permission or pending approval cannot permit promotion. Mark ready and obtain human review before merging. " +
            "Automation never merges, publishes a package or deploys. Release notes are not executed or used as instructions.";
    }

    private static string Text(JsonElement value, string name) => HumanChannelHttp.Text(value, name)
        ?? throw new InvalidDataException("GitHub omitted required release-proposal metadata.");
    private static JsonElement Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : throw new InvalidDataException("GitHub omitted a required release-proposal object.");
    private static JsonElement[] Array(JsonElement value, string name) =>
        value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray() : throw new InvalidDataException("GitHub omitted a required complete release-proposal array.");
    private static bool Boolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var child) && child.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? child.GetBoolean() : throw new InvalidDataException("GitHub omitted required repository availability metadata.");
    private sealed record PullIdentity(long Number, string Head, string State, string Url, bool Merged);
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();
    [GeneratedRegex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hash();
}
