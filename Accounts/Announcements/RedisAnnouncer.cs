using System.Text.Json;
using StackExchange.Redis;

namespace Accounts.Announcements;

// Tells whoever listens on the deployment's Redis that something changed here — a tunnel credential
// revoked, a household's configuration saved — as the metrics feed is told of events: the stack's one
// way for a service to tell others something without knowing who listens. A command run inside the
// container tells them the same way. With no Redis configured nobody is told, and the answer says so.
public sealed class RedisAnnouncer(AccountsRedis redis)
{
    // Why the listeners were not told, or null when they were.
    public async Task<string?> AnnounceAsync<T>(string channel, T message, CancellationToken ct)
    {
        if (!redis.Configured)
        {
            return "no Redis is configured (Redis:ConnectionString), so nothing listens";
        }

        try
        {
            var connection = await redis.OpenAsync(ct);
            await connection.GetSubscriber().PublishAsync(
                RedisChannel.Literal(channel), JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
            return null;
        }
        // A publish that times out throws a TimeoutException, which is not a RedisException.
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return $"Redis could not be reached ({ex.Message})";
        }
    }
}