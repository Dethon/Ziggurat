using Domain.Contracts;
using Domain.DTOs;
using Domain.Outposts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Where a call to exec would run, answered from the mounts of the session that built the tool.
// It travels on the exec function itself, so the approval client reads it off the very call it
// is deciding: an agent can hold several sessions, and a lookup held anywhere else would have to
// guess which one a call came from.
public sealed class ExecReach(Func<string, ShellReach?> reachOf)
{
    // Null for a path no mount serves, and for a mount with no shell (/ha) that no sandbox runs.
    public ShellReach? Of(string path) => reachOf(path);

    // `reroutes`: an exec on a mount with no shell of its own runs in the session's sandbox
    // (VfsExecTool), so it reaches as far as the sandbox does — when there is a sandbox to run it in.
    public static ExecReach Over(IVirtualFileSystemRegistry registry, bool reroutes = false) => new(path =>
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out _))
        {
            return null;
        }

        var mounts = registry.GetMounts();
        var mount = mounts.FirstOrDefault(m =>
            string.Equals(m.MountPoint, resolution.MountPoint, StringComparison.OrdinalIgnoreCase));
        return mount?.ShellReach
               ?? (reroutes && mount is not null && !OutpostMountPoint.Addresses(mount.MountPoint)
                   && mounts.Any(m => m.ShellReach == ShellReach.Contained)
                   ? ShellReach.Contained
                   : null);
    });

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