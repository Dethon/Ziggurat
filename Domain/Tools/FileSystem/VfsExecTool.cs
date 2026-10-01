using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// `offered` answers whether this session offers a file tool by name: a command may do through the
// mounts only what the session's own tools could, so a tool the agent was never given is refused
// to the shell too. Null offers every tool.
public class VfsExecTool(
    IVirtualFileSystemRegistry registry,
    VfsBridge? bridge = null,
    Func<string, bool>? offered = null)
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
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var exec = Bridged(resolution) is { } bridged && bridge is not null
            ? await RunBridgedAsync(bridged, bridge, resolution, command, timeoutSeconds, Permission(arguments), cancellationToken)
            : await resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, cancellationToken);

        // The working directory is a path the caller never named — the backend answers it relative
        // to its own root — so it gets the mount point in front of it through the same translation
        // glob entries and search hits use. The root comes back as the empty path, which becomes
        // the mount point with a trailing slash.
        return exec.Map(e => e with { Cwd = resolution.ToVirtualPath(e.Cwd) }).ToNode();
    }

    // The command runs with the other mounts served to it at /vfs: one token for this call, bound
    // to this session's registry and to what its tools may do unasked, completed however the call
    // ends — returned, failed or cancelled — so it never outlives the command it serves.
    private async Task<FsResult<FsExecResult>> RunBridgedAsync(
        IBridgedExecBackend backend,
        VfsBridge vfs,
        FileSystemResolution resolution,
        string command,
        int? timeoutSeconds,
        ToolPermission permission,
        CancellationToken ct)
    {
        var call = vfs.Mint(registry, toolName => (offered?.Invoke(toolName) ?? true) && permission.RunsUnasked(
            FileSystemToolFeature.Callable(toolName)));
        try
        {
            var exec = await backend.ExecAsync(
                resolution.RelativePath, command, timeoutSeconds, new VfsBridgeGrant(call.Token), ct);
            return exec.Map(e => e with { VfsChanges = vfs.Complete(call.Token) });
        }
        finally
        {
            vfs.Complete(call.Token);
        }
    }

    // Only the deployment's own container gets a bridge: an outpost is somebody's computer and the
    // mounts are never served onto it.
    private IBridgedExecBackend? Bridged(FileSystemResolution resolution) =>
        resolution.Backend is IBridgedExecBackend bridged
        && registry.GetMounts().Any(m =>
            string.Equals(m.MountPoint, resolution.MountPoint, StringComparison.OrdinalIgnoreCase)
            && m.ShellReach == ShellReach.Contained)
            ? bridged
            : null;

    private static ToolPermission Permission(AIFunctionArguments? arguments) =>
        arguments?.Context?.TryGetValue(ToolPermission.ContextKey, out var view) == true && view is ToolPermission permission
            ? permission
            : ToolPermission.None;
}