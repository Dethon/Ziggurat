using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

// A text disk root that can also run commands. Exec is the one thing the sandbox has and the vault
// does not, so it is the one method this adds — and the only reason /sandbox advertises fs_exec.
public class SandboxFileSystem(
    string filesystemName,
    string mountDescription,
    IFileSystemClient client,
    LibraryPathConfig root,
    string[] allowedExtensions,
    ICommandRunner runner,
    string homeDirectory)
    : TextDiskFileSystem(filesystemName, mountDescription, client, root, allowedExtensions)
{
    // The home directory is the one writable, persistent place in the container: the compose volume
    // is mounted there and everything else is either root-owned or reset with the container. It
    // arrives as the container path the server is configured with and is published relative to the
    // configured root, because every path the backend takes is relative to that root. A home the
    // root does not contain — or one equal to it, which would publish the mount root ADR 0025
    // exists to remove — is a configuration error, refused before the server can land anything.
    private readonly string _workspace = WorkspaceUnder(root.BaseLibraryPath, homeDirectory);

    public override string Workspace => _workspace;

    // The sandbox is where a person's attachments belong: a container that is part of the
    // deployment, whose volume nobody else owns. Every other mount declares the default.
    public override bool IsLandingTarget => true;

    // Everything at the container's root but the links the launcher made into /vfs: a mount named
    // like one of these — `etc`, `home`, or `sandbox`, this mount's own alias of the root — is
    // served to a command only at /vfs/<name>. Read once: the image's root does not change under a
    // running server.
    private readonly Lazy<IReadOnlyList<string>> _occupied = new(() => Occupied(root.BaseLibraryPath));

    public override IReadOnlyList<string>? OccupiedNames => _occupied.Value;

    private static IReadOnlyList<string> Occupied(string containerRoot)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFileSystemEntries(containerRoot)
                    .Where(entry => new FileInfo(entry).LinkTarget is not { } target
                                    || !target.StartsWith("/vfs/", StringComparison.Ordinal))
                    .Select(entry => Path.GetFileName(entry))
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A container that is part of the deployment: what a command breaks here, a restart repairs.
    public override ShellReach? ShellReach => DTOs.ShellReach.Contained;

    private static string WorkspaceUnder(string containerRoot, string homeDirectory)
    {
        var relative = Path.GetRelativePath(containerRoot, homeDirectory).Replace('\\', '/');
        return relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)
               || Path.IsPathRooted(relative)
            ? throw new ArgumentException(
                $"HomeDir '{homeDirectory}' does not sit under ContainerRoot '{containerRoot}', "
                + "so it cannot be published as this mount's workspace.",
                nameof(homeDirectory))
            : relative;
    }

    public override string DescribeExec =>
        "Execute a bash command (`bash -lc <command>`) inside the sandbox container. The path "
        + "argument is a path under the sandbox root that becomes the CWD; empty string or \".\" "
        + "is the root itself. Inside the command, a mount-prefixed path and the container's own "
        + "path name the same file. The reported cwd is relative to the sandbox root. Output is "
        + "truncated at the configured cap. On timeout the process tree is killed. Non-zero exit "
        + "codes are returned in the result, not as errors. Where the call carries the agent's "
        + "bridge, the calling session's other mounts are served at /vfs/<name>, linked at /<name> "
        + "wherever the image leaves the name free.";

    // The token this caller's exec carries, if the agent minted one; the server's own instance has
    // none, and the registrar asks for a view per call.
    private VfsBridgeGrant? _bridge;

    // A shallow copy with the token swapped, so the view keeps every dependency the mount was built
    // with, as HaFileSystem's caller view does.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (SandboxFileSystem)MemberwiseClone();
        view._bridge = caller.Bridge;
        return view;
    }

    public override Task<FsResult<FsExecResult>> ExecAsync(
        string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        runner.RunAsync(path, command, timeoutSeconds, ct, _bridge);
}