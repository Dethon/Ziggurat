using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Who a file tool's call acts for, as data the call itself carries: the approval client puts the
// turn's conversation on every call's own arguments (ToolApprovalChatClient), and a tool resolves its
// paths to the backends as that caller sees them. A server behind a mount is told who is calling
// from there, and a write that raises later turns — a schedule, a watch — is delegated for that
// caller alone. A call with nothing on its arguments acts for nobody.
public static class ToolCaller
{
    public static ConversationContext? Of(AIFunctionArguments? arguments) =>
        arguments?.Context?.TryGetValue(ConversationContext.ContextKey, out var caller) == true
            ? caller as ConversationContext
            : null;

    public static FsResult<FileSystemResolution> Resolve(
        this IVirtualFileSystemRegistry registry, string virtualPath, AIFunctionArguments? arguments) =>
        registry.Resolve(virtualPath).Map(resolution => resolution.As(Of(arguments)));

    // Both ends of a transfer as the caller sees them, through one view where they share a backend:
    // Transfer tells a move within one mount from a move across two by the backend's identity.
    public static (FileSystemResolution Source, FileSystemResolution Destination) As(
        FileSystemResolution source, FileSystemResolution destination, AIFunctionArguments? arguments)
    {
        var from = source.As(Of(arguments));
        return (from, ReferenceEquals(source.Backend, destination.Backend)
            ? destination with { Backend = from.Backend }
            : destination.As(Of(arguments)));
    }
}