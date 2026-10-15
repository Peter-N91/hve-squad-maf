using System.Text;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

        var planners = artifacts.Skills.Where(s => s.Name.Equals("rpi-plan", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (planners.Length > 1)
        {
            throw new InvalidDataException("Multiple canonical 'rpi-plan' skills remain after precedence.");
        }

        if (planners.Length == 1)
        {
            if (artifacts.Instructions.Count(i =>
                    i.FileName.Equals("squad-floor.instructions.md", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                throw new InvalidDataException(
                    "The methodology installation must contain exactly one 'squad-floor.instructions.md'. Reacquire the complete pinned artifacts.");
            }

            var planFiles = new ProjectFiles(planners[0].DirectoryPath, 200_000);
            _ = planFiles.Read("templates/implementation-plan.md");
            RequiresSeparatePlanDetails = File.Exists(planFiles.Resolve("templates/implementation-details.md"));
        }
        else if (roots.Release is { } release &&
                 release.Tag != Sources.SupportedSquadRelease.Current.LegacyAdvisoryRelease)
        {
            throw new InvalidDataException(
                "An official current/candidate release must contain the canonical 'rpi-plan' skill and plan template.");
        }
        else
        {
            RequiresSeparatePlanDetails = true;
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
    internal bool RequiresSeparatePlanDetails { get; }

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
                     i.FileName.Equals("squad-floor.instructions.md", StringComparison.OrdinalIgnoreCase) ||
                     options.InstructionFilter?.Invoke(role, i) == true))
        {
            result.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"\n## Released instruction: {instruction.FileName}\n{instruction.Body}");
        }

        if (role is null && initializing)
        {
            result.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"\n## Released squad reference: seed-templates\n{Reference("seed-templates")}");
        }

        result.AppendLine("\n## Native instruction inventory (read on demand by exact file name)");
        foreach (var instruction in _artifacts.Instructions)
        {
            result.AppendLine(instruction.FileName);
        }

        result.AppendLine("\nUse load_skill/read_skill_resource with canonical skill names. " +
            "For coordinator procedure load 'squad' and read references/operating-procedure.md and " +
            "references/gates-and-modes.md as applicable. Role/mode-specific instructions and cold references " +
            "are available on demand, not injected wholesale. Select applicable conventions only.");
        return result.ToString();
    }

    internal string ReadInstruction(string fileName) => _artifacts.Instructions
        .SingleOrDefault(i => i.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Body
        ?? throw new InvalidOperationException($"Unknown instruction '{fileName}'. Use the supplied mapping.");

    internal AgentSkillsProvider Skills(SquadRosterEntry? role, SquadRuntimeOptions options,
        IReadOnlyDictionary<string, AIFunction>? registeredScripts = null)
    {
        var selected = _artifacts.Skills.Where(s => role is null
            ? s.Name.Equals("squad", StringComparison.OrdinalIgnoreCase)
            : options.SkillFilter?.Invoke(role, s) != false).ToArray();
        // MAF's discovery source rejects APM-renamed folders whose names differ from frontmatter.
        // Bind the actual released files by canonical name instead; native provider tools still
        // load/read them without copying files. Executable resources use only guarded host bindings.
        return new AgentSkillsProvider(selected.Select(s => new MappedFileSkill(s, options.MaxArtifactCharacters,
            options.Tools.Where(tool => tool.SkillScript?.SkillName == s.Name &&
                registeredScripts?.ContainsKey(tool.Function.Name) == true)
                .ToDictionary(tool => tool.SkillScript!.ResourcePath, tool => registeredScripts![tool.Function.Name],
                    StringComparer.Ordinal))),
            new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                DisableRunSkillScriptApproval = true,
            }, options.LoggerFactory);
    }

    internal string? OptionalReference(string name)
    {
        var files = new ProjectFiles(_squad.DirectoryPath, 200_000);
        var path = "references/" + name + ".md";
        return File.Exists(files.Resolve(path)) ? files.Read(path) : null;
    }

    internal string Reference(string name)
    {
        return new ProjectFiles(_squad.DirectoryPath, 200_000).Read("references/" + name + ".md");
    }

    private static bool IsSquadInstruction(SquadInstruction instruction) =>
        instruction.FileName.StartsWith("squad-", StringComparison.OrdinalIgnoreCase) ||
        instruction.SourcePath.Replace('\\', '/').Contains("/instructions/squad/", StringComparison.OrdinalIgnoreCase);
}
