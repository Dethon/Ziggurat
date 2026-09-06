using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Exceptions;

namespace Infrastructure.Clients.Voice;

// Reads the satellite roster from the hub and forwards target resolution to the hub. The roster is
// fetched fresh on every call — it only changes when the hub restarts with new config, which is
// exactly when a process-lifetime cache would wrongly reject the new satellite (creates are rare, so
// the extra GET is free). Resolution is never done locally: the hub's registry dual-keys rooms on
// Room and DisplayLocation, so forwarding is what keeps create-time validation identical to firing.
public sealed class HttpSatelliteCatalog(IHttpClientFactory httpClientFactory, string token) : ISatelliteCatalog
{
    public async Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "api/voice/satellites");
        message.Headers.Add("X-Announce-Token", token);

        using var response = await VoiceHubHttp.SendAsync(httpClientFactory, message, ct);
        Answered(response);
        return await response.Content.ReadFromJsonAsync<List<SatelliteDescriptor>>(ct) ?? [];
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/voice/satellites/resolve")
        {
            Content = JsonContent.Create(target)
        };
        message.Headers.Add("X-Announce-Token", token);

        using var response = await VoiceHubHttp.SendAsync(httpClientFactory, message, ct);
        Answered(response);
        return await response.Content.ReadFromJsonAsync<List<string>>(ct) ?? [];
    }

    // An error status is the hub answering — a token mismatch, its own failure — not the hub being
    // unreachable, so it is typed apart from VoiceHubUnavailableException and carries the status.
    private static void Answered(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new VoiceHubRejectedException((int)response.StatusCode,
                $"The voice hub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
    }
}