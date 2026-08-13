namespace Domain.DTOs.FileSystem;

public sealed record FsExecResult
{
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required int ExitCode { get; init; }
    public required bool Truncated { get; init; }
    public required bool TimedOut { get; init; }
    public required long DurationMs { get; init; }

    // The directory the command ran in, relative to the backend's own configured root — the root
    // itself is the empty string. The backend is the only component that knows that root; the exec
    // tool puts the mount point in front before the model sees it, so a backend that answered in
    // container-absolute coordinates here would produce a path nothing can resolve.
    public required string Cwd { get; init; }
}