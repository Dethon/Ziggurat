using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelCancelNotification
{
    public required string ConversationId { get; init; }
    public string? AgentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    // The turn keys of the messages the stop handed back to their senders' composers. A steer is sent
    // under its key at once, so the agent may have started its turn before the hub heard the turn it cut
    // end: the agent keeps no turn under one of these, or its sender would have it twice. Optional, so a
    // channel from before and an agent from before each read the other's cancel as they always did.
    public IReadOnlyList<string>? ReturnedTurnKeys { get; init; }
}