using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsExecTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "exec";
    public const string Name = "exec";

    // One description for every exec-capable mount, so it names no single mount's directories:
    // where a mount's layout matters — which directory is writable, how its own path spelling and
    // the mount-prefixed one relate — its skill says so, and a run loads the skill by rule.
    public const string ToolDescription = """
        Runs a bash command (`bash -lc`, so the login PATH is set) on a filesystem that supports
        execution, with `path` as the working directory: the mount point is the filesystem's root,
        a deeper path that directory. A non-zero exit code comes back as data in `exitCode`; output
        is capped per stream (`truncated`); a timeout kills the process tree. Paths in the output are
        in the filesystem's native spelling, while the reported `cwd` is already virtual.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path used as CWD: the mount point itself for the filesystem's root, or any directory under it")]
        string path,
        [Description("Bash command line; passed to `bash -lc`")]
        string command,
        [Description("Optional timeout in seconds. Backend clamps to its max.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        // The working directory is a path the caller never named — the backend answers it relative
        // to its own root — so it gets the mount point in front of it through the same translation
        // glob entries and search hits use. The root comes back as the empty path, which becomes
        // the mount point with a trailing slash.
        return (await resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, cancellationToken))
            .Map(exec => exec with { Cwd = resolution.ToVirtualPath(exec.Cwd) })
            .ToNode();
    }
}