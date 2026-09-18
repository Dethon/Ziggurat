using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Judgments;
using Domain.Prompts;

namespace Domain.Skills;

public sealed class SkillPreloader(IJudge judge, SkillPreloadSettings settings, TimeProvider timeProvider) : ISkillPreloader
{
    public const string ChoiceQuestionId = "skill";

    public static string NeedsQuestionId(string skillName) => $"needs_{skillName}";

    // "Request", never "what a person said": a worker's request is a delegation prompt, and the
    // instructions are the same whoever is asking.
    private const string Framing = "`request` is a request made to an assistant, in Spanish or English.";

    public const string ChoiceInstructions =
        Framing + " Which one skill does carrying out the request need first?";

    public const string NoneCriterion =
        "The request needs none of the listed skills: conversation, a question answered from knowledge or memory, reading or searching notes, arithmetic, or anything else.";

    public static string NeedsInstructions(string description) =>
        Framing + " Does carrying out the request need this skill? Skill: " + description;

    public async Task<SkillPreload> PreloadAsync(SkillPreloadRequest request, CancellationToken ct)
    {
        if (!settings.Enabled)
        {
            return SkillPreload.NotAsked;
        }

        if (LemonadeModelId.IsLemonade(request.ConfigPatchModel))
        {
            return SkillPreload.SkippedLemonade;
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

        var outcome = await judge.JudgeAsync(Ask(request.Text, candidates), linked.Token);
        var latency = timeProvider.GetElapsedTime(started);

        return outcome switch
        {
            JudgmentOutcome.Absent { Reason: AbsenceReason.Unconfigured } => SkillPreload.NotAsked,
            JudgmentOutcome.Absent { Reason: AbsenceReason.Deadline } => new SkillPreload(SkillPreloadOutcome.Deadline, [], latency),
            JudgmentOutcome.Absent => new SkillPreload(SkillPreloadOutcome.Error, [], latency),
            JudgmentOutcome.Answered answered => Decide(answered.Judgment, candidates, latency),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.GetType().Name, "Unknown judgment outcome")
        };
    }

    private static JudgmentRequest Ask(string text, IReadOnlyList<PromptSkill> candidates)
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

        return new JudgmentRequest(new JsonObject { ["request"] = text }, questions);
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
            new SkillJudgment(choice.Choice, choice.Confidence, needs, judgment.Usage.InputTokens, judgment.Model));
    }
}