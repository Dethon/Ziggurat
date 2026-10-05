using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public interface ICommandRunner
{
    // `bridge`: the call token the agent minted for this exec, null where it minted none — said by
    // every caller, so a new one cannot run a command without deciding. A runner that can serve the
    // other mounts to the command (the launcher's) does; the in-process one ignores it.
    Task<FsResult<FsExecResult>> RunAsync(
        string path, string command, int? timeoutSeconds, VfsBridgeGrant? bridge, CancellationToken cancellationToken);
}