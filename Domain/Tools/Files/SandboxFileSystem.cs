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
    ICommandRunner runner)
    : TextDiskFileSystem(filesystemName, mountDescription, client, root, allowedExtensions)
{
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