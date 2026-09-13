using HveSquad.AgentFramework.Artifacts;
using Microsoft.Agents.AI;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// File-backed MAF skill with explicit canonical-name/deployed-path mapping. Unlike MAF directory
/// discovery, this accepts APM-renamed folders. No script object or runner is ever supplied.
/// </summary>
internal sealed class MappedFileSkill : AgentSkill
{
    private readonly SquadSkill _skill;
    private readonly ProjectFiles _files;

    internal MappedFileSkill(SquadSkill skill, int maxCharacters)
    {
        _skill = skill;
        _files = new ProjectFiles(skill.DirectoryPath, maxCharacters);
        Frontmatter = new AgentSkillFrontmatter(skill.Name, skill.Description ?? $"Released {skill.Name} skill");
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _files.Read("SKILL.md");
        return ValueTask.FromResult(_skill.Body);
    }

    public override ValueTask<AgentSkillResource?> GetResourceAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _files.Resolve(name);
        return ValueTask.FromResult<AgentSkillResource?>(File.Exists(path) ? new Resource(_files, name) : null);
    }

    private sealed class Resource(ProjectFiles files, string name) : AgentSkillResource(name)
    {
        public override Task<object?> ReadAsync(IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(files.Read(Name));
        }
    }
}
