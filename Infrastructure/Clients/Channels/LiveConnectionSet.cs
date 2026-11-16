using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Contracts;

namespace Infrastructure.Clients.Channels;

// The agent's channel connections as they come to be: the dialled channels' from startup, and each
// household's own channel server's from the first time that household opens its tunnel. Nothing is
// ever taken out — a connection whose tunnel closed is not connected, which is a state of the
// connection (ADR 0011), not its absence — so a watcher sees every connection exactly once.
public sealed class LiveConnectionSet<T> : IConnectionSet<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _held;
    private readonly List<Channel<T>> _watchers = [];

    public LiveConnectionSet(IEnumerable<T> initial) => _held = [.. initial];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    public void Add(T connection)
    {
        lock (_lock)
        {
            _held.Add(connection);
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(connection);
            }
        }
    }

    public IEnumerator<T> GetEnumerator() => Snapshot().AsEnumerable().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerable<T> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var added = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
        T[] held;
        lock (_lock)
        {
            held = [.. _held];
            _watchers.Add(added);
        }

        try
        {
            foreach (var connection in held)
            {
                yield return connection;
            }

            await foreach (var connection in added.Reader.ReadAllAsync(ct))
            {
                yield return connection;
            }
        }
        finally
        {
            lock (_lock)
            {
                _watchers.Remove(added);
            }
        }
    }

    private T[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _held];
        }
    }
}