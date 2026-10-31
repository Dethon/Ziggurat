using Domain.Contracts;
using Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Clients.Accounts;

// Where a host finds the accounts service. Every server that resolves identities or verifies the
// service's assertions binds its own `Accounts` section onto this record; the credential it asks
// with is its own MCP one.
public record AccountsClientConfig
{
    public string BaseUrl { get; init; } = "http://accounts:8080";

    // Bounds a resolution: it is asked on the way to a person's turn, and an accounts service that
    // accepts connections but never answers must not hold the turn.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // Where the service publishes the keys its assertions verify with: per deployment, so compose
    // sets it (ACCOUNTS__JWKSURL); the default is compose's own value.
    public string JwksUrl { get; init; } = AssertionVerifier.DefaultJwksUrl;
}

public static class AccountsClientRegistration
{
    // The credential is the server's own, the one its /mcp asks for: the accounts service lists
    // each caller by the credential it already holds, so no secret exists for this alone. One client
    // answers both contracts; only the Telegram bot asks the second.
    public static IServiceCollection AddAccountResolver(
        this IServiceCollection services, AccountsClientConfig config, string credential)
    {
        services.AddHttpClient(AccountsResolverClient.ClientName, http =>
        {
            http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = config.RequestTimeout;
        });
        return services
            .AddSingleton(sp => new AccountsResolverClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AccountsResolverClient.ClientName), credential))
            .AddSingleton<IAccountResolver>(sp => sp.GetRequiredService<AccountsResolverClient>())
            .AddSingleton<ITelegramAccounts>(sp => sp.GetRequiredService<AccountsResolverClient>())
            .AddSingleton<ISessionAccounts>(sp => sp.GetRequiredService<AccountsResolverClient>());
    }
}