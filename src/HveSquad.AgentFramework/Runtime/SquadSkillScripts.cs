using System.Text.Json;
using System.Text.RegularExpressions;
using HveSquad.AgentFramework.Artifacts;
using HveSquad.AgentFramework.Sources;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>An explicit, non-secret host binding to one canonical released script and executor revision.</summary>
/// <param name="SkillName">The installed frontmatter name, not an APM directory name.</param>
/// <param name="ResourcePath">The exact skill-relative script path.</param>
/// <param name="Release">Actual acquisition provenance; null denotes non-official local artifacts.</param>
/// <param name="SkillSha256">Digest of the canonical skill entry point.</param>
/// <param name="ResourceSha256">Digest of the executable resource.</param>
/// <param name="ExecutorBinding">Opaque host implementation/configuration revision or executor digest.</param>
public sealed record SquadSkillScriptBinding(
    string SkillName, string ResourcePath, SquadRelease? Release,
    string SkillSha256, string ResourceSha256, string ExecutorBinding)
{
    /// <summary>Resolves a canonical resource from actual acquired artifacts without guessing folders or granting permission.</summary>
    /// <param name="roots">The exact host-acquired artifact roots and provenance.</param>
    /// <param name="skillName">The canonical installed skill name.</param>
    /// <param name="resourcePath">The exact skill-relative executable resource path.</param>
    /// <param name="executorBinding">A non-secret stable binding which changes when the executor or its scopes change.</param>
    /// <returns>The immutable binding to use on a trusted <see cref="SquadToolRegistration"/>.</returns>
    public static SquadSkillScriptBinding Create(
        SquadArtifactRoots roots, string skillName, string resourcePath, string executorBinding)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var artifacts = SquadArtifactLoader.Load(roots.Roots, roots.RosterPath);
        var skill = SkillScriptContracts.Find(artifacts, skillName, resourcePath);
        var files = new ProjectFiles(skill.DirectoryPath, 200_000);
        var binding = new SquadSkillScriptBinding(skill.Name, resourcePath, roots.Release,
            files.HashFile("SKILL.md"), files.HashFile(resourcePath), executorBinding);
        SkillScriptContracts.ValidateShape(binding);
        return binding;
    }
}

/// <summary>Protected execution scope captured before approval/execution, never inferred from script text.</summary>
/// <param name="Binding">The exact canonical resource and trusted executor.</param>
/// <param name="Inputs">Authorized project input paths and original byte digests.</param>
/// <param name="ProjectWritePaths">The same pre-execution outputs claimed by the dispatch.</param>
public sealed record SquadSkillExecution(
    SquadSkillScriptBinding Binding, IReadOnlyList<SquadArtifactEvidence> Inputs,
    IReadOnlyList<string> ProjectWritePaths);

internal static partial class SkillScriptContracts
{
    internal static SquadSkill Find(SquadArtifacts artifacts, string skillName, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (!resourcePath.StartsWith("scripts/", StringComparison.Ordinal) ||
            resourcePath.Contains('\\', StringComparison.Ordinal) ||
            resourcePath.Split('/').Any(part => part.Length == 0 || part is "." or "..") ||
            Path.GetExtension(resourcePath) is not (".ps1" or ".py" or ".sh"))
        {
            throw new InvalidDataException("A registered script requires its exact canonical scripts/ resource path and a supported script extension.");
        }
        if (skillName == "squad")
        {
            throw new InvalidDataException(
                "Squad state scripts are not generic executors. Use the deterministic native Scribe/Consumption results and GetModelRoutingChoicesAsync; Copilot-schema scripts cannot write native state.");
        }
        var skill = artifacts.Skills.SingleOrDefault(skill => skill.Name.Equals(skillName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Canonical skill '{skillName}' is not installed.");
        var files = new ProjectFiles(skill.DirectoryPath, 200_000);
        if (!File.Exists(files.Resolve(resourcePath)))
        {
            throw new InvalidDataException($"Canonical resource '{skillName}/{resourcePath}' is not installed.");
        }
        return skill;
    }

    internal static void ValidateShape(SquadSkillScriptBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.SkillName) || string.IsNullOrWhiteSpace(binding.ResourcePath) ||
            !Digest().IsMatch(binding.SkillSha256 ?? "") || !Digest().IsMatch(binding.ResourceSha256 ?? "") ||
            !ExecutorId().IsMatch(binding.ExecutorBinding ?? "") ||
            binding.Release is { } release && (string.IsNullOrWhiteSpace(release.Tag) ||
                !Commit().IsMatch(release.CommitSha ?? "")))
        {
            throw new ArgumentException("Skill scripts require exact resource/entry digests, release provenance and a non-secret executor binding.");
        }
    }

    internal static void Verify(SquadArtifacts artifacts, SquadArtifactRoots roots, SquadSkillScriptBinding binding)
    {
        ValidateShape(binding);
        var skill = Find(artifacts, binding.SkillName, binding.ResourcePath);
        var files = new ProjectFiles(skill.DirectoryPath, 200_000);
        if (binding.Release != roots.Release || files.HashFile("SKILL.md") != binding.SkillSha256 ||
            files.HashFile(binding.ResourcePath) != binding.ResourceSha256)
        {
            throw new InvalidDataException("The registered skill's release, canonical entry point or executable resource changed. Rebind and review the trusted capability.");
        }
    }

    internal static void ValidateCall(
        IReadOnlyList<SquadSkillScriptBinding> bindings, IDictionary<string, object?> arguments, RuntimeGuard guard)
    {
        static string? Text(IDictionary<string, object?> values, string name) =>
            values.TryGetValue(name, out var value) ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                _ => null,
            } : null;
        var skill = Text(arguments, "skillName");
        var script = Text(arguments, "scriptName");
        if (!bindings.Any(binding => binding.SkillName == skill && binding.ResourcePath == script))
        {
            throw guard.Stop(SquadRunStatus.Blocked,
                "This canonical skill/script is not registered for the invoking role and charter; no executor was invoked.");
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
    [GeneratedRegex("^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Commit();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:_-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutorId();
}

public sealed partial class SquadRuntime
{
    private void VerifySkillRegistrations()
    {
        foreach (var registration in _options.Tools.Where(tool => tool.SkillScript is not null))
        {
            SkillScriptContracts.Verify(Artifacts, Provenance, registration.SkillScript!);
        }
        if (_options.Tools.Where(tool => tool.SkillScript is not null)
            .GroupBy(tool => tool.SkillScript!.SkillName + "/" + tool.SkillScript.ResourcePath,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("One canonical skill script may have only one trusted executor registration.");
        }
    }

    private sealed partial class RunContext
    {
        private SquadSkillExecution BindSkillExecution(
            SquadToolRegistration registration, AIFunctionArguments arguments,
            DispatchCheckpoint dispatch, IReadOnlyList<string>? outputs)
        {
            var binding = registration.SkillScript
                ?? throw new InvalidOperationException("A script execution requires a registered binding.");
            SkillScriptContracts.Verify(runtime.Artifacts, runtime.Provenance, binding);
            if (runtime._resources.Fingerprint() != runtime.ArtifactFingerprint)
            {
                throw _guard.Stop(SquadRunStatus.Blocked, "The exact acquired release artifacts changed before script execution.");
            }
            var paths = registration.ProjectReadPaths?.Invoke(arguments)
                ?? throw new InvalidDataException("Registered scripts require a trusted input-scope projection, including an explicit empty scope.");
            if (paths.Count > runtime._options.MaxArtifactsPerDispatch ||
                outputs?.Count > runtime._options.MaxArtifactsPerDispatch)
            {
                throw _guard.Stop(SquadRunStatus.LimitReached, "Registered script input/output scope exceeds MaxArtifactsPerDispatch.");
            }
            var inputs = new List<SquadArtifactEvidence>();
            foreach (var path in paths.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    runtime._files.CheckAgentRead(path,
                        runtime._options.InputPaths.Concat(checkpoint.Readiness?.Inputs.Select(input => input.Path) ?? []));
                    CheckDispatchRead(path, dispatch);
                }
                catch (InvalidOperationException exception)
                {
                    throw _guard.Stop(SquadRunStatus.Blocked, exception.Message);
                }
                if (runtime._files.IsProtectedStatePath(path) || ProjectFiles.IsWithin(path, ".git"))
                {
                    throw _guard.Stop(SquadRunStatus.Blocked,
                        "Generic scripts cannot consume private native state or Git metadata as file inputs. Use a native equivalent or an explicitly scoped host integration.");
                }
                inputs.Add(new(binding.SkillName, path, runtime._files.HashFile(path)));
            }
            return new(binding, inputs.ToArray(), outputs?.ToArray() ?? []);
        }
    }
}
