using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Domain.Contracts;
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
using Tests.Unit.McpChannelVoice;

namespace Tests.Integration.McpChannelVoice;

public class AnnounceEndToEndTests
{
    [Fact]
    public async Task PostAnnounce_PushesAudioToDialedSatellite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        // Fake satellite Wyoming server the hub will dial out to.
        var satListener = new TcpListener(IPAddress.Loopback, 0);
        satListener.Start();
        var satPort = ((IPEndPoint)satListener.LocalEndpoint).Port;

        var audioEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // Each entry is one audio-start frame's `alert` flag as it arrived on the wire.
        var audioStartAlerts = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var sawAudioStop = new TaskCompletionSource();
        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await satListener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type is "audio-start" or "audio-chunk" or "audio-stop")
                {
                    audioEvents.Enqueue(evt.Type);
                }
                if (evt.Type == "audio-start")
                {
                    audioStartAlerts.Enqueue(evt.Data["alert"]?.GetValue<bool>() ?? false);
                }
                if (evt.Type == "audio-stop")
                {
                    sawAudioStop.TrySetResult();
                }
            }
        }, ct);

        var settings = new VoiceSettings
        {
            WyomingClient = new() { ReconnectDelaySeconds = 1 },
            Announce = new() { Enabled = true, Token = "secret", QueueMaxDepth = 4 },
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
        builder.Services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());

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
            new FakeTopicMinter(),
            sp.GetRequiredService<ReplyTextAccumulator>(),
            sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance));
        builder.Services.AddSingleton<AnnouncementService>();
        builder.Services.AddSingleton<ActiveAlertRegistry>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<InsistentAnnouncementController>();
        // The host takes the arbiter as a constructor dependency; without these the container fails
        // to activate it and the whole test dies at StartAsync before reaching any assertion.
        builder.Services.AddSingleton(settings.Arbitration);
        builder.Services.AddSingleton<WakeArbiter>();
        builder.Services.AddSingleton<SilenceGateFactory>();
        builder.Services.AddHostedService<WyomingSatelliteHost>();

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync(ct);

        // Wait for the hub to dial out and register the satellite session.
        var sessions = app.Services.GetRequiredService<SatelliteSessionRegistry>();
        await WaitForAsync(() => sessions.Get("kitchen-01") is not null, TimeSpan.FromSeconds(5));
        sessions.Get("kitchen-01").ShouldNotBeNull();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        http.DefaultRequestHeaders.Add("X-Announce-Token", "secret");

        var response = await http.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "hello",
                Priority = AnnouncePriority.Normal
            }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The satellite must receive a full Wyoming playback envelope, not bare chunks:
        // without audio-stop the satellite's paplay never closes stdin and a short clip
        // never clears the prebuffer, so nothing is audible.
        await sawAudioStop.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var seq = audioEvents.ToArray();
        seq.ShouldContain("audio-start");
        seq.ShouldContain("audio-chunk");
        seq.ShouldContain("audio-stop");
        Array.IndexOf(seq, "audio-start").ShouldBeLessThan(Array.IndexOf(seq, "audio-chunk"));
        Array.IndexOf(seq, "audio-chunk").ShouldBeLessThan(Array.LastIndexOf(seq, "audio-stop"));

        // The mirror image of the insistent case: a plain announce must NOT be marked, or it would
        // ring on the satellite's non-attenuated alert route instead of the conversational level.
        var alerts = audioStartAlerts.ToArray();
        alerts.ShouldNotBeEmpty();
        alerts.ShouldAllBe(alert => !alert);

        await app.StopAsync(CancellationToken.None);
        satListener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation / disposal */ }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
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