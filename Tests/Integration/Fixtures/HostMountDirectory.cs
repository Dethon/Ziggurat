namespace Tests.Integration.Fixtures;

// A host directory for a container to bind-mount. Docker Desktop on WSL stages every bind mount under
// /mnt/wsl/docker-desktop-bind-mounts by its source path and never takes the staging down, so a fresh
// temp directory per test left a mount behind for good — dozens a run, and every mount in that table
// is rescanned by systemd on each container start, so the whole suite ran slower by the day. A path
// it has seen before reuses its staging, so these are a few stable directories per name, each claimed
// across test processes by a lock file (as PortClaims claims ports) and emptied for its next holder.
// The directory itself is never deleted: one made again is another inode, which the old staging no
// longer shows the container.
public sealed class HostMountDirectory : IDisposable
{
    private static readonly string _machineRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ziggurat-test-mounts");

    private readonly FileStream _claim;

    private HostMountDirectory(string path, FileStream claim) => (Path, _claim) = (path, claim);

    public string Path { get; }

    public static HostMountDirectory Claim(string name) => Claim(name, _machineRoot);

    // A slot whose leftovers cannot be removed (a container wrote them as another user) is passed over
    // rather than handed out with somebody else's files in it.
    internal static HostMountDirectory Claim(string name, string root)
    {
        Directory.CreateDirectory(root);
        for (var slot = 0; ; slot++)
        {
            var path = System.IO.Path.Combine(root, $"{name}-{slot}");
            FileStream claim;
            try
            {
                claim = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                continue;
            }

            Directory.CreateDirectory(path);
            if (TryEmpty(path))
            {
                return new HostMountDirectory(path, claim);
            }

            claim.Dispose();
        }
    }

    public void Dispose()
    {
        TryEmpty(Path);
        _claim.Dispose();
    }

    private static bool TryEmpty(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            foreach (var file in directory.EnumerateFiles())
            {
                file.Delete();
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                child.Delete(recursive: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}