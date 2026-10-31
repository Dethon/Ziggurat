using System.Text.Json;
using Accounts.Settings;
using Domain.DTOs.Tunnel;
using StackExchange.Redis;

namespace Accounts.Tunnels;

// Tells whoever listens that a tunnel credential was revoked, so the relay closes the tunnel it
// opened at once rather than at its next open. On the deployment's Redis, as the metrics feed is
// told of events (metrics:events): the stack's one way for a service to tell another something
// without knowing who listens. A command revoking from inside the container tells them the same way.
//
// Opened when first told rather than at start, so a service or a command that revokes nothing never
// connects; with no connection string configured nobody is told, and the answer says so.
public sealed class TunnelRevocationPublisher(AccountsSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _opening = new(1, 1);
    private ConnectionMultiplexer? _redis;

    // Why the listeners were not told, or null when they were.
    public async Task<string?> TellAsync(TunnelCredentialRevoked revoked, CancellationToken ct)
    {
        if (settings.Redis.ConnectionString.Length == 0)
        {
            return "no Redis is configured (Redis:ConnectionString), so nothing listens for revocations";
        }

        try
        {
            var redis = await OpenAsync(ct);
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(TunnelCredentialProtocol.RevokedChannel), JsonSerializer.Serialize(revoked, JsonSerializerOptions.Web));
            return null;
        }
        // A publish that times out throws a TimeoutException, which is not a RedisException.
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return $"Redis could not be reached ({ex.Message})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        _opening.Dispose();
    }

    // Not aborting on a failed first connect keeps the multiplexer retrying in the background, so a
    // Redis that was down when the service first told it is used once it is back.
    private async Task<ConnectionMultiplexer> OpenAsync(CancellationToken ct)
    {
        await _opening.WaitAsync(ct);
        try
        {
            if (_redis is null)
            {
                var options = ConfigurationOptions.Parse(settings.Redis.ConnectionString);
                options.AbortOnConnectFail = false;
                _redis = await ConnectionMultiplexer.ConnectAsync(options);
            }

            return _redis;
        }
        finally
        {
            _opening.Release();
        }
    }
}