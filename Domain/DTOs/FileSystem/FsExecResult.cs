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

    // What the command changed through the other mounts, when it ran in the sandbox with the
    // bridge serving them; null — and left off the wire — where exec has no bridge (an outpost, an
    // in-process host), so those answer exactly what they answered before.
    public IReadOnlyList<VfsChange>? VfsChanges { get; init; }

    // The served directories whose listing the mount cut short at its walk budget: a recursive
    // command over them saw only part of the tree. Null when every listing was whole.
    public IReadOnlyList<string>? VfsTruncated { get; init; }
}