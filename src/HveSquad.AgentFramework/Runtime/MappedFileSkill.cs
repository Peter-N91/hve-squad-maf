using HveSquad.AgentFramework.Artifacts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// File-backed MAF skill with explicit canonical-name/deployed-path mapping. Unlike MAF directory
/// discovery, this accepts APM-renamed folders. Only explicitly registered guarded scripts exist.
/// </summary>
internal sealed class MappedFileSkill : AgentSkill
{
    private readonly SquadSkill _skill;
    private readonly ProjectFiles _files;
    private readonly IReadOnlyDictionary<string, AIFunction> _scripts;
    private readonly int _maximumCharacters;

    internal MappedFileSkill(SquadSkill skill, int maxCharacters, IReadOnlyDictionary<string, AIFunction>? scripts = null)
    {
        _skill = skill;
        _files = new ProjectFiles(skill.DirectoryPath, maxCharacters);
        _scripts = scripts ?? new Dictionary<string, AIFunction>();
        _maximumCharacters = maxCharacters;
        Frontmatter = new AgentSkillFrontmatter(skill.Name, skill.Description ?? $"Released {skill.Name} skill");
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _files.Read("SKILL.md");
        var content = _skill.Body;
        if (_scripts.Count != 0)
        {
            content += "\n\nRegistered host scripts (no other executable resources are authorized):\n" +
                string.Join("\n", _scripts.Select(script => $"{script.Key}: {script.Value.JsonSchema}"));
        }
        if (content.Length > _maximumCharacters)
        {
            throw new InvalidDataException("Registered script schemas and skill content exceed the configured artifact read limit.");
        }
        return ValueTask.FromResult(content);
    }

    public override ValueTask<AgentSkillScript?> GetScriptAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AgentSkillScript?>(_scripts.TryGetValue(name, out var function)
            ? new Script(name, function) : null);
    }

    private sealed class Script(string name, AIFunction function) : AgentSkillScript(name, function.Description)
    {
        public override JsonElement? ParametersSchema => function.JsonSchema;

        public override Task<object?> RunAsync(AgentSkill skill, JsonElement? arguments,
            IServiceProvider? serviceProvider = null, CancellationToken cancellationToken = default)
        {
            if (arguments is not { ValueKind: JsonValueKind.Object } payload)
            {
                throw new InvalidDataException("A registered host script requires an explicit JSON argument object.");
            }
            var values = new AIFunctionArguments(payload.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal))
            {
                Services = serviceProvider,
            };
            return function.InvokeAsync(values, cancellationToken).AsTask();
        }
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
