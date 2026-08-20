namespace Domain.DTOs;

// Which agent answers a message whose channel named none. It is a configured decision and never a
// positional one: the agent array is a catalogue, ordered for display, and reordering it must not
// hand a general request to the download assistant. A channel that resolves no default here has
// its message refused with a message saying so, rather than routed by guesswork.
public record AgentDefaults
{
    // Keyed by ChannelId, for a transport whose messages belong to one agent by nature — every
    // spoken word goes to the voice agent, whatever the satellite was configured with.
    public Dictionary<string, string> ByChannel { get; init; } = [];

    // Every other channel. Null means no channel has a default at all.
    public string? Fallback { get; init; }

    public string? For(string? channelId) =>
        ByChannel
            .FirstOrDefault(entry => entry.Key.Equals(channelId, StringComparison.OrdinalIgnoreCase))
            .Value
        ?? Fallback;

    // Read once at startup: a default naming an agent that does not exist is a deployment that
    // routes nothing, and it should say so on the first line of the log rather than on the first
    // message of the day.
    public void Validate(IEnumerable<string> configuredAgentIds)
    {
        var configured = configuredAgentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = ((IEnumerable<string?>)ByChannel.Values)
            .Append(Fallback)
            .OfType<string>()
            .Where(id => !configured.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"agentDefaults names agents that are not configured: {string.Join(", ", unknown)}.");
        }
    }
}