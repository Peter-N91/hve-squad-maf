using HveSquad.AgentFramework.Artifacts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Agents;

public sealed class SquadAgentFactoryOptions
{
    /// <summary>
    /// Chooses the chat client for a charter, letting the charter's <c>model:</c> preferences drive
    /// provider selection. Falls back to the factory's default client when null or when it returns null.
    /// </summary>
    public Func<AgentCharter, IChatClient?>? ChatClientSelector { get; set; }

    /// <summary>Wraps each agent in <see cref="OpenTelemetryAgent"/> for tracing.</summary>
    public bool EnableOpenTelemetry { get; set; }

    public string OpenTelemetrySourceName { get; set; } = "HveSquad.AgentFramework";

    public ILoggerFactory? LoggerFactory { get; set; }
}

/// <summary>Materializes squad charters as Microsoft Agent Framework agents.</summary>
public sealed class SquadAgentFactory
{
    private readonly IChatClient _defaultChatClient;
    private readonly SquadAgentFactoryOptions _options;

    public SquadAgentFactory(IChatClient defaultChatClient, SquadAgentFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(defaultChatClient);

        _defaultChatClient = defaultChatClient;
        _options = options ?? new SquadAgentFactoryOptions();
    }

    /// <summary>Creates a raw caller-owned model agent, excluding the authenticated human-only learning helper.</summary>
    /// <param name="charter">Actual parsed charter.</param>
    /// <returns>The model agent; governed delivery remains a separate runtime surface.</returns>
    /// <exception cref="InvalidOperationException">The charter identifies human-only Squad Learn.</exception>
    public AIAgent Create(AgentCharter charter)
    {
        ArgumentNullException.ThrowIfNull(charter);
        if (IsLearningHelper(charter))
        {
            throw new InvalidOperationException(
                "Squad Learn is an authenticated human-only helper, not a MAF agent. Use SquadLearning with explicit host authentication.");
        }

        var chatClient = _options.ChatClientSelector?.Invoke(charter) ?? _defaultChatClient;

        var agentOptions = new ChatClientAgentOptions
        {
            Name = charter.Name,
            Description = charter.Description,
            ChatOptions = new ChatOptions { Instructions = charter.Instructions },
        };

        AIAgent agent = chatClient.AsAIAgent(agentOptions, _options.LoggerFactory);

        if (_options.EnableOpenTelemetry)
        {
            agent = agent.AsBuilder().UseOpenTelemetry(_options.OpenTelemetrySourceName).Build();
        }

        return agent;
    }

    /// <summary>Creates model agents keyed by charter name, excluding human-only Squad Learn.</summary>
    /// <param name="artifacts">Loaded deployed artifacts.</param>
    /// <returns>Raw model agents, not human learning capabilities.</returns>
    public IReadOnlyDictionary<string, AIAgent> CreateAll(SquadArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var agents = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase);

        foreach (var charter in artifacts.Charters)
        {
            if (!IsLearningHelper(charter))
            {
                agents[charter.Name] = Create(charter);
            }
        }

        return agents;
    }

    private static bool IsLearningHelper(AgentCharter charter) =>
        charter.Name.Equals("Squad Learn", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(charter.SourcePath).EndsWith("squad-learn.agent.md", StringComparison.OrdinalIgnoreCase);
}
