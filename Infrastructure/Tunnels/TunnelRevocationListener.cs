using System.Net.WebSockets;
using System.Text.Json;
using Domain.DTOs.Tunnel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Tunnels;

// Closes the tunnel a revoked credential opened, and every tunnel of a household the operator
// suspended, the moment the accounts service says so. The relay verifies every open anyway, so the
// server's next attempt is refused as Revoked or Suspended; this is what takes away the tunnel that is
// already open, without a restart.
//
// Subscribed before the host counts as started, so no tunnel can be admitted while a revocation
// would go unheard: pub/sub reaches only who is listening at that moment.
public sealed class TunnelRevocationListener(
    IConnectionMultiplexer redis,
    TunnelRegistry registry,
    ILogger<TunnelRevocationListener> logger) : IHostedService
{
    private static readonly RedisChannel _revoked = RedisChannel.Literal(TunnelCredentialProtocol.RevokedChannel);
    private static readonly RedisChannel _suspended = RedisChannel.Literal(TunnelCredentialProtocol.SuspendedChannel);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscriber = redis.GetSubscriber();
        await subscriber.SubscribeAsync(_revoked, Heard<TunnelCredentialRevoked>(revoked =>
            CloseAsync(registry.OpenedWith(revoked.CredentialId), "its credential was revoked", TunnelProtocol.Revoked)));
        await subscriber.SubscribeAsync(_suspended, Heard<HouseholdSuspended>(suspended =>
            CloseAsync(registry.Open.Where(t => t.TenantId == suspended.TenantId), "its household was suspended",
                TunnelProtocol.Suspended)));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var subscriber = redis.GetSubscriber();
        await subscriber.UnsubscribeAsync(_revoked);
        await subscriber.UnsubscribeAsync(_suspended);
    }

    private Action<RedisChannel, RedisValue> Heard<T>(Func<T, Task> act) where T : class =>
        async void (_, message) =>
        {
            try
            {
                if (JsonSerializer.Deserialize<T>((string)message!, JsonSerializerOptions.Web) is { } heard)
                {
                    await act(heard);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An announcement on {Channel} could not be acted on", typeof(T).Name);
            }
        };

    // Taken out of the registry first, so nothing reaches the server through it while its close is
    // on the way.
    private Task CloseAsync(IEnumerable<OpenTunnel> tunnels, string why, string reason) =>
        Task.WhenAll(tunnels.ToList().Select(tunnel =>
        {
            logger.LogWarning(
                "Closing the tunnel of {ServerName} for household {TenantId}: {Why}", tunnel.ServerName, tunnel.TenantId, why);
            registry.Remove(tunnel, reason);
            return tunnel.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason);
        }));
}