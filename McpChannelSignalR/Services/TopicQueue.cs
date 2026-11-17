using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// A topic's queue, read and written only under StreamService's stream lock, so nothing here locks on its
// own. Two lifetimes in one place: what waits on the running stream goes with it, while what a stop
// handed back outlives it, until taken or the topic is deleted, since a sender with no page open finds
// it the next time they open the topic.
internal sealed class TopicQueue
{
    public RunningQueue? Running { get; set; }

    // One list for every sender rather than one each: a sender takes theirs out by account, in the order
    // they were handed back, and a null account (nobody's) needs no key of its own.
    public List<QueuedMessage> Returned { get; } = [];

    // Kept until the topic is deleted: a message still being admitted to that stream's queue as it
    // stopped goes back to its sender with the rest, rather than be answered "nothing runs" and sent
    // by its page as a new turn behind the cancel, and a remove or an edit arriving after the stop is
    // told where the message went even once a page has taken it.
    public StoppedStream? LastStopped { get; set; }

    public bool IsEmpty => Running is null && Returned.Count == 0 && LastStopped is null;
}

internal sealed class RunningQueue(StreamKey stream)
{
    public StreamKey Stream { get; } = stream;

    public List<QueuedMessage> Waiting { get; } = [];

    // Heads taken as the turn before them ended, until the dispatcher starts each (StartAsync): not
    // started yet, so a stop meanwhile returns them with the rest.
    public List<QueuedMessage> Dispatching { get; } = [];

    // Steers sent to the agent whose turns have not started, in the order they were sent: each counts
    // as pending, so the running turn's end starts the first of them (writing its message there)
    // rather than the queue's head, and a stop meanwhile returns them to their senders.
    public List<WaitingSteer> Steers { get; } = [];

    // The topic's owner, once they stopped sharing it while this stream ran: a message from anyone else
    // still being admitted then goes to its sender, never into the queue.
    public Account? PrivateTo { get; set; }

    // The account whose own turn the stream is running: none for a turn an agent started (a
    // schedule's, a watch's), and none for a queued turn taken but not started yet. Only that person
    // may steer it.
    public Account? RunningFor { get; set; }

    // Uncounted until something counts: a turn ending on a stream nobody counted leaves nothing
    // pending rather than less than nothing.
    private int? _pending;

    public void AddPending() => _pending = (_pending ?? 0) + 1;

    public int RemovePending() => (_pending = _pending is { } count ? count - 1 : 0).Value;

    // A queued head's turn is the one turn left on the stream once it is taken, whatever went before.
    public void PendingOnlyTheHead() => _pending = 1;
}

// A steer sent and not started, with whatever the agent answered under its turn's key meanwhile: the
// agent refuses a steer as it arrives (its sender disabled since) with an error and then the stream's
// end while the turn it was meant to cut still runs. That answer is the steer's, held until the
// running turn ends and drawn after the steer's message there, where its turn would have run; the
// running turn is not ended by it.
internal sealed class WaitingSteer(QueuedMessage message)
{
    public QueuedMessage Message { get; } = message;

    public List<ChatStreamMessage> Held { get; } = [];

    // Its turn ended already (the reply that said IsComplete came under its key).
    public bool Answered { get; set; }
}

internal sealed class StoppedStream(StreamKey stream, IEnumerable<QueuedMessage> returned)
{
    private readonly Dictionary<string, Account?> _senders =
        returned.ToDictionary(m => m.Id, m => m.Sender.Principal.Account);

    public StreamKey Stream { get; } = stream;

    public void Add(QueuedMessage message) => _senders[message.Id] = message.Sender.Principal.Account;

    public bool Returned(string queuedId, out Account? sender) => _senders.TryGetValue(queuedId, out sender);
}