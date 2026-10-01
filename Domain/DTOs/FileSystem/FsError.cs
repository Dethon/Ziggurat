using Domain.Tools;

namespace Domain.DTOs.FileSystem;

// The envelope shapes every filesystem failure takes. One definition so a not-found from a disk
// mount reads the same as a not-found from a virtual one, and so no caller invents a new code.
public static class FsError
{
    public static FsResult<T> NotFound<T>(string path) where T : class =>
        Fail<T>(ToolError.Codes.NotFound, $"Path not found: {path}");

    public static FsResult<T> Invalid<T>(string message) where T : class =>
        Fail<T>(ToolError.Codes.InvalidArgument, message);

    public static FsResult<T> ReadOnly<T>(string path) where T : class =>
        Fail<T>(ToolError.Codes.UnsupportedOperation, $"{path} is read-only");

    // An action file runs and is never opened: reading it, writing it or removing it is refused on
    // every route, with the one call that does work named. The name is the file's own, because a
    // real shell will need `./` in front of it once exec runs in the sandbox.
    public static FsResult<T> ExecutableOnly<T>(string path, string actionName) where T : class =>
        Fail<T>(ToolError.Codes.UnsupportedOperation,
            $"{path} is executable-only: an action file runs, it cannot be read or written.",
            $"Run it with exec ./{actionName} from its directory (--help, where the action takes arguments, lists them).");

    public static FsResult<T> AlreadyExists<T>(string message) where T : class =>
        Fail<T>(ToolError.Codes.AlreadyExists, message);

    public static FsResult<T> Fail<T>(string code, string message, string? hint = null)
        where T : class =>
        new FsResult<T>.Err(new ToolErrorResult
        {
            ErrorCode = code,
            Message = message,
            Hint = hint
        });
}