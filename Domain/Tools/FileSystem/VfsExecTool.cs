using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsExecTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "exec";
    public const string Name = "exec";

    public const string ToolDescription = """
        Execute a bash command on a filesystem that supports execution.
        The path argument is the working directory (CWD) for the command, expressed as a virtual path
        and used literally: the mount point itself is the filesystem's root (/sandbox is the sandbox
        container's root directory), and a deeper path is that directory.
        Inside the command string both spellings work: on the sandbox, /sandbox/etc/hosts and
        /etc/hosts name the same file, so a path you were taught and a path you were given are both
        usable as written.
        Paths that come back in command output (`pwd`, `find`, `which`, ...) are container-native:
        put the mount point in front of one before passing it to a filesystem tool as a path.
        The `cwd` in the result is already a virtual path and needs no such prefixing.
        Commands run via `bash -lc` so login shell env (PATH, etc.) is initialised.
        Non-zero exit codes are returned as data (in `exitCode`), not as errors.
        Output is truncated at the backend's per-stream cap; check `truncated` in the result.
        Optional `timeoutSeconds` is clamped to the backend's max; on timeout the process tree is killed.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path used as CWD (e.g., /sandbox for the container root, or any directory under it)")]
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