using Domain.Contracts;
using Mcp.Hosting;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// What a hub built by hand is given beside its other collaborators, over the same stream service and
// sender, on the wall clock as the shipped registration's.
public static class HubParts
{
    public static PersonTurns Turns(
        StreamService streams, ChannelNotificationEmitter emitter, ISessionAccounts accounts, ILogger<PersonTurns>? logger = null) =>
        new(streams, emitter, accounts, TimeProvider.System, logger ?? NullLogger<PersonTurns>.Instance);

    public static QueueNews News(StreamService streams, IThreadStateStore store, IHubNotificationSender sender) =>
        new(streams, store, sender, NullLogger<QueueNews>.Instance);
}