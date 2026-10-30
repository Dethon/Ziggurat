using Domain.DTOs.Channel;
using Domain.Security;

namespace Domain.Tools.Web;

// A login handoff as a delegation describes it: the turn that asked — its agent, its conversation and
// where it answers — read off that turn's context on both ends, the agent's as it sends web_login and
// the browse server's as it keeps the handoff. Named by the conversation it wakes, the one id both
// ends know before the server has opened anything.
public static class LoginDelegation
{
    public static DelegatedObject Of(ConversationContext asking) =>
        DelegatedObject.Of(DelegatedObject.Login, asking.ConversationId,
            new DelegatedLogin(asking.AgentId, asking.ConversationId, asking.Origin));
}