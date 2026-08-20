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