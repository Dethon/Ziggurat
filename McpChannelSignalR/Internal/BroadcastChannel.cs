using System.Threading.Channels;

namespace McpChannelSignalR.Internal;

public sealed class BroadcastChannel<T>
{
    private readonly List<Channel<T>> _subscribers = [];
    private readonly Lock _lock = new();

    public ChannelReader<T> Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>();
        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    public async Task WriteAsync(T item, CancellationToken cancellationToken)
    {
        List<Channel<T>> subs;
        lock (_lock)
        {
            subs = [.. _subscribers];
        }

        await Task.WhenAll(subs.Select(s => s.Writer.WriteAsync(item, cancellationToken).AsTask()));
    }

    // Every subscriber's channel is unbounded, so a write never waits: it is made under the caller's
    // lock, beside the stream's buffer, so a reader joining takes the buffer and its subscription as one.
    public void Write(T item)
    {
        lock (_lock)
        {
            foreach (var s in _subscribers)
            {
                s.Writer.TryWrite(item);
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            foreach (var s in _subscribers)
            {
                s.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}