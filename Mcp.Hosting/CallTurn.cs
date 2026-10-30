using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The one place a server reads who a call acts for: the identity the agent's call token verified as,
// which CallTokenGate left on the call. The conversation the call is part of still rides in its
// _meta as the agent wrote it — which conversation, where a reply goes, what raised the turn — but
// who it acts for is the token's alone, so a principal written into the _meta is replaced, never
// believed. That only holds while no tool reads the _meta around this file
// (CallTurnConformanceTests). Null means the call carried no turn, and the caller refuses rather
// than guesses.
public static class CallTurn
{
    // Where the gate leaves the identity it verified, on the call's own Items.
    internal const string IdentityKey = "com.herfluffness/callIdentity";

    extension(RequestContext<CallToolRequestParams> call)
    {
        // Null on a call no gate checked — the outpost, which takes the token and is not required to
        // check it — and then the call acts for nobody.
        public IdentityClaims? Identity =>
            call.Items.TryGetValue(IdentityKey, out var identity) ? identity as IdentityClaims : null;

        // The identity the agent wrote into the context for its own signing is replaced as the
        // principal is: what a server sees there is the token's, or nobody's.
        public ConversationContext? Turn =>
            ConversationScope.Parse(call.Params?.Meta) is { } claimed
                ? claimed with { Principal = PrincipalOf(call.Identity), Identity = call.Identity }
                : null;

        // The delegation the agent signed for whatever this call creates or rewrites — a schedule, a
        // watch, a login handoff — as the call carried it: the server stores it with the object and
        // hands it back with the object's fire, and never reads it, since only the agent can say
        // whether it holds. Null when the call delegates nothing, and the object is then nobody's.
        public string? Delegation => CallText.StringAt(call.Params?.Meta, ChannelProtocol.DelegationMetaKey);

        // The namespace a stateful tool caches the turn's state under (ConversationScope).
        public bool TryScope(out string scope)
        {
            if (call.Turn is not { } turn)
            {
                scope = string.Empty;
                return false;
            }

            scope = ConversationScope.Build(turn.AgentId, turn.ConversationId);
            return true;
        }
    }

    // A person is their account on the channel the token names; the guest is somebody on that
    // channel with no account; nobody has no principal at all.
    private static Principal? PrincipalOf(IdentityClaims? identity) => identity switch
    {
        null or { IsNobody: true } => null,
        { IsGuest: true } => new Principal(identity.ChannelId, IdentityClaims.GuestSubject),
        _ => new Principal(identity.ChannelId, identity.Subject, identity.Account)
    };
}