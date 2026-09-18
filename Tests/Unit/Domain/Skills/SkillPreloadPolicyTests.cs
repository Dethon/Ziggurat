using Domain.Judgments;
using Domain.Skills;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// The rule that turns Jev's answers into a preload, pinned one clause at a time: a confident
// none vetoes, the choice winner at its bar comes first, other skills at the noul bar follow
// highest first, and the cap cuts the tail.
public class SkillPreloadPolicyTests
{
    private static readonly SkillPreloadSettings Settings = new()
    {
        ChoiceConfidence = 0.9,
        NoulProbability = 0.9,
        NoneVeto = 0.9,
        MaxSkills = 2
    };

    private static ChoiceAnswer Choice(string pick, double confidence) =>
        new(pick, confidence, new Dictionary<string, double> { [pick] = confidence });

    private static Dictionary<string, double> Needs(params (string Skill, double P)[] needs) =>
        needs.ToDictionary(n => n.Skill, n => n.P);

    [Fact]
    public void Decide_AConfidentNone_VetoesEvenANoulOverTheBar()
    {
        var decision = SkillPreloadPolicy.Decide(Choice("none", 0.95), Needs(("home", 0.97)), Settings);

        decision.Skills.ShouldBeEmpty();
        decision.NoneVetoed.ShouldBeTrue();
    }

    [Fact]
    public void Decide_ANoneUnderTheVeto_StillLetsANoulThrough()
    {
        var decision = SkillPreloadPolicy.Decide(Choice("none", 0.6), Needs(("home", 0.93)), Settings);

        decision.Skills.ShouldBe(["home"]);
        decision.NoneVetoed.ShouldBeFalse();
    }

    [Fact]
    public void Decide_AWinnerAtTheBar_IsPreloadedFirst()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("timers", 0.92), Needs(("timers", 0.5), ("home", 0.95)), Settings);

        decision.Skills.ShouldBe(["timers", "home"]);
    }

    [Fact]
    public void Decide_AWinnerUnderTheBarWithANoulOverIt_PreloadsOnlyTheNoulsSkill()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("timers", 0.83), Needs(("timers", 0.67), ("home", 0.91)), Settings);

        decision.Skills.ShouldBe(["home"]);
    }

    [Fact]
    public void Decide_AWinnerUnderTheBarWhoseOwnNoulIsOverIt_IsPreloadedByTheNoul()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("timers", 0.89), Needs(("timers", 0.91), ("home", 0.6)), Settings);

        decision.Skills.ShouldBe(["timers"]);
    }

    [Fact]
    public void Decide_OtherSkillsOverTheNoulBar_FollowHighestFirst()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("web", 0.99), Needs(("web", 0.99), ("vault", 0.91), ("home", 0.95)),
            Settings with { MaxSkills = 3 });

        decision.Skills.ShouldBe(["web", "home", "vault"]);
    }

    [Fact]
    public void Decide_ThreeQualifying_AreCutToTwoByProbability()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("web", 0.99), Needs(("web", 0.99), ("vault", 0.91), ("home", 0.95)), Settings);

        decision.Skills.ShouldBe(["web", "home"]);
    }

    [Fact]
    public void Decide_NothingOverAnyBar_IsEmptyAndNotAVeto()
    {
        var decision = SkillPreloadPolicy.Decide(
            Choice("timers", 0.7), Needs(("timers", 0.8), ("home", 0.3)), Settings);

        decision.Skills.ShouldBeEmpty();
        decision.NoneVetoed.ShouldBeFalse();
    }

    [Fact]
    public void Decide_TheBarsAreInclusive()
    {
        var decision = SkillPreloadPolicy.Decide(Choice("timers", 0.9), Needs(("home", 0.9)), Settings);

        decision.Skills.ShouldBe(["timers", "home"]);
    }
}