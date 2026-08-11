using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// All the tool does is pick a branch: a live session, a scheduled delivery target, or neither. The
// reply policy itself lives in ReplySpeaker and is tested there, without a container.
public class SendReplyToolTests
{
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly VoiceConversationManager _manager;
    private readonly List<VoiceEvent> _published = [];
    private readonly IServiceProvider _services;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public SendReplyToolTests()
    {
        var accumulator = new ReplyTextAccumulator();
        var clock = _clock;
        var settings = new VoiceSettings();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt =>
            {
                if (evt is VoiceEvent voiceEvent)
                {
                    lock (_published)
                    { _published.Add(voiceEvent); }
                }
            });
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["office-01"] = new() { Identity = "household", Room = "Office" }
        });

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-office");
                var topic = new TopicMetadata("topic-office", identity.ChatId, identity.ThreadId, "mycroft",
                    "household @ Office", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        _manager = new VoiceConversationManager(
            factory.Object, accumulator, clock,
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_manager)
            .AddSingleton(registry)
            .AddSingleton(new VoiceDeliveryRegistry(
                clock, TimeSpan.FromMinutes(5), accumulator,
                NullLogger<VoiceDeliveryRegistry>.Instance))
            .AddSingleton(new ReplySpeaker(
                accumulator, _tts.Object, settings, metrics.Object, clock,
                NullLogger<ReplySpeaker>.Instance))
            .AddSingleton(new AnnouncementService(
                registry, _sessions, _tts.Object, settings, metrics.Object,
                NullLogger<AnnouncementService>.Instance))
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_ConversationBoundToNoSatelliteAndNoDeliveryTarget_ReturnsOkWithoutSpeaking()
    {
        var result = await SendReplyTool.McpRun(
            "never-seen", "hi", ReplyContentType.Text, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    // The gap create_conversation leaves open: it acknowledged the conversation without a binding
    // because a live session owned it, and that session died before the reply arrived. The manager
    // still maps the conversation to its satellite, and that mapping is the fallback target.
    [Fact]
    public async Task McpRun_SatelliteRebootsBetweenAnnounceAndReply_TheChunksSentWhileItWasDownStillSpeak()
    {
        var session = new SatelliteSession(
            "office-01", new SatelliteConfig { Identity = "household", Room = "Office" });
        _sessions.Register(session);
        var convId = await _manager.GetOrCreateAsync(session, "mycroft", "hola", CancellationToken.None);
        _sessions.Unregister("office-01");   // the reboot window opens

        var result = await SendReplyTool.McpRun(
            convId, "La película terminó de descargarse.", ReplyContentType.Text, false, "m-1", _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();   // nothing to speak on yet — but the text must not be dropped

        // The satellite is back before the stream completes; the buffered text speaks with the rest.
        _sessions.Register(new SatelliteSession(
            "office-01", new SatelliteConfig { Identity = "household", Room = "Office" }));
        await SendReplyTool.McpRun(convId, "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(
            t => t.SynthesizeAsync(
                "La película terminó de descargarse.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The other side of that gap: the announce landed while the satellite was down, so
    // create_conversation DID bind a delivery. The satellite comes back, the user speaks, and the
    // answer to that live turn buffers under the same conversation id — while the stray binding is
    // still counting down to an expiry that flushes exactly that buffer. A live turn owns the
    // conversation, so taking a reply through the live session has to supersede the binding.
    [Fact]
    public async Task McpRun_LiveTurnAfterAnAnnounceBoundWhileTheSatelliteWasDown_StillSpeaksTheAnswer()
    {
        var session = new SatelliteSession(
            "office-01", new SatelliteConfig { Identity = "household", Room = "Office" });
        _sessions.Register(session);
        var convId = await _manager.GetOrCreateAsync(session, "mycroft", "hola", CancellationToken.None);
        _sessions.Unregister("office-01");   // the satellite drops off

        // The download alert announces into the conversation and, seeing no live session, binds.
        await CreateConversationTool.McpRun(
            "mycroft", string.Empty, "fran", _services, "[download-complete] film.mkv", "office-01", convId);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _sessions.Register(session);   // reconnected, and the user speaks into the same conversation
        await _manager.GetOrCreateAsync(session, "mycroft", "qué hora es", CancellationToken.None);
        await SendReplyTool.McpRun(convId, "Son las cinco.", ReplyContentType.Text, false, "m-1", _services);

        // The binding's 5-minute expiry falls inside the turn (the conversation's own timer was
        // renewed when the user spoke, so it is not what fires here).
        _clock.Advance(TimeSpan.FromMinutes(3.5));

        await SendReplyTool.McpRun(convId, "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(
            t => t.SynthesizeAsync("Son las cinco.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task McpRun_SatelliteStillDownAtStreamComplete_AnnouncesOnItsSatelliteInsteadOfDroppingSilently()
    {
        var session = new SatelliteSession(
            "office-01", new SatelliteConfig { Identity = "household", Room = "Office" });
        _sessions.Register(session);
        var convId = await _manager.GetOrCreateAsync(session, "mycroft", "hola", CancellationToken.None);
        _sessions.Unregister("office-01");

        await SendReplyTool.McpRun(convId, "Recordatorio.", ReplyContentType.Text, false, "m-1", _services);
        var result = await SendReplyTool.McpRun(convId, "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        // The reply reached the announce path and reported the satellite offline — an observable
        // outcome, not a silent drop returning ok.
        lock (_published)
        {
            _published.ShouldContain(e =>
                e.Metric == VoiceMetric.AnnounceError && e.SatelliteId == "office-01" && e.Outcome == "offline");
        }
    }
}