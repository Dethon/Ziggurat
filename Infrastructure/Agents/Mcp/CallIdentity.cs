using Domain.DTOs.Channel;
using Domain.Security.Signing;

namespace Infrastructure.Agents.Mcp;

// Who a turn's calls are signed as acting for — the one place the agent decides it.
//
// A person's own turn is whoever its assertion named: the conversation group verified it as the turn
// arrived (the accounts service's signature, in date, for the channel it came on) and put what it
// verified on the turn's context, so every call the turn makes — a worker's included, which carries
// its parent's context verbatim — is signed as that person, or as the household's guest with its
// household, for as long as the turn runs.
//
// A turn no assertion stands behind is a fire, a login wake or a watch: nobody sent it at that
// moment, so the group never verified anybody for it. Until the agent signs delegations for those
// (ticket 07, which replaces exactly this fallback), it is read as before off the principal its
// server stamped — the account it carries, or nobody.
internal static class CallIdentity
{
    public static IdentityClaims Of(ConversationContext? turn) =>
        turn?.Identity
        ?? (Principal.Of(turn) is { Account: { TenantId.Length: > 0, UserId.Length: > 0 } account } principal
            ? IdentityClaims.Person(account.UserId, account.TenantId, principal.ChannelId, [])
            : IdentityClaims.Nobody(turn?.Origin.ChannelId ?? ""));
}