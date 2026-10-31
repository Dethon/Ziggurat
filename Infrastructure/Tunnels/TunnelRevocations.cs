using System.Net.WebSockets;
using System.Text.Json;
using Domain.DTOs.Tunnel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Tunnels;

// Closes the tunnel a revoked credential opened, the moment the accounts service says so. The relay
// verifies every open anyway, so the server's next attempt is refused as Revoked; this is what takes
// away the tunnel that is already open, without a restart.
//
// Subscribed before the host counts as started, so no tunnel can be admitted while a revocation
// would go unheard: pub/sub reaches only who is listening at that moment.
public sealed class TunnelRevocations(
    IConnectionMultiplexer redis,
    TunnelRegistry registry,
    ILogger<TunnelRevocations> logger) : IHostedService
{
    private static readonly RedisChannel _channel = RedisChannel.Literal(TunnelCredentialProtocol.RevokedChannel);

    public Task StartAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().SubscribeAsync(_channel, async void (_, message) =>
        {
            try
            {
                if (JsonSerializer.Deserialize<TunnelCredentialRevoked>((string)message!, JsonSerializerOptions.Web) is { } revoked)
                {
                    await CloseAsync(revoked);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A tunnel credential's revocation could not be acted on");
            }
        });

    public Task StopAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().UnsubscribeAsync(_channel);

    // Taken out of the registry first, so nothing reaches the server through it while its close is
    // on the way.
    private Task CloseAsync(TunnelCredentialRevoked revoked) =>
        Task.WhenAll(registry.OpenedWith(revoked.CredentialId).Select(tunnel =>
        {
            logger.LogWarning(
                "Closing the tunnel of {ServerName} for household {TenantId}: its credential was revoked",
                tunnel.ServerName, tunnel.TenantId);
            registry.Remove(tunnel, TunnelProtocol.Revoked);
            return tunnel.CloseAsync(WebSocketCloseStatus.PolicyViolation, TunnelProtocol.Revoked);
        }));
}