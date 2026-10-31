using Domain.DTOs;
using Domain.Metrics;
using Domain.Security.Signing;
using Microsoft.Agents.AI;

namespace Domain.Monitor;

// One message the agent answers, together with where its replies go, the key every reply of
// this turn echoes back, the latency span that ends on its first delivered chunk, and who it acts
// for as its assertion verified when it arrived (none for a fire). Minted once, when the turn
// starts, so an update cannot travel with targets belonging to a different message.
internal sealed record Turn(
    ChannelMessage Message,
    IReadOnlyList<DeliveryTarget> Targets,
    LatencyScope FirstReply,
    string TurnKey,
    IdentityClaims? Identity = null)
{
    // The same fact the group already tests when it announces a turn start, read here so a reply
    // and the announce cannot disagree about what started this turn.
    public bool AgentInitiated => Message.Origin is not null;
}

internal sealed record TurnUpdate(AgentResponseUpdate Update, Turn Turn);