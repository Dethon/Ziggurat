using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

// Fires timer rings by POSTing to the voice hub's announce endpoint. The hub owns the live satellite
// sessions, so the out-of-process timers server can only reach them over HTTP. The named voice-hub
// client carries the hub base address; only the shared announce token is passed here.
public sealed class HttpInsistentAnnouncer(IHttpClientFactory httpClientFactory, string token, string? household = null) : IInsistentAnnouncer
{
    public async Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/voice/announce")
        {
            Content = JsonContent.Create(request)
        };
        VoiceHubHttp.Address(message, token, household);

        var response = await VoiceHubHttp.SendAsync(httpClientFactory, message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AnnounceResponse>(ct)
            ?? throw new InvalidOperationException("Voice hub returned an empty announce response.");
    }
}