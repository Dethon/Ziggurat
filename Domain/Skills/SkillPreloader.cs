using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Prompts;
using Domain.Tools;

namespace Domain.Skills;

public sealed class SkillPreloader(
    IJudge judge,
    SkillPreloadSettings settings,
    TimeProvider timeProvider,
    IMetricsPublisher? metricsPublisher = null) : ISkillPreloader
{

    public const string ChoiceQuestionId = "skill";

    public static string NeedsQuestionId(string skillName) => $"needs_{skillName}";

    // "Request", never "what a person said": a worker's request is a delegation prompt, and the
    // instructions are the same whoever is asking.
    private const string Framing = "`request` is a request made to a home assistant, in Spanish or English.";

    public const string ChoiceInstructions =
        Framing + " Which one skill does carrying out the request need first?";

    public const string NoneCriterion =
        "The request needs none of the listed skills: conversation, a question answered from knowledge or memory, reading or searching notes, arithmetic, or anything else.";

    public static string NeedsInstructions(string description) =>
        Framing + " Does carrying out the request need this skill? Skill: " + description;

    // The flag alone: whether a key is configured is the judge's own answer, and asking it that
    // would mean a round trip to find out there is nothing to ask.
    public bool IsOff => !settings.Enabled;

    public async Task<SkillPreload> PreloadAsync(SkillPreloadRequest request, CancellationToken ct)
    {
        if (!settings.Enabled)
        {
            return SkillPreload.NotAsked;
        }

        var loaded = SkillLoadTool.LoadedIn(request.History);
        var candidates = request.Skills.Where(s => !loaded.Contains(s.Name)).ToList();
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(request.Text))
        {
            return SkillPreload.NotAsked;
        }

        // The deadline runs from here, whoever awaits the result and whenever they look.
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.DeadlineMs), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var outcome = await judge.JudgeAsync(Ask(request.Text, candidates, request.ConfigPatchModel), linked.Token);
        var latency = timeProvider.GetElapsedTime(started);

        // The turn being torn down is not a judgment that missed. The judge reads any cancellation
        // as a deadline, so without this a /clear or a shutdown within the deadline publishes a
        // deadline event — and the deadline is tuned from exactly that rate.
        ct.ThrowIfCancellationRequested();

        // An answer that arrives after the deadline is discarded here, whatever the judge made of
        // the cancellation: late is late, and a body inserted late would land on the wrong turn.
        var preload = outcome switch
        {
            // The client sent nothing because the turn was addressed to the local box. Before the
            // deadline arm on purpose: nothing was asked, so there is nothing to have been late.
            JudgmentOutcome.Absent { Reason: AbsenceReason.LocalTurn } => SkillPreload.SkippedLemonade,
            _ when deadline.IsCancellationRequested => new SkillPreload(SkillPreloadOutcome.Deadline, [], latency),
            JudgmentOutcome.Absent { Reason: AbsenceReason.Unconfigured } => SkillPreload.NotAsked,
            JudgmentOutcome.Absent { Reason: AbsenceReason.Deadline } => new SkillPreload(SkillPreloadOutcome.Deadline, [], latency),
            JudgmentOutcome.Absent => new SkillPreload(SkillPreloadOutcome.Error, [], latency),
            JudgmentOutcome.Answered answered => Decide(answered.Judgment, candidates, latency),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.GetType().Name, "Unknown judgment outcome")
        };

        if (preload.Outcome == SkillPreloadOutcome.Preloaded && request.Reader is not null)
        {
            // A budget of their own, on top of the turn's token: the reads are not raced against
            // the judge's deadline, but the turn's first model call waits on this whole task, so
            // a mount that hangs must not hold it. Whatever answered inside the budget is kept.
            using var budget = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(settings.ReadsBudgetMs), timeProvider);
            using var readsToken = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

            preload = preload with { Reads = await ReadAsync(preload.Skills, request.Reader, readsToken.Token) };
            ct.ThrowIfCancellationRequested();
        }

        return Published(request, preload);
    }

    // The reads the preloaded skills declare, made now that the judge has answered — on the turn's
    // token and the reads' own budget rather than the judge's deadline, because a read is the
    // mount rendering what it holds, not a round trip to be raced. A read that throws, runs past
    // the budget or answers an error envelope is left out: the body still tells the model to read,
    // and an error the model never asked for would sit in the conversation as if it had. Never a
    // lost preload.
    private static async Task<IReadOnlyList<SkillPreloadRead>> ReadAsync(
        IReadOnlyList<PromptSkill> skills, PreloadFileReader reader, CancellationToken ct)
    {
        var reads = new List<SkillPreloadRead>();
        foreach (var skill in skills)
        {
            foreach (var path in skill.Declaration.PreloadReads)
            {
                if (ct.IsCancellationRequested)
                {
                    return reads;
                }

                JsonNode? result;
                try
                {
                    result = await reader(path, ct);
                }
                catch (Exception)
                {
                    // The budget running out is the same as a mount that refused: this read is
                    // not in the conversation and the body still asks for it. Which of the two
                    // cancelled is the caller's question, answered once this returns.
                    continue;
                }

                if (result is null || ToolErrorResult.IsErrorEnvelope(result))
                {
                    continue;
                }

                reads.Add(new SkillPreloadRead(skill.Name, path, result));
            }
        }

        return reads;
    }

    // One event per judgment, and none where no question was worth asking: an unconfigured judge
    // is the feature off, not a thing to count.
    private SkillPreload Published(SkillPreloadRequest request, SkillPreload preload)
    {
        if (preload.Outcome == SkillPreloadOutcome.NotAsked)
        {
            return preload;
        }

        metricsPublisher?.Publish(ToEvent(request, preload));
        return preload;
    }

    public static SkillPreloadEvent ToEvent(SkillPreloadRequest request, SkillPreload preload) => new()
    {
        AgentId = request.AgentId,
        ConversationId = request.ConversationId,
        Channel = request.ChannelId,
        Outcome = WireOutcome(preload.Outcome),
        Skills = [.. preload.Skills.Select(s => s.Name)],
        Reads = [.. preload.Reads.Select(r => r.Path)],
        Choice = preload.Judgment?.Choice,
        ChoiceConfidence = preload.Judgment?.ChoiceConfidence,
        Needs = preload.Judgment?.Needs,
        DurationMs = preload.Latency is { } latency ? (long)latency.TotalMilliseconds : null,
        InputTokens = preload.Judgment?.InputTokens,
        Cost = preload.Judgment?.Cost,
        Model = preload.Judgment?.Model
    };

    public static string WireOutcome(SkillPreloadOutcome outcome) => outcome switch
    {
        SkillPreloadOutcome.Preloaded => SkillPreloadOutcomes.Preloaded,
        SkillPreloadOutcome.Abstained => SkillPreloadOutcomes.Abstained,
        SkillPreloadOutcome.None => SkillPreloadOutcomes.None,
        SkillPreloadOutcome.Deadline => SkillPreloadOutcomes.Deadline,
        SkillPreloadOutcome.Error => SkillPreloadOutcomes.Error,
        SkillPreloadOutcome.SkippedLemonade => SkillPreloadOutcomes.SkippedLemonade,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An outcome with no wire spelling")
    };

    private static JudgmentRequest Ask(string text, IReadOnlyList<PromptSkill> candidates, string? turnModel)
    {
        var criteria = candidates
            .Select(s => KeyValuePair.Create(s.Name, s.Description))
            .Append(KeyValuePair.Create(SkillPreloadPolicy.NoneChoice, NoneCriterion))
            .ToDictionary(StringComparer.Ordinal);

        var questions = candidates
            .Select(s => KeyValuePair.Create<string, JudgmentQuestion>(
                NeedsQuestionId(s.Name), new NoulQuestion(NeedsInstructions(s.Description))))
            .Prepend(KeyValuePair.Create<string, JudgmentQuestion>(
                ChoiceQuestionId, new ChoiceQuestion(ChoiceInstructions, criteria)))
            .ToDictionary(StringComparer.Ordinal);

        return new JudgmentRequest(new JsonObject { ["request"] = text }, questions, turnModel);
    }

    private SkillPreload Decide(Judgment judgment, IReadOnlyList<PromptSkill> candidates, TimeSpan latency)
    {
        if (!judgment.Answers.TryGetValue(ChoiceQuestionId, out var answer) || answer is not ChoiceAnswer choice)
        {
            return new SkillPreload(SkillPreloadOutcome.Error, [], latency);
        }

        var needs = candidates
            .Select(s => (s.Name, Answer: judgment.Answers.GetValueOrDefault(NeedsQuestionId(s.Name)) as NoulAnswer))
            .Where(n => n.Answer is not null)
            .ToDictionary(n => n.Name, n => n.Answer!.Probability, StringComparer.Ordinal);

        var decision = SkillPreloadPolicy.Decide(choice, needs, settings);

        // Only what was asked about can be preloaded: a name the judge invented, or one already
        // in the conversation, has no body to insert.
        var byName = candidates.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var skills = decision.Skills
            .Select(name => byName.GetValueOrDefault(name))
            .OfType<PromptSkill>()
            .ToList();

        var outcome = skills.Count > 0
            ? SkillPreloadOutcome.Preloaded
            : decision.NoneVetoed
                ? SkillPreloadOutcome.None
                : SkillPreloadOutcome.Abstained;

        return new SkillPreload(
            outcome,
            skills,
            latency,
            new SkillJudgment(choice.Choice, choice.Confidence, needs, judgment.Usage.InputTokens, judgment.Usage.Cost, judgment.Model));
    }
}