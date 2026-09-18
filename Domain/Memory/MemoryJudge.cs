using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Microsoft.Extensions.AI;

namespace Domain.Memory;

// Who the judgment is about, for the event it publishes.
public sealed record MemoryJudgmentContext(string UserId, string? AgentId = null, string? ConversationId = null);

// Gate: whether the extractor is asked at all. Skip is only ever true on an answer.
public sealed record GateVerdict(bool Skip, IReadOnlyDictionary<string, double> Scores)
{
    public static readonly GateVerdict Extract = new(false, new Dictionary<string, double>());
}

// Verify: whether a candidate is stored. Store is true on an absence, as today.
public sealed record VerifyVerdict(bool Store, IReadOnlyDictionary<string, double> Scores)
{
    public static readonly VerifyVerdict Stored = new(true, new Dictionary<string, double>());
}

// The three memory judgments, each a small typed question to Jev over the extraction window as
// fields, and each failing toward what happens today: an unsure, late or absent answer extracts,
// stores and merges as if nothing had been asked. The questions are worded as in the probe
// script (`.scratch/jev-memory-judgments/probe/jev_memory_probe.py`), which is what the bars in
// MemoryJudgmentSettings were measured against.
//
// Jev reads the window as `context` and `current`, never the rendered string with its markers,
// and never a tool result: ExtractionWindow.Build leaves tool traffic out before it gets here.
public sealed class MemoryJudge(
    IJudge judge,
    MemoryJudgmentSettings settings,
    TimeProvider timeProvider,
    IMetricsPublisher? metricsPublisher = null)
{
    public const string FactQuestionId = "fact";
    public const string PreferenceQuestionId = "preference";
    public const string InstructionQuestionId = "instruction";

    public const string SupportedQuestionId = "supported";
    public const string AboutUserQuestionId = "about_user";
    public const string DurableQuestionId = "durable";
    public const string NotAQuestionQuestionId = "not_a_question";

    private const string ReadWithContext = "`current` — read with `context` only to resolve what it refers to —";

    public static readonly IReadOnlyDictionary<string, string> GateQuestions = new Dictionary<string, string>
    {
        [FactQuestionId] = "Does " + ReadWithContext + " state a lasting fact about the person who wrote it: their identity, work, home, health, family, relationships, possessions, skills or ongoing projects? A question, a command to a device, or something true only today is not a lasting fact.",
        [PreferenceQuestionId] = "Does " + ReadWithContext + " state a standing preference, habit or routine of the person who wrote it, something they say holds generally and not just for this request?",
        [InstructionQuestionId] = "Does " + ReadWithContext + " give the assistant a standing instruction for the future, such as 'always', 'never', 'from now on', or a correction of something the assistant has wrong about the person?"
    };

    private const string CandidateIsANote = "`candidate` is a note an assistant wants to save about the person who wrote `current`.";

    public static readonly IReadOnlyDictionary<string, string> VerifyQuestions = new Dictionary<string, string>
    {
        [SupportedQuestionId] = CandidateIsANote + " Did that person actually state what `candidate` says, in `current` (read with `context` only to resolve what it refers to)? Something only the assistant said, or something the note adds that the person never said, is not stated by them.",
        [AboutUserQuestionId] = CandidateIsANote + " Is `candidate` about that person — themselves, their life, their people, their preferences or their instructions — rather than about the assistant, the system, or the world in general?",
        [DurableQuestionId] = "`candidate` is a note an assistant wants to save. Read six months from now, with no knowledge of this conversation, would `candidate` still tell something true and useful about the person? A one-off event, a mood, today's task or a single request would not.",
        [NotAQuestionQuestionId] = CandidateIsANote + " Is `candidate` something other than a restatement that the person asked, requested or ordered something? A note that only records that they asked or requested something is a restatement."
    };

    public bool Enabled => settings.Enabled;

    // The window as Jev reads it: every turn but the last as `context`, the last as `current`.
    public static JsonObject State(IReadOnlyList<ChatMessage> window)
    {
        var context = window
            .Take(window.Count - 1)
            .Select(m => (JsonNode?)new JsonObject
            {
                ["role"] = m.Role == ChatRole.Assistant ? "assistant" : "user",
                ["text"] = m.Text
            })
            .ToArray();

        return new JsonObject
        {
            ["context"] = new JsonArray(context),
            ["current"] = window.Count > 0 ? window[^1].Text : string.Empty
        };
    }

    public async Task<GateVerdict> GateAsync(
        IReadOnlyList<ChatMessage> window, MemoryJudgmentContext context, CancellationToken ct)
    {
        if (!settings.Enabled || window.Count == 0)
        {
            return GateVerdict.Extract;
        }

        var (outcome, latency) = await AskAsync(State(window), Nouls(GateQuestions), ct);
        if (outcome is not JudgmentOutcome.Answered answered)
        {
            PublishAbsence(MemoryJudgmentKinds.Gate, context, outcome, latency);
            return GateVerdict.Extract;
        }

        var scores = Scores(answered.Judgment, GateQuestions.Keys);

        // Skip only on a full answer: a question the judge did not answer is not a low score.
        var skip = GateQuestions.Keys.All(id =>
            scores.TryGetValue(id, out var p) && p <= settings.Gate.SkipAtOrBelow);

        Publish(new MemoryJudgmentEvent
        {
            Kind = MemoryJudgmentKinds.Gate,
            UserId = context.UserId,
            AgentId = context.AgentId,
            ConversationId = context.ConversationId,
            Answered = true,
            Scores = scores,
            Skipped = skip,
            DurationMs = (long)latency.TotalMilliseconds,
            InputTokens = answered.Judgment.Usage.InputTokens,
            Model = answered.Judgment.Model
        });

        return new GateVerdict(skip, scores);
    }

    private async Task<(JudgmentOutcome Outcome, TimeSpan Latency)> AskAsync(
        JsonObject state, IReadOnlyDictionary<string, JudgmentQuestion> questions, CancellationToken ct)
    {
        // No deadline of its own: nothing here is on a reply path, and the client's own timeout
        // bounds a call that hangs.
        var started = timeProvider.GetTimestamp();
        var outcome = await judge.JudgeAsync(new JudgmentRequest(state, questions), ct);
        return (outcome, timeProvider.GetElapsedTime(started));
    }

    private static IReadOnlyDictionary<string, JudgmentQuestion> Nouls(IReadOnlyDictionary<string, string> questions) =>
        questions.ToDictionary(q => q.Key, q => (JudgmentQuestion)new NoulQuestion(q.Value), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> Scores(Judgment judgment, IEnumerable<string> ids) =>
        ids.Select(id => (Id: id, Answer: judgment.Answers.GetValueOrDefault(id) as NoulAnswer))
            .Where(a => a.Answer is not null)
            .ToDictionary(a => a.Id, a => a.Answer!.Probability, StringComparer.Ordinal);

    // An unconfigured judge is the feature off, not a thing to count; any other absence is.
    private void PublishAbsence(string kind, MemoryJudgmentContext context, JudgmentOutcome outcome, TimeSpan latency)
    {
        if (outcome is JudgmentOutcome.Absent { Reason: AbsenceReason.Unconfigured })
        {
            return;
        }

        Publish(new MemoryJudgmentEvent
        {
            Kind = kind,
            UserId = context.UserId,
            AgentId = context.AgentId,
            ConversationId = context.ConversationId,
            Answered = false,
            DurationMs = (long)latency.TotalMilliseconds
        });
    }

    private void Publish(MemoryJudgmentEvent evt) => metricsPublisher?.Publish(evt);
}