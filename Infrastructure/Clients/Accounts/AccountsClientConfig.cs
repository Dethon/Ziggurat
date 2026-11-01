using Domain.Contracts;
using Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

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

    // How long a view of a household's configuration is kept when no save of it is announced: the
    // bound on a missed announcement, since a heard one drops it at once. A generic tunable.
    public TimeSpan ConfigurationLifetime { get; init; } = TimeSpan.FromMinutes(1);
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

    // A household's configuration as this host may read it, asked with its own credential: the
    // agent's CALLERS__agent one with a token of its own (agentToken, HouseholdConfigurationClient.AgentToken),
    // a shared server its own /mcp credential and no token. Saves are heard on the host's Redis
    // (its IConnectionMultiplexer) when it registers one; without, a view is kept for its lifetime alone.
    public static IServiceCollection AddHouseholdConfiguration(
        this IServiceCollection services, AccountsClientConfig config, string credential,
        Func<IServiceProvider, Func<string>>? agentToken = null)
    {
        services.AddHttpClient(HouseholdConfigurationClient.ClientName, http =>
        {
            http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = config.RequestTimeout;
        });
        // Only the agent, which holds a token of its own, says a household's key failed in use.
        if (agentToken is not null)
        {
            services.AddSingleton<IHouseholdKeyReports>(sp => sp.GetRequiredService<HouseholdConfigurationClient>());
        }

        return services
            .AddSingleton(sp => new HouseholdConfigurationClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HouseholdConfigurationClient.ClientName),
                credential,
                agentToken?.Invoke(sp),
                sp.GetService<IConnectionMultiplexer>(),
                TimeProvider.System,
                config.ConfigurationLifetime,
                sp.GetService<ILogger<HouseholdConfigurationClient>>()))
            .AddSingleton<IHouseholdConfiguration>(sp => sp.GetRequiredService<HouseholdConfigurationClient>());
    }
}