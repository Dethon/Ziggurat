using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Clients.Topics;

// Where a channel server finds the agent's internal routes. No credential: what the agent decides
// there rests on the accounts service's assertion each request carries, never on who is asking.
public record AgentClientConfig
{
    public string BaseUrl { get; init; } = "http://agent:8080";

    // Bounds a mint: it is asked on the way to a conversation's first turn, and an agent that
    // accepts connections but never answers must not hold the satellite waiting.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public static class AgentTopicsRegistration
{
    // A named client, so a server that reaches the agent another way — over its tunnel — configures
    // that client's handler and leaves the minter as it is.
    public static IServiceCollection AddTopicMinter(this IServiceCollection services, AgentClientConfig config)
    {
        services.AddHttpClient(AgentTopicsClient.ClientName, http =>
        {
            http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = config.RequestTimeout;
        });
        return services.AddSingleton<ITopicMinter>(sp => new AgentTopicsClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AgentTopicsClient.ClientName)));
    }
}