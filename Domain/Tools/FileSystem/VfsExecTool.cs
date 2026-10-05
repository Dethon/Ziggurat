using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// `offered` answers whether this session offers a file tool by name: a command may do through the
// mounts only what the session's own tools could, so a tool the agent was never given is refused
// to the shell too. Null offers every tool.
public class VfsExecTool(
    IVirtualFileSystemRegistry registry,
    IVfsBridge? bridge = null,
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

    // Added to the description where the session's sandbox commands see its other mounts.
    public const string BridgedDescription = """
        In the sandbox, every other mount of this session but the machines is a directory at its own
        path, and `exec` on a mount with no shell of its own runs in the sandbox in that directory.
        `vfsChanges` in the result lists every change the command made through those mounts —
        `applied`, `refused` with the mount's reason, or `dropped` — because bash does not report a
        refused write; `vfsTruncated` names a directory a recursive command saw only part of.
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

        if (bridge is not null && Bridged(resolution) is { } bridged)
        {
            // The working directory is a path the caller never named — the backend answers it
            // relative to its own root — so it gets the mount point in front of it through the same
            // translation glob entries and search hits use. The root comes back as the empty path,
            // which becomes the mount point with a trailing slash.
            return (await RunBridgedAsync(bridged, bridge, resolution.RelativePath, command, timeoutSeconds,
                    Permission(arguments), Caller(arguments), cancellationToken))
                .Map(e => e with { Cwd = resolution.ToVirtualPath(e.Cwd) })
                .ToNode();
        }

        // A mount with no shell of its own runs the command in the sandbox, in that mount's
        // directory under /vfs — always there, whether or not the image let it have /<name> as well.
        // The caller named the directory, so it is echoed back as the working directory.
        if (bridge is not null && Rerouted(resolution) is { } sandbox)
        {
            var cwd = $"vfs/{resolution.MountPoint.Trim('/')}/{resolution.RelativePath.Trim('/')}".TrimEnd('/');
            return (await RunBridgedAsync(sandbox, bridge, cwd, command, timeoutSeconds, Permission(arguments), Caller(arguments), cancellationToken))
                .Map(e => e with { Cwd = path })
                .ToNode();
        }

        return (await resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, cancellationToken))
            .Map(e => e with { Cwd = resolution.ToVirtualPath(e.Cwd) })
            .ToNode();
    }

    // The command runs with the other mounts served to it at /vfs: one token for this call, bound
    // to this session's registry and to what its tools may do unasked, completed however the call
    // ends — returned, failed or cancelled — so it never outlives the command it serves.
    private async Task<FsResult<FsExecResult>> RunBridgedAsync(
        IBridgedExecBackend backend,
        IVfsBridge vfs,
        string cwd,
        string command,
        int? timeoutSeconds,
        ToolPermission permission,
        ConversationContext? caller,
        CancellationToken ct)
    {
        var call = vfs.Mint(registry, toolName => (offered?.Invoke(toolName) ?? true) && permission.RunsUnasked(
            FileSystemToolFeature.Callable(toolName)), caller);
        // A call cancelled from this side is revoked from this side, at once: the launcher revokes
        // too, ahead of its kill, but through a daemon and a request that can each fail, and what
        // the dying command flushes must arrive revoked either way.
        using var revokedOnCancel = ct.Register(() => vfs.Revoke(call.Token));
        try
        {
            var exec = await backend.ExecAsync(cwd, command, timeoutSeconds, new VfsBridgeGrant(call.Token), ct);
            var record = vfs.Complete(call.Token);
            return exec.Map(e => e with
            {
                VfsChanges = record.Changes,
                VfsTruncated = record.Truncated.Count > 0 ? record.Truncated : null
            });
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

    // The session's sandbox, for an exec on a mount that has no shell — whether the mount has an
    // exec of its own (the timers' catalog) or none (the vault): either is a directory there. Never
    // an outpost, which is somebody's own computer and keeps its own exec.
    private IBridgedExecBackend? Rerouted(FileSystemResolution resolution) =>
        ExecReach.SandboxFor(registry.GetMounts(), resolution.MountPoint) is { } sandbox
        && registry.Resolve(sandbox.MountPoint).TryGetValue(out var resolved, out _)
            ? resolved.Backend as IBridgedExecBackend
            : null;

    private static ConversationContext? Caller(AIFunctionArguments? arguments) =>
        arguments?.Context?.TryGetValue(typeof(ConversationContext), out var caller) == true
            ? caller as ConversationContext
            : null;

    private static ToolPermission Permission(AIFunctionArguments? arguments) =>
        arguments?.Context?.TryGetValue(ToolPermission.ContextKey, out var view) == true && view is ToolPermission permission
            ? permission
            : ToolPermission.None;
}