using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

// The out-of-process "stop ringing": ./dismiss in the timers server POSTs here so the hub cancels
// the live alert CancellationTokenSources, which only exist in the hub process.
public sealed class HttpAlertDismisser(IHttpClientFactory httpClientFactory, string token) : IAlertDismisser
{
    public async Task<IReadOnlyList<DismissedAlert>> DismissAllAsync(CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/voice/dismiss");
        message.Headers.Add("X-Announce-Token", token);

        var response = await VoiceHubHttp.SendAsync(httpClientFactory, message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DismissedAlert>>(ct) ?? [];
    }
}