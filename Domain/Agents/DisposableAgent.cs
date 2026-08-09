using Domain.Contracts;
using Microsoft.Agents.AI;

namespace Domain.Agents;

public abstract class DisposableAgent : AIAgent, IAsyncDisposable
{
    public abstract ValueTask DisposeAsync();
    public abstract ValueTask DisposeThreadSessionAsync(AgentSession thread);

    // The filesystems this agent's session mounted, once it has one. Null for an agent with no
    // session built yet and for one with no filesystem at all; a caller that wants to put a file
    // somewhere the model can reach has to cope with both, because whether an agent has a sandbox
    // follows its configured servers.
    public virtual IVirtualFileSystemRegistry? GetFileSystemRegistry(AgentSession thread) => null;

    // Optional: pre-initialize the per-conversation session (MCP connections + tool
    // discovery) so that setup overlaps with first-message handling instead of
    // blocking the first LLM turn. No-op for agents without expensive session setup.
    public virtual Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
        => Task.CompletedTask;
}