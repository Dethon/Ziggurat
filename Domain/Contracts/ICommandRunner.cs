using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public interface ICommandRunner
{
    // `bridge`: the call token the agent minted for this exec, null where it minted none — said by
    // every caller, so a new one cannot run a command without deciding. A runner that can serve the
    // other mounts to the command (the launcher's) does; the in-process one ignores it.
    Task<FsResult<FsExecResult>> RunAsync(
        string path, string command, int? timeoutSeconds, VfsBridgeGrant? bridge, CancellationToken cancellationToken);

    // A path the server's own file tools just wrote, as a full container path. Where commands run
    // as a user other than the server's (the launcher's), it is handed to that user before the next
    // command, so a command may chmod what a file tool made; a runner with one user ignores it.
    void Wrote(string fullPath);
}