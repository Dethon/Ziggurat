using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// A person's message waiting in a topic's queue for the running turn to end: everything needed to send
// it once nobody's hub call is in hand, and the id the hub gave it, by which its sender names it.
public sealed record QueuedMessage(string Id, SenderSnapshot Sender, TurnMessage Message, DateTimeOffset QueuedAt)
{
    public static QueuedMessage Of(SenderSnapshot sender, TurnMessage message) =>
        new(Guid.NewGuid().ToString("N"), sender, message, DateTimeOffset.UtcNow);

    public ReturnedMessage ToReturned() =>
        new(Id, Message.Content, Message.Attachments ?? [], Message.ConfigPatch);

    public QueuedMessageItem ToItem() =>
        new(Id, Message.Content, Message.Attachments?.Count ?? 0, Sender.UserId, Sender.DisplayName, QueuedAt);
}

// What became of a message admitted to a running stream's queue: waiting in it, handed back to its
// sender because a stop took that stream while it was being admitted, or nowhere, since nothing it was
// held on runs (a deleted topic's stream).
public enum Queueing
{
    Queued,
    Returned,
    NotRunning
}

// The head of a queue, taken as the turn before it ended, on the stream it was taken from: a stream
// stopped or replaced while its dispatch was being vouched for is not the one to write it on.
public sealed record QueuedTurn(QueuedMessage Message, StreamKey Stream);

// One stream of a topic's, as opposed to the one opened after it: a place held, a head taken or a stop
// names the stream it happened on, so nothing it leaves behind lands on another.
public readonly record struct StreamKey(string TopicId, long Number);

// A topic's queue as of one change, numbered so a page told of two changes keeps the later.
// With whose own turn the stream is running, by their account's user id: the one person who may steer it.
public sealed record QueueSnapshot(IReadOnlyList<QueuedMessage> Messages, long Revision, string? RunningFor = null)
{
    public QueueChangedNotification ToNotification(string topicId, string spaceSlug) =>
        new(topicId, [.. Messages.Select(m => m.ToItem())], Revision, spaceSlug, RunningFor);
}

// What taking a queued message back came to: the message, with the ones still queued behind it, or why
// not — it left the queue already, or it is somebody else's.
public abstract record Withdrawal;

public sealed record Withdrawn(QueuedMessage Message, IReadOnlyList<QueuedMessage> StillQueued) : Withdrawal
{
    // The files the queue still names once this message has left it.
    public IEnumerable<string> StillQueuedAttachments() =>
        StillQueued.SelectMany(m => m.Message.Attachments ?? []).Select(a => a.Id);
}

public sealed record WithdrawalRefused(string Refusal) : Withdrawal;

// What steering with a queued message came to: taken out of the queue to wait for the running turn's
// end, or why not — a withdrawal's refusals, someone else's turn, or nobody there to send it to.
public abstract record Steering;

public sealed record Steered(QueuedMessage Message) : Steering;

public sealed record SteerRefused(string Refusal) : Steering;