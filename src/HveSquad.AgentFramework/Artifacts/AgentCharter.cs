namespace HveSquad.AgentFramework.Artifacts;

/// <summary>
/// A squad agent charter parsed from a <c>*.agent.md</c> file.
/// </summary>
public sealed class AgentCharter
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Charters with <c>user-invocable: false</c> are dispatch-only; a host must not surface them
    /// as entry points.
    /// </summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>
    /// Whether the host must prevent model/subagent invocation. This is independent of
    /// <see cref="UserInvocable"/>.
    /// </summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>Tool identifiers declared by the charter's <c>tools:</c> frontmatter.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Ordered <c>model:</c> preferences, most preferred first (host-specific names).</summary>
    public IReadOnlyList<string> ModelPreferences { get; init; } = [];

    /// <summary>Charter names this agent is permitted to delegate to, from <c>agents:</c>.</summary>
    public IReadOnlyList<string> DelegateAgents { get; init; } = [];

    /// <summary>The markdown body below the frontmatter, used verbatim as agent instructions.</summary>
    public required string Instructions { get; init; }

    public required string SourcePath { get; init; }
}
