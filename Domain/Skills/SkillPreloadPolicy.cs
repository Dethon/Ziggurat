using Domain.Judgments;

namespace Domain.Skills;

// A pure function of the answers and the settings. The four rules in order: a confident none
// vetoes; the choice winner at its bar comes first; every other skill at the noul bar follows,
// highest first; the cap cuts the tail. A winner under its bar is not special — its own noul can
// still carry it, like any other skill's.
public static class SkillPreloadPolicy
{
    public const string NoneChoice = "none";

    public static SkillPreloadDecision Decide(
        ChoiceAnswer choice,
        IReadOnlyDictionary<string, double> needs,
        SkillPreloadSettings settings)
    {
        var isNone = string.Equals(choice.Choice, NoneChoice, StringComparison.Ordinal);
        if (isNone && choice.Confidence >= settings.NoneVeto)
        {
            return new SkillPreloadDecision([], NoneVetoed: true);
        }

        var winner = !isNone && choice.Confidence >= settings.ChoiceConfidence
            ? [choice.Choice]
            : Array.Empty<string>();

        var riders = needs
            .Where(n => n.Value >= settings.NoulProbability && !winner.Contains(n.Key))
            .OrderByDescending(n => n.Value)
            .ThenBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => n.Key);

        return new SkillPreloadDecision(
            [.. winner.Concat(riders).Take(settings.MaxSkills)],
            NoneVetoed: false);
    }
}

public sealed record SkillPreloadDecision(IReadOnlyList<string> Skills, bool NoneVetoed);