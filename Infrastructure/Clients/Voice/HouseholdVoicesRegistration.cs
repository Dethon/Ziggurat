using Domain.Contracts;
using Domain.DTOs.Tunnel;
using Infrastructure.Clients.Relay;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Clients.Voice;

public static class HouseholdVoicesRegistration
{
    // A shared server's way to a household's voice server: the hub's announce, dismiss and satellites
    // API over HTTP, bound per household so each request names the household it is for. The timers
    // server rings timers through it and Sugary glucose alarms. caller and credential are the
    // server's own name (Mcp:Audience) and /mcp credential, which the relay asks for; the announce
    // token is for a hub dialled directly, which a relayed call never presents.
    public static IServiceCollection AddHouseholdVoices(
        this IServiceCollection services, string hubBaseUrl, string caller, string credential, string announceToken)
    {
        var hub = new Uri(hubBaseUrl);
        var client = services.AddHttpClient(VoiceHubHttp.ClientName, client =>
        {
            client.BaseAddress = hub;
            // Bound the wait: the hub returns 202 for announce (ringing is async) and resolves/dismisses
            // instantly, so a slow response means the hub is unhealthy. Fail fast rather than block a
            // fire loop (or an agent's fs_create) on the default 100s timeout.
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        // A household's voice server is reached through the relay's endpoint on the agent, never at
        // an address, and the relay asks who is calling: this server's name and its own credential.
        // A hub dialled directly is presented neither — it never asks for them.
        if (RelayedCallProtocol.IsRelayed(hub))
        {
            client.AddHttpMessageHandler(() => new RelayedCallCredential(caller, credential));
        }

        // The adapters create the named client per call, so the factory's handler rotation works
        // despite their singleton lifetimes.
        return services.AddSingleton<IHouseholdVoices>(sp => new HttpHouseholdVoices(
            sp.GetRequiredService<IHttpClientFactory>(), announceToken));
    }
}