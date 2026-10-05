using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem.Bridge;

// What the bridge answers one kernel operation with: a value, or the errno the daemon hands the
// kernel together with the mount's own envelope where there is one. An errno is all a syscall can
// carry; the envelope is what the exec result's change list keeps for the agent.
public abstract record BridgeAnswer<T>
{
    private BridgeAnswer() { }

    public sealed record Ok(T Value) : BridgeAnswer<T>;

    public sealed record Refused(string Errno, ToolErrorResult? Error) : BridgeAnswer<T>;

    public static BridgeAnswer<T> From(ToolErrorResult error) => new Refused(Errnos.Of(error), error);
}

// What an action printed and how it ended, passed through by the helper as its own.
public sealed record BridgeActionResult(string Stdout, string Stderr, int ExitCode);

public static class BridgeKinds
{
    public const string File = "file";
    public const string Directory = "dir";

    // An action file: served executable-only, run through the action helper and never opened.
    public const string Action = "action";
}

// A file or directory as the kernel needs it. Size is null where the mount cannot tell without
// rendering the file, which the daemon serves with direct I/O: a guessed size of zero reads as
// empty, silently (spike finding 5).
public sealed record BridgeAttr(string Kind, long? Size);

public sealed record BridgeEntry(string Name, string Kind);

// Truncated: the mount's walk stopped before the directory ended, so a listing — and whatever a
// recursive command made of it — saw only part of it.
public sealed record BridgeListing(IReadOnlyList<BridgeEntry> Entries, bool Truncated);

// The errno a refusal becomes. The names are the daemon's to turn into numbers.
public static class Errnos
{
    public const string NotFound = "ENOENT";
    public const string Denied = "EACCES";
    public const string ReadOnly = "EROFS";
    public const string Exists = "EEXIST";
    public const string Invalid = "EINVAL";
    public const string NotSupported = "ENOTSUP";
    public const string TimedOut = "ETIMEDOUT";
    public const string Io = "EIO";
    public const string TooLarge = "EFBIG";

    public static string Of(ToolErrorResult error) => error.ErrorCode switch
    {
        ToolError.Codes.NotFound => NotFound,
        ToolError.Codes.PermissionDenied => Denied,
        // A mount refusing an operation it does not do — a read-only file, an executable-only
        // action — is a permission the file does not give, which is how a shell reports it.
        ToolError.Codes.UnsupportedOperation => Denied,
        ToolError.Codes.AlreadyExists => Exists,
        ToolError.Codes.InvalidArgument => Invalid,
        ToolError.Codes.Timeout => TimedOut,
        _ => Io
    };
}