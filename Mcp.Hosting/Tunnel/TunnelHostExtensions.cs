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

        // What AddMcpHost adds for a server given a tunnel: the client that keeps it open, and the
        // trust its call-token gate checks against. Registered as a plain IHostedService so a second
        // host in one process (a test's) is not deduplicated away by implementation type.
        internal IServiceCollection AddTunnel(TunnelSettings tunnel) =>
            services
                .AddSingleton<TunnelTrust>()
                .AddSingleton(new RelayCredential(tunnel.Credential))
                .AddSingleton<IHostedService>(sp => new TunnelClient(
                    tunnel,
                    sp.GetRequiredService<TunnelTrust>(),
                    sp,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TunnelClient>>()));
    }
}