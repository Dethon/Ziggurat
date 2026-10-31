using Domain.Contracts;
using Domain.DTOs.Tunnel;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Infrastructure.Tunnels;

namespace Agent.Modules;

public static class TunnelModule
{
    extension(IServiceCollection services)
    {
        // The relay every household server's tunnel ends at, and the agent reaching those servers
        // through it. It asks the accounts service with the agent API's own credential, which the
        // service lists the agent by (CALLERS__agent), so no secret exists for the relay alone; it
        // hands servers the keys of the agent's own SigningKeyStore, and hears revocations on the
        // agent's Redis.
        //
        // A household server's back-calls are answered by a gateway of the relay's own, which lets
        // them through to the accounts service it already knows, the shared Lemonade and the agent's
        // own routes, and nowhere else.
        public IServiceCollection AddTunnelRelay(
            TunnelSettings tunnel, AccountsClientConfig accounts, string credential, BackCallSettings? backCalls = null)
        {
            services.AddHttpClient(TunnelCredentialClient.ClientName, http =>
            {
                http.BaseAddress = new Uri(accounts.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = accounts.RequestTimeout;
            });

            return services
                .AddSingleton<TunnelRegistry>()
                .AddSingleton(sp => new TunnelCredentialClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(TunnelCredentialClient.ClientName), credential))
                // On the wall clock: when a tunnel opened is a fact about the world, never the instant
                // an eval pins its scenario to.
                .AddSingleton(sp => new TunnelRelay(
                    sp.GetRequiredService<TunnelRegistry>(),
                    sp.GetRequiredService<TunnelCredentialClient>(),
                    sp.GetRequiredService<SigningKeyStore>(),
                    tunnel,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<TunnelRelay>>(),
                    sp.GetService<IHostApplicationLifetime>(),
                    sp.GetRequiredService<BackCallGateway>()))
                .AddSingleton(BackCallTargets.From(backCalls ?? new BackCallSettings(), accounts.BaseUrl))
                .AddSingleton(sp => new BackCallGateway(
                    sp.GetRequiredService<BackCallTargets>(),
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp,
                    sp.GetRequiredService<ILoggerFactory>()))
                .AddHostedService(sp => sp.GetRequiredService<BackCallGateway>())
                .AddSingleton<IOwnServers>(sp => new RelayedOwnServers(sp.GetRequiredService<TunnelRegistry>()))
                .AddHostedService<TunnelRevocations>();
        }
    }
}