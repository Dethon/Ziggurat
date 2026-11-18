using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

// Speaks Home Assistant's websocket API at /api/websocket, verified against HA 2026.7:
//   - {"type":"auth_required"} on connect; {"type":"auth","access_token"} in;
//     {"type":"auth_ok"} or {"type":"auth_invalid","message"} (then the socket closes) out,
//   - every command carries an integer id and is answered by one
//     {"id","type":"result","success",...} frame — "result" on success, "error":{"code","message"}
//     otherwise,
//   - the calendar commands mutate the shared FakeCalendarStore, so what the REST calendars
//     endpoint lists afterwards is what this side did,
//   - and, for a client that is handed one address for both halves (Sugary's glucose import), the
//     REST reads `GET /api/states/{id}` and `GET /api/history/period/{start}` from `States` and
//     `History`, behind the same bearer token.
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

    // The entity registry's entries per entity id, as `config/entity_registry/get_entries`
    // answers them: an id the registry knows answers its platform, config entry and capabilities
    // (null for a kind that persists none), an id it does not know answers null for the entry.
    public Dictionary<string, RegistryEntry> Registry { get; } = [];
    public IReadOnlyList<string>? LastRegistryRequest { get; private set; }

    public sealed record RegistryEntry(string Platform, string ConfigEntryId, JsonObject? Capabilities = null);

    // Entity states for `GET /api/states/{id}`: the state and its attributes. An id not here is 404.
    public Dictionary<string, (string State, JsonObject Attributes)> States { get; } = [];

    // The recorder's changes per entity, for `GET /api/history/period/{start}`, answered as Home
    // Assistant answers a minimal, attribute-free read: the state the entity had at the window's start
    // stamped at that start (when it had one), then every change after the start up to `end_time`.
    public Dictionary<string, List<(DateTimeOffset At, string State)>> History { get; } = [];

    // Every history window asked, as (start, end) in the strings the client sent.
    public List<(string Start, string End)> HistoryRequests { get; } = [];

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

        app.MapGet("/api/states/{entityId}", (HttpContext context, string entityId) =>
            !server.Authorized(context) ? Results.Unauthorized()
            : server.States.TryGetValue(entityId, out var state)
                ? Results.Json(new JsonObject
                {
                    ["entity_id"] = entityId,
                    ["state"] = state.State,
                    ["attributes"] = state.Attributes.DeepClone()
                })
                : Results.NotFound());
        app.MapGet("/api/history/period/{start}", (HttpContext context, string start) =>
            !server.Authorized(context) ? Results.Unauthorized() : Results.Json(server.HistoryOf(context, start)));

        await app.StartAsync();
        server = new FakeHomeAssistantSocket(app, $"http://127.0.0.1:{port}", port, token, calendar ?? new FakeCalendarStore());
        return server;
    }

    private bool Authorized(HttpContext context) =>
        context.Request.Headers.Authorization.ToString() == $"Bearer {_token}";

    private JsonArray HistoryOf(HttpContext context, string start)
    {
        var entityId = context.Request.Query["filter_entity_id"].ToString();
        var end = context.Request.Query["end_time"].ToString();
        lock (HistoryRequests)
        {
            HistoryRequests.Add((start, end));
        }

        var from = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        if (!History.TryGetValue(entityId, out var changes))
        {
            return [];
        }

        var ordered = changes.OrderBy(c => c.At).ToList();
        var atStart = ordered.LastOrDefault(c => c.At <= from);
        var series = new JsonArray();
        if (atStart.State is not null)
        {
            series.Add(Point(atStart.State, from));
        }
        foreach (var change in ordered.Where(c => c.At > from && c.At <= to))
        {
            series.Add(Point(change.State, change.At));
        }
        return series.Count == 0 ? [] : [series];

        static JsonObject Point(string state, DateTimeOffset at) => new()
        {
            ["state"] = state,
            ["last_changed"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture)
        };
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
        if (type == "config/entity_registry/get_entries")
        {
            return RegistryEntries(id, request);
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

    private JsonObject RegistryEntries(int id, JsonObject request)
    {
        var ids = request["entity_ids"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        LastRegistryRequest = ids;
        var result = new JsonObject();
        foreach (var entityId in ids)
        {
            result[entityId] = Registry.TryGetValue(entityId, out var entry)
                ? new JsonObject
                {
                    ["entity_id"] = entityId,
                    ["platform"] = entry.Platform,
                    ["config_entry_id"] = entry.ConfigEntryId,
                    ["capabilities"] = entry.Capabilities?.DeepClone()
                }
                : null;
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