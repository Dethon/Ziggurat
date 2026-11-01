using System.Text.Json;
using Accounts.Settings;
using StackExchange.Redis;

namespace Accounts.Announcements;

// Tells whoever listens on the deployment's Redis that something changed here — a tunnel credential
// revoked, a household's configuration saved — as the metrics feed is told of events: the stack's one
// way for a service to tell others something without knowing who listens. A command run inside the
// container tells them the same way.
//
// Opened when first told rather than at start, so a service or a command that announces nothing never
// connects; with no connection string configured nobody is told, and the answer says so.
public sealed class RedisAnnouncer(AccountsSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _opening = new(1, 1);
    private ConnectionMultiplexer? _redis;

    // Why the listeners were not told, or null when they were.
    public async Task<string?> AnnounceAsync<T>(string channel, T message, CancellationToken ct)
    {
        if (settings.Redis.ConnectionString.Length == 0)
        {
            return "no Redis is configured (Redis:ConnectionString), so nothing listens";
        }

        try
        {
            var redis = await OpenAsync(ct);
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(channel), JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
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