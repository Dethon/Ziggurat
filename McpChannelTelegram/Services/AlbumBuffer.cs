using Telegram.Bot;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

internal sealed record Album(string AgentId, ITelegramBotClient Client, IReadOnlyList<Message> Messages);

// Telegram delivers an album as separate updates sharing a media-group id, arriving as each file
// finishes uploading. This holds a group until it goes quiet and hands it over as one thing, so an
// album is one turn carrying every reference rather than one turn per photo.
//
// It is the only thing on this channel that needs a clock, which is the point: the intake beside
// it is a pure function of the update, so a refusal rule can be added without a fight with a timer.
//
// Held updates have already been acknowledged to Telegram, so a crash inside the window loses the
// group; the exposure is bounded by the upload and accepted.
internal sealed class AlbumBuffer(TimeProvider time, Func<Album, Task> release) : IDisposable
{
    // Sliding, reset by each arrival, with no ceiling and no early release when a group reaches
    // Telegram's limit of ten: a straggler on a slow upload must join its album rather than become
    // a second turn with files missing. A constant rather than a setting — it describes how
    // Telegram uploads behave, not something an operator keeps correct.
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<AlbumKey, Pending> _pending = [];
    private readonly Lock _gate = new();

    // Keyed by chat as well as by group: Telegram's media-group id is only unique within a chat,
    // and two albums landing in different chats are two albums. A tuple rather than a joined
    // string, so nothing turns on a separator character an agent id might contain.
    private readonly record struct AlbumKey(string AgentId, long ChatId, string MediaGroupId);

    public void Add(string agentId, ITelegramBotClient client, Message message)
    {
        var key = new AlbumKey(agentId, message.Chat.Id, message.MediaGroupId!);

        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var pending))
            {
                pending.Messages.Add(message);
                pending.LastArrival = time.GetUtcNow();
                pending.Timer.Change(Debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            pending = new Pending(agentId, client) { LastArrival = time.GetUtcNow() };
            pending.Messages.Add(message);
            _pending[key] = pending;
            pending.Timer = time.CreateTimer(_ => Release(key), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Release(AlbumKey key)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out pending))
            {
                return;
            }

            // An item that landed after this timer fired but before it took the gate has already
            // pushed the window out. Backing off here rather than releasing is what stops that
            // straggler from becoming a second turn with the rest of its album missing.
            if (time.GetUtcNow() - pending.LastArrival < Debounce)
            {
                return;
            }

            _pending.Remove(key);
        }

        pending.Timer.Dispose();

        // The release delegate owns its own failures; nothing here can tell a sender to try again.
        _ = release(new Album(pending.AgentId, pending.Client, pending.Messages));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending.Values.ToList().ForEach(pending => pending.Timer.Dispose());
            _pending.Clear();
        }
    }

    private sealed class Pending(string agentId, ITelegramBotClient client)
    {
        public string AgentId { get; } = agentId;
        public ITelegramBotClient Client { get; } = client;
        public List<Message> Messages { get; } = [];
        public DateTimeOffset LastArrival { get; set; }
        public ITimer Timer { get; set; } = null!;
    }
}