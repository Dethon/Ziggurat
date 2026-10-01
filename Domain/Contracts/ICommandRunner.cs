using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public interface ICommandRunner
{
    // `bridge`: the call token the agent minted for this exec, where it minted one. A runner that
    // can serve the other mounts to the command (the launcher's) does; the in-process one ignores it.
    Task<FsResult<FsExecResult>> RunAsync(
        string path, string command, int? timeoutSeconds, CancellationToken cancellationToken, VfsBridgeGrant? bridge = null);
}