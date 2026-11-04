using Domain.DTOs.Tunnel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Infrastructure.Tunnels;

// A household's open tunnels as the operator is shown them, each asked for its tools through its own
// tunnel at the moment of asking: what a turn starting now would be offered. Listing tools is not a
// tool call, so nothing is signed; the tunnel's client presents its credential to the server's /mcp.
public sealed class TunnelListing(TunnelRegistry registry, ILogger<TunnelListing> logger)
{
    // A server that has not answered by then is listed without its tools rather than holding the
    // command up: it is connected, and its tools are what it says they are.
    private static readonly TimeSpan _askFor = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<ConnectedServer>> ListAsync(string tenantId, CancellationToken ct) =>
        await Task.WhenAll(registry.Open
            .Where(tunnel => tunnel.TenantId == tenantId && tunnel.IsOpen)
            .OrderBy(tunnel => tunnel.ServerName, StringComparer.Ordinal)
            .Select(async tunnel => new ConnectedServer(
                tunnel.ServerName, tunnel.Generation, tunnel.OpenedAt, await ToolsOfAsync(tunnel, ct), tunnel.CredentialId)));

    private async Task<IReadOnlyList<string>?> ToolsOfAsync(OpenTunnel tunnel, CancellationToken ct)
    {
        // A server that serves no MCP (the household's Lemonade forwarder) has no tools to be asked for.
        if (!HouseholdServerNames.ServesTools(tunnel.ServerName))
        {
            return [];
        }

        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct, tunnel.Closed);
        asking.CancelAfter(_askFor);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = new Uri($"http://{tunnel.Address.Name}/mcp") },
                    tunnel.Client,
                    ownsHttpClient: false),
                cancellationToken: asking.Token);
            var tools = await client.ListToolsAsync(cancellationToken: asking.Token);
            return [.. tools.Select(t => t.Name).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Server} of household {TenantId} did not list its tools through its tunnel",
                tunnel.ServerName, tunnel.TenantId);
            return null;
        }
    }
}