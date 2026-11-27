using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tests.Integration.Fixtures;

// The model, faked at its provider's address, so no test but the canaries spends money: every model
// request the agent's own OpenRouter client sends is kept as it arrived — its body, and the key it was
// sent with — and answered on the Responses wire by FakeModelReplies, a pure function of the request,
// or by what a test scripted for its own fake. Jev's decisions are answered unavailable, which every
// use of a judgment already survives; the model list and the key check answer as OpenRouter's do. Any
// other route is refused and kept in Unhandled, so a new call into the provider fails visibly rather
// than reaching a guess.
public sealed class FakeOpenRouter : IAsyncDisposable
{
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<string> _unhandled = new();
    private readonly ConcurrentQueue<Func<JsonObject, string>> _scripted = new();
    private volatile int _refusing;
    private volatile string _refusal = "";
    private WebApplication _app = null!;
    private int _port;

    public string ApiUrl => ApiUrlOn("127.0.0.1");

    // As a container reaches this process: through the host's gateway, by the name it is given.
    public string ApiUrlOn(string host) => $"{RootOn(host)}v1/";

    // Jev's base: OpenRouter's API root, under which both the decisions and v1 live.
    public string RootOn(string host) => $"http://{host}:{_port}/api/";

    public IReadOnlyList<Request> Requests => [.. _requests];

    public IReadOnlyList<string> Unhandled => [.. _unhandled];

    // Answers the next model requests with these, in order, and every one after them by the rules.
    // For a test that owns its fake; a fake shared between tests answers by the rules alone.
    public void Script(params string[] replies)
    {
        foreach (var reply in replies)
        {
            _scripted.Enqueue(_ => reply);
        }
    }

    public void Script(params Func<JsonObject, string>[] replies)
    {
        foreach (var reply in replies)
        {
            _scripted.Enqueue(reply);
        }
    }

    // A reply that calls one tool with these arguments (a JSON object), for a turn that has to reach a
    // tool before it ends.
    public static string CallingTool(string tool, string arguments) =>
        FakeResponsesWire.Stream(new FakeModelReplies.Reply(null, new FakeModelReplies.ToolCall(tool, arguments)));

    // The same, naming the tool as the request offered it: the agent namespaces a server's tools, so a
    // test names the tool by its own name and the fake finds it among the request's.
    public static Func<JsonObject, string> CallingToolNamed(string tool, string arguments) => request =>
        CallingTool(FakeModelReplies.ToolNamed(request, tool)
                    ?? throw new InvalidOperationException($"The request offered no tool named {tool}"), arguments);

    // A reply calling several tools at once, each named as the request offered it.
    public static Func<JsonObject, string> CallingToolsNamed(params (string Tool, string Arguments)[] calls) => request =>
        FakeResponsesWire.Stream(new FakeModelReplies.Reply(null, new FakeModelReplies.ToolCalls(
        [
            .. calls.Select(call => new FakeModelReplies.ToolCall(
                FakeModelReplies.ToolNamed(request, call.Tool)
                ?? throw new InvalidOperationException($"The request offered no tool named {call.Tool}"), call.Arguments))
        ])));

    public static string Saying(string text) =>
        FakeResponsesWire.Stream(new FakeModelReplies.Reply(null, new FakeModelReplies.Text([text], TimeSpan.Zero)));

    // Answers every request from now on with this status and OpenRouter's error body, as it answers a
    // key it does not take (401), one out of credit (402), or a request a provider will not take (400:
    // a strict schema it does not support), saying why in the message.
    public void Refuse(HttpStatusCode status, string message = "No auth credentials found")
    {
        _refusing = (int)status;
        _refusal = message;
    }

    // On every address when a container asks it, which reaches this process through the host's gateway.
    public static async Task<FakeOpenRouter> StartAsync(bool forContainers = false)
    {
        var fake = new FakeOpenRouter();
        (fake._port, fake._app) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(forContainers ? IPAddress.Any : IPAddress.Loopback, port));
            var app = builder.Build();

            app.MapPost("/api/v1/responses", (HttpContext http, CancellationToken ct) => fake.AnswerAsync(http, ct));
            app.MapPost("/api/alpha/decisions", (HttpContext http, CancellationToken _) =>
                Results.Json(new { error = new { message = "Jev is not faked", code = 503 } }, statusCode: 503));
            app.MapGet("/api/v1/models", () => Results.Json(new { data = Array.Empty<object>() }));
            app.MapGet("/api/v1/key", () => Results.Json(new { data = new { label = "sk-or-v1-fake", usage = 0 } }));
            app.MapFallback((HttpContext http) =>
            {
                fake._unhandled.Enqueue($"{http.Request.Method} {http.Request.Path}");
                return Results.NotFound();
            });
            return app;
        });
        return fake;
    }

    private async Task AnswerAsync(HttpContext http, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = JsonNode.Parse(await reader.ReadToEndAsync(ct))!.AsObject();
        _requests.Enqueue(new Request(http.Request.Path, http.Request.Headers.Authorization.ToString(), body));
        if (_refusing != 0)
        {
            http.Response.StatusCode = _refusing;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(
                new JsonObject { ["error"] = new JsonObject { ["message"] = _refusal, ["code"] = _refusing } }.ToJsonString(),
                Encoding.UTF8, ct);
            return;
        }

        if (_scripted.TryDequeue(out var scripted))
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync(scripted(body), Encoding.UTF8, ct);
            return;
        }

        var reply = FakeModelReplies.For(body);
        if (body["stream"]?.GetValue<bool>() is not true)
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(FakeResponsesWire.Whole(reply).ToJsonString(), Encoding.UTF8, ct);
            return;
        }

        // Written event by event, so a slow reply is seen growing and a stop cuts it where it got.
        http.Response.ContentType = "text/event-stream";
        await foreach (var chunk in FakeResponsesWire.StreamAsync(reply, ct))
        {
            await http.Response.WriteAsync(chunk, Encoding.UTF8, ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        TestPort.Release(_port);
    }

    public sealed record Request(string Path, string Authorization, JsonObject Body)
    {
        // The system prompt the turn went out with: the Responses wire's instructions, or a system
        // item at the head of its input.
        public string? SystemPrompt =>
            Body["instructions"]?.GetValue<string>()
            ?? Body["input"]?.AsArray()
                .FirstOrDefault(item => item?["role"]?.GetValue<string>() is "system" or "developer")?["content"] switch
            {
                JsonValue text => text.GetValue<string>(),
                JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>())),
                _ => null
            };

        // What the tools called in this conversation answered, as the model was sent it: where a test
        // once asked a real model to repeat what it read, it reads what the model was given to read.
        public IReadOnlyList<string> ToolOutputs =>
        [
            .. (Body["input"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "function_call_output")
                .Select(item => item["output"] switch
                {
                    JsonValue text => text.GetValue<string>(),
                    JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>())),
                    var other => other?.ToJsonString() ?? ""
                })
        ];

        public IReadOnlyList<string> ToolNames =>
        [
            .. (Body["tools"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(tool => tool["name"]?.GetValue<string>())
                .OfType<string>()
        ];
    }
}