using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;

namespace Infrastructure.Clients.Bash;

// The runners' own coordinates, shared so the in-process runner and the launcher-backed one agree
// on what a working directory means. Every working directory is resolved against the root and
// reported relative to it; which mount point goes in front is the exec tool's business. The jail
// is the same containment decision the file tools make, so a path that climbs out — or reaches out
// through a symlink — is refused rather than answered for a directory nobody asked for.
internal sealed class CommandCwd(string containerRoot)
{
    private readonly PathJail _jail = new(containerRoot);

    // Total: every path names a place under the root, including the empty one and an absolute one.
    // With the root at "/" nothing can climb out anyway. `mustExist` is false for the launcher's
    // runner: a directory under /vfs exists only inside the exec's own namespace, so the launcher
    // decides that there.
    public FsResult<string> Resolve(string path, bool mustExist = true)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var cwd = Path.GetFullPath(Path.Combine(_jail.Root, normalized));

        if (!_jail.Contains(cwd))
        {
            return FsError.Invalid<string>(_jail.DeniedMessage);
        }

        return !mustExist || Directory.Exists(cwd)
            ? new FsResult<string>.Ok(cwd)
            : NotADirectory(cwd);
    }

    public static FsResult<string> NotADirectory(string cwd) =>
        FsError.Fail<string>(ToolError.Codes.NotFound, $"Working directory '{cwd}' does not exist or is not a directory.");

    // The root reports as the empty path, which is the mount point with a trailing slash once the
    // tool has prefixed it — the spelling glob already uses for a directory.
    public string ToRootRelative(string cwd)
    {
        var relative = Path.GetRelativePath(_jail.Root, cwd);
        return relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static int EffectiveTimeoutSeconds(BashRunnerOptions options, int? timeoutSeconds) =>
        Math.Clamp(timeoutSeconds ?? options.DefaultTimeoutSeconds, 1, options.MaxTimeoutSeconds);
}