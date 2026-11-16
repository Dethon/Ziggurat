using System.Runtime.CompilerServices;
using Domain.Extensions;
using Shouldly;

namespace Tests.Unit.Domain;

public class MergeTests
{
    private static readonly int[] _sourceArray5 = [1, 2, 3];
    private static readonly int[] _sourceArray4 = [4, 5, 6];
    private static readonly int[] _sourceArray = [1, 2, 3];
    private static readonly int[] _sourceArray0 = [1, 2];
    private static readonly int[] _sourceArray1 = [3, 4];
    private static readonly int[] _sourceArray2 = [5, 6];
    private static readonly int[] _sourceArray3 = [3, 4];

    [Fact]
    public async Task Merge_TwoSources_CombinesAllElements()
    {
        // Arrange
        var left = _sourceArray5.ToAsyncEnumerable();
        var right = _sourceArray4.ToAsyncEnumerable();

        // Act
        var merged = await left.Merge(right, CancellationToken.None).ToListAsync();

        // Assert
        merged.Count.ShouldBe(6);
        merged.ShouldContain(1);
        merged.ShouldContain(6);
    }

    [Fact]
    public async Task Merge_EmptySources_ReturnsEmpty()
    {
        // Arrange
        var left = AsyncEnumerable.Empty<int>();
        var right = AsyncEnumerable.Empty<int>();

        // Act
        var merged = await left.Merge(right, CancellationToken.None).ToListAsync();

        // Assert
        merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task Merge_OneEmptySource_ReturnsOtherSource()
    {
        // Arrange
        var left = _sourceArray.ToAsyncEnumerable();
        var right = AsyncEnumerable.Empty<int>();

        // Act
        var merged = await left.Merge(right, CancellationToken.None).ToListAsync();

        // Assert
        merged.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Merge_MultipleSources_CombinesAll()
    {
        // Arrange
        var sources = new[]
        {
            _sourceArray0.ToAsyncEnumerable(),
            _sourceArray1.ToAsyncEnumerable(),
            _sourceArray2.ToAsyncEnumerable()
        };

        // Act
        var merged = await sources.Merge(CancellationToken.None).ToListAsync();

        // Assert
        merged.Count.ShouldBe(6);
        merged.Order().ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task Merge_WithCancellation_StopsProcessing()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // Act
        var mergeTask = Task.Run(async () =>
        {
            var items = new List<int>();
            await foreach (var item in slowSource().Merge(AsyncEnumerable.Empty<int>(), cts.Token))
            {
                items.Add(item);
                if (items.Count >= 3)
                {
                    await cts.CancelAsync();
                }
            }

            return items;
        }, cts.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await mergeTask);
        return;

        async IAsyncEnumerable<int> slowSource()
        {
            for (var i = 0; i < 100; i++)
            {
                yield return i;
                await Task.Delay(50, cts.Token);
            }
        }
    }

    [Fact]
    public async Task Merge_OneInnerStreamCancels_SiblingStreamsContinue()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var siblingFirstEmitted = new TaskCompletionSource();

        // Act
        var merged = await sources().Merge(cts.Token).ToListAsync(CancellationToken.None);

        // Assert: the sibling's post-failure item must survive a peer's cancellation
        merged.ShouldContain(1);
        merged.ShouldContain(2);
        return;

        async IAsyncEnumerable<int> failing()
        {
            await siblingFirstEmitted.Task;
            throw new OperationCanceledException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        async IAsyncEnumerable<int> sibling([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            siblingFirstEmitted.TrySetResult();
            await Task.Delay(100, ct);
            yield return 2;
        }

        async IAsyncEnumerable<IAsyncEnumerable<int>> sources()
        {
            yield return failing();
            await Task.Yield();
            yield return sibling();
        }
    }

    [Fact]
    public async Task Merge_OneInnerStreamThrows_SiblingStreamsContinue()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var siblingFirstEmitted = new TaskCompletionSource();

        // Act
        var merged = await sources().Merge(cts.Token).ToListAsync(CancellationToken.None);

        // Assert: a peer's genuine fault must not tear down the sibling stream
        merged.ShouldContain(1);
        merged.ShouldContain(2);
        return;

        async IAsyncEnumerable<int> failing()
        {
            await siblingFirstEmitted.Task;
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        async IAsyncEnumerable<int> sibling([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            siblingFirstEmitted.TrySetResult();
            await Task.Delay(100, ct);
            yield return 2;
        }

        async IAsyncEnumerable<IAsyncEnumerable<int>> sources()
        {
            yield return failing();
            await Task.Yield();
            yield return sibling();
        }
    }

    [Fact]
    public async Task Merge_AsyncEnumerableOfAsyncEnumerables_Works()
    {
        // Act
        var merged = await sourceOfSources().Merge(CancellationToken.None).ToListAsync();

        // Assert
        merged.Count.ShouldBe(4);
        merged.Order().ShouldBe([1, 2, 3, 4]);
        return;

        // Arrange
        static async IAsyncEnumerable<IAsyncEnumerable<int>> sourceOfSources()
        {
            yield return _sourceArray0.ToAsyncEnumerable();
            await Task.Yield();
            yield return _sourceArray3.ToAsyncEnumerable();
        }
    }

    [Fact]
    public async Task Merge_AsyncEnumerableOfAsyncEnumerables_DoesNotBufferOuterStream()
    {
        using var cts = new CancellationTokenSource();

        var readTask = Task.Run(async () =>
        {
            // ReSharper disable AccessToDisposedClosure
            await foreach (var item in outer(cts.Token).Merge(cts.Token))
            {
                await cts.CancelAsync();
                return item;
            }

            throw new InvalidOperationException("No items were produced.");
        }, cts.Token);

        // A buffering merge waits on an outer stream that never ends, so the read never completes; the
        // cap only says how long to wait for that hang, and a loaded machine took the read past a second.
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        completed.ShouldBe(readTask);

        (await readTask).ShouldBe(1);
        return;

        static async IAsyncEnumerable<IAsyncEnumerable<int>> outer([EnumeratorCancellation] CancellationToken ct)
        {
            yield return first(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        static async IAsyncEnumerable<int> first([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return 1;
            await Task.Delay(10, ct);
            yield return 2;
        }
    }
}