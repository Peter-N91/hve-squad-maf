using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

public sealed partial class SquadRuntime
{
    private sealed record RouteMatch(SquadRoutingRule Rule, string Pattern, int Start, int Length);

    private RouteMatch[] MatchRoutes(string request)
    {
        return RoutingPatterns.Match(request, Catalog.RoutingRules, rule => rule.Patterns)
            .Select(match => new RouteMatch(match.Owner, match.Pattern, match.Start, match.Length)).ToArray();
    }

    internal static class RoutingPatterns
    {
        internal sealed record PatternMatch<T>(T Owner, string Pattern, int Start, int Length);

        internal static PatternMatch<T>[] Match<T>(string request, IEnumerable<T> owners, Func<T, IEnumerable<string>> patterns)
        {
            var matches = owners.SelectMany(owner => patterns(owner).Where(pattern => pattern != "*").SelectMany(pattern =>
                Regex.Matches(request, @"(?<![\p{L}\p{N}])" +
                    Regex.Escape(pattern).Replace("\\ ", @"\s+", StringComparison.Ordinal) + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    .Select(match => new PatternMatch<T>(owner, pattern, match.Index, match.Length)))).ToArray();
            return matches.Where(match => !matches.Any(other => other.Length > match.Length &&
                other.Start <= match.Start && other.Start + other.Length >= match.Start + match.Length)).ToArray();
        }
    }

    private (AgentCharter Charter, string Rationale) SelectAgent(SquadRosterEntry entry, string request, string task)
    {
        var selected = _options.AgentSelector?.Invoke(new(entry, request, task));
        var clauses = AlternateClauses(entry);
        if (selected is null)
        {
            var matching = clauses.Where(clause => CueMatches(request + "\n" + task, clause.Cue)).ToArray();
            var agents = matching.Select(c => c.Agent).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (agents.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Selection Cues for '{entry.Role}' match multiple alternates. Supply a trusted AgentSelector with the exact cue and rationale.");
            }
            selected = matching.FirstOrDefault();
        }
        if (selected is null || selected.Agent.Equals(entry.PrimaryAgent, StringComparison.OrdinalIgnoreCase))
        {
            return (Catalog.ResolveAgent(entry), "Primary: no justified canonical alternate Selection Cue.");
        }
        if (string.IsNullOrWhiteSpace(selected.Rationale) || selected.Rationale.Length > 2000 ||
            !clauses.Any(clause => clause.Agent.Equals(selected.Agent, StringComparison.OrdinalIgnoreCase) &&
                clause.Cue.Equals(selected.Cue, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Alternate selection for '{entry.Role}' must cite that exact agent's canonical Selection Cue and a bounded rationale.");
        }
        return (Catalog.ResolveAgent(entry, selected.Agent), $"Selection Cue: {selected.Cue}. {selected.Rationale}");
    }

    private static SquadAgentSelection[] AlternateClauses(SquadRosterEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SelectionCue))
        {
            return [];
        }
        return entry.AlternateAgents.SelectMany(agent => Regex.Matches(entry.SelectionCue,
                @"(?:^|[;.\n])\s*(?<cue>[^;\n]*?)\s*→\s*" + Regex.Escape(agent) + @"(?=$|[;.,]|\s*\()",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => new SquadAgentSelection(agent, match.Groups["cue"].Value.Trim(),
                "The actual request/assignment matches this installed cue."))).ToArray();
    }

    private static bool CueMatches(string request, string cue)
    {
        var words = Regex.Matches(cue, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Value).Where(word => word.Length > 2 &&
                word.ToLowerInvariant() is not ("the" or "for" or "and" or "one" or "single" or "against" or "about" or "this"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return words.Length >= 2 && words.All(word =>
            Regex.IsMatch(request, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    private string MissingRoleDiagnostic(string role)
    {
        var resources = Catalog.RegisteredResources.Where(resource => resource.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).ToArray();
        return $"Required role '{role}' is off-roster. Add the role with a confirmed roster update or switch profiles; no substitution is allowed." +
            (resources.Length == 0 ? "" : "\nRegistered prerequisite/install guidance:\n" +
                string.Join("\n", resources.Select(resource => resource.Resource + ": " + resource.InstallOrEntry +
                    "; prerequisites: " + resource.Prerequisites)));
    }

    private sealed partial class RunContext
    {
        private async Task<(string Role, SquadRunScope Scope, bool Council, IReadOnlyList<string>? Patterns, string? Rationale)>
            ResolveOwningRouteAsync(string proposedRole, SquadRunScope scope)
        {
            if (checkpoint.Routing is { } saved)
            {
                return (saved.ProducingRole, saved.Scope, saved.RequiresCouncil, saved.MatchedPatterns, saved.RouteRationale);
            }
            if (scope != SquadRunScope.Delivery)
            {
                return (proposedRole, scope, false, null, null);
            }
            var matches = runtime.MatchRoutes(request);
            if (matches.Length == 0)
            {
                return (proposedRole, scope, false, null,
                    "No unambiguous literal catalog intent; the coordinator's owning-role proposal requires human confirmation.");
            }
            if (matches.Any(match => match.Rule.AutonomyTier == "escalate"))
            {
                throw _guard.Stop(SquadRunStatus.Blocked,
                    "The canonical route is escalate-only. No role was dispatched; this operation needs an explicit supported host integration and human decision.");
            }
            var councilRoles = new[] { "architect", "security", "cost-manager", "product-owner", "rai" };
            var council = matches.Any(match => match.Rule.Roles.Count > 1 &&
                match.Rule.Roles.All(role => councilRoles.Contains(role, StringComparer.OrdinalIgnoreCase)));
            var ordinary = matches.Where(match => !(match.Rule.Roles.Count > 1 &&
                match.Rule.Roles.All(role => councilRoles.Contains(role, StringComparer.OrdinalIgnoreCase)))).ToArray();
            var candidates = ordinary.SelectMany(match => match.Rule.Roles.Except(match.Rule.OptionalRoles, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var owner = proposedRole;
            if (council && candidates.Length == 0)
            {
                owner = "lead";
                scope = SquadRunScope.Plan;
            }
            if (candidates.Length == 1)
            {
                owner = candidates[0];
            }
            else if (candidates.Length > 1)
            {
                owner = await AskQuestionAsync(new("routing",
                    "These canonical patterns match different outcomes: " + string.Join("; ", ordinary.Select(match =>
                        match.Pattern + " -> " + string.Join(", ", match.Rule.Roles))) +
                    ". Choose the role that most directly owns the requested outcome; disjoint intents are not ranked by keyword length.",
                    candidates), reuseConfirmed: true).ConfigureAwait(false);
                if (!candidates.Contains(owner, StringComparer.OrdinalIgnoreCase))
                {
                    throw _guard.Stop(SquadRunStatus.InputRequired, "Choose one of the matched canonical owning roles; no substitute was dispatched.");
                }
            }
            if (runtime.Roster.Resolve(owner) is null)
            {
                throw _guard.Stop(SquadRunStatus.Blocked, runtime.MissingRoleDiagnostic(owner));
            }
            var resolvedScope = owner switch
            {
                "tester" or "researcher" when council => SquadRunScope.Plan,
                "researcher" => SquadRunScope.Research,
                "lead" => SquadRunScope.Plan,
                "tester" => SquadRunScope.Review,
                _ => scope,
            };
            if (council && owner is "tester" or "researcher")
            {
                owner = "lead";
            }
            return (owner, resolvedScope, council, matches.Select(match => match.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                "Most-specific contained intent wins; distinct outcome conflicts use a confirmed human owner choice.");
        }
    }
}
