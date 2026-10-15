using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Runtime;

internal sealed partial class RuntimeScribe
{
    internal SquadGateRecord RecordGate(RuntimeState state, string id, string kind, string topicId,
        string scopeSha256, object payload, string body)
    {
        using (SyncRoot.EnterScope())
        {
            VerifyIntegrity(state);
            var checkpoint = state.Checkpoint ?? throw new InvalidDataException("A readiness record requires its current run.");
            var recordedAt = checkpoint.Readiness?.RecordedAt
                ?? throw new InvalidDataException("A gate requires the trusted run's recorded readiness time.");
            var heading = $"## {kind} {recordedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {topicId}-{id}";
            var decisionRef = files.SquadRoot + "/decisions.md#" +
                Regex.Replace(heading[3..].ToLowerInvariant(), "[^a-z0-9 -]", "",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Replace(' ', '-');
            var record = new SquadGateRecord(id, kind, checkpoint.RunId, topicId, scopeSha256,
                decisionRef, JsonSerializer.SerializeToElement(payload), recordedAt);
            if (payload is SquadCouncilVerdict verdict)
            {
                record = record with { Payload = JsonSerializer.SerializeToElement(verdict with { DecisionRef = decisionRef }) };
            }
            state.GateRecords ??= [];
            var prior = state.GateRecords.SingleOrDefault(gate => gate.Id == id);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior, JsonOptions) != JsonSerializer.Serialize(record, JsonOptions))
                {
                    throw new RuntimeHistoryIntegrityException("history-integrity: a readiness verdict cannot be replaced.");
                }
                return prior;
            }
            state.GateRecords.Add(record);
            Decision(state, heading + "\n\n* Topic: " + topicId + "\n" + body +
                "\n\n```json\n" + JsonSerializer.Serialize(record, JsonOptions) + "\n```\n");
            RecordNativeOperation(state, "RecordReadinessGate", checkpoint.RunId, null, gateRecord: record);
            Save(state);
            return record;
        }

    }

    private bool RecoverGateReceipts(RuntimeState state)
    {
        var recovered = false;
        foreach (var gate in state.GateRecords ?? [])
        {
            if (state.NativeOperations.Any(operation => operation.GateRecord?.Id == gate.Id))
            {
                continue;
            }
            if (state.Checkpoint?.RunId != gate.RunId ||
                !files.Read(files.SquadRoot + "/decisions.md", MaximumStateCharacters)
                    .Contains(JsonSerializer.Serialize(gate, JsonOptions), StringComparison.Ordinal))
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: an incomplete readiness receipt cannot be recovered from exact decisions.");
            }
            if (recovered)
            {
                throw new RuntimeHistoryIntegrityException("history-integrity: more than one incomplete readiness receipt requires explicit reconciliation.");
            }
            RecordNativeOperation(state, "RecordReadinessGate", gate.RunId, null, gateRecord: gate);
            Save(state);
            recovered = true;
        }
        return recovered;
    }

    internal static string RenderDiscovery(string depth, string optIn, IReadOnlyList<SquadDispatchEvidence> evidence,
        SquadDiscoveryFindings? findings, string? brief, string handoff)
    {
        var text = new StringBuilder();
        text.Append("* Opt-In: ").AppendLine(optIn);
        text.Append("* Depth: ").AppendLine(depth);
        text.Append("* Roles Dispatched: ").AppendLine(string.Join(", ", evidence.Select(item => item.Agent)));
        text.Append("* Brief: ").AppendLine(brief ?? "none");
        text.Append("* Handoff: ").AppendLine(handoff);
        text.AppendLine("\n### Framing");
        text.Append("* Problem: ").AppendLine(findings?.Problem ?? "");
        text.Append("* Success Measure: ").AppendLine(findings?.SuccessMeasure ?? "");
        text.AppendLine("\n### Options Considered\n\n| Option | Outcome | Reason |\n| --- | --- | --- |");
        foreach (var option in findings?.Options ?? [])
        {
            text.Append("| ").Append(Cell(option.Description)).Append(" | ").Append(Cell(option.Outcome))
                .Append(" | ").Append(Cell(option.Reason)).AppendLine(" |");
        }
        text.AppendLine("\n### Objections");
        foreach (var issue in evidence.SelectMany(item =>
            item.StructuredReturn is null ? [] : ReadinessContracts.Parse<SquadDiscoveryFindings>(item.StructuredReturn.Payload).Objections))
        {
            text.Append("* ").Append(Cell(issue.Dimension)).Append(": ").AppendLine(Cell(issue.Description));
        }
        var experiment = evidence.Where(item => item.Role == "experimenter")
            .Select(item => ReadinessContracts.Parse<SquadDiscoveryFindings>(item.StructuredReturn!.Payload)).LastOrDefault();
        text.AppendLine("\n### Riskiest Assumption");
        text.Append("* ").Append(experiment?.RiskiestAssumption).Append("; proposed test: ").AppendLine(experiment?.ProposedTest ?? "");
        text.AppendLine("\n### Open Questions");
        foreach (var question in findings?.OpenQuestions ?? [])
        {
            text.Append("* ").AppendLine(Cell(question.Question));
        }
        text.AppendLine("\n### Discovery Gate");
        text.Append("* Permits Intake Dispatch: ").AppendLine(handoff == "pending" && brief is not null ? "yes" : "no");
        return text.ToString();
    }

    internal static string RenderIntake(IReadOnlyList<SquadArtifactEvidence> inputs, SquadDispatchEvidence evidence,
        SquadIntakeFindings findings, string verdict, int cycles, bool permits)
    {
        var text = new StringBuilder();
        text.Append("* Inputs Reviewed: ").AppendLine(string.Join(", ", inputs.Select(item => item.Path)));
        text.Append("* Validator Dispatched: ").AppendLine(evidence.Agent);
        text.Append("* Verdict: ").AppendLine(verdict);
        text.Append("* Remediation Cycles: ").AppendLine(cycles.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("\n### Findings\n\n| Dimension | Result | Blocking Gaps | Non-Blocking Gaps |\n| --- | --- | --- | --- |");
        foreach (var dimension in ReadinessContracts.IntakeDimensions)
        {
            var issues = findings.Issues.Where(issue => issue.Dimension == dimension).ToArray();
            text.Append("| ").Append(dimension).Append(" | ").Append(issues.Length == 0 ? "pass" : "fail")
                .Append(" | ").Append(Cell(string.Join("; ", issues.Where(issue => issue.Blocking).Select(Describe))))
                .Append(" | ").Append(Cell(string.Join("; ", issues.Where(issue => !issue.Blocking).Select(Describe)))).AppendLine(" |");
        }
        text.AppendLine("\n### Clarifying Questions");
        foreach (var question in findings.ClarifyingQuestions)
        {
            text.Append("* ").AppendLine(Cell(question));
        }
        text.AppendLine("\n### Recorded Assumptions");
        foreach (var assumption in findings.Assumptions)
        {
            text.Append("* ").Append(assumption.IssueId).Append(": ").AppendLine(Cell(assumption.Description));
        }
        text.AppendLine("\n### Intake Gate");
        text.Append("* Permits Downstream Dispatch: ").AppendLine(permits ? "yes" : "no");
        text.Append("* Blocking Gaps Outstanding: ").AppendLine(findings.Issues.Count(issue => issue.Blocking)
            .ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    internal static string RenderCouncil(SquadCouncilProposal proposal, SquadDispatchEvidence plan,
        IReadOnlyList<SquadDispatchEvidence> evidence, string verdict)
    {
        var text = new StringBuilder();
        text.Append("* Proposal Ref: ").AppendLine(plan.ArtifactPath);
        text.Append("* Council Members Dispatched: ").AppendLine(string.Join(", ", evidence.Select(item => item.Role)));
        text.Append("* Council Members Not Proposed: ").AppendLine(string.Join("; ",
            proposal.Lenses.Where(lens => !proposal.Roles.Contains(lens.Role, StringComparer.Ordinal))
                .Select(lens => lens.Role + ": " + lens.Reason)));
        text.Append("* Verdict: ").AppendLine(verdict);
        text.AppendLine("\n### Findings by Role\n\n| Role | Verdict | Risk | Blocking Issues | Conditions | Suggested Follow-ups |\n| --- | --- | --- | --- | --- | --- |");
        foreach (var item in evidence)
        {
            var findings = ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload);
            text.Append("| ").Append(item.Role).Append(" | ").Append(item.Verdict).Append(" | ").Append(item.Risk)
                .Append(" | ").Append(Cell(string.Join("; ", findings.Issues.Where(issue => issue.Blocking).Select(Describe))))
                .Append(" | ").Append(Cell(string.Join("; ", findings.Conditions.Select(condition => condition.Id + ": " + condition.Description))))
                .Append(" | ").Append(Cell(string.Join("; ", findings.FollowUps))).AppendLine(" |");
        }
        text.AppendLine("\n### Synthesis");
        text.AppendLine("* Blocking Issues: " + (evidence.Any(item => item.BlockingIssues > 0) ? "listed below" : "none"));
        text.AppendLine("* Conditions: " + (evidence.Any(item =>
            ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Conditions.Count > 0) ? "listed below" : "none"));
        text.AppendLine("* Suggested Follow-ups: " + (evidence.Any(item =>
            ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).FollowUps.Count > 0) ? "listed below" : "none"));
        foreach (var item in evidence)
        {
            var findings = ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload);
            foreach (var issue in findings.Issues.Where(issue => issue.Blocking))
            {
                text.Append("* Blocking Issue (").Append(item.Role).Append("): ").AppendLine(Describe(issue));
            }
            foreach (var condition in findings.Conditions)
            {
                text.Append("* Condition (").Append(item.Role).Append("): ").Append(condition.Id).Append(": ")
                    .AppendLine(Cell(condition.Description));
            }
            foreach (var followUp in findings.FollowUps)
            {
                text.Append("* Suggested Follow-up (").Append(item.Role).Append("): ").AppendLine(Cell(followUp));
            }
        }
        text.AppendLine("\n### Implementation Gate");
        text.Append("* Permits Implementation Dispatch: ").AppendLine(verdict == "Stop" ? "no" : "yes");
        text.Append("* Conditions Outstanding: ").AppendLine(evidence.Sum(item =>
            ReadinessContracts.Parse<SquadCouncilFindings>(item.StructuredReturn!.Payload).Conditions.Count).ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static string Describe(SquadGateIssue issue) => issue.Id + ": " + issue.Description +
        " (" + string.Join(", ", issue.Sources.Select(source => source.Path)) + ")";
}
