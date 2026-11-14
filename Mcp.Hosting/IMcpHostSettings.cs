using Domain.Security;

namespace Mcp.Hosting;

// What the host needs from a server's settings: the secret its /mcp asks for. A constraint on
// AddMcpHost rather than an argument to it, so a server cannot be hosted without its settings
// saying which secret guards it — a new server that forgot fails to compile, not to be gated.
//
// A deployment server binds the section from configuration, which is the one property it adds.
// The outpost computes it — its tunnel, from its flags and its credential — so no variable of the
// deployment's can reach it.
public interface IMcpHostSettings
{
    McpGateSettings Mcp { get; }

    // Whether a tool call must carry the agent's call token for this server (CallTokenGate). Every
    // server says yes by saying nothing — the outpost too, since its tunnel delivers the agent's keys.
    // Not a setting: nothing a deployment binds can switch the check off.
    bool ChecksCallTokens => true;
}