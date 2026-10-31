using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Tests.Integration.Fixtures;

// The model, faked at its provider's address: every request the agent's own OpenRouter client sends
// is kept as it arrived — its body, and the key it was sent with — and answered with one short
// streamed reply, so a turn built by the real factory runs to its end without a model.
public sealed class FakeOpenRouter : IAsyncDisposable
{
    // The Responses wire's events for a reply of "ok", which is what the agent's client speaks.
    private const string Reply =
        """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress","store":false}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","item":{"content":[],"id":"msg_1","role":"assistant","status":"in_progress","type":"message"}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"ok"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","item":{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","store":false,"model":"test-model","output":[{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}

        data: [DONE]


        """;

    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<string> _scripted = new();
    private WebApplication _app = null!;
    private int _port;

    public string ApiUrl => $"http://127.0.0.1:{_port}/api/v1/";

    public IReadOnlyList<Request> Requests => [.. _requests];

    // Answers the next requests with these, in order, and every one after them with the short reply.
    public void Script(params string[] replies)
    {
        foreach (var reply in replies)
        {
            _scripted.Enqueue(reply);
        }
    }

    // A reply that calls one tool with these arguments (a JSON object), for a turn that has to reach a
    // tool before it ends.
    public static string CallingTool(string tool, string arguments)
    {
        var escaped = JsonValue.Create(arguments).ToJsonString();
        return $$$$"""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_call","object":"response","status":"in_progress","store":false}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"{{{{tool}}}}","arguments":"","status":"in_progress"}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"{{{{tool}}}}","arguments":{{{{escaped}}}},"status":"completed"}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_call","object":"response","status":"completed","store":false,"model":"test-model","output":[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"{{{{tool}}}}","arguments":{{{{escaped}}}},"status":"completed"}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}

            data: [DONE]


            """;
    }

    public static async Task<FakeOpenRouter> StartAsync()
    {
        var fake = new FakeOpenRouter { _port = TestPort.GetAvailable() };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, fake._port));
        fake._app = builder.Build();
        fake._app.MapPost("/{**path}", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            fake._requests.Enqueue(new Request(http.Request.Path, http.Request.Headers.Authorization.ToString(), body));
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync(fake._scripted.TryDequeue(out var scripted) ? scripted : Reply, Encoding.UTF8);
        });
        await fake._app.StartAsync();
        return fake;
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
    }
}