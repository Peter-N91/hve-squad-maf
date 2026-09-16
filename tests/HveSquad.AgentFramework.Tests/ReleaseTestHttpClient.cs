using System.Net.Http.Headers;

namespace HveSquad.AgentFramework.Tests;

internal static class ReleaseTestHttpClient
{
    internal static HttpClient Create(string? token, HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}
