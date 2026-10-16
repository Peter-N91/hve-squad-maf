using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Runtime;

internal static partial class LearningContracts
{
    internal const string UpstreamPath = "squad-src/.github/skills/squad/learnings/shared-learnings.md";
    internal const string TenantPath = "squad-src/.github/skills/squad-learnings-tenant/tenant-learnings.md";
    internal static readonly string[] Columns =
        ["id", "Scenario / Trigger", "Learning / Rule", "Scope / Applicability", "Source Context", "Date Added"];
    private static readonly HashSet<string> GeneralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "all", "an", "and", "any", "as", "at", "avoid", "before", "by", "check", "compare", "confirm",
        "consumer", "consumers", "convention", "correction", "data", "dependency", "do", "each", "ensure",
        "evidence", "for", "from", "gate", "gates", "human", "if", "in", "independent", "it", "keep", "known",
        "local", "memory", "missing", "never", "no", "not", "note", "of", "on", "one", "only", "or", "original",
        "preserve", "read", "record", "records", "require", "review", "rule", "rules", "same", "scenario",
        "scenarios", "scope", "source", "store", "test", "the", "their", "then", "this", "to", "unknown",
        "use", "validate", "verification", "verified", "when", "with", "without", "write",
    };

    internal static SquadLearningTarget ValidateTarget(SquadLearningTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is not ("upstream" or "tenant") || !WatchContracts.IsRepository(target.Repository) ||
            target.DestinationRepository is not null && !WatchContracts.IsRepository(target.DestinationRepository))
        {
            throw new ArgumentException("Choose explicit upstream or tenant and structural owner/repository targets.", nameof(target));
        }
        var result = target with
        {
            Repository = target.Repository.ToLowerInvariant(),
            DestinationRepository = (target.DestinationRepository ?? target.Repository).ToLowerInvariant(),
        };
        if (result.Kind == "upstream" &&
            (result.Repository == result.DestinationRepository ||
                result.Repository.Split('/')[1] != result.DestinationRepository!.Split('/')[1]) ||
            result.Kind == "tenant" && result.Repository != result.DestinationRepository ||
            result.Kind == "tenant" && result.ForkOrganization)
        {
            throw new ArgumentException("Upstream promotion needs an explicit same-name fork; tenant promotion uses its explicit source repository.", nameof(target));
        }
        return result;
    }

    internal static string TargetPath(SquadLearningTarget target) => target.Kind == "upstream" ? UpstreamPath : TenantPath;
    internal static string Prefix(SquadLearningTarget target) => target.Kind == "upstream" ? "SL-" : "TL-";
    internal static string ActionId(string promotionId, string kind) =>
        HumanActions.Hash(new { PromotionId = promotionId, Kind = kind })[..32].ToLowerInvariant();
    internal static bool IsCommit([NotNullWhen(true)] string? value) => value is not null && Commit().IsMatch(value);
    internal static bool IsBranch([NotNullWhen(true)] string? value) => value is not null && value.Length <= 200 &&
        Branch().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("//", StringComparison.Ordinal) && !value.EndsWith(".lock", StringComparison.Ordinal);

    internal static SquadLearningDraft? Sanitize(SquadLearningDraft draft, IReadOnlyList<string> secrets,
        IReadOnlyList<string> names, int maximumCharacters)
    {
        var values = new[] { draft.Scenario, draft.Rule, draft.Scope, draft.SourceContext };
        if (values.Any(string.IsNullOrWhiteSpace) || values.Sum(value => (long)value.Length) > maximumCharacters)
        {
            return null;
        }
        if (values.Any(value => value.Contains("-----BEGIN", StringComparison.Ordinal) &&
            value.Contains("PRIVATE KEY", StringComparison.Ordinal) && !PrivateKey().IsMatch(value)))
        {
            return null;
        }
        string Scrub(string text)
        {
            foreach (var secret in secrets.OrderByDescending(secret => secret.Length))
            {
                text = text.Replace(secret, "[credential]", StringComparison.Ordinal);
            }
            text = PrivateKey().Replace(text, "[credential]");
            text = Credential().Replace(text, "[credential]");
            text = Url().Replace(text, "[url]");
            text = AbsolutePath().Replace(text, "[path]");
            text = Contact().Replace(text, "[contact]");
            foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)).OrderByDescending(name => name.Length))
            {
                text = Regex.Replace(text, Regex.Escape(name), "[name]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            text = NamedToken().Replace(text, match => GeneralWords.Contains(match.Value) ? match.Value : "[name]");
            return Whitespace().Replace(text, " ").Trim();
        }
        var clean = values.Select(Scrub).ToArray();
        if (clean.Any(value => !ReportSafety.IsSafe(value, secrets) || Url().IsMatch(value) ||
            AbsolutePath().IsMatch(value) || names.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase))) ||
            clean.Any(value => Placeholder().Replace(value, "").Count(char.IsLetter) < 4) ||
            Placeholder().Replace(clean[1], "").Count(char.IsLetter) < 12)
        {
            return null;
        }
        return new(clean[0], clean[1], clean[2], clean[3]);
    }

    internal static (SquadLearningEntry Entry, string Content, string Diff) Append(
        SquadLearningTarget target, SquadLearningTargetSnapshot snapshot, SquadLearningDraft draft, DateTimeOffset at, int maximumCharacters)
    {
        var path = TargetPath(target);
        if (!snapshot.Repository.Equals(target.Repository, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Path != path || !IsCommit(snapshot.CommitSha) || !IsCommit(snapshot.FileSha) ||
            !IsBranch(snapshot.BaseBranch) || string.IsNullOrWhiteSpace(snapshot.Content) ||
            snapshot.Content.Length > maximumCharacters)
        {
            throw new InvalidDataException("Learning preparation requires the exact bounded target file, branch, commit and blob snapshot.");
        }
        var table = MarkdownTables.Parse(snapshot.Content).SingleOrDefault(table =>
            table.Headers.SequenceEqual(Columns, StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The source playbook does not have the exact six-column released learning schema.");
        if (table.Rows.Count > 4096)
        {
            throw new InvalidDataException("Learning target row bound exceeded; no partial ID scan is allowed.");
        }
        var prefix = Prefix(target);
        var used = new HashSet<int>();
        foreach (var row in table.Rows)
        {
            var id = row["id"];
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || !EntryNumber().IsMatch(id[prefix.Length..]) ||
                !int.TryParse(id[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                !used.Add(number) || number <= 0 ||
                !DateOnly.TryParseExact(row["Date Added"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                Columns.Skip(1).Any(column => string.IsNullOrWhiteSpace(row[column])))
            {
                throw new InvalidDataException("Existing learning rows have invalid or duplicate identities, dates or fields; promotion is blocked.");
            }
            if (row["Scenario / Trigger"] == Cell(draft.Scenario) && row["Learning / Rule"] == Cell(draft.Rule) &&
                row["Scope / Applicability"] == Cell(draft.Scope))
            {
                throw new InvalidOperationException("This sanitized learning already exists in the source playbook; no duplicate row is prepared.");
            }
        }
        var next = checked(used.Count == 0 ? 1 : used.Max() + 1);
        if (next > 999_999_999)
        {
            throw new InvalidDataException("The released numeric learning identity range is exhausted.");
        }
        var entry = new SquadLearningEntry(prefix + next.ToString("D3", CultureInfo.InvariantCulture),
            draft.Scenario, draft.Rule, draft.Scope, draft.SourceContext,
            at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var lines = snapshot.Content.Split('\n');
        var header = Array.FindIndex(lines, line => line.Trim().StartsWith('|') &&
            line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).SequenceEqual(Columns, StringComparer.OrdinalIgnoreCase));
        if (header < 0)
        {
            throw new InvalidDataException("The exact source table insertion boundary is unavailable.");
        }
        var end = header + 2;
        while (end < lines.Length && lines[end].TrimStart().StartsWith('|'))
        {
            end++;
        }
        var offset = lines.Take(end).Sum(line => line.Length + 1);
        var rowText = Row(entry);
        var newline = snapshot.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefixText = snapshot.Content[..Math.Min(offset, snapshot.Content.Length)];
        var suffix = snapshot.Content[Math.Min(offset, snapshot.Content.Length)..];
        var separator = prefixText.EndsWith('\n') ? "" : newline;
        var content = prefixText + separator + rowText + newline + suffix;
        if (content.Length > maximumCharacters)
        {
            throw new InvalidDataException("The proposed target file exceeds its bounded size.");
        }
        var diff = prefixText.EndsWith('\n')
            ? $"--- a/{path}\n+++ b/{path}\n@@ -{end},0 +{end + 1},1 @@\n+{rowText}\n"
            : $"--- a/{path}\n+++ b/{path}\n@@ -{end},1 +{end},2 @@\n-{lines[end - 1].TrimEnd('\r')}\n" +
                "\\ No newline at end of file\n+" + lines[end - 1].TrimEnd('\r') + "\n+" + rowText + "\n";
        return (entry, content, diff);
    }

    internal static string Row(SquadLearningEntry entry) =>
        "| " + string.Join(" | ", new[] { entry.Id, entry.Scenario, entry.Rule, entry.Scope, entry.SourceContext, entry.DateAdded }.Select(Cell)) + " |";

    private static string Cell(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal).Replace('\\', '/').ReplaceLineEndings(" ");

    internal static bool AllowedSource(ProjectFiles files, SquadLearningSource source) =>
        HumanActions.IsHash(source.Sha256) && (source.Kind == "role-memory" && MemoryContracts.IsMemoryPath(files, source.Path) ||
            source.Kind == "decisions" && source.Path == files.SquadRoot + "/decisions.md" ||
            source.Kind == "history" && ProjectFiles.IsWithin(source.Path, files.SquadRoot + "/history") &&
            Path.GetExtension(source.Path) == ".md");

    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Commit();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._/-]*[a-zA-Z0-9]$|^[a-zA-Z0-9]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Branch();
    [GeneratedRegex("^[0-9]{3,9}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EntryNumber();
    [GeneratedRegex(@"-----BEGIN[^\r\n]*PRIVATE KEY-----[\s\S]*?-----END[^\r\n]*PRIVATE KEY-----", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"(?ix)\b(?:authorization|proxy-authorization)\s*:\s*[^\r\n]+|\bbearer\s+[a-z0-9._~+/-]+=*|\b(?:api[-_ ]?key|access[-_ ]?token|password|client[-_ ]?secret|connection[-_ ]?string)\s*[:=]\s*[^\r\n]+|(?:gh[pousr]_|github_pat_)[a-z0-9_]{8,}|\beyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Credential();
    [GeneratedRegex("""(?i)\b(?:https?|ftp)://[^\s<>"'`|]+""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Url();
    [GeneratedRegex("""(?i)(?<![\p{L}\p{N}])(?:[a-z]:[\\/]|\\\\|//)[^\r\n"'`|]*|(?<![\p{L}\p{N}])/(?:[^\s/<>"'`|]+/)*[^\s<>"'`|]+""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AbsolutePath();
    [GeneratedRegex(@"\b[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Contact();
    [GeneratedRegex(@"\b(?:\p{Lu}[\p{L}\p{M}]*(?:[-'][\p{L}\p{M}]+)*)\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NamedToken();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\[(?:credential|name|url|path|contact)\]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Placeholder();
}
