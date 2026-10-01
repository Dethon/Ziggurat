using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Where a call to exec would run, answered from the mounts of the session that built the tool.
// It travels on the exec function itself, so the approval client reads it off the very call it
// is deciding: an agent can hold several sessions, and a lookup held anywhere else would have to
// guess which one a call came from.
public sealed class ExecReach(Func<string, ShellReach?> reachOf)
{
    // Null for a path no mount serves, and for a mount with no shell (/ha).
    public ShellReach? Of(string path) => reachOf(path);

    public static ExecReach Over(IVirtualFileSystemRegistry registry) => new(path =>
        registry.Resolve(path).TryGetValue(out var resolution, out _)
            ? registry.GetMounts()
                .FirstOrDefault(m => string.Equals(m.MountPoint, resolution.MountPoint, StringComparison.OrdinalIgnoreCase))
                ?.ShellReach
            : null);

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