using System.Text.Json.Nodes;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// Asks which skills a request needs before the model's first call, so the ones it is sure of
// are in the conversation as if loaded. Only ever a head start: below the bar nothing happens
// and the model loads for itself as it does today (docs/adr/0039, refined 2026-09-18).
public interface ISkillPreloader
{
    Task<SkillPreload> PreloadAsync(SkillPreloadRequest request, CancellationToken ct);

    // Whether asking is off outright — the flag down, in this deployment's own settings. A caller
    // that must read a whole thread from Redis just to say which skills are already loaded asks
    // this first, so a deployment with the feature off pays exactly what it paid before the
    // preload existed. Stated as the negative deliberately: false is "carry on and ask", which is
    // what an implementation that has not thought about it — a mock, a decorator — should do.
    // Not "will it preload": a judge with no key still answers that, and answers it as an absence.
    bool IsOff => false;
}

// The current request's text alone — no prior turns, no assistant text, because the judge's
// accuracy falls with irrelevant state and a follow-up's skill is normally already loaded. The
// history is read only for which skills it already holds.
public sealed record SkillPreloadRequest(
    string Text,
    IReadOnlyList<PromptSkill> Skills,
    IEnumerable<ChatMessage> History)
{
    // The model the turn asked for, handed to the judge's client, which sends nothing for a turn
    // addressed to the local box. A worker's request has no patch; it names its parent turn's.
    public string? ConfigPatchModel { get; init; }

    // Where the request came from, for the event each judgment publishes. Optional: a run with
    // no channel — an eval, a worker — is judged all the same.
    public string? AgentId { get; init; }

    public string? ChannelId { get; init; }

    public string? ConversationId { get; init; }

    // How a preload makes the reads a skill declares (SkillDeclaration.PreloadReads): the turn's
    // own file_read, over the session's mounts, answering what the tool would have answered the
    // model. Null where there is no filesystem to read — a host without mounts, a session not
    // yet built — and the preload is then the skills alone.
    public PreloadFileReader? Reader { get; init; }
}

public delegate Task<JsonNode?> PreloadFileReader(string path, CancellationToken ct);

public sealed record SkillPreload(
    SkillPreloadOutcome Outcome,
    IReadOnlyList<PromptSkill> Skills,
    TimeSpan? Latency = null,
    SkillJudgment? Judgment = null)
{
    public static readonly SkillPreload NotAsked = new(SkillPreloadOutcome.NotAsked, []);

    public static readonly SkillPreload SkippedLemonade = new(SkillPreloadOutcome.SkippedLemonade, []);

    // The reads made beside the skills, in the order the skills declared them. Only reads that
    // answered: a read that failed is not here, and the body tells the model to make it.
    public IReadOnlyList<SkillPreloadRead> Reads { get; init; } = [];
}

// One file the host read for a preloaded skill, with what the read tool answered.
public sealed record SkillPreloadRead(string Skill, string Path, JsonNode Result);

// What the judge said, kept whole for the telemetry and the eval's scorecard.
public sealed record SkillJudgment(
    string Choice,
    double ChoiceConfidence,
    IReadOnlyDictionary<string, double> Needs,
    int InputTokens,
    decimal? Cost,
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