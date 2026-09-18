using Domain.Contracts;
using Domain.Judgments;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Judgments;

public static class TypeSafeRegistration
{
    public const string KeepAliveMetricService = "typesafe-connection-keepalive";

    // Being a host that asks Jev is one call, so a third use cannot register it a third way. An
    // empty key registers a judge that answers absence, so nothing downstream checks the key; a
    // configured one rides the chat clients' pool and gets its own keep-alive, because a cold
    // handshake to this host measured 560 ms against a judgment worth about a tenth of that.
    public static IServiceCollection AddTypeSafeJudge(this IServiceCollection services, TypeSafeOptions options)
    {
        services.AddSingleton<IJudge>(sp => TypeSafeJudge.Create(
            new HttpClient(HostedConnectionPool.Shared, disposeHandler: false),
            options,
            sp.GetRequiredService<ILogger<TypeSafeJudge>>()));

        if (!options.IsConfigured)
        {
            return services;
        }

        // A plain singleton for the reason the OpenRouter one is: AddHostedService would see the
        // same class twice and keep only the first.
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