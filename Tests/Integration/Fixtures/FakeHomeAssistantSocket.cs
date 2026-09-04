using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

// Speaks Home Assistant's websocket API at /api/websocket, verified against HA 2026.7:
//   - {"type":"auth_required"} on connect; {"type":"auth","access_token"} in;
//     {"type":"auth_ok"} or {"type":"auth_invalid","message"} (then the socket closes) out,
//   - every command carries an integer id and is answered by one
//     {"id","type":"result","success",...} frame — "result" on success, "error":{"code","message"}
//     otherwise,
//   - the calendar commands mutate the shared FakeCalendarStore, so what the REST calendars
//     endpoint lists afterwards is what this side did.
// `Recorder`, when set, hears every calendar mutation as (service, entity, payload) — the eval's
// fake home records them beside its REST calls.
public sealed class FakeHomeAssistantSocket : IAsyncDisposable
{
    public const string ValidToken = "test-token";

    private readonly IHost _host;
    private readonly int _port;
    private readonly string _token;

    public string BaseUrl { get; }
    public FakeCalendarStore Calendar { get; }
    public int AuthCount { get; private set; }
    public Action<string, string, JsonObject>? Recorder { get; set; }

    // Entities the fake refuses to touch, answering not_found the way HA does for an unknown id.
    public HashSet<string> KnownCalendars { get; } = [];

    // The recorder's compiled rows per statistic id, in the shape the command answers them; an id
    // with none is absent from the result, as it is in Home Assistant. The window is ignored.
    public Dictionary<string, JsonArray> Statistics { get; } = [];
    public StatisticsRequest? LastStatisticsRequest { get; private set; }

    public sealed record StatisticsRequest(IReadOnlyList<string> StatisticIds, string Start, string End, string Period);

    private FakeHomeAssistantSocket(IHost host, string baseUrl, int port, string token, FakeCalendarStore calendar)
    {
        _host = host;
        BaseUrl = baseUrl;
        _port = port;
        _token = token;
        Calendar = calendar;
    }

    public static async Task<FakeHomeAssistantSocket> StartAsync(
        FakeCalendarStore? calendar = null, string token = ValidToken)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        FakeHomeAssistantSocket server = null!;
        app.UseWebSockets();
        app.Map("/api/websocket", async context =>
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
        server = new FakeHomeAssistantSocket(app, $"http://127.0.0.1:{port}", port, token, calendar ?? new FakeCalendarStore());
        return server;
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        await SendAsync(socket, new JsonObject { ["type"] = "auth_required", ["ha_version"] = "2026.7.3" }, ct);

        var buffer = new byte[64 * 1024];
        var first = await ReceiveAsync(socket, buffer, ct);
        if (first is null)
        {
            return;
        }

        AuthCount++;
        var auth = JsonNode.Parse(first)!;
        if (auth["type"]?.GetValue<string>() != "auth" || auth["access_token"]?.GetValue<string>() != _token)
        {
            await SendAsync(socket, new JsonObject
            {
                ["type"] = "auth_invalid",
                ["message"] = "Invalid access token or password"
            }, ct);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth_invalid", CancellationToken.None);
            return;
        }
        await SendAsync(socket, new JsonObject { ["type"] = "auth_ok", ["ha_version"] = "2026.7.3" }, ct);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var received = await ReceiveAsync(socket, buffer, ct);
            if (received is null)
            {
                return;
            }

            var request = JsonNode.Parse(received)!.AsObject();
            var id = request["id"]!.GetValue<int>();
            await SendAsync(socket, Handle(id, request), ct);
        }
    }

    private JsonObject Handle(int id, JsonObject request)
    {
        var type = request["type"]?.GetValue<string>() ?? "";
        var entityId = request["entity_id"]?.GetValue<string>() ?? "";

        if (type == "recorder/statistics_during_period")
        {
            return StatisticsDuringPeriod(id, request);
        }
        if (type is not ("calendar/event/create" or "calendar/event/delete"))
        {
            return Error(id, "unknown_command", $"Unknown command: {type}");
        }
        if (KnownCalendars.Count > 0 && !KnownCalendars.Contains(entityId))
        {
            return Error(id, "not_found", "Entity not found");
        }

        switch (type)
        {
            case "calendar/event/create":
                var @event = request["event"]!.AsObject();
                Calendar.Create(entityId, @event);
                Recorder?.Invoke("create_event", entityId, @event.DeepClone().AsObject());
                return Result(id);

            default:
                var uid = request["uid"]!.GetValue<string>();
                if (!Calendar.Delete(entityId, uid))
                {
                    return Error(id, "failed", $"Event {uid} not found");
                }
                var payload = new JsonObject { ["uid"] = uid };
                if (request["recurrence_id"] is { } recurrenceId)
                {
                    payload["recurrence_id"] = recurrenceId.DeepClone();
                }
                if (request["recurrence_range"] is { } range)
                {
                    payload["recurrence_range"] = range.DeepClone();
                }
                Recorder?.Invoke("delete_event", entityId, payload);
                return Result(id);
        }
    }

    private JsonObject StatisticsDuringPeriod(int id, JsonObject request)
    {
        var start = request["start_time"]?.GetValue<string>() ?? "";
        var end = request["end_time"]?.GetValue<string>() ?? "";
        var ids = request["statistic_ids"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        LastStatisticsRequest = new StatisticsRequest(ids, start, end, request["period"]?.GetValue<string>() ?? "");

        // Home Assistant answers a start it cannot parse with this code; the client surfaces it.
        if (!DateTimeOffset.TryParse(start, out _))
        {
            return Error(id, "invalid_start_time", "Invalid start_time");
        }

        var result = new JsonObject();
        foreach (var statisticId in ids.Where(Statistics.ContainsKey))
        {
            result[statisticId] = Statistics[statisticId].DeepClone();
        }
        var frame = Result(id);
        frame["result"] = result;
        return frame;
    }

    private static JsonObject Result(int id) =>
        new() { ["id"] = id, ["type"] = "result", ["success"] = true, ["result"] = null };

    private static JsonObject Error(int id, string code, string message) =>
        new()
        {
            ["id"] = id,
            ["type"] = "result",
            ["success"] = false,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };

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