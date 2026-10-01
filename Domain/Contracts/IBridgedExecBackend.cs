using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

// A backend whose exec can carry a call token to the server behind it: the agent's proxy of a
// remote mount. The exec tool asks for this beside the plain exec, so a backend that cannot carry
// one simply runs commands with no bridge, and nothing about the wire schema changes.
public interface IBridgedExecBackend
{
    Task<FsResult<FsExecResult>> ExecAsync(
        string path, string command, int? timeoutSeconds, VfsBridgeGrant grant, CancellationToken ct);
}