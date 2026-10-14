using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A channel that parses the reply asks for its shape on the message, and the request that leaves
// for OpenRouter is where that has to be true: a real agent over a real chat client, the body read
// off the wire. An assertion on the turn's options alone would pass while the wire stayed wrong.
public sealed class OpenRouterChatClientResponseFormatTests
{
    // Nested object, enum and numeric bounds: the keywords a meal estimate leans on, so a rewrite
    // between the channel and the wire that dropped any of them shows here.
    private static readonly JsonElement _schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "confidence": { "type": "string", "enum": ["low", "medium", "high"] },
                  "carbs": { "type": "number", "minimum": 0, "maximum": 500 }
                },
                "required": ["name", "confidence", "carbs"],
                "additionalProperties": false
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """);

    private static readonly ResponseFormat _format = new() { Name = "meal_estimate", Schema = _schema };

    [Fact]
    public async Task AMessageCarryingAFormat_SendsItAsAStrictJsonSchema()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire);

        await agent.RunStreamingAsync([UserMessage("a plate of paella", _format)]).ToListAsync();

        var format = TextFormat(wire.Bodies.ShouldHaveSingleItem());
        format.ShouldNotBeNull();
        format["type"]!.GetValue<string>().ShouldBe("json_schema");
        format["name"]!.GetValue<string>().ShouldBe("meal_estimate");
        format["strict"]!.GetValue<bool>().ShouldBeTrue();
        JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(format["schema"]), _schema)
            .ShouldBeTrue($"the schema reached the wire as {format["schema"]?.ToJsonString()}");
    }

    [Fact]
    public async Task AMessageWithoutAFormat_SendsNone()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire);

        await agent.RunStreamingAsync([UserMessage("hi")]).ToListAsync();

        TextFormat(wire.Bodies.ShouldHaveSingleItem())?["type"]?.GetValue<string>()
            .ShouldNotBe("json_schema");
    }

    // The format is a request about one turn's answer, not a setting: nothing of it may surface
    // as a field OpenRouter would read as something else.
    [Fact]
    public async Task AFormat_AddsNoTopLevelFieldOfItsOwn()
    {
        var withFormat = new RecordingResponsesHandler();
        var without = new RecordingResponsesHandler();
        await using (var agent = CreateAgent(withFormat))
        {
            await agent.RunStreamingAsync([UserMessage("hi", _format)]).ToListAsync();
        }

        await using (var agent = CreateAgent(without))
        {
            await agent.RunStreamingAsync([UserMessage("hi")]).ToListAsync();
        }

        var added = withFormat.Bodies.Single().Select(p => p.Key)
            .Except(without.Bodies.Single().Select(p => p.Key))
            .ToList();
        added.ShouldBeSubsetOf(["text", "provider"]);
    }

    // The shipped pins are a deployment constraint and survive the turn; the requirement is added
    // beside them, so the format is never silently dropped by a provider that ignores it.
    [Fact]
    public async Task ATurnWithAFormat_RequiresProvidersThatHonourItAndKeepsTheRouting()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, routing: _pinned);

        await agent.RunStreamingAsync([UserMessage("paella", _format)]).ToListAsync();

        var provider = wire.Bodies.ShouldHaveSingleItem()["provider"]!.AsObject();
        provider["require_parameters"]!.GetValue<bool>().ShouldBeTrue();
        provider["sort"]!.GetValue<string>().ShouldBe("throughput");
        provider["preferred_max_latency"]!.GetValue<double>().ShouldBe(1);
    }

    [Fact]
    public async Task ATurnWithoutAFormat_RoutesExactlyAsConfigured()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, routing: _pinned);

        await agent.RunStreamingAsync([UserMessage("hi")]).ToListAsync();

        var provider = wire.Bodies.ShouldHaveSingleItem()["provider"]!.AsObject();
        provider.ContainsKey("require_parameters").ShouldBeFalse();
        provider["sort"]!.GetValue<string>().ShouldBe("throughput");
    }

    // nabu's shipped routing, the one non-default pin in Agent/appsettings.json.
    private static readonly ProviderRouting _pinned = new()
    {
        Sort = ProviderSort.Throughput,
        PreferredMaxLatency = new ProviderThreshold { P50 = 1 }
    };

    private static JsonObject? TextFormat(JsonObject body) => body["text"]?["format"] as JsonObject;

    private static ChatMessage UserMessage(string text, ResponseFormat? format = null)
    {
        var message = new ChatMessage(ChatRole.User, text);
        message.SetResponseFormat(format);
        return message;
    }

    private static McpAgent CreateAgent(
        RecordingResponsesHandler wire,
        IThreadStateStore? store = null,
        ProviderRouting? routing = null,
        IReadOnlyList<AIFunction>? tools = null)
    {
        var client = new OpenRouterChatClient(
            "http://localhost/api/v1",
            "test-key",
            "configured/model",
            providerRouting: routing,
            transportHandler: wire);
        return new McpAgent(
            TestAgentSpec.Default with { Model = "configured/model" },
            client,
            store ?? new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            tools ?? [],
            []);
    }
}

// Records every request body, because a turn that calls a tool is more than one request, and answers
// each from a script: a function call while one is queued, a one-word text answer after that.
internal sealed class RecordingResponsesHandler : HttpMessageHandler
{
    private readonly Queue<string> _functionCalls = new();
    private readonly List<JsonObject> _bodies = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<JsonObject> Bodies
    {
        get
        {
            lock (_lock)
            {
                return [.. _bodies];
            }
        }
    }

    public RecordingResponsesHandler ThenCalls(string toolName)
    {
        _functionCalls.Enqueue(toolName);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        string sse;
        lock (_lock)
        {
            _bodies.Add(JsonNode.Parse(body)!.AsObject());
            sse = _functionCalls.TryDequeue(out var tool) ? FunctionCall(tool, _bodies.Count) : Text(_bodies.Count);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }

    private static string Text(int n) =>
        """
          event: response.created
          data: {"type":"response.created","response":{"id":"resp_#N","object":"response","status":"in_progress","store":false}}

          event: response.output_item.added
          data: {"type":"response.output_item.added","output_index":0,"item":{"content":[],"id":"msg_#N","role":"assistant","status":"in_progress","type":"message"}}

          event: response.output_text.delta
          data: {"type":"response.output_text.delta","item_id":"msg_#N","output_index":0,"content_index":0,"delta":"{}"}

          event: response.output_item.done
          data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","status":"completed","id":"msg_#N","content":[{"type":"output_text","annotations":[],"text":"{}"}],"role":"assistant"}}

          event: response.completed
          data: {"type":"response.completed","response":{"id":"resp_#N","object":"response","created_at":1,"status":"completed","store":false,"model":"configured/model","output":[{"type":"message","status":"completed","id":"msg_#N","content":[{"type":"output_text","annotations":[],"text":"{}"}],"role":"assistant"}],"usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}

          data: [DONE]


          """.Replace("#N", n.ToString());

    private static string FunctionCall(string tool, int n) =>
        """
          event: response.created
          data: {"type":"response.created","response":{"id":"resp_#N","object":"response","status":"in_progress","store":false}}

          event: response.output_item.added
          data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_#N","call_id":"call_#N","name":"#TOOL","arguments":"","status":"in_progress"}}

          event: response.output_item.done
          data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_#N","call_id":"call_#N","name":"#TOOL","arguments":"{}","status":"completed"}}

          event: response.completed
          data: {"type":"response.completed","response":{"id":"resp_#N","object":"response","created_at":1,"status":"completed","store":false,"model":"configured/model","output":[{"type":"function_call","id":"fc_#N","call_id":"call_#N","name":"#TOOL","arguments":"{}","status":"completed"}],"usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}

          data: [DONE]


          """.Replace("#N", n.ToString()).Replace("#TOOL", tool);
}