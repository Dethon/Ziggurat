using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.DTOs.Channel;

// What a channel answers send_reply with, beyond having taken it: the turn keys of messages a stop handed
// back to their senders. The agent starts the next turn of a conversation only once its last reply was
// answered, so a turn named here, still waiting behind the one that ended, is never run — the cancel that
// names it too may reach the agent only after that turn ran and was kept. Optional both ways: a channel
// that names nothing answers "ok", as every channel did, and an agent reads anything it does not know as
// nothing.
public sealed record ReplyReceipt(
    [property: JsonPropertyName("returnedTurnKeys")] IReadOnlyList<string> ReturnedTurnKeys)
{
    public static readonly ReplyReceipt None = new([]);

    private const string Ok = "ok";

    // The steers the turn's end drew, in the order they were steered, which go in together as one turn
    // answered once under the first one's key: the channel decided which were waiting as that turn ended,
    // and the agent, whose pump may not have admitted every one of them yet, takes exactly these.
    [JsonPropertyName("steeredTogether")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SteeredTogether { get; init; }

    public string ToWire() => ReturnedTurnKeys.Count == 0 && SteeredTogether is not { Count: > 0 }
        ? Ok
        : JsonSerializer.Serialize(this);

    public static ReplyReceipt Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer == Ok)
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(answer);
            if (document.RootElement is not { ValueKind: JsonValueKind.Object } root)
            {
                return None;
            }

            var together = Keys(root, "steeredTogether");
            return new ReplyReceipt(Keys(root, "returnedTurnKeys") ?? [])
            {
                SteeredTogether = together is { Count: > 0 } ? together : null
            };
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static IReadOnlyList<string>? Keys(JsonElement root, string name) =>
        root.TryGetProperty(name, out var keys) && keys.ValueKind == JsonValueKind.Array
            ? [.. keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!)]
            : null;
}