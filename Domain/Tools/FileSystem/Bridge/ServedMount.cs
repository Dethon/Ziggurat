namespace Domain.Tools.FileSystem.Bridge;

// Where a sandbox command finds a mount it is served: the one FUSE filesystem the exec's daemon
// mounts, a directory per mount named by the mount point without its slash. One spelling for
// everything on this side that composes it — the bridge's table of served names, the exec tool's
// rerouted working directory, the prompt's shell section, and the sandbox telling the launcher's
// links from the names its image already holds. The launcher's half is `MOUNTPOINT` in
// sandbox-runtime/src/vfs/mod.rs: another language and a separate build, so the two are kept by hand.
public static class ServedMount
{
    public const string Root = "/vfs";

    // The name a mount is served under, at Root and — where the image leaves it free — linked at /.
    public static string NameOf(string mountPoint) => mountPoint.Trim('/');

    public static string PathOf(string mountPoint) => $"{Root}/{NameOf(mountPoint)}";
}