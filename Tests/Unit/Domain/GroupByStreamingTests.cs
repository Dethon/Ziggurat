using Domain.Extensions;
using Shouldly;

namespace Tests.Unit.Domain;

public class GroupByStreamingTests
{
    private static readonly int[] _sourceArray0 = [1, 2, 3];
    private static readonly int[] _sourceArray1 = [1, 2, 3, 4, 5, 6];
    private static readonly string[] _sourceArray2 = ["a1", "b1", "a2", "c1", "b2"];
    private static readonly int[] _sourceArray3 = [1, 2, 3];
    private static readonly int[] _sourceArray4 = [1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public async Task GroupByStreaming_GroupContainsCorrectElements()
    {
        // Arrange
        var source = _sourceArray1.ToAsyncEnumerable();

        // Act
        var groups = await source
            .GroupByStreaming((x, _) => ValueTask.FromResult(x % 2 == 0 ? "even" : "odd"))
            .ToListAsync();

        var oddGroup = groups.First(g => g.Key == "odd");
        var evenGroup = groups.First(g => g.Key == "even");

        // Assert
        var oddItems = await oddGroup.ToListAsync();
        var evenItems = await evenGroup.ToListAsync();

        oddItems.ShouldBe([1, 3, 5]);
        evenItems.ShouldBe([2, 4, 6]);
    }

    [Fact]
    public async Task GroupByStreaming_YieldsGroupsAsTheyAreDiscovered()
    {
        // Arrange
        var source = _sourceArray2.ToAsyncEnumerable();
        var yieldedKeys = new List<string>();

        // Act
        await foreach (var group in source.GroupByStreaming((x, _) => ValueTask.FromResult(x[0].ToString())))
        {
            yieldedKeys.Add(group.Key);
        }

        // Assert - keys should be yielded in order of first appearance
        yieldedKeys.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public async Task GroupByStreaming_WithEmptySource_ReturnsNoGroups()
    {
        // Arrange
        var source = AsyncEnumerable.Empty<int>();

        // Act
        var groups = await source
            .GroupByStreaming((x, _) => ValueTask.FromResult(x))
            .ToListAsync();

        // Assert
        groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task GroupByStreaming_WithCancellation_StopsProcessing()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Act
        var groups = infiniteSource()
            .GroupByStreaming((x, _) =>
            {
                processedCount++;
                if (processedCount >= 5)
                {
                    cts.Cancel();
                }
                return ValueTask.FromResult(x % 2);
            }, ct: cts.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in groups)
            { }
        });
        processedCount.ShouldBeGreaterThanOrEqualTo(5);
        return;

        static async IAsyncEnumerable<int> infiniteSource()
        {
            var i = 0;
            while (true)
            {
                yield return i++;
                await Task.Yield();
            }
            // ReSharper disable once IteratorNeverReturns
        }
    }

    [Fact]
    public async Task GroupByStreaming_WithAsyncKeySelector_AwaitsCorrectly()
    {
        // Arrange
        var source = _sourceArray3.ToAsyncEnumerable();
        var keySelectorCalled = 0;

        // Act
        var groups = await source
            .GroupByStreaming(async (x, ct) =>
            {
                keySelectorCalled++;
                await Task.Delay(10, ct);
                return x % 2;
            })
            .ToListAsync();

        // Assert
        keySelectorCalled.ShouldBe(3);
        groups.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GroupByStreaming_ItemWrittenAfterTheGroupCompleted_ReportsItAsDropped()
    {
        // The consumer completes each group at the yield, before the producer writes the
        // item into it — the same shape as a message racing in just behind a teardown.
        // Silently ignoring the write would make the item vanish; it must be reported.
        var dropped = new List<int>();

        await foreach (var group in _sourceArray0.ToAsyncEnumerable()
            .GroupByStreaming((_, _) => ValueTask.FromResult("key"), dropped.Add))
        {
            group.Complete();
        }

        dropped.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task GroupByStreaming_GroupsCanBeConsumedConcurrently()
    {
        // Arrange
        var source = _sourceArray4.ToAsyncEnumerable();
        var results = new List<(int key, List<int> items)>();

        // Act - collect groups and start consuming them concurrently
        var consumeTasks = new List<Task>();
        await foreach (var group in source.GroupByStreaming((x, _) => ValueTask.FromResult(x % 2)))
        {
            var key = group.Key;
            consumeTasks.Add(Task.Run(async () =>
            {
                var items = await group.ToListAsync();
                lock (results)
                {
                    results.Add((key, items));
                }
            }));
        }

        await Task.WhenAll(consumeTasks);

        // Assert
        results.Count.ShouldBe(2);
        var oddResult = results.First(r => r.key == 1);
        var evenResult = results.First(r => r.key == 0);
        oddResult.items.ShouldBe([1, 3, 5, 7]);
        evenResult.items.ShouldBe([2, 4, 6, 8]);
    }
}