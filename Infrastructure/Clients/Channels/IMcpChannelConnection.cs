using Domain.DTOs.Channel;

namespace Infrastructure.Clients.Channels;

// A channel connection as its supervisor sees it. There is one verb, because driving a connection
// is one sequence and it belongs to the connection: a caller that could connect, register, check
// health and reconnect in some other order would be a caller that can get the order wrong.
//
// Being **not connected** — before the first connect, and for the whole of a reconnect — is five
// behaviours, one per member, and they differ because their callers differ
// (docs/adr/0011-not-connected-is-five-behaviours-and-stays-that-way.md):
//
// - SendReplyAsync, RequestApprovalAsync and NotifyAutoApprovedAsync throw. They are called by an
//   agent mid-turn, which has somewhere to report a failure.
// - CreateConversationAsync returns null. DeliveryTargetResolver reads null as "this channel minted
//   nothing", which is also what an attach-only channel and a channel with no create_conversation
//   tool return; its job is to try the next target, and an exception would make it catch in order
//   to continue.
// - RegisterAgentsAsync returns silently and IsHealthyAsync returns false. Both are called by the
//   connection's own supervision, which reacts to the answer rather than to an exception.
// - Messages yields forever. The agent's read loop awaits messages for the process lifetime, so a
//   reconnect is invisible to it; a completed sequence would end the loop.
//
// The first three of those members are on Domain's IChannelConnection, which the same type
// implements. Only the run verb is here, because it is the only one a supervisor calls.
public interface IMcpChannelConnection
{
    string ChannelId { get; }

    // Runs this connection for its lifetime: connect with retry, register the agent catalog, watch
    // health, and on a failed check reconnect with retry and re-register. Returns when ct is
    // cancelled, and does not throw for a link that is merely down.
    //
    // The catalog is a function rather than a list because it is not constant: attachment
    // capability is discovered from the model provider and refreshed hourly, so every registration
    // has to read what is true now rather than what was true when the host started.
    Task RunAsync(string endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct);
}