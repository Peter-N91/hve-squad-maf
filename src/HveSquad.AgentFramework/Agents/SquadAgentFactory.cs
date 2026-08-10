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

    public AIAgent Create(AgentCharter charter)
    {
        ArgumentNullException.ThrowIfNull(charter);

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

    /// <summary>Creates one agent per charter, keyed by charter name.</summary>
    public IReadOnlyDictionary<string, AIAgent> CreateAll(SquadArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var agents = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase);

        foreach (var charter in artifacts.Charters)
        {
            agents[charter.Name] = Create(charter);
        }

        return agents;
    }
}
