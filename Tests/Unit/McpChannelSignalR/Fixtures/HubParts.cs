using Domain.Contracts;
using Domain.DTOs;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
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

    // An upload store holding nothing, at a directory nothing creates: every file a message names is one
    // it keeps for no conversation, so a hub that is not about files lets them through.
    public static AttachmentService NoUploads()
    {
        var settings = new AttachmentSettings { StoragePath = Path.Combine(Path.GetTempPath(), "hub-parts-no-uploads") };
        return new AttachmentService(
            settings, new AttachmentTickets(settings, TimeProvider.System),
            new AttachmentStore(settings, new RetentionSettings(), new FakeConversationLiveness(), TimeProvider.System,
                NullLogger<AttachmentStore>.Instance),
            NullLogger<AttachmentService>.Instance);
    }

    public static QueueNews News(StreamService streams, IThreadStateStore store, IHubNotificationSender sender) =>
        new(streams, store, sender, NullLogger<QueueNews>.Instance);
}