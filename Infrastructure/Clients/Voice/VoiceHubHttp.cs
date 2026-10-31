using System.Net;
using Domain.Exceptions;

namespace Infrastructure.Clients.Voice;

// Shared send path for the three hub adapters: connection failures and request timeouts become the
// typed VoiceHubUnavailableException so callers can fail closed with a retryable error instead of
// leaking a raw HTTP exception. An error *status* is deliberately not mapped — that is the hub
// answering (e.g. a token mismatch), which retrying will not fix — but for a gateway's, which is the
// relay saying a household's hub cannot be reached.
public static class VoiceHubHttp
{
    // The named client carrying the hub base address and fail-fast timeout (configured by the
    // timers server's DI). Created fresh per send so the factory's handler rotation keeps working
    // even though the adapters themselves are singletons.
    public const string ClientName = "voice-hub";

    public static async Task<HttpResponseMessage> SendAsync(
        IHttpClientFactory factory, HttpRequestMessage message, CancellationToken ct)
    {
        var client = factory.CreateClient(ClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new VoiceHubUnavailableException($"The voice hub is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VoiceHubUnavailableException("The voice hub timed out.", ex);
        }

        // A household's hub is reached through the relay, which answers a gateway's status when the
        // hub cannot be reached (its tunnel is not open, or closed under the call) and says why. The
        // hub itself never answers one.
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            using (response)
            {
                var reason = await response.Content.ReadAsStringAsync(ct);
                throw new VoiceHubUnavailableException(
                    $"The voice hub is unreachable: {(reason.Length > 0 ? reason : response.ReasonPhrase)}");
            }
        }

        return response;
    }
}