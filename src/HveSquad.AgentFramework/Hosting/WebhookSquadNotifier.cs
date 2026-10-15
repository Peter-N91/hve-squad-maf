using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

/// <summary>
/// A real outbound-only webhook notifier. The host owns the secret HTTPS endpoint, HttpClient,
/// redirect policy and suppression/redaction of private endpoint URLs in HTTP instrumentation.
/// </summary>
public sealed class WebhookSquadNotifier : ISquadHumanNotifier
{
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, ValueTask<Uri>> _endpoint;

    /// <summary>Registers an opaque notifier key and host-owned secret endpoint resolver.</summary>
    /// <param name="binding">Non-secret registration key captured by notification opt-in.</param>
    /// <param name="client">Caller-owned client with automatic redirects disabled and secret URL telemetry suppressed.</param>
    /// <param name="endpoint">Resolves the secret endpoint at send time; it is never persisted or logged.</param>
    public WebhookSquadNotifier(string binding, HttpClient client, Func<CancellationToken, ValueTask<Uri>> endpoint)
    {
        if (!HumanActions.IsReference(binding))
        {
            throw new ArgumentException("Webhook registration needs an opaque non-secret binding.", nameof(binding));
        }
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        Binding = binding;
        _client = client;
        _endpoint = endpoint;
    }

    /// <inheritdoc/>
    public string Binding { get; }

    /// <inheritdoc/>
    public async ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken)
    {
        var endpoint = await _endpoint(cancellationToken).ConfigureAwait(false);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new SquadHumanTransportException("Webhook resolver must supply a trusted absolute HTTPS endpoint without user info or fragment.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("Idempotency-Key", action.Binding.Id);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                action.Binding,
                action.IsQuestion,
                action.CreatedAt,
                action.ExpiresAt,
                Awaiting = "An authenticated human response through the separately registered approval channel.",
                Detail = "Private case text, raw arguments and private artifact paths are not included.",
                OutboundOnly = true,
            }), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? new("sent") :
                new((int)response.StatusCode >= 500 ? "uncertain" : "failed",
                    Diagnostic: "Webhook did not acknowledge delivery. Its status cannot authorize work.");
        }
        catch (HttpRequestException)
        {
            throw new SquadHumanTransportException("Webhook transport failed; acknowledgement and approval are unknown.", outcomeUncertain: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SquadHumanTransportException("Webhook delivery timed out; no approval was inferred.", outcomeUncertain: true);
        }
    }
}
