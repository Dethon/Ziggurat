using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Channels;
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
using Tests.Unit.McpChannelVoice;

namespace Tests.Integration.McpChannelVoice;

// One test, over a real TCP socket: dial, the Wyoming framing, a full turn and the hosted service,
// proved together. Everything else this file used to hold now runs against SatelliteConnection
// directly, with the same assertions and no listener.
public class WyomingSatelliteHostTests
{
    // Every test here dials a single satellite, and the arbiter no-ops below two registered
    // handles — so a default instance keeps these constructions real without changing what they
    // exercise. Multi-satellite arbitration has its own coverage.
    private static WakeArbiter Arbiter(VoiceConversationManager conversations) =>
        new(new ArbitrationSettings(), conversations, Mock.Of<IMetricsPublisher>(),
            TimeProvider.System, NullLogger<WakeArbiter>.Instance);

    // Gate resolution and the per-satellite room-noise memory live in one place now, so a test
    // builds the same factory the process would rather than assembling a tracker of its own.
    private static SilenceGateFactory Gates(VoiceSettings voice, WyomingClientSettings wyoming) =>
        new(voice, wyoming, TimeProvider.System);

    private static byte[] Pcm(short value, int bytes = 3200)
    {
        var buf = new byte[bytes];
        for (var i = 0; i + 1 < buf.Length; i += 2)
        {
            buf[i] = (byte)(value & 0xFF);
            buf[i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return buf;
    }

    [Fact]
    public async Task Hub_DialsSatelliteRunsAndStreams_TranscribesAndSendsTranscriptBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawRunSatellite = new TaskCompletionSource();
        var sawTranscript = new TaskCompletionSource<string>();

        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            var readLoop = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    if (evt.Type == "run-satellite")
                    {
                        sawRunSatellite.TrySetResult();
                    }
                    else if (evt.Type == "transcript")
                    {
                        sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? "");
                    }
                }
            }, ct);

            await sawRunSatellite.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Wake fired: announce the pipeline, then stream mic audio (no audio-stop).
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
            // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
            // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }, ct);

        var stt = new Mock<ISpeechToText>();
        string? capturedLanguage = null;
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                         It.IsAny<TranscriptionOptions>(),
                                         It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, opts, token) =>
                {
                    capturedLanguage = opts.Language;
                    await foreach (var _ in audio.WithCancellation(token))
                    { }
                    return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
                });

        var emitter = new ChannelInboxProbe("voice", DeliveryPolicy.Broadcast);
        var publisher = new Mock<IMetricsPublisher>();
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
        var dispatcher = new TranscriptDispatcher(
            emitter.Emitter, new FakeAccountResolver(), publisher.Object, manager, new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]), new ReplyTextAccumulator(), -1.0, 0.6, -1.4, 2000, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new()
            {
                Identity = "household",
                Room = "Kitchen",
                WakeWord = "hey_jarvis",
                Address = $"tcp://127.0.0.1:{port}",
                // Per-satellite STT language override must reach the backend (symmetric with the
                // per-satellite Tts.OpenAi.Voice override), not be silently dropped.
                Stt = new SttOverrides { OpenAi = new OpenAiSttOverrides { Language = "en" } }
            }
        });

        var wyoming = new WyomingClientSettings
        {
            ReconnectDelaySeconds = 1,
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        var voice = new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = false } };
        var host = new WyomingSatelliteHost(
            wyoming,
            voice,
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object,
            TimeProvider.System,
            Arbiter(manager),
            Gates(voice, wyoming),
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        var msg = await emitter.FirstAsync(TimeSpan.FromSeconds(10), ct);
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldNotBeNullOrWhiteSpace();
        msg.Sender.ShouldBe("household");
        msg.AgentId.ShouldBe("mycroft");

        capturedLanguage.ShouldBe("en"); // per-satellite Stt.OpenAi.Language threaded into TranscriptionOptions

        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        transcriptText.ShouldBe(""); // legacy path re-arms with an (ignored) empty transcript

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation / disposal */ }
    }

}