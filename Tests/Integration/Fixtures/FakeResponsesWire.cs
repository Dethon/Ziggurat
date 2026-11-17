using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using static Tests.Integration.Fixtures.FakeModelReplies;

namespace Tests.Integration.Fixtures;

// A fake reply spelled on OpenRouter's Responses wire, the one the agent's client speaks: streamed as
// the provider streams it (reasoning summary, then text deltas or one function call), or whole for a
// request that did not ask for a stream.
public static class FakeResponsesWire
{
    private const string Model = "fake-model";

    private static readonly JsonObject _usage = new()
    {
        ["input_tokens"] = 1, ["output_tokens"] = 1, ["total_tokens"] = 2, ["cost"] = 0
    };

    public static string Stream(Reply reply) => string.Concat(Events(reply).Select(e => e.Sse));

    public static async IAsyncEnumerable<string> StreamAsync(Reply reply, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (sse, delay) in Events(reply))
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            yield return sse;
        }
    }

    public static JsonObject Whole(Reply reply) => Response("completed", Output(reply));

    private static IEnumerable<(string Sse, TimeSpan Delay)> Events(Reply reply)
    {
        yield return (Event("response.created", new JsonObject { ["response"] = Response("in_progress", []) }), TimeSpan.Zero);

        var index = 0;
        if (reply.Reasoning is { } reasoning)
        {
            yield return (Event("response.output_item.added", new JsonObject
            {
                ["output_index"] = index,
                ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JsonArray() }
            }), TimeSpan.Zero);
            yield return (Event("response.reasoning_summary_text.delta", new JsonObject
            {
                ["item_id"] = "rs_1", ["output_index"] = index, ["summary_index"] = 0, ["delta"] = reasoning
            }), TimeSpan.Zero);
            yield return (Event("response.output_item.done", new JsonObject
            {
                ["output_index"] = index, ["item"] = ReasoningItem(reasoning)
            }), TimeSpan.Zero);
            index++;
        }

        switch (reply.Output)
        {
            case Text text:
                yield return (Event("response.output_item.added", new JsonObject
                {
                    ["output_index"] = index,
                    ["item"] = new JsonObject
                    {
                        ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["status"] = "in_progress",
                        ["content"] = new JsonArray()
                    }
                }), TimeSpan.Zero);
                foreach (var chunk in text.Chunks)
                {
                    yield return (Event("response.output_text.delta", new JsonObject
                    {
                        ["item_id"] = "msg_1", ["output_index"] = index, ["content_index"] = 0, ["delta"] = chunk
                    }), text.ChunkDelay);
                }

                yield return (Event("response.output_item.done", new JsonObject
                {
                    ["output_index"] = index, ["item"] = MessageItem(text.Whole)
                }), TimeSpan.Zero);
                break;
            case ToolCall call:
                yield return (Event("response.output_item.added", new JsonObject
                {
                    ["output_index"] = index, ["item"] = CallItem(call, "in_progress", "")
                }), TimeSpan.Zero);
                yield return (Event("response.output_item.done", new JsonObject
                {
                    ["output_index"] = index, ["item"] = CallItem(call, "completed", call.Arguments)
                }), TimeSpan.Zero);
                break;
        }

        yield return (Event("response.completed", new JsonObject { ["response"] = Response("completed", Output(reply)) }), TimeSpan.Zero);
        yield return ("data: [DONE]\n\n", TimeSpan.Zero);
    }

    private static JsonArray Output(Reply reply)
    {
        var items = new JsonArray();
        if (reply.Reasoning is { } reasoning)
        {
            items.Add(ReasoningItem(reasoning));
        }

        items.Add(reply.Output switch
        {
            Text text => MessageItem(text.Whole),
            ToolCall call => CallItem(call, "completed", call.Arguments),
            _ => throw new InvalidOperationException($"No wire form for {reply.Output}")
        });
        return items;
    }

    private static JsonObject Response(string status, JsonArray output) => new()
    {
        ["id"] = "resp_1", ["object"] = "response", ["status"] = status, ["store"] = false, ["model"] = Model,
        ["output"] = output, ["usage"] = status == "completed" ? _usage.DeepClone() : null
    };

    private static JsonObject ReasoningItem(string reasoning) => new()
    {
        ["type"] = "reasoning", ["id"] = "rs_1",
        ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reasoning })
    };

    private static JsonObject MessageItem(string text) => new()
    {
        ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray()
        })
    };

    private static JsonObject CallItem(ToolCall call, string status, string arguments) => new()
    {
        ["type"] = "function_call", ["id"] = $"fc_{call.Id}", ["call_id"] = call.Id,
        ["name"] = call.Name, ["arguments"] = arguments, ["status"] = status
    };

    private static string Event(string type, JsonObject data)
    {
        data["type"] = type;
        var spelled = new StringBuilder();
        spelled.Append("event: ").Append(type).Append('\n');
        spelled.Append("data: ").Append(data.ToJsonString()).Append("\n\n");
        return spelled.ToString();
    }
}