using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// Asks which skills a request needs before the model's first call, so the ones it is sure of
// are in the conversation as if loaded. Only ever a head start: below the bar nothing happens
// and the model loads for itself as it does today (docs/adr/0039, refined 2026-09-18).
public interface ISkillPreloader
{
    Task<SkillPreload> PreloadAsync(SkillPreloadRequest request, CancellationToken ct);
}

// The current request's text alone — no prior turns, no assistant text, because the judge's
// accuracy falls with irrelevant state and a follow-up's skill is normally already loaded. The
// history is read only for which skills it already holds.
public sealed record SkillPreloadRequest(
    string Text,
    IReadOnlyList<PromptSkill> Skills,
    IEnumerable<ChatMessage> History)
{
    // The turn's config patch model, if any: a turn addressed to the local box never reaches the
    // judge, the boundary ADR 0042 drew for extraction.
    public string? ConfigPatchModel { get; init; }

    // Where the request came from, for the event each judgment publishes. Optional: a run with
    // no channel — an eval, a worker — is judged all the same.
    public string? AgentId { get; init; }

    public string? ChannelId { get; init; }

    public string? ConversationId { get; init; }
}

public sealed record SkillPreload(
    SkillPreloadOutcome Outcome,
    IReadOnlyList<PromptSkill> Skills,
    TimeSpan? Latency = null,
    SkillJudgment? Judgment = null)
{
    public static readonly SkillPreload NotAsked = new(SkillPreloadOutcome.NotAsked, []);

    public static readonly SkillPreload SkippedLemonade = new(SkillPreloadOutcome.SkippedLemonade, []);
}

// What the judge said, kept whole for the telemetry and the eval's scorecard.
public sealed record SkillJudgment(
    string Choice,
    double ChoiceConfidence,
    IReadOnlyDictionary<string, double> Needs,
    int InputTokens,
    string Model);

public enum SkillPreloadOutcome
{
    // No question was worth asking: the feature is off, nothing is advertised, or everything
    // advertised is already in the conversation. Publishes nothing.
    NotAsked,

    Preloaded,

    // Asked and answered, but nothing reached a bar.
    Abstained,

    // A confident none.
    None,

    Deadline,

    Error,

    SkippedLemonade
}