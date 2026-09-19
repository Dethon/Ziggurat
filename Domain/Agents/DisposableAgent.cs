using Domain.Contracts;
using Domain.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

    // The skills this agent's session advertises, once it has one, and the conversation as it
    // is persisted: what a skill preload started before the turn is judged over. Empty for an
    // agent with no session built yet and for one whose servers ship no skill, and nothing for
    // an agent that keeps no history — a preload then has nothing to be judged over or against.
    public virtual IReadOnlyList<PromptSkill> GetSkills(AgentSession thread) => [];

    public virtual Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(AgentSession thread, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>([]);
}