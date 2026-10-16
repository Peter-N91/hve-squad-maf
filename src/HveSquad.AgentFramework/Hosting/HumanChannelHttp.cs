using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HveSquad.AgentFramework.Runtime;

namespace HveSquad.AgentFramework.Hosting;

internal sealed class HumanChannelHttp(HttpClient client, Uri apiRoot)
{
    internal const int MaximumPages = 10;
    private const int MaximumResponseBytes = 1024 * 1024;

    internal Uri Address(string path) => new(apiRoot, path);

    internal async ValueTask<JsonElement?> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var mutating = method != HttpMethod.Get;
        try
        {
            using var request = new HttpRequestMessage(method, Address(path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            request.Headers.UserAgent.ParseAdd("HveSquad.AgentFramework");
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new SquadHumanTransportException("GitHub human-channel HTTP " +
                    ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "; inspect host permissions/setup without logging credentials or response bodies.",
                    mutating && (int)response.StatusCode >= 500);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    throw new SquadHumanTransportException("GitHub human-channel response exceeds its bounded size.", mutating);
                }
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            throw new SquadHumanTransportException("GitHub human-channel transport failed; no decision was inferred.", mutating);
        }
        catch (IOException)
        {
            throw new SquadHumanTransportException("GitHub human-channel response read failed; no decision was inferred.", mutating);
        }
        catch (JsonException)
        {
            throw new SquadHumanTransportException("GitHub human-channel returned malformed JSON; no decision was inferred.", mutating);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SquadHumanTransportException("GitHub human-channel timed out; no decision was inferred.", mutating);
        }
    }

    internal async ValueTask<JsonElement[]> ListAsync(string path, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var result = await SendAsync(HttpMethod.Get,
                path + separator + "per_page=100&page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null, cancellationToken).ConfigureAwait(false);
            if (result is not { ValueKind: JsonValueKind.Array } array)
            {
                throw new SquadHumanTransportException("GitHub human-channel list is not an array.");
            }
            var batch = array.EnumerateArray().Select(item => item.Clone()).ToArray();
            rows.AddRange(batch);
            if (batch.Length < 100)
            {
                return rows.ToArray();
            }
        }
        throw new SquadHumanTransportException("GitHub human-channel pagination bound reached; incomplete results cannot authorize or create a duplicate issue.");
    }

    internal static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;

    internal static long? Id(JsonElement value, string property = "id") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id) && id > 0
            ? id : null;

    internal static DateTimeOffset? Time(JsonElement value, string property) =>
        DateTimeOffset.TryParse(Text(value, property), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
