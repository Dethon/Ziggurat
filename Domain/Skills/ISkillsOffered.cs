using Domain.Prompts;

namespace Domain.Skills;

// The skills each agent's last warmed session offered, per household. The skills arrive with the
// servers a warmup dials, so a group's first turn could only be judged once its warmup ended; most
// voice turns open a conversation of their own and paid that wait on top of the judgment. A later
// group judges over what the same agent offered last time while its own warmup is still dialling,
// and keeps only what its session turns out to offer (ConversationGroup.PreloadAsync).
public interface ISkillsOffered
{
    IReadOnlyList<PromptSkill>? Last(string agentId, string? household);

    void Remember(string agentId, string? household, IReadOnlyList<PromptSkill> skills);
}
