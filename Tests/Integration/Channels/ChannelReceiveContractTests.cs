using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using McpChannelTelegram.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;
// Aliased because Tests.Integration has a sibling namespace named after the channel project
// (Tests.Integration.McpChannelSignalR / .McpChannelVoice), which shadows the project namespace
// from inside Tests.
using TelegramSettings = McpChannelTelegram.Settings;

namespace Tests.Integration.Channels;

public class ChannelReceiveContractTests
{
    // What McpChannelConnection("signalr") derives for itself, spelled out so a test that pins the
    // id cannot drift with the implementation it is pinning.
    private const string SignalRSubscriberId = ChannelProtocol.ChannelClientNamePrefix + "signalr";

    // The channel-capable rows of the one server table. Every row drives that server's REAL
    // registration entry point — the ConfigModule that ships. Hand-registering ChannelReceiveTool
    // and ChannelInbox here instead would stay green against a module that forgot either (a
    // silently dead channel), and would pin the SDK's transport defaults rather than the options
    // each module actually passes to WithHttpTransport.
    //
    // The delivery policy travels with the row, so McpServerRegistrations is the single place where
    // "this is a channel server, and this is the policy it chose" is verified against the real
    // registration. That is the check that would have caught all three rounds of the
    // stale-subscriber defect: every one of them was a server buffering differently from what its
    // caller assumed, and none of them was visible anywhere but inside the emitter.
    private static IReadOnlyList<McpServerRow> Registrations => McpServerRegistrations.ChannelServers;

    public static TheoryData<string, Action<IServiceCollection>> Servers =>
        Registrations.Aggregate(
            new TheoryData<string, Action<IServiceCollection>>(),
            (data, row) =>
            {
                data.Add(row.Id, row.Configure);
                return data;
            });

    public static TheoryData<string, Action<IServiceCollection>, DeliveryPolicy> ServerPolicies =>
        Registrations.Aggregate(
            new TheoryData<string, Action<IServiceCollection>, DeliveryPolicy>(),
            (data, row) =>
            {
                data.Add(row.Id, row.Configure, row.Policy!.Value);
                return data;
            });

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ChannelServer_NegotiatesStatelessProtocolAndDeliversEnqueuedMessages(
        string channelId, Action<IServiceCollection> configureChannel)
    {
        var subscriberId = ChannelProtocol.ChannelClientNamePrefix + channelId;
        var port = TestPort.GetAvailable();

        var app = await StartChannelServerAsync(port, configureChannel);
        var inbox = app.Services.GetRequiredService<ChannelInbox>();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            // The clean break, pinned per server rather than in one place. Stateless = false does
            // not select a mode of the current protocol: it falls back to the legacy initialize
            // handshake and renegotiates all the way down to 2025-11-25, where unsolicited
            // SendNotificationAsync works again — so a future "fix" that sets it on any single
            // channel server would quietly undo this whole migration for that server alone. These
            // two lines are what make that fail loudly.
            client.NegotiatedProtocolVersion.ShouldBe(
                "2026-07-28", $"{channelId} must stay on the stateless protocol");
            client.SessionId.ShouldBeNull($"{channelId} must not mint an Mcp-Session-Id");

            // Register the subscriber, then enqueue while a poll is in flight.
            //
            // Proven here: an item enqueued server-side reaches a client polling over real
            // HTTP + MCP, and survives the ChannelReceiveResult round trip. NOT proven here: the
            // long-poll wake. The delay below is a best-effort guess at "the poll has arrived"; if
            // it hasn't, the enqueue lands first and the poll returns through the pending-items
            // fast path — still green. The wake itself is pinned deterministically on a fake clock
            // by ChannelInboxTests.ReceiveAsync_WhenEmpty_WakesOnEnqueue. Do not read this sleep as
            // the authority on long-poll behaviour.
            await Poll(client, subscriberId, maxWaitMs: 0);

            var pending = Poll(client, subscriberId, maxWaitMs: 10_000);
            await Task.Delay(200);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello"
            }));

            var result = await pending;

            result.Items.Count.ShouldBe(1);
            result.Items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
            result.Items[0].Message!.Content.ShouldBe("hello");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ServerPolicies))]
    public void ChannelServerRegistration_DeclaresItsDeliveryPolicy(
        string channelId, Action<IServiceCollection> configureChannel, DeliveryPolicy expected)
    {
        var services = new ServiceCollection();
        configureChannel(services);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ChannelNotificationEmitter>().Policy
            .ShouldBe(expected, $"{channelId} must register with the {expected} delivery policy");
    }

    // The buffer-always target has to be the id McpChannelConnection derives for itself. If it is
    // not, items pile into a queue nobody ever drains and nothing reports an error — the failure is
    // completely silent. Pinned end to end through Telegram's real registration rather than by
    // comparing two constants, so the match cannot drift on either side.
    [Fact]
    public async Task Telegram_BuffersForTheIdTheAgentConnectionDerives()
    {
        var port = TestPort.GetAvailable();
        var app = await StartChannelServerAsync(
            port,
            services => services.ConfigureChannel(
                new TelegramSettings.ChannelSettings { Bots = [], AllowedUsernames = [] }));
        await using var connection = new McpChannelConnection("telegram");
        try
        {
            // Emitted before anything has ever polled: the cold-start window this policy exists for.
            await app.Services.GetRequiredService<ChannelNotificationEmitter>().EmitAsync(
                new ChannelMessageNotification
                {
                    ConversationId = "42:42",
                    Sender = "user",
                    Content = "buffered before the agent arrived"
                });

            await connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.Content.ShouldBe("buffered before the agent arrived");
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_SurfacesEnqueuedMessagesOnItsStream()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<McpChannelReceiveTool>());
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None);

            await Task.Delay(300);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello from the inbox",
                AgentId = "nabu"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.ConversationId.ShouldBe("conv-1");
            received.Content.ShouldBe("hello from the inbox");
            received.ChannelId.ShouldBe("signalr");
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_AfterReconnect_KeepsPumping()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<McpChannelReceiveTool>());
        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            // The reconnect stops the pump and starts a fresh one on the new client; the stream
            // must go on carrying items rather than going quiet.
            await connection.ReconnectAsync(endpoint, CancellationToken.None);

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-2",
                Sender = "user",
                Content = "buffered"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.Content.ShouldBe("buffered");
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_WithAStableSubscriberId_DrainsWhatBufferedWhileItWasDown()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<McpChannelReceiveTool>());
        var endpoint = $"http://localhost:{port}/mcp";
        var restarted = new McpChannelConnection("signalr");
        var original = new McpChannelConnection("signalr");
        try
        {
            // One agent process connects — registering its subscriber — and then goes away.
            await original.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);
            await original.DisposeAsync();

            // Barrier, not a fixture: retires the waiter the departed pump left behind, so the
            // item below cannot be handed to a poll whose caller is already gone. (That handover
            // loses the batch — see the delivery-gap note in the task report.)
            (await inbox.ReceiveAsync(SignalRSubscriberId, TimeSpan.Zero, CancellationToken.None))
                .ShouldBeEmpty();

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-2",
                Sender = "user",
                Content = "buffered"
            }));

            // A brand-new connection derives the same channel-signalr id from its channel id, so it
            // inherits the queue. An id minted per connect would orphan it and lose precisely the
            // messages that arrived while the agent was restarting.
            await restarted.ConnectAsync(endpoint, CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await restarted.Messages.FirstAsync(cts.Token);

            received.Content.ShouldBe("buffered");
        }
        finally
        {
            await original.DisposeAsync();
            await restarted.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_WhenServerIsDown_DisposesWithoutHanging()
    {
        // No server listening on this port at all.
        var port = TestPort.GetAvailable();
        await using var connection = new McpChannelConnection("signalr");

        // ConnectAsync throws, so no pump was ever started; dispose must not wait on one.
        await Should.ThrowAsync<Exception>(
            () => connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }

    [Fact]
    public async Task McpChannelConnection_ConnectingTwice_LeavesOnlyOnePumpPolling()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();
        var posts = 0;

        var app = await StartServerAsync(
            port,
            inbox,
            b => b.WithTools<McpChannelReceiveTool>(),
            web => web.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    Interlocked.Increment(ref posts);
                }

                await next(context);
            }));

        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            // A second connect must retire the first pump. Two pumps share one subscriberId and
            // displace each other's waiter, which ChannelInbox retires with an *instant* empty
            // batch — indistinguishable from a timeout, so both re-poll at once and peg a core.
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            Interlocked.Exchange(ref posts, 0);
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // One pump holding one long poll open: the server should see nothing else.
            Volatile.Read(ref posts).ShouldBeLessThanOrEqualTo(3);
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // Two agent processes pointed at one channel server derive the same subscriber id, and the
    // inbox keeps one waiter per id: each poll displaces the other process's, so every message
    // reaches exactly one of them, non-deterministically — the dev/prod contention failure shape.
    // A single displaced poll is routine (our own reconnect does it); a *run* of them is the
    // signature of a second poller, and it must surface as a Warning, not stay buried at Debug.
    [Fact]
    public async Task McpChannelConnection_WhenASecondPollerSharesItsSubscriberId_WarnsAboutTheSplitStream()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var app = await StartServerAsync(port, inbox, b => b.WithTools<McpChannelReceiveTool>());
        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection(
            "signalr", logger: new CapturingConnectionLogger(warnings));
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);

            await using var rival = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }));

            using var contention = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var rivalLoop = Task.Run(async () =>
            {
                // Mutual displacement keeps every poll short, so this loop spins fast; it stops
                // as soon as the warning has been observed (or the deadline hits).
                while (!contention.IsCancellationRequested)
                {
                    try
                    {
                        await Poll(rival, SignalRSubscriberId, maxWaitMs: 30_000);
                    }
                    catch
                    {
                        return;
                    }
                }
            });

            while (warnings.IsEmpty && !contention.IsCancellationRequested)
            {
                await Task.Delay(100);
            }

            await contention.CancelAsync();
            await rivalLoop.WaitAsync(TimeSpan.FromSeconds(35));

            warnings.ShouldContain(w => w.Contains("splitting the stream"));
        }
        finally
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AHeldLongPoll_DoesNotBlockOtherToolCallsOnTheSameClient()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var app = await StartServerAsync(
            port,
            inbox,
            b => b.WithTools<McpChannelReceiveTool>().WithTools<ImmediateTool>());
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            var held = Poll(client, SignalRSubscriberId, maxWaitMs: 10_000);
            await Task.Delay(200);

            var startedAt = Stopwatch.GetTimestamp();
            foreach (var _ in Enumerable.Range(0, 5))
            {
                var pong = await client.CallToolAsync("ping");
                pong.Content.OfType<TextContentBlock>().First().Text.ShouldBe("pong");
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            // The pump holds a 30 s channel_receive open on the same McpClient that send_reply
            // streams over. If the SDK ever serialized concurrent tools/call, every reply chunk
            // would queue behind that poll — invisible to every other test, catastrophic for voice.
            held.IsCompleted.ShouldBeFalse("the poll must still be held while the pings run");
            elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-3",
                Sender = "user",
                Content = "release"
            }));

            (await held).Items.Count.ShouldBe(1);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // Boots a channel server from its own ConfigModule, so the MCP registration under test — tools,
    // transport options and all — is byte-for-byte what ships.
    private static async Task<WebApplication> StartChannelServerAsync(
        int port, Action<IServiceCollection> configureChannel)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

        var hostServices = builder.Services.Count;
        configureChannel(builder.Services);

        // Only the workers the module itself added: a Telegram poller, a Service Bus processor, a
        // Wyoming dialer — all reaching for infrastructure that is not here. The web host's own
        // hosted service was registered before this point and must survive, or Kestrel never
        // listens.
        var moduleWorkers = builder.Services
            .Skip(hostServices)
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToArray();
        foreach (var worker in moduleWorkers)
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartServerAsync(
        int port,
        ChannelInbox inbox,
        Action<IMcpServerBuilder> registerTool,
        Action<WebApplication>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        registerTool(builder.Services.AddMcpServer().WithHttpTransport());

        var app = builder.Build();
        configure?.Invoke(app);
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    private static async Task<ChannelReceiveResult> Poll(McpClient client, string subscriberId, int maxWaitMs)
    {
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?>
            {
                ["subscriberId"] = subscriberId,
                ["maxWaitMs"] = maxWaitMs
            });

        var text = call.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions)!;
    }
}

// Captures the pump's warnings for the contention pin; thread-safe because the pump logs from
// its own loop while the test thread polls the queue.
public sealed class CapturingConnectionLogger(
    System.Collections.Concurrent.ConcurrentQueue<string> warnings) : ILogger<McpChannelConnection>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            warnings.Enqueue(formatter(state, exception));
        }
    }
}

// A second tool for the concurrency pin: something that answers instantly while a long poll is
// held open on the same client.
[McpServerToolType]
public sealed class ImmediateTool
{
    [McpServerTool(Name = "ping")]
    [Description("Test tool that answers immediately.")]
    public static string McpRun() => "pong";
}