using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's agents as its owners manage them (.claude/rules/admin.md, "Agents"): of each, only
// its name and custom instructions are the owner's; everything else is its template's or its seeded
// definition's, so it is shown and never sent back. Answered as the household's turns run them — a
// household that saved none has the two starting templates — beside the shipped templates an agent
// can be added from and the longest custom instructions a save takes.
//
// VoiceAgent is the agent the owner marked to answer the household's satellites, null for none;
// AnswersAloud the one that does (HouseholdAgents.VoiceAgent: the marked one, else the household's agent
// of the voice template), null when none of its agents does.
[PublicAPI]
public sealed record AdminAgents(
    string? DefaultAgent,
    IReadOnlyList<AdminAgent> Agents,
    IReadOnlyList<AdminAgentTemplate> Templates,
    int InstructionsLength,
    string? VoiceAgent = null,
    string? AnswersAloud = null);

// Template is the shipped template it was made from, or null for a definition seeded for the
// household (the home's own agents). Model is shown, never edited.
[PublicAPI]
public sealed record AdminAgent(
    string Id, string Name, string? CustomInstructions, string? Template, string? Description, string Model,
    bool MayAnswerAloud = true)
{
    public const int NameLength = 100;
}

[PublicAPI]
public sealed record AdminAgentTemplate(string Name, string AgentName, string? Description);

// What an owner sends to change an agent: its name and its custom instructions, both whole. Nothing
// else about an agent is theirs to send.
[PublicAPI]
public sealed record AgentEdit(string Name, string? CustomInstructions)
{
    // The name and instructions the page loaded, so another owner's change to the other field
    // since then stands (AdminEdit); none is saved as sent.
    public AgentEdit? Loaded { get; init; }
}

// An agent added from a shipped template, under the name the owner gives it.
[PublicAPI]
public sealed record AgentAddition(string Template, string Name);

// One of an agent's earlier versions: the name and instructions it had before a change replaced them,
// when that change was made and by whom.
[PublicAPI]
public sealed record AdminAgentVersion(
    string Id, string Name, string? CustomInstructions, DateTimeOffset ReplacedAt, string ReplacedBy);
// Which of the household's agents answers its satellites; null marks none.
[PublicAPI]
public sealed record VoiceAgentChoice(string? AgentId);