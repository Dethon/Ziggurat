using System.Text.Json;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace McpChannelSignalR.Services;

// Hears the accounts service say a person's sessions ended and has the hub close the connections of
// the ones that did (SessionConnections). Subscribed before the host counts as started: pub/sub
// reaches only who is listening at that moment.
public sealed class EndedSessionsListener(
    IConnectionMultiplexer redis,
    SessionConnections connections,
    ILogger<EndedSessionsListener> logger) : IHostedService
{
    private static readonly RedisChannel _ended = RedisChannel.Literal(SessionsEnded.EndedChannel);

    public Task StartAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().SubscribeAsync(_ended, Heard);

    public Task StopAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().UnsubscribeAsync(_ended, Heard);

    private async void Heard(RedisChannel _, RedisValue message)
    {
        try
        {
            if (JsonSerializer.Deserialize<SessionsEnded>((string)message!, JsonSerializerOptions.Web) is { } ended)
            {
                await connections.CloseEndedAsync(ended, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An announcement that sessions ended could not be acted on");
        }
    }
}