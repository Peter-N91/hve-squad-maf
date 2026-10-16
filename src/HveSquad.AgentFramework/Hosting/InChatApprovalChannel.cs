using System.Security.Claims;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>Durable in-chat approval backed by the host's actual authenticated session and case authorization.</summary>
public sealed class InChatApprovalChannel : ISquadHumanApprovalChannel
{
    private readonly Func<ClaimsPrincipal, SquadPendingHumanAction, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Func<CancellationToken, ValueTask<ClaimsPrincipal>> _principal;
    private readonly Func<SquadPendingHumanAction, CancellationToken, ValueTask<IReadOnlyList<SquadHumanResponse>>>? _read;
    private readonly Func<ClaimsPrincipal, SquadLearningInvocationAccess, CancellationToken, ValueTask<bool>>? _authorizeLearning;
    private readonly TextWriter _writer;

    /// <summary>Registers host authentication/authorization; never deserialize a principal from response or model text.</summary>
    /// <param name="binding">Stable non-secret session-authority registration key.</param>
    /// <param name="principal">The already authenticated principal of the actual submitting session.</param>
    /// <param name="authorize">The host's exact case/action rights check.</param>
    /// <param name="writer">An authorized notification writer, receiving safe metadata only.</param>
    /// <param name="read">Optional bounded session input reader for polling; direct submission needs no reader.</param>
    /// <param name="authorizeLearningInvocation">Optional separate check of an actual human-initiated learning event, never merely session identity.</param>
    public InChatApprovalChannel(string binding, Func<CancellationToken, ValueTask<ClaimsPrincipal>> principal,
        Func<ClaimsPrincipal, SquadPendingHumanAction, CancellationToken, ValueTask<bool>> authorize,
        TextWriter writer,
        Func<SquadPendingHumanAction, CancellationToken, ValueTask<IReadOnlyList<SquadHumanResponse>>>? read = null,
        Func<ClaimsPrincipal, SquadLearningInvocationAccess, CancellationToken, ValueTask<bool>>? authorizeLearningInvocation = null)
    {
        if (!HumanActions.IsReference(binding))
        {
            throw new ArgumentException("Use a bounded non-secret in-chat registration key.", nameof(binding));
        }
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(writer);
        Binding = "in-chat:" + binding;
        _principal = principal;
        _authorize = authorize;
        _writer = TextWriter.Synchronized(writer);
        _read = read;
        _authorizeLearning = authorizeLearningInvocation;
    }

    /// <inheritdoc/>
    public string Name => "in-chat";
    /// <inheritdoc/>
    public string Binding { get; }

    /// <inheritdoc/>
    public async ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteLineAsync(
                $"Human action {action.Binding.Id}; run {action.Binding.RunId}; {action.Binding.Gate}; " +
                $"expires {action.ExpiresAt:O}. Private details require the authorized case UI.").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new("sent", "in-chat");
        }
        catch (IOException)
        {
            throw new SquadHumanTransportException("In-chat notification writer failed; the decision remains pending.");
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SquadHumanResponse>> ReadResponsesAsync(
        SquadPendingHumanAction action, CancellationToken cancellationToken) =>
        _read is null ? ValueTask.FromResult<IReadOnlyList<SquadHumanResponse>>([]) : _read(action, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<SquadHumanAuthorization?> AuthorizeAsync(
        SquadPendingHumanAction action, SquadHumanResponse response, CancellationToken cancellationToken)
    {
        var principal = await _principal(cancellationToken).ConfigureAwait(false);
        var authorization = PrincipalAuthorization(principal);
        if (authorization is null ||
            !await _authorize(principal, action, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return authorization;
    }

    /// <summary>Authenticates a separately registered actual human learning-initiation event; default is denial.</summary>
    /// <param name="access">Exact human event reference, case and helper operation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The actual session actor only when the host verifies human initiation and case rights.</returns>
    public async ValueTask<SquadHumanAuthorization?> AuthorizeLearningInvocationAsync(
        SquadLearningInvocationAccess access, CancellationToken cancellationToken)
    {
        var principal = await _principal(cancellationToken).ConfigureAwait(false);
        var authorization = PrincipalAuthorization(principal);
        if (authorization is null || _authorizeLearning is null ||
            !await _authorizeLearning(principal, access, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return authorization;
    }

    private SquadHumanAuthorization? PrincipalAuthorization(ClaimsPrincipal principal)
    {
        var subjects = principal.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(ClaimTypes.NameIdentifier)).ToArray();
        return subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value) ? null :
            new("in-chat", ProjectFiles.Hash(Binding + "\n" + subjects[0].Issuer + "\n" + subjects[0].Value));
    }
    /// <inheritdoc/>
    public async ValueTask<SquadHumanDelivery> ResolveAsync(
        SquadPendingHumanAction action, SquadHumanDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteLineAsync($"Human action {action.Binding.Id}: authenticated {decision} recorded.").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new("sent", "in-chat");
        }
        catch (IOException)
        {
            throw new SquadHumanTransportException("In-chat outcome writer failed; the authenticated decision remains recorded.");
        }
    }
}
