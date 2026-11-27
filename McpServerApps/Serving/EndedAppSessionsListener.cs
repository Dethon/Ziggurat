using System.Text.Json;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace McpServerApps.Serving;

// Hears the accounts service say a person's sessions ended — signed out, a device or passkey revoked,
// disabled — and ends every app session of theirs, whatever origin it opens. Not asked session by
// session as the chat hub asks: a live session's app sessions cost only a silent trip to the app
// sign-in. Subscribed before the host counts as started: pub/sub reaches only who is listening.
public sealed class EndedAppSessionsListener(
    IConnectionMultiplexer redis,
    AppSessions sessions,
    ILogger<EndedAppSessionsListener> logger) : IHostedService
{
    private static readonly RedisChannel _ended = RedisChannel.Literal(SessionsEnded.EndedChannel);

    public Task StartAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().SubscribeAsync(_ended, Heard);

    public Task StopAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().UnsubscribeAsync(_ended, Heard);

    private void Heard(RedisChannel _, RedisValue message)
    {
        try
        {
            if (JsonSerializer.Deserialize<SessionsEnded>((string)message!, JsonSerializerOptions.Web) is { } ended)
            {
                var count = sessions.EndAll(new Account(ended.TenantId, ended.UserId));
                logger.LogInformation("Ended {Count} app sessions of account {UserId}: their sessions ended", count, ended.UserId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An announcement that sessions ended could not be acted on");
        }
    }
}