using Domain.DTOs.Channel;
using Domain.Security.Signing;

namespace Infrastructure.Agents.Mcp;

// Who a turn's calls are signed as acting for — the one place the agent decides it.
//
// Today it is read off the principal the channel stamped on the turn: the account it carries is the
// person, and anything else is nobody. The voice guest is nobody here too, because nothing in the
// agent knows which household a guest is in, and a guest is signed with its household.
//
// The seam ticket 05 plugs into: once the agent verifies the turn's assertion at its start, the
// identity it verified rides on the turn's ConversationContext and this answers that instead — the
// person, the household's guest or nobody exactly as the accounts service vouched. Nothing else in
// the signing path changes: workers already carry their parent's context, so they carry its identity.
internal static class CallIdentity
{
    public static IdentityClaims Of(ConversationContext? turn) =>
        Principal.Of(turn) is { Account: { TenantId.Length: > 0, UserId.Length: > 0 } account } principal
            ? IdentityClaims.Person(account.UserId, account.TenantId, principal.ChannelId, [])
            : IdentityClaims.Nobody(turn?.Origin.ChannelId ?? "");
}