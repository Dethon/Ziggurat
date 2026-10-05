using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

// Who a filesystem call is from, as the call's own `_meta` says: the conversation it serves, and
// the exec bridge's token where the agent minted one for it. Plain data the registrar hands down
// per call (FileSystemBackendBase.For); null halves mean the call carried none.
public sealed record FileSystemCaller(ConversationContext? Conversation, VfsBridgeGrant? Bridge)
{
    public static readonly FileSystemCaller Nobody = new(null, null);
}