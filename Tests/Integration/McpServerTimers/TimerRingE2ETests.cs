using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Voice;
using Infrastructure.Timers;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using McpServerTimers.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpServerTimers;

// End-to-end proof of the extracted architecture: the timers half (store + TimerFileSystem +
// TimerFireService) runs in-process against the HTTP adapters, and the voice hub runs on loopback
// Kestrel exposing announce/dismiss/satellites. Arming a timer resolves its target over HTTP, firing
// rings the satellite over HTTP, and both wake-dismiss and remote dismiss.sh cross the boundary.
public class TimerRingE2ETests
{
    // TimerFireService's own poll cadence. Named so an advance moves the clock by exactly the span
    // the loop waits on rather than a multiple guessed at the call site.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ArmedTimer_FiresRingsAndDismissesAcrossTheHubBoundary()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var satListener = new TcpListener(IPAddress.Loopback, 0);
        satListener.Start();
        var satPort = ((IPEndPoint)satListener.LocalEndpoint).Port;

        var audioStarts = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await satListener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "audio-start")
                {
                    audioStarts.Enqueue(DateTimeOffset.UtcNow);
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

        // ---- The voice hub: rings satellites, holds the ring state, resolves targets ----
        var apiPort = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, apiPort));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Announce);
        builder.Services.AddSingleton(settings.WyomingClient);
        builder.Services.AddSingleton(new SatelliteRegistry(settings.Satellites));
        builder.Services.AddSingleton<SatelliteSessionRegistry>();
        builder.Services.AddSingleton<ActiveAlertRegistry>();
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
            Mock.Of<IConversationFactory>(), sp.GetRequiredService<ReplyTextAccumulator>(),
            sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance));
        builder.Services.AddSingleton<AnnouncementService>();
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
        DismissEndpoint.Map(app);
        SatellitesEndpoint.Map(app);
        await app.StartAsync(ct);

        var sessions = app.Services.GetRequiredService<SatelliteSessionRegistry>();
        await WaitForAsync(() => sessions.Get("kitchen-01") is not null, TimeSpan.FromSeconds(5));

        // ---- The timers server half: store + VFS + fire loop, reaching the hub only over HTTP ----
        // This half owns the only two spans that made the test slow and racy: a timer's duration
        // and the fire loop's one-second poll. Both are taken off an injected clock the test drives,
        // so the timer comes due because this test said so rather than because four real seconds
        // happened to fit inside a ten-second cap. The hub half above keeps the system clock — its
        // waits are Kestrel's and the satellite socket's, which are not this test's to fake.
        var timers = new ArmedClock();
        using var hubClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        var hubClientFactory = new FixedClientFactory(hubClient);
        var store = new InMemoryTimerStore();
        var fs = new TimerFileSystem(
            store, timers,
            new HttpAlertDismisser(hubClientFactory, "secret"),
            new HttpSatelliteCatalog(hubClientFactory, "secret"));
        var fireLoop = new TimerFireService(
            store, new HttpInsistentAnnouncer(hubClientFactory, "secret"), timers,
            NullLogger<TimerFireService>.Instance);
        await fireLoop.StartAsync(ct);

        // Arm through the VFS — CreateAsync resolves {room: Kitchen} against the hub's /satellites.
        var created = await fs.CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "text": "pasta is ready", "target": {"room": "Kitchen"}}""",
            false, true, ct);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        // Fires on the poll tick that first sees it due, and rings on the satellite via POST
        // /api/voice/announce. The clock is driven forward until that ring lands; it still crosses
        // a real socket, so what is waited for is the arrival rather than a span.
        await AdvanceUntilAsync(timers, () => !audioStarts.IsEmpty, "the first timer to ring");

        // Wake on the satellite dismisses it (hub-local acknowledgment).
        var dismissed = app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01");
        dismissed.ShouldHaveSingleItem();
        dismissed[0].Text.ShouldBe("pasta is ready");
        dismissed[0].Kind.ShouldBe(AnnounceKind.Timer);

        // A second timer, silenced remotely through the VFS: exec dismiss.sh -> POST /api/voice/dismiss.
        var startsBefore = audioStarts.Count;
        var created2 = await fs.CreateAsync("/tea/timer.json",
            """{"durationSeconds": 2, "text": "tea is ready", "target": {"room": "Kitchen"}}""",
            false, true, ct);
        created2.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        await AdvanceUntilAsync(
            timers, () => audioStarts.Count > startsBefore, "the second timer to ring");

        var exec = (await fs.ExecAsync("/", "dismiss.sh", null, ct))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain("timer \"tea is ready\"");
        app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01").ShouldBeEmpty();

        await fireLoop.StopAsync(CancellationToken.None);
        await app.StopAsync(CancellationToken.None);
        satListener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation */ }
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    // The fire loop parks on a one-second PeriodicTimer between passes, and a PeriodicTimer arms
    // once and reuses that timer rather than arming a fresh one per tick. So "a one-second wait is
    // outstanding" is true almost continuously here and cannot, on its own, tell a loop waiting for
    // the next tick from one still working through the last — an advance landing in that gap is
    // consumed without the timer this test wants ever coming due, and the wait after it then spends
    // its whole cap. Driving the clock until the effect appears is the honest shape for a loop whose
    // tick the test cannot observe individually: each pass moves time on by a whole poll interval,
    // so a due timer is found within one of them, and it returns the moment the ring lands.
    private static async Task AdvanceUntilAsync(ArmedClock clock, Func<bool> fired, string because)
    {
        await clock.WaitForLiveAsync(_pollInterval);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (fired())
            {
                return;
            }

            clock.Advance(_pollInterval);
            await Task.Delay(20);
        }

        fired().ShouldBeTrue($"waited 10s for {because}");
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