using Domain.DTOs.Channel;
using Domain.Tools.FileSystem.Bridge;

namespace Domain.Contracts;

// The exec bridge's tokens: minted by the exec tool for one sandbox command, looked up by the
// bridge endpoint for each file operation that command makes, revoked ahead of a kill and completed
// when the exec returns. Explicit calls, no ambient "current call". The registry holding them is
// state, so it lives in Infrastructure (VfsBridge); what a call means is VfsCall's, here.
public interface IVfsBridge
{
    // `caller`: the conversation the command serves, so a mount that answers by who is calling —
    // the Home Assistant watches record their author — sees the same caller through the shell as
    // through a tool call. Null where the call carried none.
    VfsCall Mint(IVirtualFileSystemRegistry registry, Func<string, bool> permits, ConversationContext? caller = null);

    // Null for a token never minted, completed or expired: all three are a caller with no call.
    VfsCall? Find(string token);

    // The command is being killed or the call cancelled: whatever arrives from now on is logged as
    // dropped rather than applied, and the token still answers so the drops are recorded.
    void Revoke(string token);

    // The exec returned: the token stops answering, and what it recorded is the result's.
    VfsCallRecord Complete(string token);
}