using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests : IDisposable
{
    private SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly Mock<ISpeechToText> _stt = new();
    private readonly CancellationTokenSource _pump = new();
    private readonly Task _pumpTask;
    private readonly VoiceConversationManager _manager;
    private readonly string _conversationId;
    private readonly IServiceProvider _services;

    public RequestApprovalToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        _pumpTask = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), _pump.Token);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        _manager = new VoiceConversationManager(
            factory.Object, _accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _conversationId = _manager.GetOrCreateAsync(_session, "agent-1", "hi", default).GetAwaiter().GetResult();

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        _services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            new WyomingClientSettings
            {
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 3000,
                MinSpeechMs = 100
            });
    }

    private IServiceProvider BuildServices(
        VoiceSettings voice, WyomingClientSettings wyoming, Action<SilenceGateFactory>? seedRoomNoise = null)
    {
        var gates = new SilenceGateFactory(voice, wyoming, TimeProvider.System);
        seedRoomNoise?.Invoke(gates);
        return BuildServices(voice, wyoming, gates);
    }

    private IServiceProvider BuildServices(
        VoiceSettings voice, WyomingClientSettings wyoming, SilenceGateFactory gates,
        TimeProvider? time = null)
    {
        // A capture pays back into the room-noise memory of the factory that supplied its gate, so
        // a test running against its own factory listens on a microphone built over that factory.
        // The playback queue is carried over, because the pump is already running on it.
        _session = new SatelliteSession(
            _session.SatelliteId, _session.Config, _session.Playback,
            new Microphone(_session.SatelliteId, gates));
        _sessions.Register(_session);

        return new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(voice)
            .AddSingleton<ISpeechToText>(_stt.Object)
            .AddSingleton(wyoming)
            .AddSingleton(gates)
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton<ILogger<RequestApprovalTool>>(NullLogger<RequestApprovalTool>.Instance)
            .AddSingleton(time ?? TimeProvider.System)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _pump.Cancel();
        try
        { _pumpTask.GetAwaiter().GetResult(); }
        catch { /* OCE on teardown */ }
        _pump.Dispose();
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static AudioChunk Loud()
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

    // The tool queues its question synchronously, before its first await, so a job queued once
    // McpRun has returned its task sits behind the question in the same FIFO queue: when THIS
    // outcome arrives, the question has been heard. That is the same signal the tool itself waits
    // on before opening the mic, so the wait is on an outcome rather than on a flag flipping.
    private async Task PromptHeardThenMicOpenAsync()
    {
        var behindThePrompt = _session.Playback.Enqueue(new PlaybackJob(
            Label: "after-the-prompt",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio()));
        (await behindThePrompt.Completed.WaitAsync(TimeSpan.FromSeconds(10)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);

        // The tool opens the mic from its own continuation on that outcome, so this is the handoff
        // between two continuations of the same signal, not a poll for something unrelated.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_session.Mic.IsOpen)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the confirmation prompt never opened its mic");
            }
            await Task.Delay(5);
        }
    }

    // Whenever the tool opens a capture, feed one speech-then-silence answer into it.
    // Five silent chunks (500 ms) — not three — because the capture opens with no
    // leading gap: the floor tracker's smoothed floor needs a full smoothing window
    // of true silence to descend enough for the next "Loud" burst to cross the entry
    // bar (a shorter gap is exactly what the smoothing is designed to ride through).
    private Task FeedAnswersAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.Mic.IsOpen)
            {
                _session.Mic.Feed(Loud());
                _session.Mic.Feed(Loud());
                _session.Mic.Feed(Silent());
                _session.Mic.Feed(Silent());
                _session.Mic.Feed(Silent());
                _session.Mic.Feed(Silent());
                _session.Mic.Feed(Silent());
                await Task.Delay(60, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);

    private static ToolApprovalRequest MakeRequest(string toolName = "mcp__lib__download") =>
        new(null, toolName, new Dictionary<string, object?>());

    // An answer given in an audible room: the capture opens on the background itself (the prompt has
    // just finished playing), the user speaks over it, then stops.
    private Task FeedAnswerOverBackgroundAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.Mic.IsOpen)
            {
                foreach (var _ in Enumerable.Range(0, 5))
                { _session.Mic.Feed(Level(4000)); }   // the room, which the capture has to open on top of
                _session.Mic.Feed(Level(12_000));     // "sí, la de las tres"
                _session.Mic.Feed(Level(12_000));
                _session.Mic.Feed(Level(0));
                _session.Mic.Feed(Level(0));
                await Task.Delay(60, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);

    // Constant-amplitude S16LE: for a flat signal the RMS is the amplitude itself.
    private static AudioChunk Level(short amplitude)
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)(amplitude >> 8);
        }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    // DemoteMarginDb above EnterMarginDb is what makes the capture-level accept bar bite: an answer
    // whose peak does not stand clear of the floor is thrown away rather than transcribed.
    private static WyomingClientSettings AudibleRoomSettings() => new()
    {
        SilenceRmsThreshold = 500,
        TrailingSilenceMs = 200,
        MaxUtteranceMs = 3000,
        MinSpeechMs = 100,
        DemoteMarginDb = 20
    };

    // A prompt nobody answers: the mic hears only the room for its whole window.
    private Task FeedRoomToneAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.Mic.IsOpen)
            {
                _session.Mic.Feed(Level(90));
                await Task.Delay(10, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);

    private static double FloorOfNextGateFor(SilenceGateFactory gates, SatelliteSession session)
    {
        var next = gates.Create(session.SatelliteId, session.Config);
        // One loud frame, so the gate's own measurement is well above anything remembered — what is
        // left is whatever the memory caps it to.
        var chunk = Level(4000);
        next.Process(chunk.Data.Span, chunk.Format.SampleRateHz,
            chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        return next.FloorRms;
    }

    [Fact]
    public async Task RequestMode_AnswerCapture_PaysBackIntoTheRoomMemoryAndAnchorsNoTurn()
    {
        // The two halves of what makes an approval mic an approval mic. It listens through the
        // microphone, so closing pays back into the room-noise memory — a satellite used mostly for
        // confirmations still learns what its room sounds like. And it is not a turn: marking a turn
        // start or a speech end here would report this prompt's latency against the turn actually in
        // flight, so a job queued afterwards must still find both anchors unset.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var voice = new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 500 } };
        var wyoming = new WyomingClientSettings
        {
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        var gates = new SilenceGateFactory(voice, wyoming, TimeProvider.System);
        var services = BuildServices(voice, wyoming, gates);

        using var feed = new CancellationTokenSource();
        var feeder = FeedRoomToneAsync(feed.Token);

        (await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services)).ShouldBe("rejected");
        await feed.CancelAsync();

        FloorOfNextGateFor(gates, _session).ShouldBe(90, tolerance: 5);

        FirstAudioTiming? timing = null;
        var probe = _session.Playback.Enqueue(new PlaybackJob(
            Label: "after-the-approval",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio(),
            OnFirstAudio: t => { timing = t; return Task.CompletedTask; }));
        (await probe.Completed.WaitAsync(TimeSpan.FromSeconds(10)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);

        timing.ShouldNotBeNull();
        timing.SinceTurnStart.ShouldBeNull();
        timing.SinceSpeechEnd.ShouldBeNull();
    }

    [Fact]
    public async Task RequestMode_AnswerCaptureAbortedByArbiter_LeavesNoRoomReading()
    {
        // Arbitration took the turn away mid-answer, so this capture never established what silence
        // sounded like. Recording it would cap every later capture with a measurement that was never
        // made.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var voice = new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } };
        var wyoming = new WyomingClientSettings
        {
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        var gates = new SilenceGateFactory(voice, wyoming, TimeProvider.System);
        var services = BuildServices(voice, wyoming, gates);

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        await PromptHeardThenMicOpenAsync();
        _session.Mic.Feed(Level(90));
        _session.Mic.TryAbort().ShouldBeTrue();

        (await run).ShouldBe("rejected");

        // Uncapped: the next gate reads its own measurement of the loud frame, not a remembered one.
        FloorOfNextGateFor(gates, _session).ShouldBeGreaterThan(1000);
    }

    [Fact]
    public async Task RequestMode_NoRoomSample_DiscardsTheAnswerAgainstAnInflatedFloor()
    {
        // The capture measures its background from its own first frames, which here are the room the
        // user is already talking over. The floor freezes 10 dB under their voice, the answer fails
        // the accept bar, and a "sí" the satellite plainly heard is discarded.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            AudibleRoomSettings());

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswerOverBackgroundAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        await feed.CancelAsync();
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_WithARecordedRoomSample_HearsTheAnswer()
    {
        // Same room, same answer — but this satellite has produced a room reading recently, exactly
        // as the wake turn the user is answering did. Capping the floor with it keeps the answer.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            AudibleRoomSettings(),
            gates => gates.RecordRoomLevel("kitchen-01", 200));

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswerOverBackgroundAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        await feed.CancelAsync();
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task NotifyMode_DoesNotSpeakOrWaitForResponse()
    {
        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // Auto-approved tool calls must not be narrated over voice — with no pending
        // reply text there is nothing to speak (the tool name itself is never read out).
        _tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyMode_WithPendingReplyText_SpeaksAcknowledgement()
    {
        // The agent wrote an acknowledgement before calling the (auto-approved) tool.
        _accumulator.Append(_conversationId, "Dame un momento");

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // The pending acknowledgement is spoken now so the user hears it while the tool runs.
        _tts.Verify(
            t => t.SynthesizeAsync("Dame un momento", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsRejected()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "no thanks", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "hmm", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_OpenAnswerCapture_IsVisibleAsArbitrationHolder()
    {
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await PromptHeardThenMicOpenAsync();

        // The approval mic is an open capture like any wake turn's: Rule B must be able to ask
        // it, retrospectively, what it heard during another satellite's wake-word span —
        // otherwise a leaked "ok nabu" during an approval wakes the other room unarbitrated.
        _session.Mic.Activity.ShouldNotBeNull();

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);
        (await run).ShouldBe("approved");
        await feed.CancelAsync();
    }

    [Fact]
    public async Task RequestMode_AnswerCaptureAbortedByArbiter_RejectsWithoutSttOrReprompt()
    {
        // If the partial audio were transcribed anyway, this setup would wrongly approve.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await PromptHeardThenMicOpenAsync();

        // The arbiter stole the turn mid-answer (and already re-armed this satellite via
        // pause-satellite): the partial audio is not an answer, and there is no one left
        // here to re-prompt.
        _session.Mic.TryAbort().ShouldBeTrue();

        (await run).ShouldBe("rejected");
        _stt.Verify(
            s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestMode_EchoGuardTail_WaitsOnTheInjectedClockNotTheWall()
    {
        // The echo guard between the prompt finishing and the answer mic opening is a timed wait
        // like FollowUpConversation's identical one, so it runs on the injected TimeProvider — a
        // two-minute tail on the injected clock must cost this test nothing on the wall.
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var voice = new VoiceSettings
        { FollowUp = new FollowUpSettings { PlaybackTailMs = 120_000, WindowMs = 2000 } };
        var wyoming = new WyomingClientSettings
        {
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        var services = BuildServices(
            voice, wyoming, new SilenceGateFactory(voice, wyoming, TimeProvider.System), fake);

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        // The prompt has been heard once a job queued behind it drains (same FIFO queue).
        var behindThePrompt = _session.Playback.Enqueue(new PlaybackJob(
            Label: "after-the-prompt",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio()));
        (await behindThePrompt.Completed.WaitAsync(TimeSpan.FromSeconds(10)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);

        // Advancing repeatedly instead of once, so the advance cannot land before the tool has
        // registered its delay on the fake clock.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!_session.Mic.IsOpen)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the echo guard never elapsed on the injected clock");
            }
            fake.Advance(TimeSpan.FromMilliseconds(120_000));
            await Task.Delay(20);
        }

        _session.Mic.TryAbort().ShouldBeTrue();
        (await run).ShouldBe("rejected");
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsRejected()
    {
        var result = await RequestApprovalTool.McpRun(
            "ghost-01:999", ApprovalMode.Request, [MakeRequest()], _services);

        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_UsesPerSatelliteGateOverride_NotGlobalSettings()
    {
        // Global RMS threshold is 500; this satellite's Gate override raises it far above
        // the "Loud" answer level. The capture must be built from session.Config (like
        // WyomingSatelliteHost does) rather than global wyoming.* settings alone — otherwise
        // this satellite's answer is wrongly heard as speech and gets approved.
        var session = new SatelliteSession("loud-room-01",
            new SatelliteConfig
            {
                Identity = "household",
                Room = "Loud Room",
                Gate = new GateSettings { SilenceRmsThreshold = 50_000 }
            });
        _sessions.Register(session);

        using var pump = new CancellationTokenSource();
        var pumpTask = session.Playback.RunAsync(async (_, _) => await Task.Yield(), pump.Token);
        try
        {
            var conversationId = await _manager.GetOrCreateAsync(session, "agent-1", "hi", default);

            _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

            using var feed = new CancellationTokenSource();
            var feeder = Task.Run(async () =>
            {
                while (!feed.IsCancellationRequested)
                {
                    if (session.Mic.IsOpen)
                    {
                        session.Mic.Feed(Loud());
                        session.Mic.Feed(Loud());
                        session.Mic.Feed(Silent());
                        session.Mic.Feed(Silent());
                        session.Mic.Feed(Silent());
                        await Task.Delay(60, feed.Token);
                    }
                    else
                    {
                        await Task.Delay(10, feed.Token);
                    }
                }
            }, feed.Token);

            var result = await RequestApprovalTool.McpRun(
                conversationId, ApprovalMode.Request, [MakeRequest()], _services);

            await feed.CancelAsync();

            // Under the fix the raised per-satellite threshold means "Loud" never
            // classifies as speech, so the capture times out with no speech and the
            // approval is declined instead of being transcribed and approved.
            result.ShouldBe("rejected");
        }
        finally
        {
            pump.Cancel();
            try
            { await pumpTask; }
            catch { /* OCE on teardown */ }
        }
    }
}