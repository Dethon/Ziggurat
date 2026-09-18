using Domain.Judgments;
using Domain.Prompts;
using Domain.Skills;

namespace Tests.Unit.Domain.Skills;

// The doubles every preload suite needs: a skill bound the way a server's is, a judge that
// answers from a script, and the answers a judge gives. Shared so the shape of a judgment is
// spelled once across the Domain, agent and monitor suites.
internal static class TestSkills
{
    public static PromptSkill Skill(string name, string description, string? body = null, bool declared = true) =>
        new SkillDeclaration
        {
            Name = name,
            Description = description,
            DescriptionBudget = 100,
            BodyBudget = 1000,
            ServedBy = "test",
            Declared = declared
        }.Bind(description, body ?? $"# {name}\n\nDo the thing.");

    public static PromptSkill Home { get; } = Skill("home-assistant", "Lights, climate and media in the house.", "# Home\n\nCall the house.");

    public static PromptSkill Timers { get; } = Skill("countdown-timers", "Countdowns under /timers.", "# Timers\n\nWrite timer.json.");
}

internal static class JudgeAnswers
{
    public static JudgmentOutcome Answered(string choice, double confidence, params (string Skill, double P)[] needs)
    {
        var answers = needs
            .Select(n => KeyValuePair.Create<string, JudgmentAnswer>(SkillPreloader.NeedsQuestionId(n.Skill), new NoulAnswer(n.P)))
            .Prepend(KeyValuePair.Create<string, JudgmentAnswer>(
                SkillPreloader.ChoiceQuestionId,
                new ChoiceAnswer(choice, confidence, new Dictionary<string, double> { [choice] = confidence })))
            .ToDictionary();

        return new JudgmentOutcome.Answered(new Judgment("jev-test", answers, new JudgmentUsage(2100, 40)));
    }

    public static JudgmentOutcome Sure(string skill) => Answered(skill, 0.99, (skill, 0.98));
}

internal sealed class ScriptedJudge(Func<JudgmentRequest, CancellationToken, Task<JudgmentOutcome>> answer) : IJudge
{
    public ScriptedJudge(Func<JudgmentRequest, JudgmentOutcome> answer)
        : this((request, _) => Task.FromResult(answer(request)))
    {
    }

    // Replaces the script mid-test, for a suite whose later turns want a different answer.
    public Func<JudgmentRequest, JudgmentOutcome>? Answer { get; set; }

    public List<JudgmentRequest> Asked { get; } = [];

    public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
    {
        Asked.Add(request);
        return Answer is { } scripted ? Task.FromResult(scripted(request)) : answer(request, deadline);
    }
}