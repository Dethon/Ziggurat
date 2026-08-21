using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit;

namespace Tests.Integration.McpChannelVoice;

public class InsistentAnnounceE2ETests
{
    // The gap the request below asks for. Named because an advance must match the wait the code
    // armed exactly — a loose match would settle some other wait on the same clock.
    private static readonly TimeSpan _ringGap = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task PostInsistentAnnounce_RepeatsUntilAcknowledged()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var satListener = new TcpListener(IPAddress.Loopback, 0);
        satListener.Start();
        var satPort = ((IPEndPoint)satListener.LocalEndpoint).Port;

        // Each entry is one audio-start frame's `alert` flag as it arrived on the wire.
        var audioStarts = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await satListener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "audio-start")
                {
                    audioStarts.Enqueue(evt.Data["alert"]?.GetValue<bool>() ?? false);
                }
            }
        }, ct);

        var settings = new VoiceSettings
        {
            WyomingClient = new() { ReconnectDelaySeconds = 1 },
            Announce = new() { Enabled = true, Token = "secret", QueueMaxDepth = 8 },
            Satellites = new()
            {
                ["kitchen-01"] = new()
                {
                    Identity = "household",
                    Room = "Kitchen",
                    WakeWord = "hey_jarvis",
                    Address = $"tcp://127.0.0.1:{satPort}"
                }
            }
        };

        var apiPort = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, apiPort));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Announce);
        builder.Services.AddSingleton(settings.WyomingClient);
        builder.Services.AddSingleton(new SatelliteRegistry(settings.Satellites));
        builder.Services.AddSingleton<SatelliteSessionRegistry>();
        builder.Services.AddSingleton<ActiveAlertRegistry>();
        // Read rather than ignored: AlarmAcknowledged is the loop's own statement that it stopped
        // because the alert was acknowledged, which is the half a count of rings cannot prove.
        var metrics = new RecordingMetricsPublisher();
        builder.Services.AddSingleton<IMetricsPublisher>(metrics);

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => FakeTtsAudio());
        builder.Services.AddSingleton(tts.Object);
        builder.Services.AddSingleton<TranscriptDispatcher>(_ => null!);

        var stt = new Mock<ISpeechToText>();
        builder.Services.AddSingleton(stt.Object);
        builder.Services.AddSingleton<ReplyTextAccumulator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<VoiceConversationManager>(sp => new VoiceConversationManager(
            Mock.Of<IConversationFactory>(), sp.GetRequiredService<ReplyTextAccumulator>(),
            sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance));
        builder.Services.AddSingleton<AnnouncementService>();
        builder.Services.AddHttpClient();
        // Only the repeat loop's gap runs on a clock this test drives. Everything else in the hub
        // — playback streaming audio down a real socket, the satellite host's reconnects — keeps
        // the system clock, because those waits are the transport's and faking them would change
        // what the test exercises. The controller is the one service whose wait belongs to the
        // behaviour under test: how long it leaves between rings.
        var rings = new ArmedClock();
        builder.Services.AddSingleton<InsistentAnnouncementController>(sp =>
            new InsistentAnnouncementController(
                sp.GetRequiredService<SatelliteRegistry>(),
                sp.GetRequiredService<SatelliteSessionRegistry>(),
                sp.GetRequiredService<ITextToSpeech>(),
                sp.GetRequiredService<VoiceSettings>(),
                sp.GetRequiredService<ActiveAlertRegistry>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                rings,
                sp.GetRequiredService<IHttpClientFactory>(),
                NullLogger<InsistentAnnouncementController>.Instance));
        // The host takes the arbiter as a constructor dependency; without these the container fails
        // to activate it and the whole test dies at StartAsync before reaching any assertion.
        builder.Services.AddSingleton(settings.Arbitration);
        builder.Services.AddSingleton<WakeArbiter>();
        builder.Services.AddSingleton<SilenceGateFactory>();
        builder.Services.AddHostedService<WyomingSatelliteHost>();

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync(ct);

        var sessions = app.Services.GetRequiredService<SatelliteSessionRegistry>();
        await WaitForAsync(() => sessions.Get("kitchen-01") is not null, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        http.DefaultRequestHeaders.Add("X-Announce-Token", "secret");

        var response = await http.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new InsistentOptions { GapSeconds = 1, MaxRepeats = 10 }
            }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // It must REPEAT: the second ring is the one the gap stands in front of, so advancing past
        // that gap — once the loop has actually parked on it — is what produces it. Waiting for the
        // envelope afterwards is still a real wait: it has a socket to cross.
        await WaitForAsync(() => !audioStarts.IsEmpty, TimeSpan.FromSeconds(10));
        await rings.AdvancePastLiveAsync(_ringGap, _ringGap);
        await WaitForAsync(() => audioStarts.Count >= 2, TimeSpan.FromSeconds(10));

        // Every ring must reach the satellite marked as an alert, or it plays on the calibrated
        // conversational route and the alarm is capped by TTS_VOLUME. This is the only coverage of
        // the host lambda that puts job.Alert on the wire — the pure builder and PlaybackKind.Alarm
        // are unit-tested either side of it, so a literal there would ship a dead feature.
        audioStarts.ToArray().ShouldAllBe(alert => alert);

        // Acknowledge -> the loop stops; no meaningful growth after a couple more gaps.
        app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01").ShouldNotBeEmpty();
        var countAtAck = audioStarts.Count;

        // Nothing announces a ring that does not happen, so this half is bought with time: move the
        // clock well past three gaps, then let anything already in flight arrive before counting.
        rings.Advance(_ringGap * 3);

        // The loop says so itself when it ends on an acknowledgement, and it publishes that once.
        // Waiting for it is what makes this a state to poll for rather than a span to sleep
        // through — and it is the assertion that fails if the loop ever ignores the token.
        await Eventually.Until(
            () => metrics.Published.OfType<VoiceEvent>()
                .Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            "the ring loop to report that it stopped on the acknowledgement");

        // Then the other half, which is an absence and so has to be bought with time.
        await Eventually.Settle();
        audioStarts.Count.ShouldBeLessThanOrEqualTo(countAtAck + 1); // at most one in-flight round
        metrics.Published.OfType<VoiceEvent>()
            .ShouldNotContain(e => e.Metric == VoiceMetric.AlarmUnacknowledged);

        await app.StopAsync(CancellationToken.None);
        satListener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation */ }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            { return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private static async IAsyncEnumerable<AudioChunk> FakeTtsAudio()
    {
        yield return new AudioChunk { Data = new byte[32], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}