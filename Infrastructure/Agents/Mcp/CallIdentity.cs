using Domain.DTOs.Channel;
using Domain.Security.Signing;

namespace Infrastructure.Agents.Mcp;

// Who a turn's calls are signed as acting for — the one place the agent decides it.
//
// The identity the conversation group verified as the turn arrived and put on its context: a person's
// own turn is whoever its assertion named (the accounts service's signature, in date, for the channel
// it came on), the household's guest with its household included; a fire, a login wake or a watch is
// whoever the delegation it carried names, when the agent's own signature on it holds for that very
// object. Every call the turn makes — a worker's included, which carries its parent's context verbatim
// — is signed as that identity for as long as the turn runs.
//
// Nothing else names a person. A context with no identity is a turn nothing verified, and its calls
// are signed as nobody whatever principal it carries: a principal is only what a channel or a server
// stamped.
internal static class CallIdentity
{
    public static IdentityClaims Of(ConversationContext? turn) =>
        turn?.Identity ?? IdentityClaims.Nobody(turn?.Origin.ChannelId ?? "");
}