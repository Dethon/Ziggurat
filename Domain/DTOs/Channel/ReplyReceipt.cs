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

    public string ToWire() => ReturnedTurnKeys.Count == 0 ? Ok : JsonSerializer.Serialize(this);

    public static ReplyReceipt Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer == Ok)
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                   && root.TryGetProperty("returnedTurnKeys", out var keys)
                   && keys.ValueKind == JsonValueKind.Array
                ? new ReplyReceipt([.. keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!)])
                : None;
        }
        catch (JsonException)
        {
            return None;
        }
    }
}