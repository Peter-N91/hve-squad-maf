using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HveSquad.AgentFramework.Runtime;

internal sealed record ReportSource(string Title, string Text, SquadReportGrounding Grounding);
internal sealed record ReportTable(string[] Headers, IReadOnlyList<string[]> Rows);
internal sealed class ReportSection(string title)
{
    internal string Title { get; } = title;
    internal List<string> Paragraphs { get; } = [];
    internal List<ReportTable> Tables { get; } = [];
    internal List<string> Excerpts { get; } = [];
}

internal sealed class ReportDocument(string title, string subtitle)
{
    internal string Title { get; } = title;
    internal string Subtitle { get; } = subtitle;
    internal List<ReportSection> Sections { get; } = [];
    internal List<SquadReportGrounding> Grounding { get; } = [];
    internal List<string> OpenQuestions { get; } = [];
    internal IReadOnlyList<(string Label, string Value)> Stats { get; set; } = [];

    internal string Markdown()
    {
        var text = new StringBuilder();
        text.Append("---\ntitle: ").AppendLine(JsonSerializer.Serialize(Title))
            .AppendLine("description: Grounded local native Squad export\n---\n")
            .AppendLine(Subtitle).AppendLine();
        foreach (var section in Sections)
        {
            text.Append("## ").AppendLine(Escape(section.Title)).AppendLine();
            foreach (var paragraph in section.Paragraphs)
            {
                text.AppendLine(Escape(paragraph)).AppendLine();
            }
            foreach (var table in section.Tables)
            {
                text.Append("| ").Append(string.Join(" | ", table.Headers.Select(Escape))).AppendLine(" |");
                text.Append("| ").Append(string.Join(" | ", table.Headers.Select(_ => "---"))).AppendLine(" |");
                foreach (var row in table.Rows)
                {
                    text.Append("| ").Append(string.Join(" | ", row.Select(Escape))).AppendLine(" |");
                }
                text.AppendLine();
            }
            foreach (var excerpt in section.Excerpts)
            {
                var fence = new string('`', Math.Max(3, Regex.Matches(excerpt, "`+", RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)).Select(match => match.Length + 1).DefaultIfEmpty(3).Max()));
                text.Append(fence).AppendLine("text").AppendLine(excerpt).AppendLine(fence).AppendLine();
            }
        }
        AppendGrounding(text);
        return text.ToString();
    }

    internal string PlainText()
    {
        var text = new StringBuilder().AppendLine(Title).AppendLine(Subtitle).AppendLine();
        foreach (var section in Sections)
        {
            text.AppendLine(section.Title);
            foreach (var paragraph in section.Paragraphs)
            {
                text.AppendLine(paragraph);
            }
            foreach (var table in section.Tables)
            {
                text.AppendLine(string.Join(" | ", table.Headers));
                foreach (var row in table.Rows)
                {
                    text.AppendLine(string.Join(" | ", row));
                }
            }
            foreach (var excerpt in section.Excerpts)
            {
                text.AppendLine(excerpt);
            }
            text.AppendLine();
        }
        AppendGrounding(text);
        return text.ToString();
    }

    internal string Html()
    {
        var html = new StringBuilder("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"UTF-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.Append("<title>").Append(Encode(Title)).Append("</title><script>")
            .Append(ReportTheme.Detection).Append("</script><style>").Append(ReportTheme.Css)
            .Append("</style></head><body><header><h1>").Append(Encode(Title)).Append("</h1><p>")
            .Append(Encode(Subtitle)).Append("</p>");
        if (Stats.Count != 0)
        {
            html.Append("<dl class=\"stats\">");
            foreach (var (label, value) in Stats)
            {
                html.Append("<div><dt>").Append(Encode(label)).Append("</dt><dd>").Append(Encode(value)).Append("</dd></div>");
            }
            html.Append("</dl>");
        }
        html.Append("</header><main>");
        foreach (var section in Sections)
        {
            html.Append("<section><h2>").Append(Encode(section.Title)).Append("</h2>");
            foreach (var paragraph in section.Paragraphs)
            {
                html.Append("<p>").Append(Encode(paragraph)).Append("</p>");
            }
            foreach (var table in section.Tables)
            {
                html.Append("<div class=\"table-scroll\"><table><thead><tr>");
                foreach (var heading in table.Headers)
                {
                    html.Append("<th scope=\"col\">").Append(Encode(heading)).Append("</th>");
                }
                html.Append("</tr></thead><tbody>");
                foreach (var row in table.Rows)
                {
                    html.Append("<tr>");
                    foreach (var cell in row)
                    {
                        html.Append("<td>").Append(Encode(cell)).Append("</td>");
                    }
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></div>");
            }
            foreach (var excerpt in section.Excerpts)
            {
                html.Append("<pre>").Append(Encode(excerpt)).Append("</pre>");
            }
            html.Append("</section>");
        }
        html.Append("</main><footer><details><summary>Grounding</summary><ul>");
        foreach (var source in Grounding.Distinct())
        {
            html.Append("<li>").Append(Encode(ReportObservation.Label(source.Squad) + source.Path))
                .Append(" <code>").Append(Encode(source.Sha256)).Append("</code></li>");
        }
        html.Append("</ul></details><p>Generated from authorized local evidence. No model dispatch, state update, " +
            "publication or human approval occurred. Estimates are not billed totals; human review remains required.</p></footer></body></html>");
        return html.ToString();
    }

    private void AppendGrounding(StringBuilder text)
    {
        text.AppendLine("## Grounding").AppendLine();
        foreach (var source in Grounding.Distinct())
        {
            text.Append("* ").Append(Escape(ReportObservation.Label(source.Squad) + source.Path))
                .Append(" (SHA-256 ").Append(source.Sha256).AppendLine(")");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal);
}

internal static class ReportContent
{
    private static readonly string[] StopWords = ["the", "and", "for", "with", "from", "this", "that", "what", "which",
        "were", "was", "are", "how", "does", "did", "can", "our", "all", "about", "summarize", "summary", "document"];

    internal static List<ReportSource> Projections(ReportObservation observation, IReadOnlyList<SquadReportGrounding> grounding,
        SquadReportConfiguration configuration)
    {
        var result = new List<ReportSource>();
        foreach (var item in observation.Cases)
        {
            var label = ReportObservation.Label(item.Name);
            var sources = grounding.Where(source => source.Squad == item.Name).ToArray();
            SquadReportGrounding Source(string file) => sources.Single(source => source.Path.EndsWith("/" + file, StringComparison.Ordinal));
            var roster = string.Join("\n", item.Roster.Members.Select(member => member.Role + " | " + member.PrimaryAgent +
                " | current deliverable root: " + (item.Roots.GetValueOrDefault(member.Role) ?? "none")));
            if (item.State.Federation is null)
            {
                result.Add(new(label + "Current roster and deliverable roots", Safe(roster, configuration), Source("team.md")));
            }
            var native = $"Native schema: {item.State.Schema}\nRelease: {item.State.ReleaseTag ?? "non-official/unknown"}\n" +
                $"Exact release commit: {item.State.ReleaseCommitSha ?? "unknown"}\n" +
                $"Recorded role dispatches: {item.State.DispatchRecords.Count.ToString(CultureInfo.InvariantCulture)}\n" +
                $"Active run: {item.State.Checkpoint?.Status ?? "none"}\n" +
                $"Outstanding human questions: {item.State.OpenQuestions.Count.ToString(CultureInfo.InvariantCulture)}";
            result.Add(new(label + "Current native status", native, Source("state.json")));
            foreach (var dispatch in observation.Dispatches.Where(dispatch => dispatch.Squad == item.Name))
            {
                var evidence = dispatch.Evidence;
                var path = Path.GetRelativePath(item.Files.ProjectRoot, item.Files.Resolve(evidence.HistoryPath)).Replace('\\', '/');
                var text = $"Role: {evidence.Role}\nAgent: {evidence.Agent}\nStage: {evidence.Stage}\n" +
                    $"Recorded verdict: {evidence.Verdict}\nRisk: {evidence.Risk}\nBlocking issues: {evidence.BlockingIssues}\n" +
                    $"Current output bytes verified: {dispatch.OutputsVerified}\n" +
                    "Model: " + (evidence.ModelRouting?.ObservedModel ?? evidence.ModelRouting?.EffectiveModel ?? evidence.Model) +
                    (configuration.IncludeCaseContent ? "\nRecorded activity (not independent test/deployment proof): " + evidence.Summary : "");
                result.Add(new(label + evidence.Role + " " + evidence.Stage + " activity", Safe(text, configuration),
                    new(item.Name, path, item.Files.HashFile(evidence.HistoryPath), "verified-native-projection")));
            }
            foreach (var gate in observation.Gates.Where(gate => gate.Squad == item.Name))
            {
                result.Add(new(label + gate.Record.Kind, "Recorded gate: " + gate.Record.Kind +
                    "\nVerdict: " + (ReportObservation.Verdict(gate.Record) ?? "see typed gate record; no scalar verdict") +
                    "\nRecorded at: " + Date(gate.Record.RecordedAt), Source("decisions.md")));
            }
            if (item.State.Federation is null)
            {
                var metrics = ReportObservation.Read([item], null, DateTimeOffset.MaxValue).Metrics();
                result.Add(new(label + "Actual provider consumption", $"Provider attempts: {metrics.ProviderAttempts}\n" +
                    $"Estimated USD: {Money(metrics.EstimatedCostUsd)}; known partial USD: {Money(metrics.KnownCostUsd)}\n" +
                    $"Unpriced attempts: {metrics.UnpricedAttempts}; calibrated attempts: {metrics.CalibratedAttempts}; " +
                    $"uncalibrated attempts: {metrics.UncalibratedAttempts}", Source("consumption.md")));
            }
        }
        return result;
    }

    internal static ReportDocument Document(string request, string mode, List<ReportSource> sources, List<string> questions)
    {
        var title = request.Length > 160 ? request[..160] : request;
        var result = new ReportDocument(title, "Scope: " + mode + ". Focused source extracts, not unsupported inference.");
        var terms = Regex.Matches(request.ToLowerInvariant(), @"[\p{L}\p{N}-]{3,}", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)).Select(match => match.Value)
            .Where(term => !StopWords.Contains(term, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        var ranked = sources.Select(source => (Source: source, Score: terms.Count(term =>
            (source.Title + "\n" + source.Text).Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Where(row => row.Score > 0 || terms.Length == 0).OrderByDescending(row => row.Score)
            .ThenBy(row => row.Source.Grounding.Path, StringComparer.Ordinal).Take(12).ToArray();
        if (ranked.Length == 0)
        {
            questions.Add("The authorized artifacts do not establish an answer to the requested topic.");
        }
        foreach (var (source, _) in ranked)
        {
            var section = new ReportSection(ReportObservation.Label(source.Grounding.Squad) + source.Title);
            section.Paragraphs.Add("Source: " + source.Grounding.Path + "; SHA-256 " + source.Grounding.Sha256);
            var lines = source.Text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            var relevant = lines.Where(line => terms.Length == 0 || terms.Any(term =>
                line.Contains(term, StringComparison.OrdinalIgnoreCase))).Take(24).ToArray();
            var excerpt = string.Join("\n", relevant.Length == 0 ? lines.Take(16) : relevant);
            section.Excerpts.Add(excerpt.Length > 6000 ? excerpt[..6000] + "\n[bounded excerpt]" : excerpt);
            result.Sections.Add(section);
            result.Grounding.Add(source.Grounding);
        }
        result.OpenQuestions.AddRange(questions.Distinct(StringComparer.Ordinal));
        var open = new ReportSection("Open Questions");
        open.Paragraphs.AddRange(result.OpenQuestions.Count == 0 ? ["none"] : result.OpenQuestions);
        result.Sections.Add(open);
        return result;
    }

    internal static ReportDocument Governance(ReportObservation observation, SquadGovernanceMetrics metrics, string mode,
        string period, DateTimeOffset? since, DateTimeOffset now, IReadOnlyList<SquadReportGrounding> grounding,
        SquadReportConfiguration configuration)
    {
        var result = new ReportDocument("Squad Governance Report",
            $"Scope: {mode}. Period: {period}; {Date(since)} to {Date(now)} (UTC).");
        result.Grounding.AddRange(grounding);
        result.OpenQuestions.AddRange(observation.Diagnostics);
        result.Stats =
        [
            ("Coordination Turns", metrics.CoordinationTurns.ToString(CultureInfo.InvariantCulture)),
            ("Role Dispatches", metrics.RoleDispatches.ToString(CultureInfo.InvariantCulture)),
            ("Est. Cost", Money(metrics.EstimatedCostUsd) + $" USD; calibrated {metrics.CalibratedAttempts}, pending {metrics.UncalibratedAttempts}"),
            ("Total Tokens", metrics.TotalTokens?.ToString(CultureInfo.InvariantCulture) ??
                "unknown; known partial " + metrics.KnownTokens.ToString(CultureInfo.InvariantCulture)),
            ("Human Gates Fired", metrics.HumanGatesFired.ToString(CultureInfo.InvariantCulture)),
            ("Escalations", metrics.Escalations.ToString(CultureInfo.InvariantCulture)),
        ];
        var councils = observation.Gates.Where(gate => gate.Record.Kind == "Council Verdict").ToArray();
        var discovery = observation.Gates.Where(gate => gate.Record.Kind == "Discovery Verdict").ToArray();
        var intake = observation.Gates.Where(gate => gate.Record.Kind == "Intake Readiness Verdict").ToArray();
        var gates = new ReportSection("1. Governance Gates");
        gates.Tables.Add(new(["Gate", "Recorded activity"],
        [
            ["Council Verdicts", Count(councils.Length)],
            ["Discovery Sessions", Count(discovery.Count(gate => Property(gate.Record.Payload, "Depth") != "skip"))],
            ["Discovery Declines", Count(discovery.Count(gate => Property(gate.Record.Payload, "OptIn") == "offer-declined"))],
            ["Intake Readiness Checks", Count(intake.Length)],
            ["Human Gates Passed", $"{metrics.HumanGatesPassed}/{metrics.HumanGatesFired}; {metrics.HumanGatesFired - metrics.HumanGatesPassed} blocked or unresolved"],
            ["Escalations", metrics.Escalations == 0 ? "None" : metrics.Escalations.ToString(CultureInfo.InvariantCulture)],
        ]));
        if (observation.Humans.Count != 0)
        {
            gates.Tables.Add(new(["Squad", "Date", "Gate Type", "Role", "Status"],
                observation.Humans.Select(human => new[]
                {
                    Squad(human.Squad), Date(human.FiredAt), Safe(human.Gate.Kind, configuration),
                    Safe(human.Gate.Role ?? "coordinator", configuration),
                    ReportObservation.Passed(human.Gate.Status) ? "PASSED (" + human.Gate.Status + ")" : "BLOCKED (" + human.Gate.Status + ")",
                }).ToArray()));
        }
        gates.Paragraphs.Add("Counts cover original recorded challenges and verdicts, not private question/answer bodies. " +
            "An escalation is a Stop/Not-Ready verdict or unresolved/denied human challenge. Historical unrecorded callback approvals are not invented.");
        gates.Tables.Add(new(["Squad", "Readiness Type", "Verdict / Depth", "Remediation Cycles"],
            discovery.Concat(intake).Select(gate => new[]
            {
                Squad(gate.Squad), gate.Record.Kind,
                Property(gate.Record.Payload, gate.Record.Kind == "Discovery Verdict" ? "Depth" : "Verdict"),
                Property(gate.Record.Payload, "Cycles"),
            }).ToArray()));
        result.Sections.Add(gates);

        var council = new ReportSection("2. Council Verdicts");
        if (councils.Length == 0)
        {
            council.Paragraphs.Add("No council reviews were triggered during this period. Council review: Not activated.");
        }
        else
        {
            council.Tables.Add(new(["Verdict", "Count", "Share"],
                councils.GroupBy(gate => ReportObservation.Verdict(gate.Record) ?? "unknown")
                    .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new[]
                    {
                        group.Key, group.Count().ToString(CultureInfo.InvariantCulture),
                        ((decimal)group.Count() / councils.Length * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%",
                    }).ToArray()));
            var findings = councils.SelectMany(gate => ReadinessContracts.Parse<SquadCouncilVerdict>(gate.Record.Payload)
                .Findings.Select(finding => (gate.Squad, Finding: finding))).ToArray();
            council.Tables.Add(new(["Squad", "Role", "Verdict", "Risk", "Blocking Issues", "Findings"],
                findings.Select(row => new[]
                {
                    Squad(row.Squad), Safe(row.Finding.Role, configuration), row.Finding.Verdict, row.Finding.Risk,
                    row.Finding.BlockingIssues.ToString(CultureInfo.InvariantCulture),
                    configuration.IncludeCaseContent ? Safe(row.Finding.Summary, configuration) : "Case summary withheld by disclosure policy",
                }).ToArray()));
            var latest = observation.Gates.Where(gate => gate.Record.Kind is "Council Verdict" or "Autonomous Revalidation")
                .GroupBy(gate => (gate.Squad, gate.Record.RunId)).Select(group => group.Last()).ToArray();
            var outstanding = latest.Sum(gate =>
            {
                var verdict = ReadinessContracts.Parse<SquadCouncilVerdict>(gate.Record.Payload);
                return verdict.Conditions.Count(condition => verdict.Findings.Where(finding => finding.Role == condition.Role)
                    .All(finding => finding.StructuredReturn is null ||
                        ReadinessContracts.Parse<SquadCouncilFindings>(finding.StructuredReturn.Payload).ResolvedConditions?
                            .Any(resolution => resolution.ConditionId == condition.Condition.Id) != true));
            });
            council.Paragraphs.Add("Conditions Outstanding: " + outstanding
                    .ToString(CultureInfo.InvariantCulture) +
                ". Resolution requires an explicit independent revalidation receipt; dispatch success alone is not condition closure.");
        }
        result.Sections.Add(council);

        var costs = new ReportSection("3. Cost Breakdown");
        costs.Paragraphs.Add($"Actual provider attempts: {metrics.ProviderAttempts}; estimated total USD: {Money(metrics.EstimatedCostUsd)}; " +
            $"known partial USD: {Money(metrics.KnownCostUsd)}. Unpriced attempts: {metrics.UnpricedAttempts}; unobserved dispatches: {metrics.UnobservedDispatches}. " +
            $"Calibrated attempts: {metrics.CalibratedAttempts}; uncalibrated/pending: {metrics.UncalibratedAttempts}. Credits: unknown.");
        costs.Tables.Add(new(["Squad", "Role", "Provider Attempts", "Estimated USD", "Calibration"],
            observation.Calls.GroupBy(call => (call.Squad, Role: call.Observation.Role ?? "coordinator"))
                .OrderByDescending(group => group.Sum(call => call.Charge?.CostUsd ?? 0m)).Select(group => new[]
                {
                    Squad(group.Key.Squad), Safe(group.Key.Role, configuration), group.Count().ToString(CultureInfo.InvariantCulture),
                    GroupCost(group), group.All(call => call.Charge?.Pricing?.Calibration.Observations > 0) ? "calibrated" : "uncalibrated/pending",
                }).ToArray()));
        costs.Tables.Add(new(["Model", "Role Usage", "Known Tokens", "Token Share"],
            observation.Calls.GroupBy(call => call.Observation.ObservedModel ?? call.Observation.EffectiveModel ?? "unknown")
                .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
                {
                    var tokens = group.Sum(call => (call.Observation.InputTokens ?? 0) + (call.Observation.OutputTokens ?? 0));
                    return new[]
                    {
                        Safe(group.Key, configuration),
                        Safe(string.Join(", ", group.Select(call => call.Observation.Role ?? "coordinator").Distinct(StringComparer.Ordinal)), configuration),
                        tokens.ToString(CultureInfo.InvariantCulture),
                        metrics.TotalTokens is null ? "unknown" : metrics.TotalTokens == 0 ? "No data" :
                            ((decimal)tokens / metrics.TotalTokens.Value * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%",
                    };
                }).ToArray()));
        costs.Paragraphs.Add("Coordinator calls are real orchestration overhead. Deterministic Scribe, Watch bootstrap/terminal and handoffs have no model calls " +
            "and add no invented role stages or spend. Reservations, cost rounds, preserved hub totals and reused artifacts are not charged again. " +
            "Totals sum original unrounded execution-time charges and round once to four decimals; calibration is not reapplied. " +
            "Tokens count input plus output, without counting cached input twice.");
        if (observation.Calls.Count == 0)
        {
            costs.Paragraphs.Add("No data: no provider attempts occurred in the selected period.");
        }
        result.Sections.Add(costs);

        var activity = new ReportSection("4. Role Dispatch Activity");
        activity.Tables.Add(new(["Squad", "Role", "Agent", "Dispatches", "Model", "Key Activities"],
            observation.Dispatches.GroupBy(dispatch => (dispatch.Squad, dispatch.Evidence.Role, dispatch.Evidence.Agent))
                .OrderBy(group => group.Key.Role, StringComparer.Ordinal).Select(group => new[]
                {
                    Squad(group.Key.Squad), Safe(group.Key.Role, configuration), Safe(group.Key.Agent, configuration),
                    group.Count().ToString(CultureInfo.InvariantCulture),
                    Safe(string.Join(", ", group.Select(dispatch => dispatch.Evidence.ModelRouting?.ObservedModel ??
                        dispatch.Evidence.ModelRouting?.EffectiveModel ?? dispatch.Evidence.Model).Distinct(StringComparer.Ordinal)), configuration),
                    configuration.IncludeCaseContent ? Safe(string.Join("; ", group.Select(dispatch => dispatch.Evidence.Summary)), configuration) :
                        string.Join(", ", group.Select(dispatch => dispatch.Evidence.Stage).Distinct(StringComparer.Ordinal)),
                }).ToArray()));
        var success = observation.Dispatches.Count(dispatch => dispatch.OutputsVerified &&
            dispatch.Evidence.BlockingIssues == 0 && dispatch.Evidence.Verdict is not ("Stop" or "Not-Ready"));
        activity.Paragraphs.Add($"Distinct Roles Used: {observation.Dispatches.Select(dispatch => dispatch.Evidence.Role).Distinct(StringComparer.Ordinal).Count()}; " +
            $"Total Dispatches: {metrics.RoleDispatches}; Dispatch Success Rate: " +
            (metrics.RoleDispatches == 0 ? "No data" : ((decimal)success / metrics.RoleDispatches * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%") +
            $"; failed or escalated completed dispatches: {metrics.RoleDispatches - success}.");
        if (metrics.RoleDispatches == 0)
        {
            activity.Paragraphs.Add("None: no completed role dispatch entries exist in this period. In-flight/failed provider attempts remain in Cost Breakdown.");
        }
        result.Sections.Add(activity);

        var compliance = new ReportSection("5. Risk & Compliance");
        compliance.Tables.Add(new(["Autonomy Tier", "Dispatches", "Observed Boundary"],
            observation.Dispatches.GroupBy(dispatch => dispatch.AutonomyTier).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new[] { group.Key, group.Count().ToString(CultureInfo.InvariantCulture),
                    "Original gate/mode evidence, not new approval or release authority" }).ToArray()));
        var effects = observation.Cases.SelectMany(item => item.State.NativeOperations
            .Where(operation => operation.ExecutedEffect is not null && operation.RecordedAt <= now &&
                (since is null || operation.RecordedAt >= since)).Select(operation => (Case: item, Operation: operation))).ToArray();
        var terminals = observation.TerminalObservations(since, now);
        var learning = observation.Cases.SelectMany(item => item.State.NativeOperations
            .Where(operation => operation.LearningRecord is not null && operation.RecordedAt <= now &&
                (since is null || operation.RecordedAt >= since))
            .Select(operation => (item.Name, Operation: operation, Record: operation.LearningRecord!)))
            .GroupBy(item => item.Record.Mutation.Id, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var notified = observation.Humans.Count(human => human.Gate.DeliveryStatus == "sent" || human.Gate.OutboundStatus == "sent");
        compliance.Tables.Add(new(["Indicator", "Status"],
        [
            ["Artifact evidence", $"{observation.Dispatches.Count(dispatch => dispatch.OutputsVerified)}/{metrics.RoleDispatches} current output sets verified"],
            ["Test verification on code changes", "Unknown: a tester verdict or process exit is not an independently attested test count"],
            ["Cost logging", $"{metrics.ProviderAttempts - metrics.UnpricedAttempts}/{metrics.ProviderAttempts} priced attempts; original receipts only"],
            ["Impactful action gating", effects.Length == 0 && learning.Length == 0 ? "None required" :
                "Original effect/learning approvals recorded; unverified learning attempts remain uncertain, never blanket compliance sign-off"],
            ["Notifications sent", $"{notified}/{metrics.HumanGatesFired} challenges have an observed channel/notifier delivery"],
            ["Open escalations", observation.Humans.Count(human => !ReportObservation.Passed(human.Gate.Status)).ToString(CultureInfo.InvariantCulture)],
        ]));
        result.Sections.Add(compliance);

        var timeline = new ReportSection("6. Activity Timeline");
        var events = observation.Dispatches.Select(dispatch => (At: dispatch.RecordedAt, Squad: dispatch.Squad,
            Title: dispatch.Evidence.Role + " / " + dispatch.Evidence.Stage, Outcome: dispatch.Evidence.Verdict))
            .Concat(observation.Gates.Select(gate => (At: (DateTimeOffset?)gate.Record.RecordedAt, Squad: gate.Squad,
                Title: gate.Record.Kind, Outcome: ReportObservation.Verdict(gate.Record) ?? "recorded")))
            .Concat(observation.Humans.Select(human => (At: (DateTimeOffset?)human.FiredAt, Squad: human.Squad,
                Title: "Human gate " + human.Gate.Kind, Outcome: human.Gate.Status)))
            .Concat(terminals.Select(terminal => (At: (DateTimeOffset?)terminal.RecordedAt, Squad: (string?)null,
                Title: "Provider terminal " + terminal.Receipt.Kind, Outcome: "Verified provider surface; not human acceptance")))
            .Concat(learning.Select(item => (At: (DateTimeOffset?)item.Operation.RecordedAt, Squad: item.Name,
                Title: "Human-only learning " + item.Record.Mutation.Kind,
                Outcome: item.Record.Status == "applied" ? "Original provider postcondition verified; final PR review is separate" : "Uncertain; no automatic replay")))
            .OrderByDescending(row => row.At).Take(30).ToArray();
        timeline.Tables.Add(new(["Date (UTC)", "Squad", "Recorded Activity", "Outcome"],
            events.Select(row => new[] { Date(row.At), Squad(row.Squad), Safe(row.Title, configuration), row.Outcome }).ToArray()));
        if (events.Length == 0)
        {
            timeline.Paragraphs.Add("No data: no dated significant activity occurred in this period.");
        }
        result.Sections.Add(timeline);

        var outcomes = new ReportSection("7. Key Outcomes This Period");
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var written = observation.Dispatches.Where(dispatch => dispatch.OutputsVerified)
            .SelectMany(dispatch => (dispatch.Evidence.EvidenceKind == SquadEvidenceKind.Artifact
                ? dispatch.Evidence.Artifacts ?? [new("primary", dispatch.Evidence.ArtifactPath, dispatch.Evidence.ArtifactSha256)]
                : []).Select(artifact => observation.Cases.Single(item => item.Name == dispatch.Squad).Files.Resolve(artifact.Path)))
            .Concat(effects.SelectMany(effect => effect.Operation.ExecutedEffect!.Receipt.Status == SquadToolOutputStatus.Succeeded
                ? (effect.Operation.ExecutedEffect.Receipt.Outputs ?? []).Select(artifact => effect.Case.Files.Resolve(artifact.Path)) : []))
            .Distinct(pathComparer).Count();
        var issues = observation.Humans.Count(human => human.Gate.GitHubIssueObserved) +
            terminals.Where(terminal => terminal.Receipt.Kind == SquadWatchTerminalKind.Issue)
                .Select(terminal => (terminal.Receipt.Repository, terminal.Receipt.ResourceId)).Distinct().Count();
        outcomes.Tables.Add(new(["Outcome", "Verified Observation"],
        [
            ["Azure Deployments", "Unknown: no typed deployment-count postcondition is inferred from tool names or prose"],
            ["GitHub Issues Created", issues.ToString(CultureInfo.InvariantCulture) + " observed human-channel/Watch issues; original delivery/effect receipts, never drafted links"],
            ["Learning Pull Requests Created", learning.Where(item => item.Record is { Status: "applied", Mutation.Kind: "pull-request", Receipt: not null })
                .Select(item => (item.Record.Mutation.OriginRepository, item.Record.Receipt!.ResourceId)).Distinct().Count()
                .ToString(CultureInfo.InvariantCulture) + " independently verified learning PRs; prepared commands and uncertain attempts are excluded"],
            ["Tests (source count)", "No independently attested execution count; source count 0"],
            ["Files Created/Modified", written.ToString(CultureInfo.InvariantCulture) + " distinct verified produced/artifact paths; no diff-based fiction"],
        ]));
        outcomes.Tables.Add(new(["Squad", "Capability Type", "Current Status"],
            observation.Dispatches.Where(dispatch => dispatch.Evidence.Stage is "produce" or "implementation")
                .Select(dispatch => new[]
                {
                    Squad(dispatch.Squad), Safe(dispatch.Evidence.Role, configuration),
                    dispatch.OutputsVerified && dispatch.Evidence.BlockingIssues == 0 ? "Produced; not independently deployed/released" : "Unproven current output",
                }).ToArray()));
        if (outcomes.Tables[1].Rows.Count == 0)
        {
            outcomes.Paragraphs.Add("None: no producing capability has verified dispatch evidence in this period.");
        }
        outcomes.Paragraphs.Add("Open Questions: " + (result.OpenQuestions.Count == 0 ? "none" : string.Join(" ", result.OpenQuestions)));
        result.Sections.Add(outcomes);
        return result;
    }

    internal static string Safe(string value, SquadReportConfiguration configuration) =>
        ReportSafety.IsSafe(value, configuration.SecretValues) ? value : "[withheld credential/transport content]";
    private static string Count(int value) => value == 0 ? "Not activated (0)" : value.ToString(CultureInfo.InvariantCulture);
    private static string Money(decimal? value) => value?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "unknown";
    private static string Date(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "unknown/all";
    private static string Squad(string? name) => name ?? "single";
    private static string Property(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : "not recorded";
    private static string GroupCost(IEnumerable<ReportCall> calls)
    {
        var group = calls.ToArray();
        var partial = Money(group.Sum(call => call.Charge?.CostUsd ?? 0m));
        return group.Any(call => call.Charge?.CostUsd is null) ? "unknown; known partial " + partial : partial;
    }
}

internal static partial class ReportSafety
{
    internal static bool IsSafe(string text, IEnumerable<string> secrets) =>
        !secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)) && !SecretTransport().IsMatch(text);

    internal static bool TextExtension(string extension) => extension.ToLowerInvariant() is ".md" or ".txt" or ".html" or ".json" or ".csv";

    internal static bool ProtectedSource(ProjectFiles files, string relative)
    {
        var path = relative.Replace('\\', '/');
        return files.IsProtectedStatePath(path) || path.Split('/').Any(part => part is "payloads" or "returns" or "history" or
            "migrations" or "watch-inputs" or "seed-image" or ".git" or ".github" or ".agents") ||
            Path.GetFileName(path) is "state.json" or "state.pending.json" or "transaction.json" or "original-state.json" or "original-team.md";
    }

    [GeneratedRegex(@"(?ix)(?:\b(?:authorization|proxy-authorization)\s*:\s*\S+|\bbearer\s+\S+|\b(?:api[-_ ]?key|access[-_ ]?token|password|client[-_ ]?secret|connection[-_ ]?string)\s*[:=]\s*\S+|(?:gh[pousr]_|github_pat_)[a-z0-9_]{8,}|-----BEGIN[^\r\n]*PRIVATE\s+KEY-----|\beyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+|https?://[^\s<>]*[?&](?:sig|token|access_token|key|code|password|x-amz-signature|x-goog-signature)=[^\s<>]+|https?://[^/\s<>]+:[^/\s<>]+@)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SecretTransport();
}

internal static class ReportFormats
{
    internal static void Verify(string format, byte[] content)
    {
        if (content.Length is 0 or > ProjectFiles.MaximumHostOutputBytes)
        {
            throw new InvalidDataException("The local renderer returned empty or oversized output.");
        }
        if (format == "pdf")
        {
            var text = Encoding.Latin1.GetString(content);
            if (!text.StartsWith("%PDF-", StringComparison.Ordinal) || !text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal) ||
                !text.Contains("/Type /Catalog", StringComparison.Ordinal) || !text.Contains("/Type /Page", StringComparison.Ordinal) ||
                !text.Contains("startxref", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The PDF renderer did not return actual PDF object/trailer bytes.");
            }
            return;
        }
        if (format != "docx")
        {
            throw new InvalidDataException("An undeclared local render format is not accepted.");
        }
        using var archive = new ZipArchive(new MemoryStream(content, false), ZipArchiveMode.Read);
        if (archive.Entries.Count > 128 || archive.Entries.Sum(entry => entry.Length) > ProjectFiles.MaximumHostOutputBytes ||
            archive.Entries.Any(entry => entry.Length > ProjectFiles.MaximumHostOutputBytes ||
            entry.FullName.Contains("..", StringComparison.Ordinal) || entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The Word renderer returned an unsafe or unbounded package.");
        }
        XDocument Read(string part)
        {
            using var stream = (archive.GetEntry(part) ?? throw new InvalidDataException("The Word renderer lost a required OOXML part.")).Open();
            using var reader = System.Xml.XmlReader.Create(stream, new()
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = ProjectFiles.MaximumHostOutputBytes,
            });
            return XDocument.Load(reader);
        }
        var types = Read("[Content_Types].xml");
        var relationships = Read("_rels/.rels");
        var document = Read("word/document.xml");
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        if (document.Root?.Name != word + "document" || document.Root.Element(word + "body") is null ||
            !types.Descendants().Any(element => (string?)element.Attribute("ContentType") ==
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml") ||
            !relationships.Descendants().Any(element => (string?)element.Attribute("Target") == "word/document.xml"))
        {
            throw new InvalidDataException("The Word renderer did not return a real document package.");
        }
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            using var reader = System.Xml.XmlReader.Create(stream, new()
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = ProjectFiles.MaximumHostOutputBytes,
            });
            if (XDocument.Load(reader).Descendants().Any(element => (string?)element.Attribute("TargetMode") == "External"))
            {
                throw new InvalidDataException("A local Word renderer cannot introduce external-resource relationships.");
            }
        }
    }
}

internal static class ReportTheme
{
    internal const string Detection = """
        (() => {
          const param = new URLSearchParams(window.location.search).get("scoutTheme");
          const theme =
            param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
          document.documentElement.setAttribute("data-theme", theme);
        })();
        """;
    internal const string Css = """
        :root {
          color-scheme: light;
          --cp-bg: #f7f4ef;
          --cp-bg-elevated: #fcfbf8;
          --cp-surface: #ffffff;
          --cp-surface-soft: #f5f5f5;
          --cp-border: #dedede;
          --cp-border-strong: #919191;
          --cp-text: #242424;
          --cp-text-muted: #5c5c5c;
          --cp-text-soft: #6f6f6f;
          --cp-accent: #b11f4b;
          --cp-accent-hover: #9a1a41;
          --cp-accent-soft: rgba(177, 31, 75, 0.08);
          --cp-accent-fg: #ffffff;
          --cp-success: #16a34a;
          --cp-danger: #dc2626;
          --cp-warning: #f59e0b;
          --cp-link: #0078d4;
          --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.12);
          --cp-overlay: rgba(255, 255, 255, 0.8);
          --cp-panel: rgba(255, 255, 255, 0.86);
          --cp-panel-strong: rgba(255, 255, 255, 0.96);
          --cp-sheen: rgba(255, 255, 255, 0.55);
          --cp-highlight: rgba(177, 31, 75, 0.12);
        }
        html[data-theme="dark"] {
          color-scheme: dark;
          --cp-bg: #3d3b3a;
          --cp-bg-elevated: #343231;
          --cp-surface: #292929;
          --cp-surface-soft: #2e2e2e;
          --cp-border: #474747;
          --cp-border-strong: #5f5f5f;
          --cp-text: #dedede;
          --cp-text-muted: #919191;
          --cp-text-soft: #b0b0b0;
          --cp-accent: #fd8ea1;
          --cp-accent-hover: #fb7b91;
          --cp-accent-soft: rgba(253, 142, 161, 0.14);
          --cp-accent-fg: #1a1a1a;
          --cp-success: #4ade80;
          --cp-danger: #f87171;
          --cp-warning: #fbbf24;
          --cp-link: #4da6ff;
          --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.32);
          --cp-overlay: rgba(41, 41, 41, 0.88);
          --cp-panel: rgba(41, 41, 41, 0.72);
          --cp-panel-strong: rgba(41, 41, 41, 0.96);
          --cp-sheen: rgba(255, 255, 255, 0.04);
          --cp-highlight: rgba(253, 142, 161, 0.12);
        }
        body { margin: 0; background: var(--cp-bg); color: var(--cp-text);
          font-family: "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif; line-height: 1.6; }
        header, main, footer { max-width: 1200px; margin: auto; padding: 20px; }
        header { border-bottom: 4px solid var(--cp-accent); }
        h1 { margin: 0; } h2 { border-bottom: 2px solid var(--cp-border); padding-bottom: 8px; }
        section { margin: 16px 0; padding: 20px; border: 1px solid var(--cp-border);
          border-radius: 16px; background: var(--cp-surface); }
        .stats { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 12px; }
        .stats div { padding: 12px; background: var(--cp-bg-elevated); border: 1px solid var(--cp-border); border-radius: 0.625rem; }
        dt { color: var(--cp-text-muted); } dd { margin: 0; font-weight: 600; overflow-wrap: anywhere; }
        .table-scroll { overflow-x: auto; } table { border-collapse: collapse; width: 100%; margin: 12px 0; }
        th, td { padding: 8px 12px; border-bottom: 1px solid var(--cp-border); text-align: left; vertical-align: top; }
        th { background: var(--cp-surface-soft); } pre, code { font-family: Consolas, "Courier New", Courier, monospace; }
        pre { white-space: pre-wrap; overflow-wrap: anywhere; padding: 16px; background: var(--cp-surface-soft); border-radius: 0.625rem; }
        footer { color: var(--cp-text-muted); } footer li { overflow-wrap: anywhere; }
        @media (max-width: 900px) { .stats { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
        @media print { body, section { background: var(--cp-surface); } section { break-inside: avoid; } }
        """;
}
