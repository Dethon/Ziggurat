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

// The four-way choice a pair judgment answers with. A link is the first two.
public static class PairRelations
{
    public const string Same = "same";
    public const string Updates = "updates";
    public const string Distinct = "distinct";
    public const string Unrelated = "unrelated";

    public static bool IsLink(string relation) => relation is Same or Updates;

    // A relation this side defined. Anything else is a body that did not answer the question.
    public static bool IsKnown(string relation) => relation is Same or Updates or Distinct or Unrelated;
}

// Pairs: which memories of a cosine cluster are linked — the same fact, or one updating the
// other — as the connected components of those links, singletons dropped. Unanswered, the
// cluster goes to the merge model as cosine made it.
public sealed record PairVerdict(bool Answered, IReadOnlyList<IReadOnlyList<MemoryEntry>> Linked)
{
    public static readonly PairVerdict Unanswered = new(false, []);
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

    public const string RelationInstructions =
        "`memories` are notes saved about the same person. How are `memories[{0}]` and `memories[{1}]` related?";

    public static readonly IReadOnlyDictionary<string, string> RelationCriteria = new Dictionary<string, string>
    {
        [PairRelations.Same] = "They state the same fact, or one is a more specific or less specific version of the other; keeping both would be redundant.",
        [PairRelations.Updates] = "They are about the same attribute of the person but disagree: one replaces, corrects or contradicts the other.",
        [PairRelations.Distinct] = "They are about a similar topic but state different facts that are both true at once; both should be kept.",
        [PairRelations.Unrelated] = "They are about different things."
    };

    public bool Enabled => settings.Enabled;

    // How many of a cluster one pairs request is asked about; the rest wait for a later pass.
    public int MaxClusterMemories => settings.Pairs.MaxClusterMemories;

    public static string PairQuestionId(int i, int j) => $"{i}-{j}";

    // One request per cluster: the memories by index, one four-way choice per pair. The state and
    // the questions carry no memory id — there is nothing for a model to retype, and the answer is
    // mapped back by index here.
    public static JudgmentRequest PairRequest(IReadOnlyList<MemoryEntry> cluster)
    {
        var state = new JsonObject
        {
            ["memories"] = new JsonArray(cluster.Select(m => (JsonNode?)m.Content).ToArray())
        };

        var questions = Pairs(cluster.Count)
            .ToDictionary(
                pair => PairQuestionId(pair.i, pair.j),
                pair => (JudgmentQuestion)new ChoiceQuestion(
                    string.Format(RelationInstructions, pair.i, pair.j), RelationCriteria),
                StringComparer.Ordinal);

        return new JudgmentRequest(state, questions);
    }

    public async Task<PairVerdict> RelateAsync(
        IReadOnlyList<MemoryEntry> cluster, MemoryJudgmentContext context, CancellationToken ct)
    {
        if (!settings.Enabled || cluster.Count < 2)
        {
            return PairVerdict.Unanswered;
        }

        var (outcome, latency) = await AskAsync(PairRequest(cluster), ct);
        if (outcome is not JudgmentOutcome.Answered answered)
        {
            PublishAbsence(MemoryJudgmentKinds.Pairs, context, outcome, latency);
            return PairVerdict.Unanswered;
        }

        var pairs = Pairs(cluster.Count)
            .Select(pair => (pair.i, pair.j, Answer: answered.Judgment.Answers.GetValueOrDefault(PairQuestionId(pair.i, pair.j)) as ChoiceAnswer))
            .ToList();

        // Every pair, or none of them. A pair left unanswered — or answered with a relation nobody
        // defined — says nothing about those two memories, exactly as an unanswered question does
        // for the gate and the check. Reading that silence as "not linked" would keep a cluster
        // from the merge model it reached before Jev existed: a failure away from the old
        // behaviour rather than toward it. Absent, the whole chunk goes as cosine made it.
        if (pairs.Any(p => p.Answer is null || !PairRelations.IsKnown(p.Answer.Choice)))
        {
            PublishAbsence(MemoryJudgmentKinds.Pairs, context, new JudgmentOutcome.Absent(AbsenceReason.Error), latency);
            return PairVerdict.Unanswered;
        }

        var linked = Components(cluster.Count, pairs
            .Where(p => PairRelations.IsLink(p.Answer!.Choice))
            .Select(p => (p.i, p.j)))
            .Select(component => (IReadOnlyList<MemoryEntry>)component.Select(i => cluster[i]).ToList())
            .ToList();

        Publish(Answered(MemoryJudgmentKinds.Pairs, context, answered.Judgment, latency) with
        {
            MemoryIds = cluster.Select(m => m.Id).ToList(),
            Relations = pairs.ToDictionary(p => PairQuestionId(p.i, p.j), p => p.Answer!.Choice, StringComparer.Ordinal),
            Scores = pairs.ToDictionary(p => PairQuestionId(p.i, p.j), p => p.Answer!.Confidence, StringComparer.Ordinal),
            Linked = linked.Select(component => (IReadOnlyList<string>)component.Select(m => m.Id).ToList()).ToList()
        });

        return new PairVerdict(true, linked);
    }

    // Every unordered pair of indices, i < j, in the order the questions are keyed.
    private static IEnumerable<(int i, int j)> Pairs(int count) =>
        Enumerable.Range(0, count).SelectMany(i => Enumerable.Range(i + 1, count - i - 1).Select(j => (i, j)));

    // The connected components of the link graph over indices, singletons dropped, each in index
    // order and the components in the order of their first member.
    private static IEnumerable<IReadOnlyList<int>> Components(int count, IEnumerable<(int I, int J)> links)
    {
        var parent = Enumerable.Range(0, count).ToArray();

        int find(int x) => parent[x] == x ? x : parent[x] = find(parent[x]);

        foreach (var (i, j) in links)
        {
            parent[find(i)] = find(j);
        }

        return Enumerable.Range(0, count)
            .GroupBy(find)
            .Select(g => (IReadOnlyList<int>)g.OrderBy(i => i).ToList())
            .Where(component => component.Count >= 2)
            .OrderBy(component => component[0]);
    }

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

        Publish(Answered(MemoryJudgmentKinds.Gate, context, answered.Judgment, latency) with
        {
            Scores = scores,
            Skipped = skip
        });

        return new GateVerdict(skip, scores);
    }

    public async Task<VerifyVerdict> VerifyAsync(
        IReadOnlyList<ChatMessage> window, ExtractionCandidate candidate, MemoryJudgmentContext context, CancellationToken ct)
    {
        if (!settings.Enabled)
        {
            return VerifyVerdict.Stored;
        }

        var state = State(window);
        state["candidate"] = candidate.Content;

        var (outcome, latency) = await AskAsync(state, Nouls(VerifyQuestions), ct);
        if (outcome is not JudgmentOutcome.Answered answered)
        {
            PublishAbsence(MemoryJudgmentKinds.Verify, context, outcome, latency);
            return VerifyVerdict.Stored;
        }

        var scores = Scores(answered.Judgment, VerifyQuestions.Keys);

        // Dropped only on a question that was answered below its bar: one left unanswered stores
        // as today. An instruction is a request, so it is judged on `supported` alone — the probe
        // showed `not_a_question` rejecting every one.
        var dropped = Bars(candidate.Category).Any(bar =>
            scores.TryGetValue(bar.Id, out var p) && p < bar.AtLeast);

        Publish(Answered(MemoryJudgmentKinds.Verify, context, answered.Judgment, latency) with
        {
            Scores = scores,
            Candidate = candidate.Content,
            Category = candidate.Category.ToString(),
            Dropped = dropped
        });

        return new VerifyVerdict(!dropped, scores);
    }

    private IEnumerable<(string Id, double AtLeast)> Bars(MemoryCategory category)
    {
        yield return (SupportedQuestionId, settings.Verify.Supported);
        if (category == MemoryCategory.Instruction)
        {
            yield break;
        }

        yield return (AboutUserQuestionId, settings.Verify.AboutUser);
        yield return (DurableQuestionId, settings.Verify.Durable);
        yield return (NotAQuestionQuestionId, settings.Verify.NotAQuestion);
    }

    private Task<(JudgmentOutcome Outcome, TimeSpan Latency)> AskAsync(
        JsonObject state, IReadOnlyDictionary<string, JudgmentQuestion> questions, CancellationToken ct) =>
        AskAsync(new JudgmentRequest(state, questions), ct);

    private async Task<(JudgmentOutcome Outcome, TimeSpan Latency)> AskAsync(JudgmentRequest request, CancellationToken ct)
    {
        // No deadline of its own: nothing here is on a reply path, and the client's own timeout
        // bounds a call that hangs.
        var started = timeProvider.GetTimestamp();
        var outcome = await judge.JudgeAsync(request, ct);

        // Shutdown is not a judgment that missed. The judge reads any cancellation as a deadline,
        // and with no deadline of our own every one of those would be the host stopping.
        ct.ThrowIfCancellationRequested();

        return (outcome, timeProvider.GetElapsedTime(started));
    }

    private static IReadOnlyDictionary<string, JudgmentQuestion> Nouls(IReadOnlyDictionary<string, string> questions) =>
        questions.ToDictionary(q => q.Key, q => (JudgmentQuestion)new NoulQuestion(q.Value), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> Scores(Judgment judgment, IEnumerable<string> ids) =>
        ids.Select(id => (Id: id, Answer: judgment.Answers.GetValueOrDefault(id) as NoulAnswer))
            .Where(a => a.Answer is not null)
            .ToDictionary(a => a.Id, a => a.Answer!.Probability, StringComparer.Ordinal);

    private static MemoryJudgmentEvent Answered(string kind, MemoryJudgmentContext context, Judgment judgment, TimeSpan latency) => new()
    {
        Kind = kind,
        UserId = context.UserId,
        AgentId = context.AgentId,
        ConversationId = context.ConversationId,
        Answered = true,
        DurationMs = (long)latency.TotalMilliseconds,
        InputTokens = judgment.Usage.InputTokens,
        Model = judgment.Model
    };

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