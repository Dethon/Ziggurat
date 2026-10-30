using Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Accounts;

// An app behind the proxy believes the person the accounts service's assertion names, checked
// against the keys the service publishes at its JWKS (Accounts:JwksUrl). One verifier per host, so
// one copy of the keys: the copy is what spares the service a request per request.
public static class AssertionVerifierRegistration
{
    public const string ClientName = "accounts-jwks";

    public static IServiceCollection AddAssertionVerifier(this IServiceCollection services, string jwksUrl)
    {
        services.AddHttpClient(ClientName, http => http.Timeout = TimeSpan.FromSeconds(5));
        services.TryAddSingleton(TimeProvider.System);
        // A client per fetch, from the factory, so a handler the factory has rotated is never kept
        // for the life of the host.
        return services.AddSingleton(sp =>
        {
            var clients = sp.GetRequiredService<IHttpClientFactory>();
            return AssertionVerifier.OverJwks(
                ct => clients.CreateClient(ClientName).GetStringAsync(jwksUrl, ct),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILoggerFactory>());
        });
    }
}