using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public interface IAsyncGrouping<out TKey, out TElement> : IAsyncEnumerable<TElement>
{
    TKey Key { get; }

    void Complete();

    // Synchronously hands over whatever is buffered but unread. For a consumer tearing down
    // mid-stream: what it does not drain here was never delivered and vanishes with the group.
    IEnumerable<TElement> DrainPending();
}

public static class IAsyncEnumerableExtensions
{
    // A group's items, until it completes. Completing hands back what its consumer had not read (handBack, in the
    // order it came) and refuses anything written after, under one lock with the write, so no item is both in
    // the group and handed back, and none is in neither.
    private sealed class AsyncGrouping<TKey, TElement>(TKey key, Action<AsyncGrouping<TKey, TElement>> onComplete,
        Action<TElement> handBack) : IAsyncGrouping<TKey, TElement>
    {
        private readonly Channel<TElement> _channel = Channel.CreateUnbounded<TElement>();
        private readonly Lock _gate = new();
        private bool _completed;

        public TKey Key => key;

        // False once the group completed: the item is the caller's to route again.
        internal bool TryWrite(TElement item)
        {
            lock (_gate)
            {
                return !_completed && _channel.Writer.TryWrite(item);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _channel.Writer.TryComplete();
                while (_channel.Reader.TryRead(out var unread))
                {
                    handBack(unread);
                }
            }

            onComplete(this);
        }

        // The source ended: nothing more comes, and what is in the group is still its consumer's to read.
        internal void End()
        {
            lock (_gate)
            {
                _completed = true;
                _channel.Writer.TryComplete();
            }
        }

        public IEnumerable<TElement> DrainPending()
        {
            while (_channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }

        public IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken ct = default)
        {
            return _channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        }
    }

    extension<TSource>(IAsyncEnumerable<TSource> source)
    {
        // A group completes when its consumer is done with it (a conversation's stop, a failed setup): what it
        // had not read, and anything routed to it as it completed, was sent after whatever ended it, so it is
        // routed again, to a fresh group of its key, in the order it came and ahead of anything the source
        // gives after. Once: a group made for an item handed back drops what it leaves unread (onDropped), as
        // does a fresh group that refuses the item it was made for (a consumer done before reading anything),
        // so a consumer that ends every group at once cannot keep an item going round.
        public async IAsyncEnumerable<IAsyncGrouping<TKey, TSource>> GroupByStreaming<TKey>(
            Func<TSource, CancellationToken, ValueTask<TKey>> keySelector,
            Action<TSource>? onDropped = null,
            [EnumeratorCancellation] CancellationToken ct = default) where TKey : notnull
        {
            var groups = new ConcurrentDictionary<TKey, AsyncGrouping<TKey, TSource>>();
            var handedBack = Channel.CreateUnbounded<(TSource Item, bool Again)>();
            await using var reading = source.GetAsyncEnumerator(ct);
            Task<bool>? next = null;
            Task<bool>? handBackWaiting = null;
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var fromSource = !handedBack.Reader.TryRead(out var routed);
                    if (fromSource)
                    {
                        next ??= reading.MoveNextAsync().AsTask();
                        handBackWaiting ??= handedBack.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                        if (await Task.WhenAny(next, handBackWaiting, cancelled) != next)
                        {
                            ct.ThrowIfCancellationRequested();
                            handBackWaiting = null;
                            continue;
                        }

                        if (!await next)
                        {
                            break;
                        }

                        next = null;
                        // Whatever was handed back meanwhile came before this item.
                        if (handedBack.Reader.TryPeek(out _))
                        {
                            handedBack.Writer.TryWrite((reading.Current, false));
                            continue;
                        }

                        routed = (reading.Current, false);
                    }

                    var key = await keySelector(routed.Item, ct);
                    if (groups.TryGetValue(key, out var group) && group.TryWrite(routed.Item))
                    {
                        continue;
                    }

                    // A group refusing an item read just now has just completed, and what it handed back came
                    // before that item.
                    if (fromSource && group is not null && handedBack.Reader.TryPeek(out _))
                    {
                        handedBack.Writer.TryWrite(routed);
                        continue;
                    }

                    // No group of its key, or the one there completed as the item reached it: a fresh one.
                    var again = routed.Again;
                    var fresh = new AsyncGrouping<TKey, TSource>(
                        key, done => groups.TryRemove(new KeyValuePair<TKey, AsyncGrouping<TKey, TSource>>(key, done)),
                        item =>
                        {
                            if (again)
                            {
                                onDropped?.Invoke(item);
                            }
                            else
                            {
                                handedBack.Writer.TryWrite((item, true));
                            }
                        });
                    groups[key] = fresh;
                    yield return fresh;
                    if (!fresh.TryWrite(routed.Item))
                    {
                        onDropped?.Invoke(routed.Item);
                    }
                }
            }
            finally
            {
                foreach (var group in groups.Values)
                {
                    group.End();
                }

                groups.Clear();

                // The source's read still out is settled before the source is let go.
                if (next is not null)
                {
                    try
                    {
                        await next;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }

        public IAsyncEnumerable<TSource> Merge(IAsyncEnumerable<TSource> right, CancellationToken ct)
        {
            return new[] { source, right }.ToAsyncEnumerable().Merge(ct);
        }

        public async IAsyncEnumerable<TSource> OnCompletion<TState>(
            TState seed,
            Func<TState, TSource, TState> fold,
            Func<TState, CancellationToken, ValueTask> onCompletion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var state = seed;
            await foreach (var item in source.WithCancellation(ct))
            {
                state = fold(state, item);
                yield return item;
            }

            await onCompletion(state, ct);
        }

        // after runs however the enumeration ends: completed, faulted, cancelled, or let go of unread.
        public async IAsyncEnumerable<TSource> Finally(
            Func<Task> after, [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                await foreach (var item in source.WithCancellation(ct))
                {
                    yield return item;
                }
            }
            finally
            {
                await after();
            }
        }

        public async IAsyncEnumerable<TSource> IgnoreCancellation(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var enumerator = source.GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
    }

    public static IAsyncEnumerable<T> Merge<T>(this IEnumerable<IAsyncEnumerable<T>> sources, CancellationToken ct)
    {
        return sources.ToAsyncEnumerable().Merge(ct);
    }

    public static async IAsyncEnumerable<T> Merge<T>(
        this IAsyncEnumerable<IAsyncEnumerable<T>> sources,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _ = Pump(sources, channel.Writer, ct);
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }

    private static Task Pump<T>(
        IAsyncEnumerable<IAsyncEnumerable<T>> sources,
        ChannelWriter<T> writer,
        CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new List<Task>();
            try
            {
                await foreach (var stream in sources.WithCancellation(linkedCts.Token))
                {
                    tasks.Add(ConsumeStream(stream, writer, linkedCts.Token));
                }

                await Task.WhenAll(tasks);
                writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }, ct);
    }

    private static async Task ConsumeStream<T>(
        IAsyncEnumerable<T> stream,
        ChannelWriter<T> writer,
        CancellationToken ct)
    {
        try
        {
            await foreach (var item in stream.WithCancellation(ct))
            {
                await writer.WriteAsync(item, ct);
            }
        }
        catch (Exception)
        {
            // Fully isolate inner streams: a single stream's cancellation or fault
            // must never cancel siblings or tear down the shared merge channel.
            // Per-stream error reporting is the responsibility of the stream itself
            // (e.g. WithErrorHandling), not the merge.
        }
    }

    public static async IAsyncEnumerable<AgentResponseUpdate> WithErrorHandling(
        this IAsyncEnumerable<AgentResponseUpdate> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        AgentResponseUpdate? errorResponse = null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    errorResponse = new AgentResponseUpdate
                    {
                        Contents = [new ErrorContent(TurnFailure.Answer(ex))
                        {
                            ErrorCode = ex.GetType().Name
                        }]
                    };
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (errorResponse is not null)
        {
            yield return errorResponse;
        }
    }
}