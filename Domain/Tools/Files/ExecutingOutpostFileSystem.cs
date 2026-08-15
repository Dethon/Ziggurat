using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.Files;

// An outpost whose operator asked for a shell on their machine. It exists as a separate type
// rather than as a flag on the one above because **a backend advertises an operation by overriding
// it**, and the registrar reflects over the type to find out: a constructor argument could not
// switch exec off, since a backend that overrides exec advertises exec whatever it was handed. So
// the outpost registers one of two types, and the model is offered fs_exec for exactly the
// machines whose operator asked for it.
public sealed class ExecutingOutpostFileSystem(
    string filesystemName,
    IFileSystemClient client,
    string workingDirectory,
    string[] allowedExtensions,
    bool jailed,
    ICommandRunner runner)
    : OutpostFileSystem(filesystemName, client, workingDirectory, allowedExtensions, jailed)
{
    public override string DescribeExec =>
        "Execute a bash command (`bash -lc <command>`) on this machine. The path argument is the "
        + "working directory: **the mount point itself means this outpost's own working directory**, "
        + "not the machine's root, because that is where its operator meant work to happen — a "
        + "deeper path is that directory. A jailed outpost refuses a path outside its working "
        + "directory here exactly as it does everywhere else. The reported cwd is a virtual path. "
        + "Output is truncated at the configured cap; on timeout the process tree is killed. "
        + "Non-zero exit codes come back in the result, not as errors.";

    public override async Task<FsResult<FsExecResult>> ExecAsync(
        string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        // Every other mount reads the mount point as its own root. Here it reads as the working
        // directory: an outpost's root is somebody's whole computer, and a command that lands
        // there because the caller named no directory is not what its operator asked for. Said out
        // loud in DescribeExec above, which is the mount's own answer the exec tool tells the model
        // to rely on.
        var cwd = string.IsNullOrEmpty(path.Trim('/')) || path == "." ? WorkingDirectory : path;
        return Refused<FsExecResult>(cwd) ?? await runner.RunAsync(cwd, command, timeoutSeconds, ct);
    }
}