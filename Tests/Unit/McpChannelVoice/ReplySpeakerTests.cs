using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ReplySpeakerTests
{
    private readonly SatelliteSession _session;

    // Standing in for CaptureSession: the turn anchors belong to ITurnAnchor, not to the
    // playback queue every producer holds.
    private ITurnAnchor Anchors => _session.Playback;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly IMetricsPublisher _metrics;
    private readonly string _conversationId;
    private readonly ReplySpeaker _speaker;
    private readonly List<VoiceEvent> _published = [];
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    // The conversation mapping's own clock: its idle timer is what tears a voice conversation down,
    // and one test advances it to reach that teardown.
    private readonly FakeTimeProvider _conversationClock = new(DateTimeOffset.UtcNow);
    private static readonly TimeSpan _conversationLifetime = TimeSpan.FromMinutes(5);

    public ReplySpeakerTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, _accumulator, _conversationClock,
            _conversationLifetime, NullLogger<VoiceConversationManager>.Instance);

        _conversationId = manager.GetOrCreateAsync(_session, "agent-1", "hello", default).GetAwaiter().GetResult();

        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
            });
        _metrics = metrics.Object;

        _speaker = Speaker(new VoiceSettings());
    }

    private ReplySpeaker Speaker(VoiceSettings settings) => Speaker(settings, _metrics);

    private ReplySpeaker Speaker(VoiceSettings settings, IMetricsPublisher metrics) => new(
        _accumulator, _tts.Object, settings, metrics, _clock, NullLogger<ReplySpeaker>.Instance);

    // The session is looked up per call rather than captured, because two tests swap in a session
    // built with a different playback queue.
    private void Say(ReplySpeaker speaker, string content, ReplyContentType contentType, bool isComplete)
    {
        var session = _sessions.Get("kitchen-01")!;
        speaker.SpeakUtteranceReply(session, new SendReplyParams
        {
            ConversationId = _conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            // The key the turn was dispatched under, so a test that stamps one gets the matching
            // path and a test that does not gets the missing-key fallback — which is the old
            // behaviour, and is why the tests written before the key still read the same.
            TurnKey = session.Turn.TurnKey,
            AgentInitiated = false
        });
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAudio(string label)
    {
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(label),
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AudioChunk> ThrowingAudio()
    {
        await Task.Yield();
        throw new InvalidOperationException("Wyoming TTS error: piper crashed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task SpeakUtteranceReply_ReplySynthesisThrows_ResolvesTurnSilentInsteadOfWedgingTheMic()
    {
        // Regression guard for the FIX #4 follow-up: a reply synthesis failure (e.g. a Wyoming TTS
        // 'error' event, which now throws) must resolve the per-turn handshake via the reply job's
        // failed outcome -> the segment fails and the turn settles silent, so FollowUpConversation ends +
        // re-arms wake. Without it the
        // mic stays wedged until the ~120s ReplyTimeoutMs. The chime and approval jobs already do this.
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => ThrowingAudio());

        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        Say(_speaker, "hola", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2)); // resolves promptly, not after a timeout
        await run.StopAsync(pump);

        spoke.ShouldBeFalse(); // no audio actually played -> end conversation + re-arm, not "spoken"
    }

    [Fact]
    public async Task SpeakUtteranceReply_Text_Complete_SynthesisesAccumulatedText()
    {
        Say(_speaker, "hola ", ReplyContentType.Text, false);
        Say(_speaker, "mundo", ReplyContentType.Text, true);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamComplete_SynthesisesAccumulatedText()
    {
        // Real agent streaming (see ReplyDispatcher.MapResponseUpdate): Text chunks are
        // emitted with isComplete=false; completion arrives only as a StreamComplete
        // event with empty content and no messageId. The reply must still be spoken.
        Say(_speaker, "hola ", ReplyContentType.Text, false);
        Say(_speaker, "mundo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_Error_SpeaksErrorPrefix()
    {
        Say(_speaker, "boom", ReplyContentType.Error, true);
        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("boom")), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_Error_FlaggedComplete_EndsTheStreamSoTheTurnResolves()
    {
        // A transport that flags the error itself is the end of the answer, so the stream has to end
        // here as it does on StreamComplete. Without it the turn never learns the agent stopped
        // sending: FollowUpConversation waits out the whole ~120 s reply timeout with the mic shut.
        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        Say(_speaker, "boom", ReplyContentType.Error, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue(); // the error reached the satellite, so the turn was spoken
    }

    [Fact]
    public async Task SpeakUtteranceReply_TextFlaggedComplete_EndsTheStreamSoTheTurnResolves()
    {
        // The same for a text chunk that carries the completion flag — the defensive branch the
        // speaker keeps for a transport that ever sends one. It spoke the answer and then left the
        // turn outstanding.
        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        Say(_speaker, "hola mundo", ReplyContentType.Text, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue();
    }

    [Fact]
    public async Task SpeakUtteranceReply_PartialTextThenError_SpeaksPartialAndErrorOnceInOrder()
    {
        // Faulted agent run as ChatMonitor emits it: buffered Text (never isComplete) -> Error
        // (isComplete=false) -> trailing StreamComplete. The partial answer and the error must be
        // spoken together, in order, as a SINGLE utterance — never the error first with the leftover
        // partial spoken after it (the divergence from the Telegram/ServiceBus flush-on-error contract).
        Say(_speaker, "El tiempo es", ReplyContentType.Text, false);
        Say(_speaker, "boom", ReplyContentType.Error, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("El tiempo es") && s.Contains("boom")
                && s.IndexOf("El tiempo es", StringComparison.Ordinal) < s.IndexOf("boom", StringComparison.Ordinal)),
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_Reasoning_DoesNothing()
    {
        Say(_speaker, "thinking", ReplyContentType.Reasoning, false);
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SpeakUtteranceReply_ToolCall_SpeaksBufferedPreambleWithoutResolvingTheTurn()
    {
        // nabu is told to emit a one-word acknowledgement ("Buscando.") before slow multi-tool work
        // so the user hears that something started. Text chunks are buffered and StreamComplete used
        // to be the only flush, so the ack was spoken glued to the front of the final answer —
        // arriving after the wait it existed to cover, and costing words for nothing. The first tool
        // call of a turn must flush and speak it. It must NOT resolve the turn handshake: that ends
        // FollowUpConversation and re-arms the mic mid-turn, before the answer is even spoken.
        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        Say(_speaker, "Buscando.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.ToolCall, false);

        _tts.Verify(t => t.SynthesizeAsync("Buscando.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { heard.TrySetResult(); await Task.Yield(); }, run.Token);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        turn.IsCompleted.ShouldBeFalse(); // the preamble is not the end of the turn
    }

    [Fact]
    public async Task SpeakUtteranceReply_PreambleThenAnswer_SpeaksThemAsSeparateUtterances()
    {
        // ReplyTextAccumulator concatenates with no separator, so before the tool-call flush the
        // satellite spoke a single "Buscando.Veintiún grados." utterance at the end of the turn.
        Say(_speaker, "Buscando.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.ToolCall, false);
        Say(_speaker, "Veintiún grados.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _tts.Verify(t => t.SynthesizeAsync("Buscando.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _tts.Verify(t => t.SynthesizeAsync("Veintiún grados.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_ToolCall_NothingBuffered_SpeaksNothing()
    {
        // The overwhelmingly common case: the model went straight to a tool without a preamble.
        Say(_speaker, "", ReplyContentType.ToolCall, false);

        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SpeakUtteranceReply_SecondToolCall_KeepsMidRunNarrationBufferedForTheAnswer()
    {
        // Only the FIRST tool call of a turn flushes. Anything the model says between later tool
        // rounds stays buffered and is spoken with the answer, so mid-run chatter can never become a
        // second utterance racing the reply into the playback queue.
        _session.Turn.Reset();

        Say(_speaker, "Buscando.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.ToolCall, false);
        Say(_speaker, "Ahora miro el termostato.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.ToolCall, false);

        _tts.Verify(t => t.SynthesizeAsync("Ahora miro el termostato.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamComplete_PublishesTtsLatencyFromPlaybackNotEnqueue()
    {
        // One clock throughout: a second FakeTimeProvider here used to stamp EnqueuedAt while this
        // one drained playback, so the queue-wait span was computed across two unrelated timelines.
        Anchors.MarkTurnStart(_clock.GetTimestamp());
        _session.Turn.MarkDispatched(_clock.GetTimestamp()); // turn-anchored spans need the dispatch proof
        _clock.Advance(TimeSpan.FromMilliseconds(1000)); // STT + agent thinking before the reply arrives

        Say(_speaker, "hola mundo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        // The reply job is enqueued (a non-blocking channel write) but playback hasn't run yet, so no
        // TTS-latency metric exists. The bug published TtsLatencyMs here, right after the enqueue (~0 ms).
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.TtsLatencyMs);

        // Drain the playback loop: the first synthesized chunk triggers OnFirstAudio, which publishes
        // the latency metrics from where synthesis actually happens.
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token, _clock);
        await Task.Delay(80);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _session.Turn.AwaitSpoken().WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        _published.Count(e => e.Metric == VoiceMetric.TtsLatencyMs).ShouldBe(1);
        var wake = _published.SingleOrDefault(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
        wake.ShouldNotBeNull();
        wake.DurationMs.ShouldBe(1000); // turn-start -> first audio (synthesis was instant in fake time)
    }

    [Fact]
    public async Task SpeakUtteranceReply_AScheduleFiringIntoALiveSession_PublishesNoTurnAnchoredSpans()
    {
        // "Recuérdame en dos minutos" fires into a voice-minted conversation whose mapping is still
        // alive (ConversationLifetime is 5 minutes) and whose satellite session is live, so McpRun
        // routes it down the utterance path — create_conversation no-ops in that state. The turn
        // anchors from the earlier real turn are never invalidated, so reporting against them would
        // publish SpeechEndToFirstAudioMs ≈ 120000: one sample that wrecks Avg/P95/Max on the
        // headline metric. It answers a different turn, so it never reaches those spans at all.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        Anchors.MarkTurnStart(_clock.GetTimestamp());
        Anchors.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        SayFor("sched-turn", "son las diez", ReplyContentType.Text, false, agentInitiated: true);
        SayFor("sched-turn", "", ReplyContentType.StreamComplete, true, agentInitiated: true);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { heard.TrySetResult(); await Task.Yield(); }, run.Token, _clock);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs);
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamCompleteAfterDispatch_PublishesAgentRoundTrip()
    {
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(4)); // the agent thinking

        Say(_speaker, "listo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        var roundTrip = _published.SingleOrDefault(e => e.Metric == VoiceMetric.AgentRoundTripMs);
        roundTrip.ShouldNotBeNull();
        roundTrip!.DurationMs.ShouldBe(4000);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamCompleteWithoutDispatch_PublishesNoAgentRoundTrip()
    {
        // An announce/scheduled delivery never went through a transcript dispatch, so there is no
        // round trip to report — publishing one would invent a span.
        _session.Turn.Reset();

        Say(_speaker, "listo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.AgentRoundTripMs);
    }

    [Fact]
    public async Task SpeakUtteranceReply_SecondReplyAfterDispatchAlreadyConsumed_PublishesNoAgentRoundTrip()
    {
        // A live session's conversation can receive a schedule-fired or agent-initiated reply
        // (CreateConversationTool routes it through this same session) with no fresh transcript
        // dispatch behind it. The stamp from the earlier real turn must not still be sitting there
        // for this second, unrelated reply to pick up and report as an invented round trip.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "listo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _session.Turn.Reset();
        Say(_speaker, "otra vez", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _published.Count(e => e.Metric == VoiceMetric.AgentRoundTripMs).ShouldBe(1);
    }

    [Fact]
    public async Task SpeakUtteranceReply_PreambleBeforeDispatchedAnswer_DoesNotConsumeTheStampForTheAnswer()
    {
        // The preamble ("Buscando") is spoken with isReply:false. If it consumed the dispatch
        // stamp, the real answer that follows would lose its AgentRoundTripMs metric entirely.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2));

        Say(_speaker, "Buscando.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.ToolCall, false);
        Say(_speaker, "Veintiún grados.", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        var roundTrip = _published.SingleOrDefault(e => e.Metric == VoiceMetric.AgentRoundTripMs);
        roundTrip.ShouldNotBeNull();
        roundTrip!.DurationMs.ShouldBe(2000);
    }

    [Fact]
    public async Task SpeakUtteranceReply_TwoDispatchedTurnsInSequence_PublishesAgentRoundTripBothTimes()
    {
        // A follow-up turn re-arms the stamp: each dispatch stands on its own, independent of the
        // previous turn's already-consumed one.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2));
        Say(_speaker, "primero", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(3));
        Say(_speaker, "segundo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        var roundTrips = _published.Where(e => e.Metric == VoiceMetric.AgentRoundTripMs).ToList();
        roundTrips.Count.ShouldBe(2);
        roundTrips[0].DurationMs.ShouldBe(2000);
        roundTrips[1].DurationMs.ShouldBe(3000);
    }

    [Fact]
    public async Task SpeakUtteranceReply_AgentRoundTripPublishCost_LandsInTheQueueWaitRatherThanNoSpan()
    {
        // EnqueuedAt used to be stamped AFTER the AgentRoundTripMs publish, so whatever that publish
        // cost belonged to no span and the decomposition silently lost a slice on every turn.
        // EnqueuedAt is taken first and the round trip measured TO it, making the two spans exactly
        // adjacent. Modelled by a publisher that costs fake time.
        const int publishMs = 50;
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
                _clock.Advance(TimeSpan.FromMilliseconds(publishMs));
            });

        var speaker = Speaker(new VoiceSettings(), metrics.Object);

        // All three anchors coincide, so SpeechEndToFirstAudioMs is the whole dispatch -> first-audio
        // span and the three sub-spans must tile it exactly.
        _session.Turn.Reset();
        // A dispatched turn has a key, and this decomposition is about a turn that really ran: the
        // missing-key fallback would publish an error whose own cost lands inside the spans.
        _session.Turn.StampTurnKey("turn-1");
        Anchors.MarkTurnStart(_clock.GetTimestamp());
        Anchors.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2)); // the agent thinking

        Say(speaker, "listo", ReplyContentType.Text, false);
        Say(speaker, "", ReplyContentType.StreamComplete, true);

        _clock.Advance(TimeSpan.FromMilliseconds(400)); // the reply waits behind another job

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token, _clock);
        await _session.Turn.AwaitSpoken().WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        var roundTrip = _published.Single(e => e.Metric == VoiceMetric.AgentRoundTripMs).DurationMs;
        var queueWait = _published.Single(e => e.Metric == VoiceMetric.ReplyQueueWaitMs).DurationMs;
        var tts = _published.Single(e => e.Metric == VoiceMetric.TtsLatencyMs).DurationMs;
        var whole = _published.Single(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs).DurationMs;

        roundTrip.ShouldBe(2000);
        queueWait.ShouldBe(400 + publishMs); // the publish's own cost is inside the queue wait
        (roundTrip + queueWait + tts).ShouldBe(whole);
    }

    [Fact]
    public async Task SpeakUtteranceReply_TextFormingACompleteSentence_SpeaksItBeforeStreamComplete()
    {
        // The whole point of streaming: the answer's opening is already synthesizing while the
        // agent is still generating the rest, instead of waiting for the turn to end.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "Mañana por la tarde hará sol y unos veintidós grados. Por la noche ", ReplyContentType.Text, false);

        _tts.Verify(t => t.SynthesizeAsync(
            "Mañana por la tarde hará sol y unos veintidós grados.",
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_PartialSentence_SpeaksNothingYet()
    {
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "Mañana por la tarde hará sol y unos", ReplyContentType.Text, false);

        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamedAnswer_SettlesTheTurnOnceAfterEverySegmentDrains()
    {
        // The regression that streaming introduces if the handshake is left per-job: sentence one
        // draining would end FollowUpConversation, chiming and reopening the mic over the rest.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();

        var written = new List<string>();
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync((chunk, _) =>
        {
            lock (written)
            { written.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span)); }
            wrote.TrySetResult();
            return Task.CompletedTask;
        }, run.Token);

        // Segment one, played to completion with the agent still generating. This is the moment the
        // per-job handshake got wrong: the turn must NOT settle here.
        Say(_speaker, "Mañana por la tarde hará sol y unos veintidós grados. ", ReplyContentType.Text, false);
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // let the loop finish the drain wait and settle the segment

        written.Count.ShouldBe(1);        // exactly the opening sentence has reached the satellite
        turn.IsCompleted.ShouldBeFalse();

        Say(_speaker, "Por la noche bajará bastante y habrá algo de viento del norte. ", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        // Awaited rather than read: the segment settles from the job's outcome, and the queue
        // signals an outcome without waiting for whoever reacts to it.
        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await run.StopAsync(pump);
        written.Count.ShouldBeGreaterThan(1); // it really did stream in pieces
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamedAnswer_PublishesTurnAnchoredSpansOnlyOnce()
    {
        // SpeechEndToFirstAudioMs/WakeToFirstAudioMs measure time-to-FIRST-audio, so N segments must
        // not publish N samples. The single-use dispatch stamp is what enforces it.
        _session.Turn.Reset();
        Anchors.MarkTurnStart(_clock.GetTimestamp());
        Anchors.MarkSpeechEnd(_clock.GetTimestamp(), 0, _clock);
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "Mañana por la tarde hará sol y unos veintidós grados. ", ReplyContentType.Text, false);
        Say(_speaker, "Por la noche bajará bastante y habrá algo de viento del norte. ", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);
        await _session.Turn.AwaitSpoken().WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        _published.Count(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs).ShouldBe(1);
        _published.Count(e => e.Metric == VoiceMetric.WakeToFirstAudioMs).ShouldBe(1);
        _published.Count(e => e.Metric == VoiceMetric.AgentRoundTripMs).ShouldBe(1);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamingDisabled_BuffersUntilStreamComplete()
    {
        // The kill switch has to genuinely restore the old behaviour.
        var speaker = Speaker(new VoiceSettings
        {
            Tts = new TtsSettings { Streaming = new StreamingTtsConfig { Enabled = false } }
        });
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(speaker, "Mañana por la tarde hará sol y unos veintidós grados. ", ReplyContentType.Text, false);

        _tts.VerifyNoOtherCalls();

        Say(speaker, "", ReplyContentType.StreamComplete, true);

        _tts.Verify(t => t.SynthesizeAsync(
            It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }


    [Fact]
    public async Task SpeakUtteranceReply_QueuedSegments_StartSynthesisWithoutWaitingForThePlaybackLoop()
    {
        // Prefetch: the TTS request must go out when the segment is queued. The playback loop is
        // sequential and will not touch a job's audio until the previous one has finished its
        // real-time drain, so leaving it lazy puts a full round trip into every sentence seam.
        // No playback loop runs in this test at all — every recorded synthesis is therefore one the
        // loop did not ask for.
        var synthesized = new List<string>();
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => Recording(text, synthesized));

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "Mañana por la tarde hará sol y unos veintidós grados. ", ReplyContentType.Text, false);
        Say(_speaker, "Por la noche bajará bastante la temperatura y habrá algo de viento del norte, así que conviene cerrar las ventanas antes de irse a dormir esta noche. ", ReplyContentType.Text, false);

        await WaitForCountAsync(synthesized, 2, TimeSpan.FromSeconds(5));
        lock (synthesized)
        { synthesized.Count.ShouldBe(2); }
    }

    [Fact]
    public async Task SpeakUtteranceReply_PrefetchDisabled_LeavesSynthesisToThePlaybackLoop()
    {
        var synthesized = new List<string>();
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => Recording(text, synthesized));

        // Prefetching is switched off by the queue having no prefetch size, which is what the
        // connection builds from Tts.Streaming.Prefetch.
        _sessions.Register(new SatelliteSession(
            "kitchen-01", _session.Config, new PlaybackQueue(prefetchBufferChunks: null)));
        var session = _sessions.Get("kitchen-01")!;
        session.Turn.Reset();
        session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "Mañana por la tarde hará sol y unos veintidós grados. ", ReplyContentType.Text, false);

        await Task.Delay(200);
        lock (synthesized)
        { synthesized.ShouldBeEmpty(); }
    }

    [Fact]
    public async Task SpeakUtteranceReply_ReplySegmentPreemptedMidPlayback_SettlesTheTurnThroughTheRealPlaybackLoop()
    {
        // Drives a real reply job through the playback loop and preempts it, which is the only
        // way to reach the preempted outcome -> segment.Fail() release. Without it the turn waits
        // out the ~120s ReplyTimeoutMs with the mic wedged.
        // Signalled from the WRITER, not the audio source: the prefetch pump starts synthesising at
        // enqueue, so a source-side signal fires before the playback loop owns the job and the
        // preempt would land on nothing.
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => Gated());

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { playing.TrySetResult(); await Task.Yield(); },
            run.Token, _clock);

        Say(_speaker, "Hola mundo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the segment is mid-drain
        _session.Playback.PreemptCurrent();

        // Settles promptly instead of wedging. Silent, not Spoken: no segment ever drained, so the
        // conversation ends and wake re-arms rather than opening a follow-up window over the alarm
        // that cut in.
        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();
        await run.StopAsync(pump);
    }

    [Fact]
    public async Task SpeakUtteranceReply_PlaybackQueueRefusesTheSegment_SettlesTheTurnAndNeverStartsTheSynthesis()
    {
        // A satellite that disconnects mid-answer completes the playback queue, so every further
        // enqueue is refused. The segment must still be released — and the synthesis must never
        // start at all, because the queue only takes ownership of audio for a job it accepted.
        var pulled = 0;
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>(
                (_, _, _) => Counting(() => Interlocked.Increment(ref pulled)));

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();
        _session.Playback.CompleteAndDiscardQueued();    // the link dropped: every enqueue now refuses

        Say(_speaker, "Hola mundo", ReplyContentType.Text, false);
        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();  // nothing ever played
        Volatile.Read(ref pulled).ShouldBe(0);
    }

    private static async IAsyncEnumerable<AudioChunk> Counting(Action onPull)
    {
        onPull();
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    [Fact]
    public async Task SpeakUtteranceReply_QueuedSegmentPreemptedBeforeItStarts_ReleasesItsPrefetchedSynthesis()
    {
        // An alarm marks every queued job for preemption, and the loop cancels a marked job BEFORE
        // touching its audio — so the release that normally runs when the consumer's enumerator is
        // torn down never happens. Without an explicit dispose on the preempt path the prefetch pump
        // stays parked on a full buffer holding an open TTS response: one leaked synthesis per
        // queued sentence, on every alarm that cuts into a streamed reply.
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synthesisCalls = 0;
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) =>
                Interlocked.Increment(ref synthesisCalls) == 1 ? Gated() : DisposeTracking(disposed));

        var speaker = Speaker(new VoiceSettings
        {
            Tts = new TtsSettings
            { Streaming = new StreamingTtsConfig { FirstSegmentMinChars = 10, MinChars = 10 } }
        });

        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { playing.TrySetResult(); await Task.Yield(); },
            run.Token, _clock);

        Say(speaker, "Primera frase completa. ", ReplyContentType.Text, false);
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));   // segment one is mid-drain
        Say(speaker, "Segunda frase completa. ", ReplyContentType.Text, false);       // segment two sits queued

        _session.Playback.Enqueue(new PlaybackJob(
            Label: "alarm", Kind: PlaybackKind.Alarm, Priority: AnnouncePriority.High,
            Audio: NoAudio()));

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.StopAsync(pump);
    }

    [Fact]
    public async Task SpeakUtteranceReply_StreamingWithNoRoomInThePlaybackQueue_LeavesTheTextBufferedInsteadOfDroppingIt()
    {
        // TryTakeSpeakable removes a sentence run from the accumulator BEFORE the enqueue is
        // accepted, so a queue with no room silently swallowed whole sentences: the user heard an
        // answer with a hole in it and the turn still settled Spoken. Text that cannot be queued
        // must stay buffered for the next flush.
        var speaker = Speaker(new VoiceSettings
        {
            Tts = new TtsSettings
            {
                Streaming = new StreamingTtsConfig { FirstSegmentMinChars = 10, MinChars = 10 }
            }
        });

        // The depth limit is the queue's now, so a satellite with room for one segment is a queue
        // built with room for one segment.
        _sessions.Register(new SatelliteSession(
            "kitchen-01", _session.Config, new PlaybackQueue(replyMaxDepth: 1, announceMaxDepth: 1)));
        var session = _sessions.Get("kitchen-01")!;
        session.Turn.Reset();
        session.Turn.MarkDispatched(_clock.GetTimestamp());
        // No playback loop is running, so nothing drains and the queue stays at its cap.

        Say(speaker, "Primera frase completa. ", ReplyContentType.Text, false);
        Say(speaker, "Segunda frase completa. ", ReplyContentType.Text, false);
        Say(speaker, "Tercera frase completa. ", ReplyContentType.Text, false);

        // Whatever could not be queued is still there for StreamComplete to flush.
        var leftover = _accumulator.Flush(_conversationId);
        leftover.ShouldContain("Segunda frase completa.");
        leftover.ShouldContain("Tercera frase completa.");
    }

    [Fact]
    public async Task SpeakUtteranceReply_AStreamThatNeverEndedAndTheSatelliteRedialled_StillSettlesTheNewTurn()
    {
        // The satellite's link to this server drops mid-answer and it redials, so the hub builds a
        // fresh session — a fresh turn — for the same conversation, because the manager's
        // satellite -> conversation mapping outlives the connection. The agent knows nothing of any
        // of it and keeps writing: the rest of the answer arrives naming the turn that was
        // discarded with the old session, against a turn that has never been dispatched. That is
        // the answer the user is waiting for, so it is adopted and spoken. Discarded instead, the
        // user hears nothing at all and has to ask again.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        SayFor("turn-1", "", ReplyContentType.ToolCall, false); // the answer is still being written

        _sessions.Register(new SatelliteSession("kitchen-01", _session.Config));
        var session = _sessions.Get("kitchen-01")!;
        var turn = session.Turn.AwaitSpoken();

        SayFor("turn-1", "hola mundo", ReplyContentType.Text, false);
        SayFor("turn-1", "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue();
        _tts.Verify(t => t.SynthesizeAsync(
            "hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SpeakUtteranceReply_TheEnqueueIsRefusedAfterTheTextWasTaken_HandsItBackToTheBuffer()
    {
        // Asking the queue first only answers for the depth limit, and it answers in advance: a
        // satellite that disconnects mid-answer completes the queue, which is not full and still
        // refuses — and the sentence has already left the accumulator by then. The refusal is what
        // must hand the text back, not the question asked before it.
        var speaker = Speaker(Streaming(firstSegmentMinChars: 10, minChars: 10));
        _session.Turn.Reset();
        _session.Playback.CompleteAndDiscardQueued();

        Say(speaker, "Primera frase completa. ", ReplyContentType.Text, false);

        _accumulator.Flush(_conversationId).ShouldContain("Primera frase completa.");
    }

    [Fact]
    public async Task SpeakUtteranceReply_TurnEndsWithNoAudio_DoesNotLeaveTheDispatchStampForALaterReply()
    {
        // A tool-only turn never reaches SpeakAsync, so TryConsumeDispatchedAt never runs and the
        // stamp outlives the turn. A schedule firing into the same live session then consumes it and
        // publishes AgentRoundTripMs/WakeToFirstAudioMs anchored to the old turn — minutes of
        // staleness on the headline metrics, which is what the stamp gate exists to prevent.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        Say(_speaker, "", ReplyContentType.StreamComplete, true);

        _session.Turn.TryConsumeDispatchedAt().ShouldBeNull();
    }

    [Fact]
    public void SpeakUtteranceReply_FirstCompleteSentenceIsShorterThanTheFirstSegmentMinimum_SpeaksNothingYet()
    {
        Say(Speaker(Streaming(firstSegmentMinChars: 40, minChars: 140)), "Claro. ",
            ReplyContentType.Text, false);

        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public void SpeakUtteranceReply_TheRunReachesTheFirstSegmentMinimum_SpeaksItWhole()
    {
        var speaker = Speaker(Streaming(firstSegmentMinChars: 40, minChars: 140));

        Say(speaker, "Claro. ", ReplyContentType.Text, false);
        Say(speaker, "En la cocina hacen veintiun grados ahora mismo. ", ReplyContentType.Text, false);

        _tts.Verify(t => t.SynthesizeAsync(
            "Claro. En la cocina hacen veintiun grados ahora mismo.",
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SpeakUtteranceReply_LaterSegmentsUseTheHigherMinimum_NotTheFirstSegmentOne()
    {
        // The first run clears a deliberately low bar because it is the wait the user feels; later
        // runs need more text, because the audio already playing is covering them.
        var speaker = Speaker(Streaming(firstSegmentMinChars: 10, minChars: 140));

        Say(speaker, "Ya lo miro. ", ReplyContentType.Text, false);
        Say(speaker, "Hace frio. ", ReplyContentType.Text, false);

        _tts.Verify(t => t.SynthesizeAsync(
            "Ya lo miro.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _accumulator.Flush(_conversationId).ShouldBe("Hace frio. ");
    }

    private static VoiceSettings Streaming(int firstSegmentMinChars, int minChars) => new()
    {
        Tts = new TtsSettings
        {
            Streaming = new StreamingTtsConfig
            {
                FirstSegmentMinChars = firstSegmentMinChars,
                MinChars = minChars
            }
        }
    };

    // One chunk, then parks: the job stays mid-drain until something cancels it, which is what makes
    // the preempt path reachable at all.
    private static async IAsyncEnumerable<AudioChunk> Gated(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Delay(Timeout.Infinite, token);
    }

    // Parks on the prefetch's own token so DisposeAsync can actually unwind it, which is the
    // behaviour under test: disposal is what releases an in-flight synthesis nobody will enumerate.
    private static async IAsyncEnumerable<AudioChunk> DisposeTracking(TaskCompletionSource disposed,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        try
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Delay(Timeout.Infinite, token);
        }
        finally
        {
            disposed.TrySetResult();
        }
    }

    // Zero chunks: the alarm must drain instantly under the frozen test clock, so it never reaches
    // the real-time drain wait that only a clock advance would release.
    private static async IAsyncEnumerable<AudioChunk> NoAudio()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<AudioChunk> Recording(string text, List<string> sink)
    {
        lock (sink)
        { sink.Add(text); }
        await Task.Yield();
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(text),
            Format = AudioFormat.WyomingStandard
        };
    }

    // The four cases the turn key decides between, one test each. Each asserts what a caller can
    // observe — whether the turn settled, and what reached the synthesizer — never the speaker's
    // own bookkeeping.

    private void SayFor(string? turnKey, string content, ReplyContentType contentType,
        bool isComplete, bool? agentInitiated = false) =>
        _speaker.SpeakUtteranceReply(_sessions.Get("kitchen-01")!, new SendReplyParams
        {
            ConversationId = _conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        });

    [Fact]
    public async Task SpeakUtteranceReply_KeyMatchesTheTurnInFlight_SpeaksItAndSettlesTheTurn()
    {
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        var turn = _session.Turn.AwaitSpoken();

        SayFor("turn-1", "Veintiún grados.", ReplyContentType.Text, false);
        SayFor("turn-1", "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue();
        _tts.Verify(t => t.SynthesizeAsync(
            "Veintiún grados.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpeakUtteranceReply_AnotherTurnsAgentInitiatedDelivery_IsSpokenAndLeavesTheTurnOutstanding()
    {
        // A timer or a scheduled message landing while the user is mid-conversation. It interrupts
        // and nothing more: the answer the user asked for still arrives, and they keep the follow-up
        // window they were owed.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        var turn = _session.Turn.AwaitSpoken();

        SayFor("sched-turn", "Han pasado diez minutos.", ReplyContentType.Text, false, agentInitiated: true);
        SayFor("sched-turn", "", ReplyContentType.StreamComplete, true, agentInitiated: true);

        _tts.Verify(t => t.SynthesizeAsync(
            "Han pasado diez minutos.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { heard.TrySetResult(); await Task.Yield(); }, run.Token);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        turn.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task SpeakUtteranceReply_AnotherUserTurnsAbandonedAnswer_IsDiscardedAndAppendsNothing()
    {
        // The hub gave that turn up at the reply timeout and the user has already asked something
        // else. Speaking it would put the tail of a question they moved on from in front of the
        // answer they are waiting for.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-2");
        var turn = _session.Turn.AwaitSpoken();

        SayFor("turn-1", "Respuesta a la pregunta anterior.", ReplyContentType.Text, false);
        SayFor("turn-1", "", ReplyContentType.StreamComplete, true);

        _tts.VerifyNoOtherCalls();
        _accumulator.Flush(_conversationId).ShouldBeEmpty();

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);
        await run.StopAsync(pump);

        turn.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task SpeakUtteranceReply_NoKeyAtAll_IsTreatedAsThisTurnsAndPublishesAnError()
    {
        // Only reachable if the echo itself is broken. Falling back to the old behaviour keeps the
        // house answering; the error is what makes the breakage visible as itself rather than as
        // satellites that stopped answering for two minutes at a time.
        var errors = new List<ErrorEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is ErrorEvent error)
                {
                    lock (errors)
                    { errors.Add(error); }
                }
            });
        var speaker = Speaker(new VoiceSettings(), metrics.Object);

        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        var turn = _session.Turn.AwaitSpoken();

        speaker.SpeakUtteranceReply(_session, new SendReplyParams
        {
            ConversationId = _conversationId,
            Content = "Veintiún grados.",
            ContentType = ReplyContentType.Text,
            IsComplete = true
        });

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue();
        errors.ShouldContain(e => e.Service == "voice" && e.ErrorType == "ReplyWithoutTurnKey");
    }

    [Fact]
    public async Task SpeakUtteranceReply_TheSatelliteRedialledMidAnswer_SettlesItsNextTurnRightAway()
    {
        // The failure the key exists for. The agent's link to this server drops mid-answer, so no
        // terminal event ever arrives for that turn. The satellite redials, the hub builds a fresh
        // session, and the user asks something else. The abandoned answer's late completion names
        // the turn it belonged to and is dropped; the new turn settles on its own answer instead of
        // waiting out the ~120 s reply timeout with the microphone shut.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        SayFor("turn-1", "Respuesta interrumpida.", ReplyContentType.Text, false);

        _sessions.Register(new SatelliteSession("kitchen-01", _session.Config));
        var session = _sessions.Get("kitchen-01")!;
        session.Turn.Reset();
        session.Turn.StampTurnKey("turn-2");
        // What dispatching the next transcript does, and the reason it does it here: the abandoned
        // run may never send another chunk, so nothing later would clear what it left behind.
        _accumulator.Flush(_conversationId);
        var turn = session.Turn.AwaitSpoken();

        // The abandoned run finally reports; it answers a turn nobody is waiting on any more.
        SayFor("turn-1", "", ReplyContentType.StreamComplete, true);

        SayFor("turn-2", "Veintiún grados.", ReplyContentType.Text, false);
        SayFor("turn-2", "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2));
        await run.StopAsync(pump);

        spoke.ShouldBeTrue();
        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("interrumpida")),
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SpeakUtteranceReply_AnAnnouncementStreamThatNeverEnded_LosesItsBufferWithTheConversation()
    {
        // An announcement heard beside a live turn buffers under its own turn, and only that
        // announcement's own terminal event flushes it. The agent's link to this server dropping
        // mid-announcement means no terminal event ever arrives, and every announcement carries a
        // fresh turn key — so on a server that runs for weeks the leftover buffers have no bound.
        // Tearing the conversation down has to take its announcements with it; the plain
        // conversation key it used to flush reaches none of them.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");

        SayFor("sched-turn", "Han pasado diez minutos", ReplyContentType.Text, false,
            agentInitiated: true);

        var buffer = ReplyTextAccumulator.TurnBuffer(_conversationId, "sched-turn");
        var buffered = _accumulator.Flush(buffer);
        buffered.ShouldBe("Han pasado diez minutos"); // it really is held under the announcement's turn
        _accumulator.PutBack(buffer, buffered);       // and put straight back, still stranded

        _conversationClock.Advance(_conversationLifetime + TimeSpan.FromMinutes(1));

        _accumulator.Flush(buffer).ShouldBeEmpty();
    }

    [Fact]
    public async Task SpeakUtteranceReply_AKeylessScheduleFiringIntoALiveSession_PublishesNoTurnAnchoredSpans()
    {
        // The same "recuérdame en dos minutos" fire, from an agent container that predates the turn
        // key — the skew send_reply's optional parameters exist for. It carries no key to divert it,
        // so Classify falls back to the turn in flight and it comes down the reply path after all.
        // The turn anchors from the earlier real turn are never invalidated, so reporting against
        // them publishes the AGE of that turn (~120000 ms) as this reply's latency: one sample that
        // wrecks Avg/P95/Max on the headline metric. The dispatch stamp the real turn already
        // consumed is the proof that a reply answers a transcript this hub dispatched.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        Anchors.MarkTurnStart(_clock.GetTimestamp());
        Anchors.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        SayFor(null, "son las diez", ReplyContentType.Text, false);
        SayFor(null, "", ReplyContentType.StreamComplete, true);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { heard.TrySetResult(); await Task.Yield(); }, run.Token, _clock);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs);
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
    }

    [Fact]
    public async Task SpeakUtteranceReply_AKeylessAbandonedAnswerCompletes_DoesNotSettleTheTurnInFlight()
    {
        // A rolling deploy where the agent container predates the turn key: every chunk arrives
        // keyless, so Classify has nothing to compare and falls back to the turn in flight. The hub
        // gave turn one up at ReplyTimeoutMs and the user asked something else, and the abandoned
        // answer's completion lands on turn two. Settling it there ends FollowUpConversation, whose
        // chime preempts the sentence being spoken and reopens the microphone over the rest of the
        // answer — so the end of a stream may only settle the turn that stream opened against.
        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-1");
        SayFor(null, "La respuesta a la primera pregunta.", ReplyContentType.Text, false);

        _session.Turn.Reset();
        _session.Turn.StampTurnKey("turn-2");
        _accumulator.Flush(_conversationId); // what dispatching the next transcript does
        var turn = _session.Turn.AwaitSpoken();

        SayFor(null, "", ReplyContentType.StreamComplete, true); // turn one's answer finally reports

        turn.IsCompleted.ShouldBeFalse();

        // The guard refuses the stale end; it does not wedge the microphone behind it. Turn two's
        // own answer, keyless like everything else this agent sends, still settles it.
        SayFor(null, "Veintiún grados.", ReplyContentType.Text, false);
        SayFor(null, "", ReplyContentType.StreamComplete, true);

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token);

        (await turn.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBeTrue();
        await run.StopAsync(pump);
    }

    private static async Task WaitForCountAsync(List<string> sink, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (sink)
            {
                if (sink.Count >= count)
                { return; }
            }
            await Task.Delay(20);
        }
    }

}