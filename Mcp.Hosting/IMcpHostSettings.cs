using Domain.Security;

namespace Mcp.Hosting;

// What the host needs from a server's settings: the secret its /mcp asks for. A constraint on
// AddMcpHost rather than an argument to it, so a server cannot be hosted without its settings
// saying which secret guards it — a new server that forgot fails to compile, not to be gated.
//
// A deployment server binds the section from configuration, which is the one property it adds.
// The outpost answers with the secret it registers with: it is not part of the deployment and
// never holds the deployment's.
public interface IMcpHostSettings
{
    McpGateSettings Mcp { get; }
}