using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Where a call to exec would run, answered from the mounts of the session that built the tool.
// It travels on the exec function itself, so the approval client reads it off the very call it
// is deciding: an agent can hold several sessions, and a lookup held anywhere else would have to
// guess which one a call came from.
public sealed partial class ExecReach(Func<string, string, ShellReach?> reachOf)
{
    // Null for a path no mount serves, and for a mount with no shell (/ha) that no sandbox runs.
    public ShellReach? Of(string path, string command) => reachOf(path, command);

    // `reroutes`: an exec on a mount with no shell of its own runs in the session's sandbox
    // (VfsExecTool), so it reaches as far as the sandbox does — when there is a sandbox to run it in.
    // Except one action file run by itself, which is what exec on that mount was before it was
    // rerouted (the mount's own catalog) and is not screened: each action is still gated by the
    // bridge, and nothing else can run, because a served mount's only executables are its action
    // files. Anything more is a sandbox script and reaches as far as one.
    public static ExecReach Over(IVirtualFileSystemRegistry registry, bool reroutes = false) => new((path, command) =>
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out _))
        {
            return null;
        }

        var mounts = registry.GetMounts();
        return MountAt(mounts, resolution.MountPoint)?.ShellReach
               ?? (reroutes && SandboxFor(mounts, resolution.MountPoint) is not null && !BareAction().IsMatch(command)
                   ? ShellReach.Contained
                   : null);
    });

    // The sandbox an exec on the mount at `mountPoint` runs in instead of the mount: the session's
    // contained shell, when the mount is one a command is served. Null for a mount with a shell of
    // its own, for an outpost, and for a session with no sandbox. The exec tool routes by this and
    // the screen judges by it, so the two cannot disagree about where a command runs.
    public static FileSystemMount? SandboxFor(IReadOnlyList<FileSystemMount> mounts, string mountPoint) =>
        MountAt(mounts, mountPoint) is { } mount && VfsCall.IsServed(mount) ? Sandbox(mounts) : null;

    // The session's contained shell: the one mount a command is the deployment's own to run on.
    public static FileSystemMount? Sandbox(IReadOnlyList<FileSystemMount> mounts) =>
        mounts.FirstOrDefault(m => m.ShellReach == ShellReach.Contained);

    // Matched as the registry matches a mount point, without regard to case.
    public static FileSystemMount? MountAt(IReadOnlyList<FileSystemMount> mounts, string mountPoint) =>
        mounts.FirstOrDefault(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

    // `./name args`, one simple command: nothing that chains, pipes, redirects, backgrounds or
    // substitutes, and no second line. Quotes are fine — arguments are data to the action.
    [GeneratedRegex(@"\A[ \t]*\./[^\s/;&|<>()$`\\]+([ \t]+[^;&|<>()$`\n\r]*)?\z")]
    private static partial Regex BareAction();

    // The exec function as the session offers it, answering GetService<ExecReach>() with this
    // lookup. Everything the model sees is the inner function's.
    public static AIFunction Carried(AIFunction exec, ExecReach reach) => new CarryingFunction(exec, reach);

    private sealed class CarryingFunction(AIFunction inner, ExecReach reach) : DelegatingAIFunction(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ExecReach)
                ? reach
                : base.GetService(serviceType, serviceKey);
    }
}