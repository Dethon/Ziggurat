using Domain.DTOs.Tunnel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mcp.Hosting.Tunnel;

public static class TunnelHostExtensions
{
    extension(IServiceCollection services)
    {
        // A listener besides /mcp's that the relay may open streams to, under this tag.
        public IServiceCollection AddTunnelListener(string tag, int port) =>
            services.AddSingleton(new TunnelListener(tag, port));

        // What AddMcpHost adds for a server given a tunnel: the client that keeps it open, the
        // trust its call-token gate checks against, and the ports its back-calls go out from. Registered as a plain IHostedService so a second
        // host in one process (a test's) is not deduplicated away by implementation type.
        internal IServiceCollection AddTunnel(TunnelSettings tunnel) =>
            services
                .AddSingleton<TunnelTrust>()
                .AddSingleton<TunnelLink>()
                .AddSingleton<IHostedService>(sp => new TunnelClient(
                    tunnel,
                    sp.GetRequiredService<TunnelTrust>(),
                    sp.GetRequiredService<TunnelLink>(),
                    sp,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TunnelClient>>()))
                .AddSingleton<IHostedService>(sp => new BackCallForwarders(
                    tunnel,
                    sp.GetRequiredService<TunnelLink>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackCallForwarders>>()));
    }
}