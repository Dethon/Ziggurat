using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Clients.Accounts;

// Where a channel server finds the accounts service. Every server that resolves identities binds
// its own `Accounts` section onto this record; the credential it asks with is its own MCP one.
public record AccountsClientConfig
{
    public string BaseUrl { get; init; } = "http://accounts:8080";

    // Bounds a resolution: it is asked on the way to a person's turn, and an accounts service that
    // accepts connections but never answers must not hold the turn.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public static class AccountsClientRegistration
{
    // The credential is the server's own, the one its /mcp asks for: the accounts service lists
    // each caller by the credential it already holds, so no secret exists for this alone.
    public static IServiceCollection AddAccountResolver(
        this IServiceCollection services, AccountsClientConfig config, string credential)
    {
        services.AddHttpClient(AccountsResolverClient.ClientName, http =>
        {
            http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = config.RequestTimeout;
        });
        return services.AddSingleton<IAccountResolver>(sp => new AccountsResolverClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AccountsResolverClient.ClientName), credential));
    }
}