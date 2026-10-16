using System.Text.RegularExpressions;

namespace HveSquad.AgentFramework.Runtime;

internal static partial class PlanContractValidator
{
    private static readonly string[] AssignmentLabels = ["Goals", "Requirements", "Details", "Guidance", "References"];
    private static readonly string[] RequiredSections =
    [
        "Task Metadata", "Executive Summary", "Phase Checklist", "User Decisions and Requirements",
        "Planning Readiness and Next Step", "Goals", "Scope and Non-Goals", "Functional Requirements",
        "Non-Functional Requirements", "Risks and Open Questions", "Dependencies", "Sources",
        "Critique Disposition", "Artifact Self-Check", "Follow-Up Items", "Handoff",
    ];

    internal static void Verify(string plan) => _ = ReadTasks(plan);

    internal static IReadOnlyList<PlanTask> ReadTasks(string plan)
    {
        var body = OutsideFences(plan);
        var sections = SectionHeading().Matches(body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredSections.Where(s => !sections.Contains(s)).ToArray();
        if (missing.Length > 0 || !Title().IsMatch(body) || body.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Single-plan contract requires a resolved RPI plan with context sections. Missing: {string.Join(", ", missing)}.");
        }

        var sectionMatches = SectionHeading().Matches(body).ToArray();
        foreach (var section in RequiredSections)
        {
            var matches = sectionMatches.Where(m => m.Groups[1].Value == section).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"Single-plan context section '{section}' must occur exactly once.");
            }

            var match = matches[0];
            var next = sectionMatches.FirstOrDefault(m => m.Index > match.Index);
            var context = body[(match.Index + match.Length)..(next?.Index ?? body.Length)].Trim();
            if (context.Length == 0)
            {
                throw new InvalidDataException($"Single-plan context section '{section}' cannot be empty.");
            }

            if (section == "Planning Readiness and Next Step" &&
                (!Ready().IsMatch(context) || NotReady().IsMatch(context)))
            {
                throw new InvalidDataException("Single-plan planning readiness must explicitly permit implementation.");
            }
        }

        foreach (var metadata in new[] { "Task ID", "Task slug", "Plan date" })
        {
            if (!Regex.IsMatch(body, @"(?m)^\* " + Regex.Escape(metadata) + @":[ \t]*\S[^\r\n]*\r?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                throw new InvalidDataException($"Single-plan contract requires nonempty '{metadata}' metadata.");
            }
        }

        var markers = Marker().Matches(body).ToArray();
        if (markers.Length == 0 || MarkerStart().Count(body) != markers.Length ||
            PhaseTaskHeading().Count(body) != markers.Length)
        {
            throw new InvalidDataException("Single-plan contract requires paired phase/task markers and checklist headings.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var tasksByPhase = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentPhase = null;
        for (var index = 0; index < markers.Length; index++)
        {
            var marker = markers[index];
            var kind = marker.Groups[1].Value;
            var id = marker.Groups[2].Value;
            var headingLevel = marker.Groups[3].Value.Length;
            if (!ids.Add(id) || !marker.Groups[4].Value.StartsWith(id + ":", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Single-plan contract has a duplicate or mismatched marker '{id}'.");
            }

            if (kind == "phase")
            {
                if (id.Length != 3 || headingLevel != 3)
                {
                    throw new InvalidDataException($"Invalid phase marker '{id}'.");
                }

                currentPhase = id;
                tasksByPhase.Add(id, 0);
            }
            else
            {
                if (headingLevel != 4 || id.Length != 7 || currentPhase is null ||
                    !id.StartsWith(currentPhase + "-T", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Task '{id}' must belong to its containing phase.");
                }

                tasksByPhase[currentPhase]++;
            }

            var end = index + 1 < markers.Length ? markers[index + 1].Index : body.Length;
            var block = body[(marker.Index + marker.Length)..end];
            blocks.Add(id, block);
            foreach (var label in kind == "task"
                         ? new[] { "Goals", "Requirements", "Details", "References", "Dependencies" }
                         : new[] { "Goals", "Dependencies" })
            {
                if (string.IsNullOrWhiteSpace(Context(block, label)))
                {
                    throw new InvalidDataException($"Single-plan '{id}' requires nonempty '{label}:' context.");
                }
            }
        }

        if (tasksByPhase.Count == 0 || tasksByPhase.Values.Any(count => count == 0))
        {
            throw new InvalidDataException("Every single-plan phase must contain at least one task.");
        }

        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (id, block) in blocks)
        {
            var refs = DependencyId().Matches(Context(block, "Dependencies"))
                .Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (refs.Any(dependency => !ids.Contains(dependency) || dependency == id))
            {
                throw new InvalidDataException($"Single-plan '{id}' names a missing or self dependency.");
            }

            dependencies.Add(id, id.Length == 3
                ? [.. refs, .. ids.Where(child => child.StartsWith(id + "-T", StringComparison.Ordinal))]
                : refs);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            Visit(id, dependencies, visited, visiting);
        }

        IEnumerable<string> Expand(string id) => id.Length == 7 ? [id] :
            ids.Where(child => child.StartsWith(id + "-T", StringComparison.Ordinal));
        return markers.Where(marker => marker.Groups[1].Value == "task").Select(marker =>
        {
            var id = marker.Groups[2].Value;
            var block = blocks[id];
            var next = markers.FirstOrDefault(candidate => candidate.Index > marker.Index);
            var original = plan[(marker.Index + marker.Length)..(next?.Index ?? plan.Length)];
            var phaseDependencies = DependencyId().Matches(Context(blocks[id[..3]], "Dependencies"))
                .Select(match => match.Value);
            var taskDependencies = DependencyId().Matches(Context(block, "Dependencies")).Select(match => match.Value);
            return new PlanTask(id, marker.Value.Contains("[x]", StringComparison.OrdinalIgnoreCase),
                string.Join("\n\n", AssignmentLabels
                    .Select(label => label + ":\n" + Context(block, label, original))),
                taskDependencies.Concat(phaseDependencies).SelectMany(Expand).Distinct(StringComparer.Ordinal).ToArray(),
                Context(block, "References", original));
        }).ToArray();
    }

    internal static string? ReadSection(string plan, string name)
    {
        var body = OutsideFences(plan);
        var headings = SectionHeading().Matches(body).ToArray();
        var matches = headings.Where(heading => heading.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException($"Plan section '{name}' must occur at most once.");
        }
        if (matches.Length == 0)
        {
            return null;
        }
        var match = matches[0];
        var next = headings.FirstOrDefault(heading => heading.Index > match.Index);
        return body[(match.Index + match.Length)..(next?.Index ?? body.Length)];
    }

    private static void Visit(string id, Dictionary<string, string[]> dependencies,
        HashSet<string> visited, HashSet<string> visiting)
    {
        if (visited.Contains(id))
        {
            return;
        }

        if (!visiting.Add(id))
        {
            throw new InvalidDataException($"Single-plan dependencies contain a cycle at '{id}'.");
        }

        foreach (var dependency in dependencies[id])
        {
            Visit(dependency, dependencies, visited, visiting);
        }

        visiting.Remove(id);
        visited.Add(id);
    }

    private static string Context(string block, string label, string? original = null)
    {
        var labels = ContextLabel().Matches(block).ToArray();
        for (var index = 0; index < labels.Length; index++)
        {
            if (labels[index].Groups[1].Value == label)
            {
                var start = labels[index].Index + labels[index].Length;
                var end = index + 1 < labels.Length ? labels[index + 1].Index : block.Length;
                var context = block[start..end];
                var section = SectionHeading().Match(context);
                var text = (original ?? block)[start..end];
                return (section.Success ? text[..section.Index] : text).Trim();
            }
        }

        return "";
    }

    private static string OutsideFences(string text)
    {
        var result = text.ToCharArray();
        string? fence = null;
        var offset = 0;
        while (offset < text.Length)
        {
            var end = text.IndexOf('\n', offset);
            if (end < 0)
            {
                end = text.Length;
            }
            var line = text[offset..end].TrimEnd('\r');
            var match = Fence().Match(line);
            var hidden = fence is not null || match.Success;
            if (match.Success)
            {
                var candidate = match.Groups[1].Value;
                if (fence is null)
                {
                    fence = candidate;
                }
                else if (candidate[0] == fence[0] && candidate.Length >= fence.Length &&
                         string.IsNullOrWhiteSpace(line[match.Length..]))
                {
                    fence = null;
                }
            }
            if (hidden)
            {
                Array.Fill(result, ' ', offset, end - offset);
            }
            offset = end + 1;
        }

        if (fence is not null)
        {
            throw new InvalidDataException("Single-plan contract has an unclosed code fence.");
        }

        return new string(result);
    }

    [GeneratedRegex(@"(?m)^# RPI Plan: \S[^\r\n]*\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex Title();
    [GeneratedRegex(@"(?m)^## ([^\r\n]+)\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();
    [GeneratedRegex(@"(?m)^<!-- rpi:(phase|task) id=(P[0-9]{2}(?:-T[0-9]{2})?) -->\r?\n(#{3,4}) \[(?: |x|X)\] ([^\r\n]+)\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
    [GeneratedRegex(@"(?m)^<!-- rpi:(?:phase|task)\b", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerStart();
    [GeneratedRegex(@"(?m)^#{3,4} \[(?: |x|X)\] P[0-9]{2}(?:-T[0-9]{2})?:", RegexOptions.CultureInvariant)]
    private static partial Regex PhaseTaskHeading();
    [GeneratedRegex(@"(?m)^(Goals|Requirements|Details|Guidance|References|Dependencies):[ \t]*\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextLabel();
    [GeneratedRegex(@"\bP[0-9]{2}(?:-T[0-9]{2})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyId();
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();
    [GeneratedRegex(@"\bready\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ready();
    [GeneratedRegex(@"\bnot[\s-]*ready\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotReady();
}

internal sealed record PlanTask(string Id, bool Checked, string Context, IReadOnlyList<string> Dependencies, string References);
