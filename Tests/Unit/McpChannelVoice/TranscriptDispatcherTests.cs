using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Mcp.Hosting;

namespace Tests.Unit.McpChannelVoice;

public class TranscriptDispatcherTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static CommandSettings Commands(bool enabled = true) =>
        new()
        {
            Enabled = enabled,
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        };

    private static (TranscriptDispatcher Sut, VoiceConversationManager Manager, ChannelInboxProbe Emitter) Build(
        CommandSettings? commands = null, IMetricsPublisher? publisher = null,
        double shortSpeechAvgLogProbThreshold = -1.4, int fullThresholdSpeechMs = 2000,
        ReplyTextAccumulator? accumulator = null)
    {
        accumulator ??= new ReplyTextAccumulator();
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        var emitter = new ChannelInboxProbe("voice", DeliveryPolicy.Broadcast);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new TranscriptDispatcher(
            emitter.Emitter, publisher ?? Mock.Of<IMetricsPublisher>(), manager,
            new LocalCommandDispatcher(
                new VoiceCommandMatcher(commands ?? new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
            accumulator,
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6,
            shortSpeechAvgLogProbThreshold: shortSpeechAvgLogProbThreshold,
            fullThresholdSpeechMs: fullThresholdSpeechMs,
            time, NullLogger<TranscriptDispatcher>.Instance);
        return (sut, manager, emitter);
    }

    private static (Mock<IMetricsPublisher> Publisher, List<MetricEvent> Published) CapturingMetricsPublisher()
    {
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => published.Add(e));
        return (publisher, published);
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_OpensConversationViaManager()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, null, null, default);

        ok.ShouldBeTrue();
        var convo = manager.GetActiveConversationId("kitchen-01");
        convo.ShouldNotBeNull();
        manager.ResolveSatelliteId(convo).ShouldBe("kitchen-01");

        emitter.Received().Count.ShouldBe(1);
        emitter.Received()[0].ConversationId.ShouldBe(convo);
        emitter.Received()[0].Content.ShouldBe("what time is it");
        emitter.Received()[0].Sender.ShouldBe("household");
        emitter.Received()[0].AgentId.ShouldBe("agent-1");
    }

    // Voice mints the key rather than leaving it to the agent, because voice is the side that has
    // to recognise the answer when it comes back.
    [Fact]
    public async Task DispatchAsync_GoodTranscript_MintsATurnKeyAndStampsItOnTheTurnAndTheMessage()
    {
        var (sut, _, emitter) = Build();
        var session = Session();

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "what time is it", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        var turnKey = emitter.Received().ShouldHaveSingleItem().TurnKey;
        turnKey.ShouldNotBeNullOrWhiteSpace();
        session.Turn.TurnKey.ShouldBe(turnKey);
    }

    [Fact]
    public async Task DispatchAsync_TwoTurns_MintADifferentKeyEach()
    {
        var (sut, _, emitter) = Build();
        var session = Session();

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "what time is it", Confidence = 0.9 },
            "agent-1", null, null, null, default);
        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "and the weather", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        emitter.Received().Select(m => m.TurnKey).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task DispatchAsync_ThePreviousTurnLeftTextBuffered_DropsItAsTheNextTurnIsDispatched()
    {
        // The abandoned run may never send anything else, so waiting for a mismatched chunk to
        // clear the buffer leaves a stale sentence to be spoken at the front of the next answer.
        var accumulator = new ReplyTextAccumulator();
        var (sut, manager, _) = Build(accumulator: accumulator);
        var session = Session();

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "what time is it", Confidence = 0.9 },
            "agent-1", null, null, null, default);
        var conversationId = manager.GetActiveConversationId("kitchen-01")!;
        accumulator.Append(conversationId, "media respuesta que nadie escuchó");

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "and the weather", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        accumulator.Flush(conversationId).ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_IdentifiedSpeaker_EmitsThatSenderNotConfigIdentity()
    {
        var (sut, _, emitter) = Build();

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "hola", Confidence = 0.9 }, "agent-1", null, 0.8, "fran", default);

        emitter.Received().Count.ShouldBe(1);
        emitter.Received()[0].Sender.ShouldBe("fran"); // conclusive identity drives per-person memory
    }

    [Fact]
    public async Task DispatchAsync_SatelliteWithLocality_EmitsRoomAndLocalityAsLocation()
    {
        var (sut, _, emitter) = Build();
        var session = new SatelliteSession(
            "kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen", Locality = "Madrid, Spain" });

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "what's the weather", Confidence = 0.9 }, "agent-1", null, null, null, default);

        emitter.Received().Count.ShouldBe(1);
        emitter.Received()[0].Location.ShouldBe("Kitchen (Madrid, Spain)");
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_EmitsSatelliteId()
    {
        var (sut, _, emitter) = Build();

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, null, null, default);

        emitter.Received().Count.ShouldBe(1);
        emitter.Received()[0].SatelliteId.ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task DispatchAsync_LowAvgLogProb_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "mumble", AvgLogProb = -2.1 }, "agent-1", null, null, null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_HighNoSpeechProb_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "ffff", NoSpeechProb = 0.8 }, "agent-1", null, null, null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NullQualitySignals_FailsOpenAndDispatches()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "sin señales" }, "agent-1", null, null, null, default);

        ok.ShouldBeTrue();
        manager.GetActiveConversationId("kitchen-01").ShouldNotBeNull();
        emitter.Received().Count.ShouldBe(1);
    }

    private static SatelliteSession SessionWithSttOverrides(OpenAiSttOverrides overrides) =>
        new("kitchen-01", new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Stt = new SttOverrides { OpenAi = overrides }
        });

    [Fact]
    public async Task DispatchAsync_SatelliteAvgLogProbOverride_TightensGate()
    {
        var (sut, manager, emitter) = Build(); // global floor -1.0
        var session = SessionWithSttOverrides(new OpenAiSttOverrides { AvgLogProbThreshold = -0.5 });

        var ok = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "mumble", AvgLogProb = -0.7 }, "agent-1", null, null, null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_SatelliteNoSpeechProbOverride_TightensGate()
    {
        var (sut, manager, emitter) = Build(); // global ceiling 0.6
        var session = SessionWithSttOverrides(new OpenAiSttOverrides { NoSpeechProbThreshold = 0.2 });

        var ok = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "ffff", NoSpeechProb = 0.4 }, "agent-1", null, null, null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_SatelliteOverrideWithoutThresholds_UsesGlobals()
    {
        var (sut, _, emitter) = Build();
        var session = SessionWithSttOverrides(new OpenAiSttOverrides { Language = "en" });

        var ok = await sut.DispatchAsync(
            session,
            new TranscriptionResult { Text = "hola", AvgLogProb = -0.7, NoSpeechProb = 0.4 },
            "agent-1", null, null, null, default);

        ok.ShouldBeTrue();
        emitter.Received().Count.ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_EmptyText_DropsAndPublishesDroppedMetric()
    {
        var factory = new Mock<IConversationFactory>();
        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var emitter = new ChannelInboxProbe("voice", DeliveryPolicy.Broadcast);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => published.Add(e));
        var sut = new TranscriptDispatcher(
            emitter.Emitter, publisher.Object, manager, new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
            new ReplyTextAccumulator(),
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6, shortSpeechAvgLogProbThreshold: -1.4, fullThresholdSpeechMs: 2000, new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "   ", Confidence = 0.9 }, "agent-1", null, null, null, default);

        ok.ShouldBeFalse();
        emitter.Received().ShouldBeEmpty();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        factory.Verify(
            f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()),
            Times.Never);
        published.OfType<VoiceEvent>()
            .ShouldContain(e => e.Metric == VoiceMetric.UtteranceTranscribed && e.Outcome == "dropped");
    }

    [Fact]
    public async Task DispatchAsync_AfterDismissal_EmitsDismissedAlertOnce()
    {
        var (sut, _, emitter) = Build();
        var session = Session();
        session.NoteDismissedAlert("alarm \"trash\"", DateTimeOffset.UtcNow);

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "five more minutes", Confidence = 0.9 }, "agent-1", null, null, null, default);
        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "thanks", Confidence = 0.9 }, "agent-1", null, null, null, default);

        emitter.Received().Count.ShouldBe(2);
        emitter.Received()[0].DismissedAlert.ShouldBe("alarm \"trash\"");
        emitter.Received()[1].DismissedAlert.ShouldBeNull(); // consumed by the first dispatch
    }

    [Fact]
    public async Task DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats()
    {
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => published.Add(e));
        var sut = new TranscriptDispatcher(
            new ChannelInboxProbe("voice", DeliveryPolicy.Broadcast).Emitter, publisher.Object, manager, new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
            new ReplyTextAccumulator(),
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6, shortSpeechAvgLogProbThreshold: -1.4, fullThresholdSpeechMs: 2000, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "hola",
                Confidence = 0.8,
                AvgLogProb = -0.22,
                NoSpeechProb = 0.05,
                CompressionRatio = 1.3
            },
            "agent-1",
            new CaptureStats(PeakRms: 4200, FloorRms: 320, SpeechMs: 1800, EndReason: "trailing_silence", TrailingRms: 610),
            0.72,
            null,
            default);

        ok.ShouldBeTrue();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dispatched");
        evt.Confidence.ShouldBe(0.8);
        evt.AvgLogProb.ShouldBe(-0.22);
        evt.NoSpeechProb.ShouldBe(0.05);
        evt.CompressionRatio.ShouldBe(1.3);
        evt.PeakRms.ShouldBe(4200);
        evt.SpeechMs.ShouldBe(1800);
        evt.FloorRms.ShouldBe(320);
        evt.TrailingRms.ShouldBe(610);
        evt.EndReason.ShouldBe("trailing_silence");
        evt.Similarity.ShouldBe(0.72);
    }

    [Fact]
    public async Task DispatchAsync_Dropped_PublishesCaptureAndWhisperStats()
    {
        var manager = new VoiceConversationManager(
            new Mock<IConversationFactory>().Object, new ReplyTextAccumulator(),
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => published.Add(e));
        var sut = new TranscriptDispatcher(
            new ChannelInboxProbe("voice", DeliveryPolicy.Broadcast).Emitter, publisher.Object, manager, new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
            new ReplyTextAccumulator(),
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6, shortSpeechAvgLogProbThreshold: -1.4, fullThresholdSpeechMs: 2000, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "grbll xzzt",
                Confidence = 0.12,
                AvgLogProb = -2.1,
                NoSpeechProb = 0.55,
                CompressionRatio = 2.8
            },
            "agent-1",
            new CaptureStats(PeakRms: 900, FloorRms: 610, SpeechMs: 450, EndReason: "max_utterance"),
            null,
            null,
            default);

        ok.ShouldBeFalse();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dropped");
        evt.Confidence.ShouldBe(0.12);
        evt.AvgLogProb.ShouldBe(-2.1);
        evt.NoSpeechProb.ShouldBe(0.55);
        evt.CompressionRatio.ShouldBe(2.8);
        evt.PeakRms.ShouldBe(900);
        evt.SpeechMs.ShouldBe(450);
        evt.FloorRms.ShouldBe(610);
        evt.EndReason.ShouldBe("max_utterance");
    }

    [Fact]
    public async Task DispatchAsync_LocalCommand_SendsSpeakerVolumeAndSkipsTheAgent()
    {
        var (sut, manager, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "sube el volumen local", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeFalse();
        written.Count.ShouldBe(1);
        written[0].Type.ShouldBe("speaker-volume");
        written[0].Data["action"]!.GetValue<string>().ShouldBe("up");
        emitter.Received().ShouldBeEmpty();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_LocalCommandWithControlWriter_PublishesCommandOutcome()
    {
        var (publisher, published) = CapturingMetricsPublisher();
        var (sut, _, _) = Build(Commands(), publisher.Object);
        var session = Session();
        session.ControlWriter = (_, _) => Task.CompletedTask;

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "sube el volumen local", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("command");
    }

    [Fact]
    public async Task DispatchAsync_LocalCommandWithNoControlWriter_PublishesCommandFailedOutcome()
    {
        var (publisher, published) = CapturingMetricsPublisher();
        var (sut, _, _) = Build(Commands(), publisher.Object);

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "sube el volumen local", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("command_failed");
    }

    // The gibberish gate runs first on purpose: acting on audio the STT itself flagged as poor is
    // exactly the misfire the gate exists to prevent.
    [Fact]
    public async Task DispatchAsync_LowQualityTranscriptMatchingAPhrase_IsDroppedNotExecuted()
    {
        var (sut, _, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session,
            new TranscriptionResult { Text = "sube el volumen local", AvgLogProb = -5.0 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeFalse();
        written.ShouldBeEmpty();
        emitter.Received().ShouldBeEmpty();
    }

    // "sube el volumen" without the local marker is a Music Assistant request and belongs to the
    // agent. It must travel the normal path untouched.
    [Fact]
    public async Task DispatchAsync_MusicVolumeRequest_StillReachesTheAgent()
    {
        var (sut, _, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "sube el volumen", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeTrue();
        written.ShouldBeEmpty();
        emitter.Received().Count.ShouldBe(1);
        emitter.Received()[0].Content.ShouldBe("sube el volumen");
    }

    [Fact]
    public async Task DispatchAsync_ShortSpeechUnderTheFullFloor_StillDispatches()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 900, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeTrue();
        emitter.Received().Count.ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_SameScoreAtFullDuration_IsDropped()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 5000, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShortSpeechUnderTheLooseFloorToo_IsStillDropped()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 900, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.9 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Received().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NoCaptureStats_UsesTheFullFloor()
    {
        var (sut, _, emitter) = Build();

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", null, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Received().ShouldBeEmpty();
    }
}