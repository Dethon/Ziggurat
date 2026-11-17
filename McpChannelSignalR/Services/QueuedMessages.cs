using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

// A person's message waiting in a topic's queue for the running turn to end: everything needed to send
// it once nobody's hub call is in hand, and the id the hub gave it, by which its sender names it.
public sealed record QueuedMessage(string Id, SenderSnapshot Sender, TurnMessage Message, DateTimeOffset QueuedAt)
{
    public static QueuedMessage Of(SenderSnapshot sender, TurnMessage message, DateTimeOffset queuedAt) =>
        new(Guid.NewGuid().ToString("N"), sender, message, queuedAt);

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

// What became of a person's send that opened the stream, once vouched for: started on it, handed back to
// its sender because a stop ended that stream while it was being vouched for, or dropped by that stop — a
// reroll's message, already on the line, and a Continue's ask, nobody's words.
public enum SendStart
{
    Started,
    Returned,
    Dropped
}

// The head of a queue, taken as the turn before it ended, on the stream it was taken from: a stream
// stopped or replaced while its dispatch was being vouched for is not the one to write it on.
public sealed record QueuedTurn(QueuedMessage Message, StreamKey Stream);

// A topic whose queue changed with no hub call in hand, with the session its pages are found by.
public sealed record QueueChange(string TopicId, ChannelSession Session);

// One stream of a topic's, as opposed to the one opened after it: a place held, a head taken or a stop
// names the stream it happened on, so nothing it leaves behind lands on another.
public readonly record struct StreamKey(string TopicId, long Number);

// A topic's queue as of one change, numbered so a page told of two changes keeps the later.
// With whose own turn the stream is running, by their account's user id: the one person who may steer it.
// Messages is what still waits; Steering what was steered into the running turn and is not drawn yet.
public sealed record QueueSnapshot(
    IReadOnlyList<QueuedMessage> Messages, long Revision, string? RunningFor = null,
    IReadOnlyList<QueuedMessage>? Steering = null)
{
    public QueueChangedNotification ToNotification(string topicId, string spaceSlug) =>
        new(topicId, [.. Messages.Select(m => m.ToItem())], Revision, spaceSlug, RunningFor,
            [.. (Steering ?? []).Select(m => m.ToItem())]);
}

// What taking a queued message back came to: the message, with the ones still queued behind it, or why
// not — it left the queue already, or it is somebody else's.
public abstract record Withdrawal;

public sealed record Withdrawn(QueuedMessage Message, IReadOnlyList<QueuedMessage> StillQueued) : Withdrawal
{
    // The files the queue still names once this message has left it.
    public IReadOnlyList<string> StillQueuedAttachments() =>
        [.. StillQueued.SelectMany(m => m.Message.Attachments ?? []).Select(a => a.Id)];
}

public sealed record WithdrawalRefused(string Refusal) : Withdrawal;

// A type apart from Withdrawal, since a steer answers nothing about what stays queued, and it can be
// refused for more than a withdrawal: someone else's turn, or nobody there to send it to.
public abstract record Steering;

public sealed record Steered(QueuedMessage Message) : Steering;

public sealed record SteerRefused(string Refusal) : Steering;