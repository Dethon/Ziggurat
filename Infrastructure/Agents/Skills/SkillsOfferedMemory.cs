using System.Collections.Concurrent;
using Domain.Prompts;
using Domain.Skills;

namespace Infrastructure.Agents.Skills;

// In process and never expired: it holds one list per agent and household, replaced by every warmed
// session, and a list that is out of date costs only a preload the session turns down.
public sealed class SkillsOfferedMemory : ISkillsOffered
{
    private readonly ConcurrentDictionary<(string AgentId, string? Household), IReadOnlyList<PromptSkill>> _last = new();

    public IReadOnlyList<PromptSkill>? Last(string agentId, string? household) =>
        _last.TryGetValue((agentId, household), out var skills) ? skills : null;

    public void Remember(string agentId, string? household, IReadOnlyList<PromptSkill> skills) =>
        _last[(agentId, household)] = skills;
}