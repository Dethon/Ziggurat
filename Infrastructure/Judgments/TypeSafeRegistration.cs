using Domain.Contracts;
using Domain.Judgments;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Judgments;

public static class TypeSafeRegistration
{
    public const string KeepAliveMetricService = "judge-connection-keepalive";

    // Being a host that asks Jev is one call, so a third use cannot register it a third way. An
    // empty key registers a judge that answers absence, so nothing downstream checks the key; a
    // configured one rides the chat clients' pool and gets a keep-alive, because a cold handshake
    // measured 600 ms against a judgment worth about half of that. The agent passes false: its
    // chat clients' keep-alive already holds this host's connection open on this pool, and a
    // second one would only ping it twice.
    public static IServiceCollection AddTypeSafeJudge(
        this IServiceCollection services, TypeSafeOptions options, bool keepConnectionAlive = true)
    {
        services.AddSingleton<IJudge>(sp => TypeSafeJudge.Create(
            new HttpClient(HostedConnectionPool.Shared, disposeHandler: false),
            options,
            sp.GetRequiredService<ILogger<TypeSafeJudge>>()));

        if (!options.IsConfigured || !keepConnectionAlive)
        {
            return services;
        }

        // A plain singleton, as the agent's is: AddHostedService dedups by implementation type.
        return services.AddSingleton<IHostedService, HostedConnectionKeepAlive>(sp => new HostedConnectionKeepAlive(
            new HttpClient(HostedConnectionPool.Shared, disposeHandler: false),
            new HostedConnectionKeepAliveOptions
            {
                BaseAddress = options.ApiUrl,
                ApiKey = options.ApiKey,
                NonBillableEndpoint = TypeSafeJudge.NonBillableEndpoint,
                MetricService = KeepAliveMetricService
            },
            sp.GetRequiredService<IMetricsPublisher>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<HostedConnectionKeepAlive>>()));
    }
}