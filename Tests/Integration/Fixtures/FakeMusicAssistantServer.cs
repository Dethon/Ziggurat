using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

// Speaks the real Music Assistant websocket protocol, verified against MA server 2.9.9:
//   - an unsolicited server-info frame on connect,
//   - {"command","message_id","args"} in,
//   - {"message_id","partial",result"} out, where partial:true means more frames follow,
//   - {"message_id","error_code","details"} for failures,
//   - every command except `auth` is rejected until authentication succeeds.
public sealed class FakeMusicAssistantServer : IAsyncDisposable
{
    public const string ValidToken = "test-token";
    public const string ShowUri = "spotify--w2nq2jMe://podcast/5dbvpKwtqz3X3hcX1BSEzf";

    private readonly IHost _host;
    private readonly int _port;

    public string BaseUrl { get; }

    // Split the episode result across this many frames (1 = single frame, as MA does for 294 items).
    public int Chunks { get; set; } = 1;
    public int AuthCount { get; private set; }

    private FakeMusicAssistantServer(IHost host, string baseUrl, int port)
    {
        _host = host;
        BaseUrl = baseUrl;
        _port = port;
    }

    public static async Task<FakeMusicAssistantServer> StartAsync()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        FakeMusicAssistantServer server = null!;
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await server.ServeAsync(socket, context.RequestAborted);
        });

        await app.StartAsync();
        server = new FakeMusicAssistantServer(app, $"http://127.0.0.1:{port}", port);
        return server;
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        await SendAsync(socket, new JsonObject
        {
            ["server_id"] = "fake",
            ["server_version"] = "2.9.9",
            ["schema_version"] = 31
        }, ct);

        var authenticated = false;
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var received = await ReceiveAsync(socket, buffer, ct);
            if (received is null)
            {
                return;
            }

            var request = JsonNode.Parse(received)!;
            var command = request["command"]!.GetValue<string>();
            var id = request["message_id"]!.GetValue<string>();
            var args = request["args"] as JsonObject ?? [];

            if (command == "auth")
            {
                AuthCount++;
                authenticated = args["token"]?.GetValue<string>() == ValidToken;
                await SendAsync(socket, authenticated
                    ? Result(id, new JsonObject { ["authenticated"] = true })
                    : Error(id, 20, "Invalid token"), ct);
                continue;
            }

            if (!authenticated)
            {
                await SendAsync(socket, Error(id, 20, "Authentication required. Please send auth command first."), ct);
                continue;
            }

            await HandleCommandAsync(socket, command, id, args, ct);
        }
    }

    private async Task HandleCommandAsync(
        WebSocket socket, string command, string id, JsonObject args, CancellationToken ct)
    {
        switch (command)
        {
            case "music/podcasts/podcast_episodes":
                if (args["item_id"]?.GetValue<string>() != "5dbvpKwtqz3X3hcX1BSEzf")
                {
                    await SendAsync(socket, Error(id, 2, $"shows/{args["item_id"]} not found"), ct);
                    return;
                }
                await SendChunkedAsync(socket, id, Episodes(), ct);
                return;

            case "music/search":
                await SendAsync(socket, Result(id, new JsonObject
                {
                    ["artists"] = new JsonArray(),
                    ["podcasts"] = new JsonArray(
                        Item("No es el fin del mundo", ShowUri),
                        Item("El Orden Mundial", "spotify--w2nq2jMe://podcast/1wsNhdPRTo47jppKnKCk3E"))
                }), ct);
                return;

            // The queue's elapsed_time is the real playback position: Home Assistant's own
            // media_position is refreshed only by a state transition, so it goes stale the moment
            // playback settles. An unknown queue answers null, exactly as the real server does.
            case "player_queues/get":
                await SendAsync(socket, Result(id,
                    args["queue_id"]?.GetValue<string>() == QueueId
                        ? new JsonObject
                        {
                            ["queue_id"] = QueueId,
                            ["state"] = "playing",
                            ["elapsed_time"] = QueueElapsedTime,
                            ["elapsed_time_last_updated"] = 1787605526.343
                        }
                        : null), ct);
                return;

            default:
                await SendAsync(socket, Error(id, 1, $"Unknown command: {command}"), ct);
                return;
        }
    }

    // Kept equal to the eval fixture's queue id and true position: the two fakes describe one
    // imagined home, and a scenario that seeks by the queue's number needs them to agree.
    public const string QueueId = "ma_kitchen";

    public const double QueueElapsedTime = 4200;

    private static JsonArray Episodes() => new(
        Item("292. La guerra por el agua: el recurso imprescindible", "spotify--w2nq2jMe://podcast_episode/5V4Bf", 5279),
        Item("291. La geopolítica de la cerámica", "spotify--w2nq2jMe://podcast_episode/3bjld", 4100),
        Item("280. Palantir: el control tecnológico de la defensa, con Marta Peirano",
            "spotify--w2nq2jMe://podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN", 7276));

    private async Task SendChunkedAsync(WebSocket socket, string id, JsonArray all, CancellationToken ct)
    {
        var items = all.Select(x => x!.DeepClone()).ToList();
        var perChunk = (int)Math.Ceiling(items.Count / (double)Chunks);
        for (var offset = 0; offset < items.Count; offset += perChunk)
        {
            var slice = new JsonArray(items.Skip(offset).Take(perChunk).ToArray());
            var last = offset + perChunk >= items.Count;
            await SendAsync(socket, new JsonObject
            {
                ["message_id"] = id,
                ["partial"] = !last,
                ["result"] = slice
            }, ct);
        }
    }

    private static JsonNode Item(string name, string uri, double? duration = null)
    {
        var node = new JsonObject { ["name"] = name, ["uri"] = uri, ["media_type"] = "podcast_episode" };
        if (duration is not null)
        {
            node["duration"] = duration;
        }
        return node;
    }

    private static JsonObject Result(string id, JsonNode result) =>
        new() { ["message_id"] = id, ["partial"] = false, ["result"] = result };

    private static JsonObject Error(string id, int code, string details) =>
        new() { ["message_id"] = id, ["error_code"] = code, ["details"] = details };

    private static async Task SendAsync(WebSocket socket, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var builder = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        TestPort.Release(_port);
    }
}