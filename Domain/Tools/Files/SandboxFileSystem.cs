using Domain.Contracts;
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
        + "codes are returned in the result, not as errors.";

    public override Task<FsResult<FsExecResult>> ExecAsync(
        string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        runner.RunAsync(path, command, timeoutSeconds, ct);
}