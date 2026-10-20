using System.Text.Json;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// The shape a channel wants this turn's final answer in: a schema name and the JSON schema itself,
// always sent strict. It belongs to the one message that carries it — the agent puts it on that
// turn's request and nothing else — so a channel that parses the answer sends it every time.
// The schema is the channel's own JSON, passed through untouched, and must already be one a strict
// schema allows: every property required, `additionalProperties: false` on every object.
[PublicAPI]
public record ResponseFormat
{
    public required string Name { get; init; }
    public required JsonElement Schema { get; init; }
}