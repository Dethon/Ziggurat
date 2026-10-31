using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Security;
using Mcp.Hosting;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tests.Unit;
using Tests.Unit.McpChannelVoice;

namespace Tests.Integration.Tunnels;

// The household's voice server as far as a timer needs it: its announce, dismiss and satellite API
// over the real registries, one satellite in the kitchen it dials and hears ringing on, and its tunnel
// to the relay — told a hub, a credential and its name, and reachable no other way the test uses.
internal sealed class TunnelledVoiceHub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TcpListener _satellite;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _listening;
    private readonly ConcurrentQueue<string> _heard = new();

    private TunnelledVoiceHub(WebApplication app, TcpListener satellite)
    {
        _app = app;
        _satellite = satellite;
        _listening = ListenAsync(_stopping.Token);
    }

    // How many rings the kitchen's satellite has started playing.
    public int Rings => _heard.Count(type => type == "audio-start");

    public ActiveAlertRegistry Alerts => _app.Services.GetRequiredService<ActiveAlertRegistry>();

    public static async Task<TunnelledVoiceHub> StartAsync(string hub, string credential, string serverName)
    {
        var satellite = new TcpListener(IPAddress.Loopback, 0);
        satellite.Start();
        var settings = new VoiceSettings
        {
            WyomingClient = new() { ReconnectDelaySeconds = 1 },
            // The deployment's own announce secret, which the household's server is never told: it is
            // reached through the relay, which presents the tunnel's credential instead.
            Announce = new() { Enabled = true, Token = "", QueueMaxDepth = 8 },
            Satellites = new()
            {
                ["kitchen-01"] = new()
                {
                    Identity = "household",
                    Room = "Kitchen",
                    WakeWord = "hey_jarvis",
                    Address = $"tcp://127.0.0.1:{((IPEndPoint)satellite.LocalEndpoint).Port}"
                }
            }
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => Audio());
        builder.Services
            .AddSingleton(settings)
            .AddSingleton(settings.Announce)
            .AddSingleton(settings.WyomingClient)
            .AddSingleton(settings.Arbitration)
            .AddSingleton(new SatelliteRegistry(settings.Satellites))
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton<ActiveAlertRegistry>()
            .AddSingleton(Mock.Of<IMetricsPublisher>())
            .AddSingleton(tts.Object)
            .AddSingleton(Mock.Of<ISpeechToText>())
            .AddSingleton<TranscriptDispatcher>(_ => null!)
            .AddSingleton<ReplyTextAccumulator>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(sp => new VoiceConversationManager(
                new FakeTopicMinter(), sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(5),
                NullLogger<VoiceConversationManager>.Instance))
            .AddSingleton<AnnouncementService>()
            .AddSingleton<InsistentAnnouncementController>()
            .AddSingleton<WakeArbiter>()
            .AddSingleton<SilenceGateFactory>()
            .AddHttpClient()
            .AddHostedService<WyomingSatelliteHost>()
            .AddToolServer(new TunnelledServer.Settings(new McpGateSettings
            {
                Tunnel = TunnelledServer.Tunnel(hub, credential, serverName)
            }));
        var app = builder.Build();
        app.MapMcp("/mcp");
        AnnounceEndpoint.Map(app);
        DismissEndpoint.Map(app);
        SatellitesEndpoint.Map(app);
        await app.StartAsync();
        await Eventually.Until(
            () => app.Services.GetRequiredService<SatelliteSessionRegistry>().Get("kitchen-01") is not null,
            "the hub dials its satellite");
        return new TunnelledVoiceHub(app, satellite);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _stopping.CancelAsync();
        _satellite.Stop();
        try
        {
            await _listening;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
        }

        _stopping.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        using var connection = await _satellite.AcceptTcpClientAsync(ct);
        await using var stream = connection.GetStream();
        await foreach (var heard in new WyomingReader(stream).ReadAllAsync(ct))
        {
            _heard.Enqueue(heard.Type);
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        yield return new AudioChunk { Data = new byte[32], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }
}