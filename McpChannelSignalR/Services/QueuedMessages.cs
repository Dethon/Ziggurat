using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// A person's message waiting in a topic's queue for the running turn to end: everything needed to send
// it once nobody's hub call is in hand, and the id the hub gave it, by which its sender names it.
public sealed record QueuedMessage(string Id, SenderSnapshot Sender, TurnMessage Message, DateTimeOffset QueuedAt)
{
    public static QueuedMessage Of(SenderSnapshot sender, TurnMessage message) =>
        new(Guid.NewGuid().ToString("N"), sender, message, DateTimeOffset.UtcNow);

    public QueuedMessageItem ToItem() =>
        new(Id, Message.Content, Message.Attachments?.Count ?? 0, Sender.UserId, Sender.DisplayName, QueuedAt);
}

// The head of a queue, taken as the turn before it ended, on the stream it was taken from: a stream
// stopped or replaced while its dispatch was being vouched for is not the one to write it on.
public sealed record QueuedTurn(QueuedMessage Message, long Stream);

// A topic's queue as of one change, numbered so a page told of two changes keeps the later.
public sealed record QueueSnapshot(IReadOnlyList<QueuedMessage> Messages, long Revision)
{
    public QueueChangedNotification ToNotification(string topicId, string spaceSlug) =>
        new(topicId, [.. Messages.Select(m => m.ToItem())], Revision, spaceSlug);
}