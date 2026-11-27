using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// The one shaping of a tool call and its answer for a reader, used as the call streams and as the
// history is read back, so what a page drew live and what it draws after a reload are the same text.
// A long value is cut where it stands and says how much was left out — a file write's content, a web
// page's text, a command's output — so a call's arguments stay JSON a page can lay out, and a history
// read (asked again several times as a turn is kept) stays small.
public static class ToolSteps
{
    public const int MaxStringLength = 4000;
    public const int MaxTextLength = 16000;
    public const int MaxReasoningLength = 24000;

    public static ToolCallStep Call(FunctionCallContent call) => new()
    {
        CallId = call.CallId,
        Name = call.Name,
        Arguments = Shape(JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(),
            AIJsonUtilities.DefaultOptions)) ?? "{}"
    };

    public static ToolCallStep Result(FunctionResultContent result)
    {
        if (result.Exception is { } exception)
        {
            return new ToolCallStep
            {
                CallId = result.CallId,
                Result = Cut(exception.Message, MaxTextLength),
                Outcome = ToolCallOutcome.Failed
            };
        }

        var node = NodeOf(result.Result);
        return new ToolCallStep
        {
            CallId = result.CallId,
            Result = Shape(node) ?? "",
            Outcome = OutcomeOf(node)
        };
    }

    public static string Reasoning(string text) => Cut(text, MaxReasoningLength);

    // A string that is itself JSON (a tool that answers JSON text) is read as that JSON.
    private static JsonNode? NodeOf(object? result)
    {
        var node = result switch
        {
            null => null,
            JsonNode n => n.DeepClone(),
            _ => JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions)
        };
        if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && text.TrimStart() is ['{', ..] or ['[', ..])
        {
            try
            {
                return JsonNode.Parse(text) ?? node;
            }
            catch (JsonException)
            {
                return node;
            }
        }

        return node;
    }

    private static ToolCallOutcome OutcomeOf(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) && ApprovalRejection.Declined(text)
            => ToolCallOutcome.Declined,
        JsonObject envelope when envelope["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var fine) && !fine
            => ToolCallOutcome.Failed,
        _ => ToolCallOutcome.Done
    };

    // Text stays text; anything else is its JSON with long strings cut, and the whole cut at the ceiling.
    private static string? Shape(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return Cut(text, MaxTextLength);
        }

        return Cut(Shortened(node).ToJsonString(), MaxTextLength);
    }

    private static JsonNode Shortened(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    obj[key] = obj[key] is { } child ? Shortened(child.DeepClone()) : null;
                }

                return obj;
            case JsonArray array:
                return new JsonArray([.. array.Select(child => child is null ? null : Shortened(child.DeepClone()))]);
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > MaxStringLength:
                return JsonValue.Create(Cut(text, MaxStringLength));
            default:
                return node;
        }
    }

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : $"{text[..max]}… [{text.Length - max:N0} more characters]";
}