using Domain.DTOs.Channel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryRecallHook
{
    // Memory is a person's, so it is keyed by the account the turn acts for and never by the name
    // its sender goes by. A turn acting for no account — the voice guest, a channel that has not
    // moved onto accounts — reads and writes nobody's.
    // The extraction the turn leaves to queue once it worked, or none.
    Task<MemoryExtraction?> EnrichAsync(
        ChatMessage message,
        Account? account,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct);
}