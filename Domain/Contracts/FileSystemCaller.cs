using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

// Who a filesystem call is from, as the call's own `_meta` says: the conversation it serves, the
// exec bridge's token where the agent minted one for it, and the delegation the agent signed for a
// write a later turn acts on (a schedule, a watch), which the mount stores with what it wrote. Plain
// data the registrar hands down per call (FileSystemBackendBase.For); null parts mean the call
// carried none.
public sealed record FileSystemCaller(ConversationContext? Conversation, VfsBridgeGrant? Bridge, string? Delegation = null)
{
    public static readonly FileSystemCaller Nobody = new(null, null);
}