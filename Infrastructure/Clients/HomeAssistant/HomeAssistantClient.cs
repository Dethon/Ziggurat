using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.Exceptions;
using JetBrains.Annotations;

namespace Infrastructure.Clients.HomeAssistant;

public class HomeAssistantClient(HttpClient httpClient, string token, TimeSpan? webSocketTimeout = null) : IHomeAssistantClient
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeSpan _webSocketTimeout = webSocketTimeout ?? TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "api/states");
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var raw = await response.Content.ReadFromJsonAsync<HaStateDto[]>(_json, ct)
                  ?? throw new HomeAssistantException("Empty Home Assistant response.");
        return raw.Select(ToEntity).ToList();
    }

    public async Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}");
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureOkAsync(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<HaStateDto>(_json, ct);
        return dto is null ? null : ToEntity(dto);
    }

    public async Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "api/services");
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var domains = await response.Content.ReadFromJsonAsync<HaServiceDomainDto[]>(_json, ct)
                      ?? throw new HomeAssistantException("Empty services payload.");

        return domains
            .SelectMany(d => (d.Services ?? new Dictionary<string, HaServiceDto>())
                .Select(kv => new HaServiceDefinition
                {
                    Domain = d.Domain ?? string.Empty,
                    Service = kv.Key,
                    Description = kv.Value.Description,
                    Fields = (kv.Value.Fields ?? new Dictionary<string, HaServiceFieldDto>())
                        .ToDictionary(f => f.Key, f => new HaServiceField
                        {
                            Description = f.Value.Description,
                            Required = f.Value.Required ?? false,
                            Example = f.Value.Example,
                            Selector = f.Value.Selector?.DeepClone()
                        }),
                    Target = kv.Value.Target?.DeepClone()
                }))
            .ToList();
    }

    public async Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (data is not null)
        {
            foreach (var kvp in data)
            {
                body[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        // HA's REST /api/services/{domain}/{service} treats the request body as `service_data`
        // and validates it against the service schema. `target` is only honored on the WebSocket
        // call_service path; on REST it gets rejected as an unknown key with a 400. Send entity_id
        // flat so the call works for any entity-targeted service.
        if (!string.IsNullOrEmpty(entityId))
        {
            body["entity_id"] = entityId;
        }

        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";

        // First attempt: ask for the service response (HA returns {changed_states, service_response}
        // for services that support it, e.g. roborock.get_maps, weather.get_forecasts, calendar.get_events).
        using var firstResp = await PostJsonAsync(path + "?return_response=true", body, ct);
        if (firstResp.StatusCode == HttpStatusCode.BadRequest)
        {
            var errBody = await firstResp.Content.ReadAsStringAsync(ct);
            // HA returns this exact phrase when return_response is passed against a service
            // whose handler is registered with SupportsResponse.NONE. The handler hasn't run,
            // so retrying without the query is safe (no double-execution).
            if (errBody.Contains("does not support responses", StringComparison.OrdinalIgnoreCase))
            {
                using var retryResp = await PostJsonAsync(path, body, ct);
                await EnsureOkAsync(retryResp, ct);
                return await ParseCallResultAsync(retryResp, ct);
            }
            throw new HomeAssistantException(
                $"Home Assistant returned 400: {(errBody.Length > 200 ? errBody[..200] + "…" : errBody)}", 400);
        }
        await EnsureOkAsync(firstResp, ct);
        return await ParseCallResultAsync(firstResp, ct);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        return await httpClient.SendAsync(request, ct);
    }

    public async Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
    {
        var body = new JsonObject { ["template"] = template };
        using var request = NewRequest(HttpMethod.Post, "api/template");
        request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HaServiceCallResult> ParseCallResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(_json, ct);
        return node switch
        {
            JsonArray arr => new HaServiceCallResult
            {
                ChangedEntities = (arr.Deserialize<HaStateDto[]>(_json) ?? [])
                    .Select(ToEntity).ToList()
            },
            JsonObject obj => new HaServiceCallResult
            {
                ChangedEntities = (obj["changed_states"]?.Deserialize<HaStateDto[]>(_json) ?? [])
                    .Select(ToEntity).ToList(),
                Response = obj["service_response"]?.DeepClone()
            },
            _ => new HaServiceCallResult { ChangedEntities = [] }
        };
    }

    // GET /api/calendars/{entity} is the one read that lists an event's uid — the get_events
    // service's response is filtered to start/end/summary/description/location/status.
    public async Task<IReadOnlyList<HaCalendarEvent>> ListCalendarEventsAsync(
        string entityId, string start, string end, CancellationToken ct = default)
    {
        var path = $"api/calendars/{Uri.EscapeDataString(entityId)}"
                   + $"?start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}";
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var events = await response.Content.ReadFromJsonAsync<HaCalendarEventDto[]>(_json, ct) ?? [];
        return events.Select(ToCalendarEvent).ToList();
    }

    // Creating and deleting are WebSocket commands (`calendar/event/create`, `calendar/event/delete`);
    // the service catalog's create_event takes no recurrence rule and there is no delete service.
    public async Task CreateCalendarEventAsync(string entityId, HaCalendarEventDraft draft, CancellationToken ct = default)
    {
        var @event = new JsonObject
        {
            ["dtstart"] = draft.Start,
            ["dtend"] = draft.End,
            ["summary"] = draft.Summary
        };
        PutIfPresent(@event, "description", draft.Description);
        PutIfPresent(@event, "location", draft.Location);
        PutIfPresent(@event, "rrule", draft.Rrule);

        await SendCommandAsync(new JsonObject
        {
            ["type"] = "calendar/event/create",
            ["entity_id"] = entityId,
            ["event"] = @event
        }, ct);
    }

    public async Task DeleteCalendarEventAsync(
        string entityId, string uid, string? recurrenceId = null, string? recurrenceRange = null,
        CancellationToken ct = default)
    {
        var command = new JsonObject
        {
            ["type"] = "calendar/event/delete",
            ["entity_id"] = entityId,
            ["uid"] = uid
        };
        PutIfPresent(command, "recurrence_id", recurrenceId);
        PutIfPresent(command, "recurrence_range", recurrenceRange);

        await SendCommandAsync(command, ct);
    }

    private static void PutIfPresent(JsonObject node, string name, string? value)
    {
        if (value is not null)
        {
            node[name] = value;
        }
    }

    // One connection per command, closed after it: a calendar edit happens at most once per user
    // request, so a pooled connection would only add reconnect and re-auth state to maintain. The
    // protocol: HA opens with auth_required, takes {type:auth, access_token}, answers auth_ok or
    // auth_invalid, then correlates each command by its id with one {type:result} frame.
    private async Task<JsonNode?> SendCommandAsync(JsonObject command, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_webSocketTimeout);
        var callCt = timeoutCts.Token;
        var uri = WebSocketUri();

        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, callCt);

            var hello = await ReceiveFrameAsync(socket, callCt);
            if (hello?["type"]?.GetValue<string>() != "auth_required")
            {
                throw new HomeAssistantException("Home Assistant's websocket did not ask for authentication.");
            }

            await SendFrameAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = token }, callCt);
            var auth = await ReceiveFrameAsync(socket, callCt);
            if (auth?["type"]?.GetValue<string>() != "auth_ok")
            {
                throw new HomeAssistantUnauthorizedException("Home Assistant rejected the access token (401).");
            }

            command["id"] = 1;
            await SendFrameAsync(socket, command, callCt);

            while (true)
            {
                var frame = await ReceiveFrameAsync(socket, callCt)
                            ?? throw new HomeAssistantException("Home Assistant closed the websocket before answering.");
                if (frame["id"]?.GetValue<int>() != 1 || frame["type"]?.GetValue<string>() != "result")
                {
                    continue;
                }
                if (frame["success"]?.GetValue<bool>() == true)
                {
                    return frame["result"];
                }
                throw CommandFailure(frame["error"]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HomeAssistantException(
                $"Home Assistant did not answer the websocket command within {_webSocketTimeout.TotalSeconds:0}s.", 504);
        }
        catch (WebSocketException ex)
        {
            throw new HomeAssistantException($"Could not reach Home Assistant's websocket at {uri}: {ex.Message}", 503, ex);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The command already produced its answer; a failed goodbye is not the caller's problem.
                }
            }
        }
    }

    // HA's error codes are strings; the two a caller acts on differently keep their HTTP meaning.
    private static HomeAssistantException CommandFailure(JsonNode? error)
    {
        var code = error?["code"]?.GetValue<string>() ?? "unknown_error";
        var message = error?["message"]?.GetValue<string>() ?? "no reason given";
        return code switch
        {
            "not_found" => new HomeAssistantNotFoundException($"Home Assistant returned 404: {message}"),
            "unauthorized" => new HomeAssistantUnauthorizedException("Home Assistant rejected the access token (401)."),
            _ => new HomeAssistantException($"Home Assistant rejected the calendar call ({code}): {message}")
        };
    }

    private Uri WebSocketUri()
    {
        var http = httpClient.BaseAddress
                   ?? throw new HomeAssistantException("The Home Assistant client has no base address.");
        var builder = new UriBuilder(http)
        {
            Scheme = http.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/api/websocket",
            Query = ""
        };
        return builder.Uri;
    }

    private static async Task SendFrameAsync(ClientWebSocket socket, JsonNode payload, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonNode?> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var builder = new StringBuilder();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            if (received.EndOfMessage)
            {
                return JsonNode.Parse(builder.ToString());
            }
        }
    }

    private static HaCalendarEvent ToCalendarEvent(HaCalendarEventDto dto) => new()
    {
        Uid = dto.Uid ?? string.Empty,
        Summary = dto.Summary ?? string.Empty,
        Start = dto.Start?.DateTime ?? dto.Start?.Date ?? string.Empty,
        End = dto.End?.DateTime ?? dto.End?.Date ?? string.Empty,
        AllDay = dto.Start?.DateTime is null && dto.Start?.Date is not null,
        Description = dto.Description,
        Location = dto.Location,
        Rrule = dto.Rrule,
        RecurrenceId = dto.RecurrenceId
    };

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var snippet = await SafeReadAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new HomeAssistantUnauthorizedException(
                "Home Assistant rejected the access token (401)."),
            HttpStatusCode.NotFound => new HomeAssistantNotFoundException(
                $"Home Assistant returned 404: {snippet}"),
            _ => new HomeAssistantException(
                $"Home Assistant returned {(int)response.StatusCode}: {snippet}",
                (int)response.StatusCode)
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 200 ? body[..200] + "…" : body;
        }
        catch
        {
            return "<unreadable body>";
        }
    }

    private static HaEntityState ToEntity(HaStateDto dto) => new()
    {
        EntityId = dto.EntityId ?? string.Empty,
        State = dto.State ?? string.Empty,
        Attributes = dto.Attributes ?? new Dictionary<string, JsonNode?>(),
        LastChanged = dto.LastChanged,
        LastUpdated = dto.LastUpdated
    };

    [PublicAPI]
    private record HaStateDto
    {
        [JsonPropertyName("entity_id")] public string? EntityId { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("attributes")] public Dictionary<string, JsonNode?>? Attributes { get; init; }
        [JsonPropertyName("last_changed")] public DateTimeOffset? LastChanged { get; init; }
        [JsonPropertyName("last_updated")] public DateTimeOffset? LastUpdated { get; init; }
    }

    [PublicAPI]
    private record HaServiceDomainDto
    {
        [JsonPropertyName("domain")] public string? Domain { get; init; }
        [JsonPropertyName("services")] public Dictionary<string, HaServiceDto>? Services { get; init; }
    }

    [PublicAPI]
    private record HaServiceDto
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("fields")] public Dictionary<string, HaServiceFieldDto>? Fields { get; init; }
        [JsonPropertyName("target")] public JsonNode? Target { get; init; }
    }

    [PublicAPI]
    private record HaCalendarEventDto
    {
        [JsonPropertyName("uid")] public string? Uid { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("start")] public HaCalendarInstantDto? Start { get; init; }
        [JsonPropertyName("end")] public HaCalendarInstantDto? End { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("location")] public string? Location { get; init; }
        [JsonPropertyName("rrule")] public string? Rrule { get; init; }
        [JsonPropertyName("recurrence_id")] public string? RecurrenceId { get; init; }
    }

    // The calendars endpoint writes an instant as {"dateTime": "..."} or, all-day, {"date": "..."}.
    [PublicAPI]
    private record HaCalendarInstantDto
    {
        [JsonPropertyName("dateTime")] public string? DateTime { get; init; }
        [JsonPropertyName("date")] public string? Date { get; init; }
    }

    [PublicAPI]
    private record HaServiceFieldDto
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("required")] public bool? Required { get; init; }
        [JsonPropertyName("example")] public JsonNode? Example { get; init; }
        [JsonPropertyName("selector")] public JsonNode? Selector { get; init; }
    }
}