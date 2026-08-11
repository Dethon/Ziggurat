using System.Runtime.CompilerServices;
using System.Text;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.McpChannelVoice.PlaybackFakes;

namespace Tests.Unit.McpChannelVoice;

// The queue's one promise: every job it is handed gets exactly one outcome — including a job it
// turned away, and including every job still waiting when the satellite disappears. Before this the
// rule was five callbacks, three of them terminal and mutually exclusive with nothing in the type
// saying so, and teardown settled nothing at all.
public class PlaybackQueueOutcomeTests
{
    [Theory]
    [InlineData(PlaybackOutcomeKind.Drained)]
    [InlineData(PlaybackOutcomeKind.Preempted)]
    [InlineData(PlaybackOutcomeKind.Failed)]
    [InlineData(PlaybackOutcomeKind.Refused)]
    [InlineData(PlaybackOutcomeKind.Discarded)]
    public async Task Enqueue_EveryWayAJobCanEnd_SettlesExactlyOneOutcome(PlaybackOutcomeKind expected)
    {
        var outcome = await EndingAsync(expected);

        outcome.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Enqueue_AfterTheQueueIsClosed_RefusesAsQueueClosed()
    {
        // The satellite disconnected and the loop tore down. Announce tells a missing speaker from a
        // busy one by this reason.
        var queue = new PlaybackQueue();
        queue.CompleteAndDiscardQueued();

        var ticket = queue.Enqueue(Job("x", PlaybackKind.Announce));

        ticket.Refused.ShouldBe(RefusalReason.QueueClosed);
    }

    [Fact]
    public void Dispose_AfterTheLinkDropClose_LeavesALateEnqueueRefusingRatherThanThrowing()
    {
        // The queue owns a semaphore and a token source, one pair per satellite connection, and the
        // drain disposes them once the loop has stopped. A producer can still arrive after that —
        // send_reply resolving a session the drain has just torn down — and it must get the refusal
        // every closed queue gives, never an ObjectDisposedException out of a metrics-free path.
        var queue = new PlaybackQueue();
        queue.CompleteAndDiscardQueued();
        queue.Dispose();

        queue.Enqueue(Job("late", PlaybackKind.Announce)).Refused.ShouldBe(RefusalReason.QueueClosed);
    }

    [Fact]
    public void Dispose_WithoutAPriorClose_StillRefusesALateProducer()
    {
        // Disposal is not a close, but it is the end of the queue either way: a producer arriving
        // after it must get the refusal every closed queue gives rather than an
        // ObjectDisposedException out of the semaphore, whichever order the two calls came in.
        var queue = new PlaybackQueue();
        queue.Dispose();

        queue.Enqueue(Job("late", PlaybackKind.Announce)).Refused.ShouldBe(RefusalReason.QueueClosed);
    }

    [Fact]
    public void Dispose_CalledTwice_IsANoOp()
    {
        // The drain is not the only unwinding path (shutdown cancels the run token first), so
        // disposal has to survive being asked for twice.
        var queue = new PlaybackQueue();
        queue.CompleteAndDiscardQueued();
        queue.Dispose();

        Should.NotThrow(() => queue.Dispose());
    }

    [Fact]
    public void Enqueue_LowPriorityWhileAnythingIsQueued_RefusesAsLowPriorityBehindQueue()
    {
        var queue = new PlaybackQueue();
        queue.Enqueue(Job("a", PlaybackKind.Announce));

        var low = Job("low", PlaybackKind.Announce) with { Priority = AnnouncePriority.Low };

        queue.Enqueue(low).Refused.ShouldBe(RefusalReason.LowPriorityBehindQueue);
    }

    [Fact]
    public async Task Enqueue_RefusedJob_CarriesAnAlreadySettledOutcome()
    {
        // One settle path rather than a branch: a caller reads the reason for its own status and
        // still awaits the same outcome it would have awaited had the job been accepted.
        var queue = new PlaybackQueue();
        queue.CompleteAndDiscardQueued();

        var ticket = queue.Enqueue(Job("x", PlaybackKind.Announce));

        ticket.Completed.IsCompleted.ShouldBeTrue();
        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Refused);
    }

    [Fact]
    public async Task Run_JobPreemptedBeforeItsFirstPull_ReportsZeroChunksWritten()
    {
        // Proving preemption by counting label strings said nothing about what the satellite heard.
        // A job the loop cancels before it touches its audio must report that nothing was spoken.
        var queue = new PlaybackQueue();
        var normal = Job("normal", PlaybackKind.Announce);
        var high = Job("alarm", PlaybackKind.Alarm) with { Priority = AnnouncePriority.High };

        var preempted = queue.Enqueue(normal);
        queue.Enqueue(high);

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, run.Token);
        var outcome = await preempted.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);
        outcome.Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        outcome.ChunksWritten.ShouldBe(0);
    }

    [Fact]
    public async Task Run_JobCutMidSentence_ReportsTheChunksThatReachedTheWriter()
    {
        var queue = new PlaybackQueue();
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> twoThenBlock(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return Chunk();
            yield return Chunk();
            wrote.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        var ticket = queue.Enqueue(Job("reply", PlaybackKind.Reply) with { Audio = twoThenBlock() });
        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, run.Token);

        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.PreemptCurrent();
        var outcome = await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);
        outcome.Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        outcome.ChunksWritten.ShouldBe(2);
    }

    [Fact]
    public async Task Run_AudioCompletedBeforeTeardownCutTheTail_ReportsDrained()
    {
        // The audio was written, so the satellite has it and will play it out. Today that case
        // reports nothing at all, which is why every waiting producer had to carry a token.
        var queue = new PlaybackQueue();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var connection = new CancellationTokenSource();
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 16000 bytes at 16 kHz/16-bit/mono = 500 ms of audio the loop will wait out in real time.
        static async IAsyncEnumerable<AudioChunk> halfSecond()
        {
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var ticket = queue.Enqueue(Job("announce", PlaybackKind.Announce) with { Audio = halfSecond() });
        var pump = queue.RunAsync(
            (_, _) => { wrote.TrySetResult(); return Task.CompletedTask; },
            connection.Token, time);

        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.CancelAsync();          // the satellite drops mid-tail
        await Should.NotThrowAsync(async () =>
        {
            try
            { await pump; }
            catch (OperationCanceledException) { }
        });
        queue.DiscardUnplayed();

        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task DiscardUnplayed_ConnectionDied_SettlesTheInFlightJobAndEverythingBehindIt()
    {
        var queue = new PlaybackQueue();
        using var connection = new CancellationTokenSource();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> parked(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return Chunk();
            playing.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        var inFlight = queue.Enqueue(Job("reply-1", PlaybackKind.Reply) with { Audio = parked() });
        var behind = queue.Enqueue(Job("reply-2", PlaybackKind.Reply));
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, connection.Token);

        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.CancelAsync();
        try
        { await pump.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        queue.DiscardUnplayed();

        (await inFlight.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        (await behind.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
    }

    [Fact]
    public async Task CompleteAndDiscardQueued_TheLinkDropped_DiscardsQueuedJobsWithoutPlayingThem()
    {
        // A link drop is not a shutdown: the run token stays uncancelled, so Complete() alone let
        // ReadAllAsync hand the loop every job still queued — each synthesized and written into the
        // dead socket, settling Failed and firing one spurious error report per job. The link-drop
        // close discards them instead, while the job being played still ends as it really did.
        var queue = new PlaybackQueue();
        var reachedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulled = 0;
        var errors = new List<Exception>();

        async IAsyncEnumerable<AudioChunk> holdThenEnd()
        {
            yield return Chunk();
            reachedFirst.TrySetResult();
            await releaseFirst.Task;
        }

        var inFlight = queue.Enqueue(Job("playing", PlaybackKind.Announce) with { Audio = holdThenEnd() });
        var queuedBehind = queue.Enqueue(
            Job("waiting", PlaybackKind.Announce) with { Audio = Counting(() => Interlocked.Increment(ref pulled)) });
        var pump = queue.RunAsync(
            new PlaybackSink(
                (_, _) => Task.CompletedTask,
                OnError: (_, ex) =>
                {
                    lock (errors)
                    { errors.Add(ex); }
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        await reachedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.CompleteAndDiscardQueued();
        releaseFirst.TrySetResult();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        (await inFlight.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await queuedBehind.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        Volatile.Read(ref pulled).ShouldBe(0);   // its synthesis was never pulled
        errors.ShouldBeEmpty();                  // and no spurious error report fired
    }

    [Fact]
    public async Task CompleteAndDiscardQueued_DuringTheRealtimeTail_ReturnsPromptlyAndSettlesTheJob()
    {
        // A fully-buffered job's audio can be written seconds before the satellite finishes playing
        // it, and the loop waits that tail out under the still-live run token. When the link drops
        // in that window the drain awaits the loop, so reconnect stalled for the remaining audio
        // duration. The link-drop close must cut the tail; the audio was already written, so the
        // job keeps the Drained outcome it earned.
        var queue = new PlaybackQueue();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 160000 bytes at 16 kHz/16-bit/mono = 5 s of audio, written in one chunk.
        static async IAsyncEnumerable<AudioChunk> fiveSeconds()
        {
            yield return new AudioChunk { Data = new byte[160000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var ticket = queue.Enqueue(Job("announce", PlaybackKind.Announce) with { Audio = fiveSeconds() });
        var pump = queue.RunAsync(
            (_, _) => { wrote.TrySetResult(); return Task.CompletedTask; },
            CancellationToken.None, time);

        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(80);               // let the loop reach the real-time tail wait
        queue.CompleteAndDiscardQueued();   // the link dropped

        // The fake clock never advances: only cutting the tail lets the loop return.
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task CompleteAndDiscardQueued_WithTheWriteParkedOnADeadSocket_ReturnsPromptlyAndSettlesEveryJob()
    {
        // A satellite that loses power mid-reply leaves the socket write parked until the TCP
        // retransmit timeout — minutes — and a Wyoming frame is deliberately uncancellable once it
        // starts, so the token buys nothing there. The connection's drain awaits this loop, so that
        // one write held the entire teardown: queued jobs never settled and the host never redialled.
        var queue = new PlaybackQueue();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = queue.Enqueue(Job("playing", PlaybackKind.Announce));
        var behind = queue.Enqueue(Job("waiting", PlaybackKind.Announce));
        // Never returns and never observes its token: the socket write on a satellite that vanished.
        var pump = queue.RunAsync(
            (_, _) => { writing.TrySetResult(); return parked.Task; }, CancellationToken.None);

        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.CompleteAndDiscardQueued();   // the link dropped

        // Bounded by the loop's own backstop (a couple of seconds), where it used to be bounded by
        // the TCP retransmit timeout — that is, not at all as far as a reconnect is concerned.
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        queue.DiscardUnplayed();

        (await inFlight.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        (await behind.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        parked.TrySetResult();
    }

    [Fact]
    public async Task Run_AConsumerOfOneOutcomeBlocks_TheNextJobStillPlays()
    {
        // There is no ordering between an outcome being signalled and the next job starting, and a
        // producer's reaction runs on its own. A slow one used to sit in the seam between two
        // sentences of a single answer.
        var queue = new PlaybackQueue();
        var played = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPlayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.Enqueue(Job("first", PlaybackKind.Announce));
        var blocked = first.Completed.ContinueWith(
            _ => release.Task.GetAwaiter().GetResult(), TaskContinuationOptions.LongRunning);

        queue.Enqueue(Job("second", PlaybackKind.Announce));
        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(
            (chunk, _) =>
            {
                var label = Encoding.UTF8.GetString(chunk.Data.Span);
                lock (played)
                { played.Add(label); }
                if (label == "second")
                {
                    secondPlayed.TrySetResult();
                }
                return Task.CompletedTask;
            },
            run.Token);

        await secondPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        played.ShouldBe(["first", "second"]);

        release.TrySetResult();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Enqueue_ReplySegmentPreemptedBeforeItsFirstPull_ReleasesTheSynthesisAnyway()
    {
        // The prefetch pump is already running when the loop cancels a marked job before touching
        // its audio, so nothing consumer-side ever tears it down: one pump per queued sentence left
        // parked on a full buffer holding an open TTS response, on every alarm that cuts into a
        // streamed reply. The queue owns that lifetime now and releases it on every outcome.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new PlaybackQueue(prefetchBufferChunks: 1);

        var reply = queue.Enqueue(
            Job("reply", PlaybackKind.Reply) with { Audio = EndlessSynthesis(released) });
        queue.Enqueue(Job("alarm", PlaybackKind.Alarm) with { Priority = AnnouncePriority.High });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, run.Token);

        (await reply.Completed.WaitAsync(TimeSpan.FromSeconds(5))).Kind
            .ShouldBe(PlaybackOutcomeKind.Preempted);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Enqueue_ReplySegmentDrained_ReleasesTheSynthesisToo()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new PlaybackQueue(prefetchBufferChunks: 1);

        var reply = queue.Enqueue(
            Job("reply", PlaybackKind.Reply) with { Audio = OneChunkThenRelease(released) });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, run.Token);

        (await reply.Completed.WaitAsync(TimeSpan.FromSeconds(5))).Kind
            .ShouldBe(PlaybackOutcomeKind.Drained);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Enqueue_RefusedReplySegment_NeverStartsASynthesisToDisposeOf()
    {
        // The queue wraps the audio only once it has decided to accept the job, so the refused case
        // has nothing to release and the trickiest disposal path stops existing.
        var pulled = 0;
        var queue = new PlaybackQueue(replyMaxDepth: 1, prefetchBufferChunks: 4);
        queue.Enqueue(Job("first", PlaybackKind.Reply));

        var refused = queue.Enqueue(
            Job("refused", PlaybackKind.Reply) with { Audio = Counting(() => Interlocked.Increment(ref pulled)) });

        refused.Refused.ShouldBe(RefusalReason.QueueFull);
        await Task.Delay(100);   // the pump, had one been created, would have pulled by now
        Volatile.Read(ref pulled).ShouldBe(0);
    }

    private static async IAsyncEnumerable<AudioChunk> EndlessSynthesis(
        TaskCompletionSource released,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        try
        {
            while (true)
            {
                yield return Chunk();
                await Task.Delay(10, token);
            }
        }
        finally
        {
            released.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunkThenRelease(
        TaskCompletionSource released,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        try
        {
            yield return Chunk();
            await Task.Yield();
        }
        finally
        {
            released.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Counting(Action onPull)
    {
        onPull();
        yield return Chunk();
        await Task.Yield();
    }

    private static async Task<PlaybackOutcome> EndingAsync(PlaybackOutcomeKind ending)
    {
        var queue = new PlaybackQueue(replyMaxDepth: 1, announceMaxDepth: 1);
        using var connection = new CancellationTokenSource();

        if (ending == PlaybackOutcomeKind.Refused)
        {
            queue.Enqueue(Job("filling", PlaybackKind.Announce));
            return await queue.Enqueue(Job("refused", PlaybackKind.Announce)).Completed;
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = ending switch
        {
            PlaybackOutcomeKind.Failed => ThrowingAudio(),
            PlaybackOutcomeKind.Drained => Audio(),
            _ => Parked(reached)
        };

        var ticket = queue.Enqueue(Job("job", PlaybackKind.Announce) with { Audio = audio });
        var pump = queue.RunAsync((_, _) => Task.CompletedTask, connection.Token);

        if (ending is PlaybackOutcomeKind.Preempted or PlaybackOutcomeKind.Discarded)
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (ending == PlaybackOutcomeKind.Preempted)
        {
            queue.PreemptCurrent();
        }
        if (ending == PlaybackOutcomeKind.Discarded)
        {
            await connection.CancelAsync();
        }
        else
        {
            // Every other ending is the job's own, so it is what to wait for; only the discarded
            // one is caused by the connection going away.
            await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await connection.StopAsync(pump);
        queue.DiscardUnplayed();

        return await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async IAsyncEnumerable<AudioChunk> Parked(
        TaskCompletionSource reached,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        yield return Chunk();
        reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
    }

}