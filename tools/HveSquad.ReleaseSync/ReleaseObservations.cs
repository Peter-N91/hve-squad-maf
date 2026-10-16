using System.Buffers;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sources;

namespace HveSquad.ReleaseSync;

internal sealed record ObservedDeliverable(string Root, string Extension);
internal sealed record ObservedGate(string Kind, string Verdict);
internal sealed record ScenarioObservation(string ScenarioId, string ReleaseTag, string ReleaseCommitSha, int Attempt,
    IReadOnlyList<string> Roles, IReadOnlyList<ObservedDeliverable> Deliverables, IReadOnlyList<ObservedGate> Gates);
internal sealed record ObservationDimension(decimal Score, IReadOnlyList<string> Added, IReadOnlyList<string> Dropped);
internal sealed record ScenarioComparison(string ScenarioId, int Attempt, string Status, decimal? Score,
    ObservationDimension? Routing, ObservationDimension? Deliverables, ObservationDimension? Gates,
    string AnswerStatus = "not judged", decimal? AnswerScore = null, bool Advisory = true);

internal static class ReleaseObservations
{
    private static readonly SearchValues<char> ExtensionCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789");
    internal static async Task<ScenarioObservation> CaptureAsync(string projectPath, string squadRoot,
        string runId, string scenarioId, int attempt,
        Func<SquadReportAccess, CancellationToken, ValueTask<bool>> authorize, CancellationToken token = default)
    {
        if (!Identifier(scenarioId) || attempt < 1 || string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("An observation needs a bounded scenario identity, positive attempt and actual native run.");
        }
        var files = new ProjectFiles(projectPath, 200_000, squadRoot);
        var configuration = new SquadReportConfiguration { AuthorizeAsync = authorize };
        using var snapshot = await ReportSnapshot.CaptureAsync(files, configuration, "release-observation",
            null, "", token).ConfigureAwait(false);
        var cases = snapshot.Cases.Where(candidate => candidate.State.Federation is null &&
            (candidate.State.Runs.Any(run => run.RunId == runId) || candidate.State.ActiveRun?.RunId == runId)).ToArray();
        if (cases.Length != 1)
        {
            throw new InvalidDataException("An observation must name exactly one actual run in the authorized native scope.");
        }
        var item = cases[0];
        var release = new SquadRelease(item.State.ReleaseTag ?? "", item.State.ReleaseCommitSha ?? "");
        ReleaseContracts.ValidateIdentity(release);
        var actual = ReportObservation.Read([item], null, DateTimeOffset.MaxValue);
        var dispatches = actual.Dispatches.Where(dispatch => dispatch.RunId == runId).ToArray();
        if (dispatches.Length == 0 || dispatches.Any(dispatch => !dispatch.OutputsVerified))
        {
            throw new RuntimeHistoryIntegrityException("release-observation: the run has no verified dispatch set or its actual output bytes changed.");
        }
        var deliverables = new HashSet<ObservedDeliverable>();
        foreach (var dispatch in dispatches)
        {
            foreach (var artifact in dispatch.Evidence.Artifacts ?? [])
            {
                AddDeliverable(artifact.Path, dispatch.Evidence.Role);
            }
            foreach (var receipt in (dispatch.Evidence.ExecutedHostTools ?? []).Where(receipt =>
                receipt.Status == SquadToolOutputStatus.Succeeded && receipt.Effect == SquadToolEffect.ProjectWrite))
            {
                foreach (var output in receipt.Outputs ?? [])
                {
                    AddDeliverable(output.Path, dispatch.Evidence.Role);
                }
            }
        }
        void AddDeliverable(string path, string role)
        {
            if (files.IsProtectedStatePath(path) && !item.Roots.Values.OfType<string>()
                .Any(root => ProjectFiles.IsWithin(path, Prefix(root))))
            {
                return;
            }
            var bytes = files.ReadBytes(path);
            if (bytes.Length == 0)
            {
                throw new InvalidDataException("An observed deliverable must have actual nonempty fixture bytes.");
            }
            var root = item.Roots.GetValueOrDefault(role);
            var prefix = root is null ? null : Prefix(root);
            var normalized = prefix is not null && ProjectFiles.IsWithin(path, prefix)
                ? prefix : Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? ".";
            deliverables.Add(new(normalized.Length == 0 ? "." : normalized,
                Path.GetExtension(path).ToLowerInvariant()));
        }
        var gates = actual.Gates.Where(gate => gate.Record.RunId == runId)
            .Select(gate => new ObservedGate(gate.Record.Kind, Verdict(gate.Record.Payload)))
            .Concat(actual.Humans.Where(human => human.Gate.RunId == runId)
                .Select(human => new ObservedGate(human.Gate.Kind, human.Gate.Status)))
            .Distinct().OrderBy(gate => gate.Kind, StringComparer.Ordinal).ThenBy(gate => gate.Verdict, StringComparer.Ordinal).ToArray();
        var observation = new ScenarioObservation(scenarioId, release.Tag, release.CommitSha, attempt,
            dispatches.Select(dispatch => dispatch.Evidence.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            deliverables.OrderBy(value => value.Root, StringComparer.Ordinal).ThenBy(value => value.Extension, StringComparer.Ordinal).ToArray(), gates);
        Validate(observation);
        foreach (var dispatch in dispatches)
        {
            item.Scribe.VerifyOutputs(dispatch.Evidence, item.State);
        }
        snapshot.AssertUnchanged();
        return observation;
    }

    internal static ScenarioComparison[] Compare(IEnumerable<ScenarioObservation>? baseline,
        IEnumerable<ScenarioObservation> candidate, IReadOnlyDictionary<string, decimal>? answerScores = null)
    {
        var old = Latest(baseline ?? []);
        var current = Latest(candidate);
        if (answerScores?.Any(pair => !current.ContainsKey(pair.Key) || pair.Value is < 0m or > 1m) == true)
        {
            throw new ArgumentException("A judged answer score must name an observed scenario and be within zero and one.", nameof(answerScores));
        }
        return current.Values.OrderBy(item => item.ScenarioId, StringComparer.Ordinal).Select(item =>
        {
            if (!old.TryGetValue(item.ScenarioId, out var prior))
            {
                return new ScenarioComparison(item.ScenarioId, item.Attempt, "unbaselined", null, null, null, null);
            }
            var routing = Dimension(prior.Roles, item.Roles);
            var deliverables = Dimension(prior.Deliverables.Select(Key), item.Deliverables.Select(Key));
            var gates = Dimension(prior.Gates.Select(Key), item.Gates.Select(Key));
            var judged = answerScores is not null && answerScores.TryGetValue(item.ScenarioId, out _);
            decimal? answer = judged ? answerScores![item.ScenarioId] : null;
            var score = (routing.Score + deliverables.Score + gates.Score + (answer ?? 0m)) / (judged ? 4m : 3m);
            return new ScenarioComparison(item.ScenarioId, item.Attempt, "compared", score,
                routing, deliverables, gates, judged ? "judged" : "not judged", answer);
        }).ToArray();
    }

    internal static Dictionary<string, ScenarioObservation> Latest(IEnumerable<ScenarioObservation> observations)
    {
        var result = new Dictionary<string, ScenarioObservation>(StringComparer.Ordinal);
        var attempts = new HashSet<(string Scenario, int Attempt)>();
        var count = 0;
        foreach (var item in observations)
        {
            Validate(item);
            if (++count > 4096 || !attempts.Add((item.ScenarioId, item.Attempt)))
            {
                throw new InvalidDataException("Observations exceed their bound or repeat an ambiguous scenario/attempt.");
            }
            if (!result.TryGetValue(item.ScenarioId, out var prior) || item.Attempt > prior.Attempt)
            {
                result[item.ScenarioId] = item;
            }
        }
        return result;
    }

    internal static void Validate(ScenarioObservation item)
    {
        ReleaseContracts.ValidateIdentity(new(item.ReleaseTag, item.ReleaseCommitSha));
        if (!Identifier(item.ScenarioId) || item.Attempt < 1 || item.Roles is null ||
            item.Deliverables is null || item.Gates is null || item.Roles.Count is 0 or > 4096 ||
            item.Deliverables.Count > 4096 || item.Gates.Count > 4096 ||
            item.Roles.Any(role => !Identifier(role)) ||
            item.Deliverables.Any(deliverable => !Root(deliverable.Root) || !Extension(deliverable.Extension)) ||
            item.Gates.Any(gate => !Label(gate.Kind) || !Label(gate.Verdict)))
        {
            throw new InvalidDataException("An observation has invalid scenario, role, root/type, gate or attempt fields.");
        }
    }

    private static ObservationDimension Dimension(IEnumerable<string> baseline, IEnumerable<string> candidate)
    {
        var old = baseline.ToHashSet(StringComparer.Ordinal);
        var current = candidate.ToHashSet(StringComparer.Ordinal);
        var union = old.Union(current, StringComparer.Ordinal).Count();
        return new(union == 0 ? 1m : (decimal)old.Intersect(current, StringComparer.Ordinal).Count() / union,
            current.Except(old, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            old.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static string Verdict(JsonElement payload)
    {
        if (payload.TryGetProperty("Verdict", out var verdict) && verdict.ValueKind == JsonValueKind.String)
        {
            return verdict.GetString() ?? throw new InvalidDataException("An actual gate verdict is empty.");
        }
        if (payload.TryGetProperty("Converged", out var converged) && converged.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return converged.GetBoolean() ? "converged" : "not-converged";
        }
        return "recorded";
    }

    private static string Prefix(string root)
    {
        var marker = root.IndexOf('<');
        return (marker < 0 ? root : root[..marker]).Replace('\\', '/').TrimEnd('/');
    }
    private static string Key(ObservedDeliverable value) => value.Root + "|" + value.Extension;
    private static string Key(ObservedGate value) => value.Kind + "|" + value.Verdict;
    private static bool Identifier(string value) => value.Length is > 0 and <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool Label(string value) => value.Length is > 0 and <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ' ');
    private static bool Extension(string value) => value.Length <= 24 &&
        (value.Length == 0 || value[0] == '.' && value.AsSpan(1).IndexOfAnyExcept(ExtensionCharacters) < 0);
    private static bool Root(string value) => value == "." || value.Length is > 0 and <= 512 &&
        !value.StartsWith('/') && !value.EndsWith('/') && !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '/' or '-' or '_');
}
