using System.Text;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;

namespace HveSquad.AgentFramework.Runtime;

internal sealed class RuntimeResources
{
    private readonly SquadArtifacts _artifacts;
    private readonly SquadArtifactRoots _roots;
    private readonly SquadSkill _squad;

    internal RuntimeResources(SquadArtifacts artifacts, SquadArtifactRoots roots)
    {
        _artifacts = artifacts;
        _roots = roots;
        _squad = artifacts.Skills.SingleOrDefault(s => s.Name.Equals("squad", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The release must contain exactly one skill named 'squad'.");
        foreach (var name in CoordinatorReferences.Concat(["seed-templates", "scribe-procedure", "entry-schemas"]))
        {
            _ = Reference(name);
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instruction in artifacts.Instructions)
        {
            if (!paths.TryAdd(instruction.FileName, instruction.SourcePath))
            {
                throw new InvalidDataException($"Ambiguous instruction mapping: {instruction.FileName}");
            }

            paths[$".github/instructions/{(IsSquadInstruction(instruction) ? "squad/" : "")}{instruction.FileName}"] = instruction.SourcePath;
        }

        foreach (var skill in artifacts.Skills)
        {
            paths[$"skill:{skill.Name}"] = skill.DirectoryPath;
            paths[$".github/skills/{skill.Name}/SKILL.md"] = skill.SourcePath;
        }

        Paths = paths;
    }

    private static readonly string[] CoordinatorReferences =
        ["00-index", "profiles-and-packs", "operating-procedure", "gates-and-modes"];

    internal IReadOnlyDictionary<string, string> Paths { get; }

    internal string Fingerprint()
    {
        var text = new StringBuilder()
            .Append(_roots.Release?.Tag ?? "directory").Append('\n')
            .Append(_roots.Release?.CommitSha ?? "unpublished").Append('\n');
        foreach (var root in _roots.Roots)
        {
            foreach (var file in SafeFiles(root).Order(StringComparer.Ordinal))
            {
                text.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append('\n')
                    .Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))
                    .Append('\n');
            }
        }

        return ProjectFiles.Hash(text.ToString());
    }

    internal static IEnumerable<string> SafeFiles(string root)
    {
        ProjectFiles.RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            ProjectFiles.RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in SafeFiles(entry))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    internal string Instructions(SquadRosterEntry? role, SquadRuntimeOptions options, bool initializing)
    {
        var result = new StringBuilder();
        foreach (var instruction in _artifacts.Instructions.Where(i =>
                     IsSquadInstruction(i) || options.InstructionFilter?.Invoke(role, i) == true))
        {
            result.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"\n## Released instruction: {instruction.FileName}\n{instruction.Body}");
        }

        if (role is null)
        {
            foreach (var name in CoordinatorReferences.Concat(initializing ? ["seed-templates"] : []))
            {
                result.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"\n## Released squad reference: {name}\n{Reference(name)}");
            }
        }

        result.AppendLine("\n## Native artifact name/path mapping (read-only; deployed names may differ)");
        foreach (var entry in Paths)
        {
            result.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Key} => {entry.Value}");
        }

        result.AppendLine("\nOther instruction documents are available with read_instruction by exact file name. " +
            "Select applicable repository conventions, not every unrelated language's conventions.");
        return result.ToString();
    }

    internal string ReadInstruction(string fileName) => _artifacts.Instructions
        .SingleOrDefault(i => i.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Body
        ?? throw new InvalidOperationException($"Unknown instruction '{fileName}'. Use the supplied mapping.");

    internal AgentSkillsProvider Skills(SquadRosterEntry? role, SquadRuntimeOptions options)
    {
        var selected = _artifacts.Skills.Where(s => role is null
            ? s.Name.Equals("squad", StringComparison.OrdinalIgnoreCase)
            : options.SkillFilter?.Invoke(role, s) != false).ToArray();
        // MAF's discovery source rejects APM-renamed folders whose names differ from frontmatter.
        // Bind the actual released files by canonical name instead; native provider tools still
        // load/read them, without copying files or ever providing an executable skill script.
        return new AgentSkillsProvider(selected.Select(s => new MappedFileSkill(s, options.MaxArtifactCharacters)),
            new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
            }, options.LoggerFactory);
    }

    private string Reference(string name)
    {
        var file = Path.Combine(_squad.DirectoryPath, "references", name + ".md");
        ProjectFiles.RejectLinks(file);
        return File.ReadAllText(file);
    }

    private static bool IsSquadInstruction(SquadInstruction instruction) =>
        instruction.FileName.StartsWith("squad-", StringComparison.OrdinalIgnoreCase) ||
        instruction.SourcePath.Replace('\\', '/').Contains("/instructions/squad/", StringComparison.OrdinalIgnoreCase);
}
