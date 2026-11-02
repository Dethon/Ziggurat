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
    private sealed class AsyncGrouping<TKey, TElement>(TKey key, Action onComplete, Action<TElement>? onDropped)
        : IAsyncGrouping<TKey, TElement>
    {
        private readonly Channel<TElement> _channel = Channel.CreateUnbounded<TElement>();
        private int _completed;

        public TKey Key => key;

        internal async ValueTask WriteAsync(TElement item, CancellationToken ct)
        {
            // An item that races in just behind the group's completion never reaches the
            // channel, so no drain can recover it — reporting it here is its only trace.
            if (_completed != 0)
            {
                onDropped?.Invoke(item);
                return;
            }

            try
            {
                await _channel.Writer.WriteAsync(item, ct);
            }
            catch (ChannelClosedException)
            {
                onDropped?.Invoke(item);
            }
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();
            onComplete();
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
        public async IAsyncEnumerable<IAsyncGrouping<TKey, TSource>> GroupByStreaming<TKey>(
            Func<TSource, CancellationToken, ValueTask<TKey>> keySelector,
            Action<TSource>? onDropped = null,
            [EnumeratorCancellation] CancellationToken ct = default) where TKey : notnull
        {
            var groups = new ConcurrentDictionary<TKey, AsyncGrouping<TKey, TSource>>();
            try
            {
                await foreach (var item in source.WithCancellation(ct))
                {
                    var key = await keySelector(item, ct);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new AsyncGrouping<TKey, TSource>(key, () => groups.TryRemove(key, out _), onDropped);
                        groups[key] = group;
                        yield return group;
                    }

                    await group.WriteAsync(item, ct);
                }
            }
            finally
            {
                foreach (var group in groups.Values)
                {
                    group.Complete();
                }

                groups.Clear();
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
                        Contents = [new ErrorContent(ex is TurnRefusedException ? ex.Message : $"An error occurred: {ex.Message}")
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