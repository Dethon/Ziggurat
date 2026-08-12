using System.Runtime.CompilerServices;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class PrefetchedAudioTests
{
    private static AudioChunk Chunk(byte value) =>
        new() { Data = new[] { value }, Format = AudioFormat.WyomingStandard };

    private static async IAsyncEnumerable<AudioChunk> Source(
        TaskCompletionSource pulled, int count, [EnumeratorCancellation] CancellationToken ct = default)
    {
        pulled.TrySetResult();
        foreach (var i in Enumerable.Range(0, count))
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return Chunk((byte)i);
        }
    }

    [Fact]
    public async Task Chunks_ReplaysEverythingInOrder()
    {
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var prefetch = new PrefetchedAudio(Source(pulled, 4), capacity: 8);

        var seen = new List<byte>();
        await foreach (var chunk in prefetch.Chunks)
        {
            seen.Add(chunk.Data.Span[0]);
        }

        seen.ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Chunks_ForeignCancellation_SurfacesInsteadOfCompletingCleanly()
    {
        // Only OUR cancellation is a clean stop. A cancellation from somewhere else — an HTTP client
        // timeout on the Kokoro call, or a token threaded into SynthesizeAsync — must surface, or
        // the pump completes the channel with no error and the playback loop sees a zero-chunk job:
        // drained, no failure, no metric. A dropped sentence reported as spoken.
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();

        async IAsyncEnumerable<AudioChunk> foreignCancelled()
        {
            await Task.Yield();
            yield return Chunk(0);
            throw new OperationCanceledException(foreign.Token);
        }

        await using var prefetch = new PrefetchedAudio(foreignCancelled(), capacity: 8);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in prefetch.Chunks)
            {
            }
        });
    }

    [Fact]
    public async Task Chunks_SurfaceTheSourceFailure()
    {
        // A Wyoming/Kokoro error event throws mid-synthesis; the playback loop relies on that
        // surfacing so the job settles as failed instead of hanging whoever awaits it.
        static async IAsyncEnumerable<AudioChunk> throwing()
        {
            await Task.Yield();
            throw new InvalidOperationException("kokoro exploded");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        await using var prefetch = new PrefetchedAudio(throwing(), capacity: 8);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in prefetch.Chunks)
            { }
        });
    }

    [Fact]
    public async Task DisposeAsync_StopsThePumpWhenNobodyConsumes()
    {
        // A segment the playback queue refused is never enumerated, so disposal is the only thing
        // that releases the in-flight HTTP response.
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> endless([EnumeratorCancellation] CancellationToken ct = default)
        {
            pulled.TrySetResult();
            try
            {
                while (true)
                {
                    await Task.Delay(10, ct);
                    yield return Chunk(1);
                }
            }
            finally
            {
                cancelled.TrySetResult();
            }
        }

        var prefetch = new PrefetchedAudio(endless(), capacity: 4);
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await prefetch.DisposeAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Capacity_BoundsHowFarAheadItRuns()
    {
        // Unbounded would let a long announcement buffer its whole synthesis into memory; the
        // producer must park once the buffer is full and resume as the loop drains it.
        var produced = 0;
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> counting([EnumeratorCancellation] CancellationToken ct = default)
        {
            pulled.TrySetResult();
            foreach (var i in Enumerable.Range(0, 100))
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref produced);
                await Task.Yield();
                yield return Chunk((byte)i);
            }
        }

        await using var prefetch = new PrefetchedAudio(counting(), capacity: 4);
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        // capacity (4) + the one the producer is parked on, with a little slack for scheduling
        Volatile.Read(ref produced).ShouldBeLessThan(8);
    }
}