using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;

namespace Infrastructure.Agents;

internal sealed class VirtualFileSystemRegistry : IVirtualFileSystemRegistry
{
    private readonly Dictionary<string, (FileSystemMount Mount, IFileSystemBackend Backend)> _mounts =
        new(StringComparer.OrdinalIgnoreCase);

    public void Mount(FileSystemMount mount, IFileSystemBackend backend) => TryMount(mount, backend);

    // First wins. A mount point is a name the model addresses, so two mounts claiming one is not a
    // merge to resolve but a collision somebody has to lose — and the one already there is the one
    // that was configured, while the challenger is a machine that named itself. Outposts are
    // mounted after the configured filesystems for exactly this reason, so which one loses is
    // decided by mount order rather than by whichever dial happened to finish first.
    //
    // False means the mount was shadowed: perfectly valid, simply not there.
    public bool TryMount(FileSystemMount mount, IFileSystemBackend backend)
    {
        ArgumentNullException.ThrowIfNull(mount);
        return _mounts.TryAdd(mount.MountPoint, (mount, backend));
    }

    public FsResult<FileSystemResolution> Resolve(string virtualPath)
    {
        var match = _mounts
            .Where(m => virtualPath.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase)
                && (virtualPath.Length == m.Key.Length || virtualPath[m.Key.Length] == '/'))
            .OrderByDescending(m => m.Key.Length)
            .Select(m => new FileSystemResolution(
                m.Value.Backend,
                virtualPath[m.Key.Length..].TrimStart('/'),
                m.Key))
            .FirstOrDefault();

        return match is not null
            ? new FsResult<FileSystemResolution>.Ok(match)
            : new FsResult<FileSystemResolution>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message = $"No filesystem mounted for path '{virtualPath}'. Available: {FormatMounts()}",
                Retryable = false,
                Hint = "Virtual paths must start with a mount point; retry with one of the mounts listed."
            });
    }

    public IReadOnlyList<FileSystemMount> GetMounts()
        => _mounts.Values.Select(v => v.Mount).ToList();

    private string FormatMounts()
        => string.Join(", ", _mounts.Values.Select(v => $"{v.Mount.MountPoint} ({v.Mount.Name})"));
}