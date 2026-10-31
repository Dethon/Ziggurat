using Domain.Contracts;

namespace Infrastructure.Clients.Voice;

// The three hub adapters bound to one household, so every request a timer makes names the household
// it was armed for and the relay forwards it down that household's voice server's tunnel alone. The
// named client is created per request inside each adapter, so binding them per household costs
// nothing the factory's handler rotation would mind.
public sealed class HttpHouseholdVoices(IHttpClientFactory httpClientFactory, string token) : IHouseholdVoices
{
    public HouseholdVoice For(string tenantId) => new(
        new HttpInsistentAnnouncer(httpClientFactory, token, tenantId),
        new HttpAlertDismisser(httpClientFactory, token, tenantId),
        new HttpSatelliteCatalog(httpClientFactory, token, tenantId));
}