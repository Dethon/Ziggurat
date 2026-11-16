namespace Domain.DTOs;

// Which agent answers a message whose channel named none, where the channel has a default of its own.
// It is a configured decision and never a positional one: the agent array is a catalogue, ordered for
// display, and reordering it must not hand a general request to the download assistant. A channel with
// none here falls back to the household's own default (its agents section's `defaultAgent`), and one
// that resolves neither has its message refused with a message saying so, rather than routed by
// guesswork.
public record AgentDefaults
{
    // Keyed by ChannelId, for a transport whose messages belong to one agent by nature — every
    // spoken word goes to the voice agent, whatever the satellite was configured with. The deployment's,
    // since a channel is: a household that has no agent of that id has the message refused.
    public Dictionary<string, string> ByChannel { get; init; } = [];

    public string? For(string? channelId) =>
        ByChannel
            .FirstOrDefault(entry => entry.Key.Equals(channelId, StringComparison.OrdinalIgnoreCase))
            .Value;
}