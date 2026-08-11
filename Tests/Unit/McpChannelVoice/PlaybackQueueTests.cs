using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.McpChannelVoice.PlaybackFakes;

namespace Tests.Unit.McpChannelVoice;

// Ordering, preemption and the loop's own timing, driven against sources the loop pulls itself.
// Every queue here is built without a prefetch so a reply segment's audio is pulled when the loop
// reaches it: the prefetch is the subject of PlaybackQueueOutcomeTests, and having it run ahead of
// the loop would move a fake clock before the loop had taken its first reading.
public class PlaybackQueueTests
{
    // The anchors are ITurnAnchor's, not the queue's (see the surface test below): a test that
    // walks a turn stands in for CaptureSession, so it holds what CaptureSession holds.
    private static ITurnAnchor Anchors(PlaybackQueue queue) => queue;

    // ADR 0013: which type a caller holds is the statement that it is or is not a turn. Every
    // producer holds the queue, so the anchors cannot sit on it — a one-shot listen that is not a
    // turn (an approval prompt) would be one call away from corrupting the latency of the turn
    // actually in flight. They live on ITurnAnchor, which CaptureSession is the type to hold.
    [Fact]
    public void PlaybackQueue_DoesNotCarryTheTurnAnchorsOnItsOwnSurface()
    {
        var surface = typeof(PlaybackQueue)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        surface.ShouldNotContain("MarkTurnStart");
        surface.ShouldNotContain("MarkSpeechEnd");
    }

    [Fact]
    public async Task Enqueue_Alarm_PreemptsSegmentsAlreadyQueuedBehindTheCurrentOne()
    {
        // A reply is several sentence jobs now, so cancelling only _currentCts left an alarm
        // queued behind the REST of the answer: it cut sentence 1 and was then heard after sentences
        // 2..N had played in full. Every job queued when an ALARM arrives must be preempted — the
        // flush is the alarm kind's alone, not every High job's.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> gated(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new AudioChunk
            { Data = Encoding.UTF8.GetBytes("s1"), Format = AudioFormat.WyomingStandard };
            firstChunkWritten.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }

        var segment1 = new PlaybackJob(
            Label: "reply-1",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: gated());
        var segment2 = segment1 with { Label = "reply-2", Audio = Audio("s2", count: 1) };
        var segment3 = segment1 with { Label = "reply-3", Audio = Audio("s3", count: 1) };
        var alarm = segment1 with
        {
            Label = "alarm",
            Kind = PlaybackKind.Alarm,
            Priority = AnnouncePriority.High,
            Audio = Audio("alarm", count: 1)
        };

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            run.Token);

        var one = queue.Enqueue(segment1);
        var two = queue.Enqueue(segment2);
        var three = queue.Enqueue(segment3);
        await firstChunkWritten.Task;

        queue.Enqueue(alarm);
        await PlaybackLoop.UntilAsync(() => played.Count >= 2, "the alarm to be heard");
        await run.StopAsync(pumpTask);

        // The alarm is heard next, not after the rest of the answer — and every segment that was
        // queued when it arrived says so itself.
        played.ShouldBe(["s1", "alarm"]);
        foreach (var segment in new[] { one, two, three })
        {
            (await segment.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        }
    }

    [Fact]
    public async Task Enqueue_HighApprovalMidStream_QueuedReplySegmentsStillPlayAfterThePrompt()
    {
        // Only an alarm may flush the sentences queued behind the current one. An approval prompt
        // (or a chime) dropped mid-stream cuts the sentence being spoken and plays next, and the
        // rest of the answer follows it — marking every queued segment for preemption meant
        // sentence 2 of an answer was never spoken at all.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> gated(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new AudioChunk
            { Data = Encoding.UTF8.GetBytes("s1"), Format = AudioFormat.WyomingStandard };
            firstChunkWritten.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        var segment1 = new PlaybackJob(
            Label: "reply-1",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: gated());
        var segment2 = segment1 with { Label = "reply-2", Audio = Audio("s2", count: 1) };
        var segment3 = segment1 with { Label = "reply-3", Audio = Audio("s3", count: 1) };
        var prompt = new PlaybackJob(
            Label: "approval",
            Kind: PlaybackKind.Approval,
            Priority: AnnouncePriority.High,
            Audio: Audio("approval", count: 1));

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            run.Token);

        var one = queue.Enqueue(segment1);
        var two = queue.Enqueue(segment2);
        var three = queue.Enqueue(segment3);
        await firstChunkWritten.Task;

        var approval = queue.Enqueue(prompt);
        await PlaybackLoop.UntilAsync(() => played.Count >= 4, "every segment to be heard");
        await run.StopAsync(pumpTask);

        played.ShouldBe(["s1", "approval", "s2", "s3"]);
        (await one.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        (await approval.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await two.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await three.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task Enqueue_HighCutInAfterAnAlarm_DoesNotSpareTheSegmentsTheAlarmMarked()
    {
        // The mark is cleared by having drained past it, and "past" is about the jobs still queued,
        // not about the sequence of whatever was dequeued last: an approval prompt cutting in after
        // the alarm carries a higher sequence than the mark, and clearing on that alone would let
        // the reply sentences the alarm marked play in full before it rings.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> gated(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new AudioChunk
            { Data = Encoding.UTF8.GetBytes("s1"), Format = AudioFormat.WyomingStandard };
            firstChunkWritten.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        var segment1 = new PlaybackJob(
            Label: "reply-1",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: gated());
        var segment2 = segment1 with { Label = "reply-2", Audio = Audio("s2", count: 1) };
        var alarm = segment1 with
        {
            Label = "alarm",
            Kind = PlaybackKind.Alarm,
            Priority = AnnouncePriority.High,
            Audio = Audio("alarm", count: 1)
        };
        var prompt = new PlaybackJob(
            Label: "approval",
            Kind: PlaybackKind.Approval,
            Priority: AnnouncePriority.High,
            Audio: Audio("approval", count: 1));

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            run.Token);

        var one = queue.Enqueue(segment1);
        var two = queue.Enqueue(segment2);
        await firstChunkWritten.Task;

        queue.Enqueue(alarm);
        var approval = queue.Enqueue(prompt);
        await Task.WhenAll(one.Completed, two.Completed, approval.Completed)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pumpTask);

        // Exactly as if the cut-in had not landed: neither marked segment is heard.
        played.ShouldNotContain("s2");
        (await one.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        (await two.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        (await approval.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task Enqueue_SecondHighStackedBehindAHigh_StillPlays()
    {
        // The preempt mark must not swallow a second alarm that stacks in the gap — the High
        // exemption in the loop is what keeps insistent announcements ringing.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "alarm-1",
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Audio("alarm-1", count: 1));
        var second = first with { Label = "alarm-2", Audio = Audio("alarm-2", count: 1) };

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            run.Token);

        queue.Enqueue(first);
        queue.Enqueue(second);
        await PlaybackLoop.UntilAsync(() => played.Count >= 2, "both alarms to be heard");
        await run.StopAsync(pumpTask);

        played.ShouldBe(["alarm-1", "alarm-2"]);
    }

    [Fact]
    public async Task PreemptCurrent_AnAcknowledgedRoundStillQueued_NeverRings()
    {
        // The user acknowledges by waking a satellite, which can land between the ring loop
        // enqueueing a round and the loop dequeueing it — or inside the dequeue->assign gap, where
        // there is no current job to cancel at all. Cancelling _currentCts alone let that round ring
        // in full after the alarm had been dismissed.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var round = new PlaybackJob(
            Label: "alarm",
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Audio("alarm", count: 1));

        var ticket = queue.Enqueue(round);
        queue.PreemptCurrent();

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            (chunk, _) =>
            {
                played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        played.ShouldBeEmpty();
        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
    }

    [Fact]
    public async Task PreemptCurrent_AnswerSegmentsQueuedWhileItRang_AreNotTheDismissalsToDrop()
    {
        // A dismissal says the ALERT is over, nothing more. The agent can be answering while a timer
        // rings, and those sentences are queued behind the round: dropping them would swallow an
        // answer the user is waiting for, which is the mirror of the bug above.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var round = new PlaybackJob(
            Label: "alarm",
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Audio("alarm", count: 1));
        var sentence = new PlaybackJob(
            Label: "reply-1",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("s1", count: 1));

        queue.Enqueue(round);
        var reply = queue.Enqueue(sentence);
        queue.PreemptCurrent();

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            (chunk, _) =>
            {
                played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        played.ShouldBe(["s1"]);
        (await reply.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task Enqueue_Normal_RunsAfterCurrent()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "first",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("first", count: 2));
        var second = first with { Label = "second", Audio = Audio("second", count: 1) };

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            async (chunk, ct) =>
            {
                played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            run.Token);

        queue.Enqueue(first);
        queue.Enqueue(second);
        await PlaybackLoop.UntilAsync(() => played.Count >= 3, "both rounds to be heard");
        await run.StopAsync(pumpTask);

        played.ShouldBe(["first", "first", "second"]);
    }

    [Fact]
    public async Task Enqueue_EverythingThatIsNotAReply_SharesTheAnnounceAllowance()
    {
        // The preamble cue plays ahead of an answer rather than being part of it, so it shares the
        // announce depth exactly as it did when the reply tool chose the limit itself.
        var queue = new PlaybackQueue(replyMaxDepth: 8, announceMaxDepth: 2, prefetchBufferChunks: null);

        queue.Enqueue(Job("announce", PlaybackKind.Announce)).Refused.ShouldBeNull();
        queue.Enqueue(Job("preamble", PlaybackKind.Preamble)).Refused.ShouldBeNull();
        queue.Enqueue(Job("approval", PlaybackKind.Approval)).Refused.ShouldNotBeNull();
    }

    [Fact]
    public async Task Enqueue_MixedKinds_EachLimitIsMeasuredAgainstTheWholeQueueDepth()
    {
        // The two limits are thresholds on the one queue depth, not two counters. What the reply
        // limit buys is that an answer is not refused for what an announcement queued; an
        // announcement arriving behind pending sentences is still measured against the announce
        // limit, which is exactly what that limit says — waiting behind that much speech is too
        // long. Both single-kind tests above pass either way, so this is the one that pins it.
        var queue = new PlaybackQueue(replyMaxDepth: 4, announceMaxDepth: 2, prefetchBufferChunks: null);

        queue.Enqueue(Job("s1", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s2", PlaybackKind.Reply)).Refused.ShouldBeNull();

        queue.Enqueue(Job("announce", PlaybackKind.Announce)).Refused.ShouldBe(RefusalReason.QueueFull);

        queue.Enqueue(Job("s3", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s4", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s5", PlaybackKind.Reply)).Refused.ShouldBe(RefusalReason.QueueFull);
    }

    [Fact]
    public async Task Enqueue_ConcurrentProducers_NeverOvershootTheKindsAllowance()
    {
        // Deciding to accept and inserting are one act under the gate. Decided outside it, two
        // producers racing for the last slot both read a depth below the limit and both insert, so a
        // queue that holds one announcement holds two — and the user waits out speech the limit
        // exists to refuse. Nothing drains here (no loop runs), so what was accepted is the depth.
        foreach (var attempt in Enumerable.Range(0, 200))
        {
            var queue = new PlaybackQueue(replyMaxDepth: 1, announceMaxDepth: 1, prefetchBufferChunks: null);
            using var start = new Barrier(8);
            var accepted = 0;

            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                start.SignalAndWait();
                if (queue.Enqueue(Job($"a{attempt}-{i}", PlaybackKind.Announce)).Refused is null)
                {
                    Interlocked.Increment(ref accepted);
                }
            })));

            accepted.ShouldBe(1);
        }
    }

    [Fact]
    public async Task CanAccept_AnswersPerKind_SoTheReplyPathCanAskBeforeItSpendsItsText()
    {
        // TryTakeSpeakable removes a sentence run from the accumulator, so the reply path asks
        // before it takes the text rather than discovering a refusal after both text and synthesis
        // are spent.
        var queue = new PlaybackQueue(replyMaxDepth: 2, announceMaxDepth: 1, prefetchBufferChunks: null);
        queue.Enqueue(Job("s1", PlaybackKind.Reply));

        queue.CanAccept(PlaybackKind.Reply).ShouldBeTrue();
        queue.CanAccept(PlaybackKind.Announce).ShouldBeFalse();
    }

    [Fact]
    public async Task Enqueue_AlarmWhileIdle_PreemptsQueuedAheadButPlaysItself()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);

        // Enqueue a normal job then an alarm BEFORE the loop runs. When the alarm is enqueued no
        // job is marked current, exercising the dequeue->assign gap / idle preempt-sequence path: the
        // already-queued normal must be preempted, while the alarm must still play (a high job must
        // never preempt itself).
        var normal = new PlaybackJob(
            Label: "normal",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("normal", count: 2));
        var high = new PlaybackJob(
            Label: "high",
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Audio("high", count: 1));

        var queuedAhead = queue.Enqueue(normal);
        var cuttingIn = queue.Enqueue(high);

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            run.Token);
        await Task.WhenAll(queuedAhead.Completed, cuttingIn.Completed)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pumpTask);

        (await queuedAhead.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        (await cuttingIn.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task Run_JobAudioThrows_SurvivesAndReportsThenPlaysNext()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var errors = new List<string>();

        var failing = new PlaybackJob(
            Label: "failing",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: ThrowingAudio());
        var next = failing with { Label = "next", Audio = Audio("next", count: 1) };

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            new PlaybackSink(
                async (chunk, ct) =>
                {
                    played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                    await Task.Yield();
                },
                OnError: (job, ex) =>
                {
                    errors.Add(job.Label);
                    return Task.CompletedTask;
                }),
            run.Token);

        queue.Enqueue(failing);
        queue.Enqueue(next);
        await PlaybackLoop.UntilAsync(() => played.Count >= 1 && errors.Count >= 1,
            "the failure to be reported and the next job to be heard");
        await run.StopAsync(pumpTask);

        errors.ShouldBe(["failing"]);
        played.ShouldBe(["next"]);
    }

    // "a job that drains settles drained" and "a preempted job never settles drained" are the
    // exactly-one-outcome guarantee, proved once in PlaybackQueueOutcomeTests.

    // The two turn-handshake tests that used to sit here moved to VoiceTurnTests: they test the
    // handshake, not playback, and they now drive the real path (begin a segment, complete it, end
    // the stream) rather than a signal method that no longer exists.

    [Fact]
    public async Task Run_WaitsForAudioPlaybackDuration_BeforeSettlingDrained()
    {
        // Drained means the satellite finished PLAYING, not that the hub finished writing: the Pi
        // buffers the audio and plays it at real time, and the earcon's mic must not open early.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // 16000 bytes at 16 kHz/16-bit/mono = exactly 500 ms of audio.
        static async IAsyncEnumerable<AudioChunk> halfSecond()
        {
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: halfSecond());

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token, time);

        var ticket = queue.Enqueue(job);
        await Task.Delay(80); // let the loop write the audio and reach the playback wait
        ticket.Completed.IsCompleted.ShouldBeFalse(); // the 500 ms of audio has not played out yet

        time.Advance(TimeSpan.FromMilliseconds(500)); // playback completes
        (await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(2)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_FirstChunk_PublishesSynthesisAndTurnTiming()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        Anchors(queue).MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(2)); // capture + STT + agent thinking before synthesis begins

        // Synthesis takes 300 ms to produce its first chunk; 16000 bytes = 500 ms of audio.
        async IAsyncEnumerable<AudioChunk> audio()
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token, time);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // TTS latency = synthesis request -> first audio chunk (300 ms), independent of the
        // pre-synthesis turn time. Wake/turn -> first audio = 2000 + 300 = 2300 ms.
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));
        timing.SinceTurnStart.ShouldBe(TimeSpan.FromMilliseconds(2300));

        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_FirstChunk_NoTurnStart_TurnTimingNull()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // No MarkTurnStart: a job with no preceding turn (e.g. not wired) must NOT report a turn time,
        // so WakeToFirstAudioMs is simply not published rather than emitting a garbage value.
        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceTurnStart.ShouldBeNull();
        timing.SinceSynthesisStart.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_MultiChunk_InvokesOnFirstAudioOnce()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var invocations = 0;

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("x", count: 3),
            OnFirstAudio: _ => { Interlocked.Increment(ref invocations); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token);
        queue.Enqueue(job);
        await PlaybackLoop.UntilAsync(() => Volatile.Read(ref invocations) >= 1, "the first chunk");
        await run.StopAsync(pump);

        invocations.ShouldBe(1); // fires only on the first chunk, not per chunk
    }

    [Fact]
    public async Task Enqueue_TwoHighWhileIdle_BothPlay()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);

        // Two High jobs enqueued while idle (no job marked current). The second must NOT preempt the
        // first via the pending high-water mark; both play in FIFO order (regression guard for the
        // preempt-sequence fix).
        PlaybackJob high(string label)
        {
            return new(
            Label: label,
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.High,
            Audio: Audio(label, count: 1));
        }

        var first = queue.Enqueue(high("h1"));
        var second = queue.Enqueue(high("h2"));

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            run.Token);
        await Task.WhenAll(first.Completed, second.Completed).WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        (await first.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await second.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    // Enqueueing onto a completed queue is a refusal rather than a ChannelClosedException — see
    // PlaybackQueueOutcomeTests, which owns every refusal reason.

    [Fact]
    public async Task Run_FirstChunk_PublishesSpeechEndAndQueueWaitTiming()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        Anchors(queue).MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(3));            // the user talking
        Anchors(queue).MarkSpeechEnd(time.GetTimestamp(), endpointTailMs: 0, time);
        time.Advance(TimeSpan.FromSeconds(2));            // verify + STT + agent
        var enqueuedAt = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(400));     // the reply waits behind the preamble

        // Synthesis takes 300 ms to produce its first chunk; 16000 bytes = 500 ms of audio.
        async IAsyncEnumerable<AudioChunk> audio()
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; },
            EnqueuedAt: enqueuedAt);

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token, time);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Speech end -> first audio excludes the 3 s the user spent talking: 2000 + 400 + 300.
        timing.SinceSpeechEnd.ShouldBe(TimeSpan.FromMilliseconds(2700));
        // Queue wait ends at synthesis start, so it is the 400 ms behind the preamble — NOT the
        // 300 ms of synthesis, which SinceSynthesisStart already owns.
        timing.QueueWait.ShouldBe(TimeSpan.FromMilliseconds(400));
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));

        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_FirstChunk_WritesTheAudioBeforeInvokingOnFirstAudio()
    {
        // Four awaited metric publishes now hang off OnFirstAudio, so invoking it before the first
        // writer call delays the first audio byte reaching the satellite by however long Redis takes:
        // the observer changing what it observes. Every timestamp the callback reports is captured
        // before the write, so ordering the write first costs no accuracy at all.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var order = new List<string>();

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 2),
            OnFirstAudio: _ => { order.Add("metrics"); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(
            (_, _) => { order.Add("write"); return Task.CompletedTask; }, run.Token);
        queue.Enqueue(job);
        await PlaybackLoop.UntilAsync(() => order.Count >= 3, "both chunks to be written");
        await run.StopAsync(pump);

        order.ShouldBe(["write", "metrics", "write"]);
    }

    [Fact]
    public async Task Run_FirstChunk_SpeechEndAnchorRewindsTheEndpointTail()
    {
        // The caller can only see the capture CLOSE, which SilenceGate reaches a whole
        // trailingSilence run after the user stopped talking (2000 ms in production). The tail is
        // machine time the user waits through, so it belongs inside this span: without the rewind
        // SpeechEndToFirstAudioMs omits it and EndpointTailMs sits beside the span instead of nested
        // inside it, which is ~40% of the wait at production settings.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        time.Advance(TimeSpan.FromSeconds(3));            // the user talking
        time.Advance(TimeSpan.FromMilliseconds(2000));    // the endpointing tail the gate waits out
        Anchors(queue).MarkSpeechEnd(time.GetTimestamp(), endpointTailMs: 2000, time);
        time.Advance(TimeSpan.FromMilliseconds(1000));    // verify + STT + agent

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token, time);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceSpeechEnd.ShouldBe(TimeSpan.FromMilliseconds(3000)); // 2000 tail + 1000 machine

        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_FirstChunk_NoSpeechEndOrEnqueueStamp_TimingsAreNull()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A job with no preceding capture (chime, announce) must report nulls rather than a garbage
        // value, so the hub simply publishes nothing for those spans.
        var job = new PlaybackJob(
            Label: "chime:kitchen-01",
            Kind: PlaybackKind.Chime,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), run.Token);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceSpeechEnd.ShouldBeNull();
        timing.QueueWait.ShouldBeNull();

        await run.StopAsync(pump);
    }

    [Fact]
    public async Task Run_FirstAudioCallbackThrows_TheJobStillDrains()
    {
        // The one callback a job still carries is invoked between two audio writes, so it keeps its
        // guard: a producer's metrics publish failing must not cut the answer short.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var written = 0;

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 3),
            OnFirstAudio: _ => throw new InvalidOperationException("metrics down"));

        var ticket = queue.Enqueue(job);
        using var run = new CancellationTokenSource();
        var pump = queue.RunAsync((_, _) => { written++; return Task.CompletedTask; }, run.Token);
        await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        written.ShouldBe(3);
        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    // The alert bit is what the hub puts on the wire for the satellite's sink selection, so it has
    // to survive the queue and arrive with the stream it belongs to — not with a neighbouring job.
    // It follows from the alarm kind, so a producer cannot set it on something that is not an alert.
    [Fact]
    public async Task Run_ReportsTheAlarmKindAsTheAlertRouteOnAudioStart()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var flags = new List<bool>();

        using var run = new CancellationTokenSource();
        var pumpTask = queue.RunAsync(
            new PlaybackSink(
                (_, _) => Task.CompletedTask,
                OnAudioStart: (_, alert, _) =>
                {
                    lock (flags)
                    { flags.Add(alert); }
                    return Task.CompletedTask;
                }),
            run.Token);

        var reply = new PlaybackJob(
            Label: "reply",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("reply", count: 1));
        var alarm = reply with
        {
            Label = "alarm",
            Kind = PlaybackKind.Alarm,
            Audio = Audio("alarm", count: 1)
        };

        queue.Enqueue(reply);
        queue.Enqueue(alarm);
        await PlaybackLoop.UntilAsync(() => flags.Count >= 2, "both streams to start");
        await run.StopAsync(pumpTask);

        flags.ShouldBe(new[] { false, true });
    }

}