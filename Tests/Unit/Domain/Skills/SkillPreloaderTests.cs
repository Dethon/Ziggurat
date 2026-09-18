using Domain.Judgments;
using Domain.Prompts;
using Domain.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// The service that asks Jev which skills a request needs: what it asks, what it never asks
// about, and how each kind of answer becomes a preload with an outcome telemetry can name.
public class SkillPreloaderTests
{
    private static readonly SkillPreloadSettings Settings = new() { DeadlineMs = 600 };

    private static readonly PromptSkill Home = TestSkills.Skill("home-assistant", "Turning lights, climate and media on or off.");
    private static readonly PromptSkill Timers = TestSkills.Skill("countdown-timers", "Setting, reading or cancelling a countdown.");
    private static readonly PromptSkill Vault = TestSkills.Skill("obsidian-vault", "Creating or editing a note in the vault.");

    private static JudgmentOutcome Answered(string choice, double confidence, params (string Skill, double P)[] needs) =>
        JudgeAnswers.Answered(choice, confidence, needs);

    private static ChatMessage ALoadOf(string skill) =>
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", SkillLoadTool.Name,
            new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = skill })]);

    [Fact]
    public async Task Preload_ARequestNeedingOneSkill_PreloadsItWithTheJudgmentOnTheResult()
    {
        var judge = new ScriptedJudge(_ => Answered("home-assistant", 0.98, ("home-assistant", 0.96), ("countdown-timers", 0.03)));
        var preloader = Preloader(judge);

        var preload = await preloader.PreloadAsync(Request("enciende la luz del salón", [Home, Timers]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe(["home-assistant"]);
        var judgment = preload.Judgment.ShouldNotBeNull();
        judgment.Choice.ShouldBe("home-assistant");
        judgment.ChoiceConfidence.ShouldBe(0.98);
        judgment.Needs["countdown-timers"].ShouldBe(0.03);
        judgment.InputTokens.ShouldBe(2100);
        judgment.Model.ShouldBe("jev-test");
        preload.Latency.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preload_AConfidentNone_PreloadsNothingAndSaysNone()
    {
        var judge = new ScriptedJudge(_ => Answered("none", 0.97, ("home-assistant", 0.02)));

        var preload = await Preloader(judge).PreloadAsync(Request("hola, ¿qué tal?", [Home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.None);
        preload.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_NothingOverTheBar_PreloadsNothingAndSaysAbstained()
    {
        var judge = new ScriptedJudge(_ => Answered("home-assistant", 0.74, ("home-assistant", 0.83)));

        var preload = await Preloader(judge).PreloadAsync(Request("dime cuando termine la lavadora", [Home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Abstained);
        preload.Skills.ShouldBeEmpty();
        preload.Judgment.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preload_ASkillAlreadyLoaded_IsNeitherAskedAboutNorPreloaded()
    {
        var judge = new ScriptedJudge(_ => Answered("home-assistant", 0.99, ("home-assistant", 0.99), ("countdown-timers", 0.01)));
        var history = new[] { new ChatMessage(ChatRole.User, "hola"), ALoadOf("home-assistant") };

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [Home, Timers], history), CancellationToken.None);

        var asked = judge.Asked.ShouldHaveSingleItem();
        var choice = asked.Questions["skill"].ShouldBeOfType<ChoiceQuestion>();
        choice.Criteria.Keys.ShouldBe(["countdown-timers", "none"]);
        asked.Questions.Keys.ShouldNotContain("needs_home-assistant");
        preload.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_ALoadMadeByTheHost_CountsAsLoadedToo()
    {
        var judge = new ScriptedJudge(_ => Answered("none", 0.99));
        var history = new[] { SkillLoadTool.AsLoaded([Home], "preload-1").First() };

        await Preloader(judge).PreloadAsync(Request("apaga la luz", [Home, Timers], history), CancellationToken.None);

        judge.Asked.ShouldHaveSingleItem().Questions["skill"].ShouldBeOfType<ChoiceQuestion>()
            .Criteria.Keys.ShouldBe(["countdown-timers", "none"]);
    }

    [Fact]
    public async Task Preload_EverySkillLoaded_AsksNothing()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));
        var history = new[] { ALoadOf("home-assistant"), ALoadOf("countdown-timers") };

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [Home, Timers], history), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_NoSkillAdvertised_AsksNothing()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", []), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_Disabled_AsksNothing()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));
        var preloader = Preloader(judge, Settings with { Enabled = false });

        var preload = await preloader.PreloadAsync(Request("apaga la luz", [Home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_ALemonadeTurn_AsksNothingAndSaysSo()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));

        var preload = await Preloader(judge).PreloadAsync(
            Request("apaga la luz", [Home]) with { ConfigPatchModel = "lemonade/qwen3" }, CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.SkippedLemonade);
        preload.Latency.ShouldBeNull();
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_AnUnconfiguredJudge_IsTheFeatureOff()
    {
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(AbsenceReason.Unconfigured));

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [Home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
    }

    [Theory]
    [InlineData(AbsenceReason.Deadline, SkillPreloadOutcome.Deadline)]
    [InlineData(AbsenceReason.Error, SkillPreloadOutcome.Error)]
    public async Task Preload_AnAbsentAnswer_IsAnEmptyPreloadCarryingItsReason(AbsenceReason reason, SkillPreloadOutcome outcome)
    {
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(reason));

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [Home]), CancellationToken.None);

        preload.Outcome.ShouldBe(outcome);
        preload.Skills.ShouldBeEmpty();
        preload.Judgment.ShouldBeNull();
        preload.Latency.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preload_TheQuestions_SayRequestAndNeverAssumeASpeaker()
    {
        var judge = new ScriptedJudge(_ => Answered("none", 0.99));

        await Preloader(judge).PreloadAsync(Request("busca una receta", [Home, Vault]), CancellationToken.None);

        var questions = judge.Asked.ShouldHaveSingleItem().Questions.Values.Select(q => q.Instructions).ToList();
        questions.Count.ShouldBe(3);
        questions.ShouldAllBe(q => q.Contains("request"));
        questions.ShouldAllBe(q => !q.Contains("person") && !q.Contains("said") && !q.Contains("spoke") && !q.Contains("user"));
    }

    [Fact]
    public async Task Preload_TheCriteria_AreTheShippedDescriptionsVerbatimDeclaredOrNot()
    {
        var outpost = TestSkills.Skill("laptop-files", "Files on Francisco's laptop, reached through the outpost.", declared: false);
        var judge = new ScriptedJudge(_ => Answered("none", 0.99));

        await Preloader(judge).PreloadAsync(Request("busca en mi portátil", [Home, outpost]), CancellationToken.None);

        var asked = judge.Asked.ShouldHaveSingleItem();
        var criteria = asked.Questions["skill"].ShouldBeOfType<ChoiceQuestion>().Criteria;
        criteria["home-assistant"].ShouldBe(Home.Description);
        criteria["laptop-files"].ShouldBe(outpost.Description);
        criteria.Keys.Last().ShouldBe("none");
        asked.Questions["needs_laptop-files"].Instructions.ShouldEndWith(outpost.Description);
        asked.State["request"]!.GetValue<string>().ShouldBe("busca en mi portátil");
    }

    [Fact]
    public async Task Preload_TheDeadline_RunsFromWhenTheCallStarted()
    {
        var clock = new FakeTimeProvider();
        var judge = new ScriptedJudge(async (_, ct) =>
        {
            var cancelled = new TaskCompletionSource();
            await using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            return new JudgmentOutcome.Absent(AbsenceReason.Deadline);
        });
        var preloader = new SkillPreloader(judge, Settings, clock);

        var pending = preloader.PreloadAsync(Request("apaga la luz", [Home]), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(599));
        pending.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var preload = await pending;

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Deadline);
        preload.Latency.ShouldBe(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task Preload_AnAnswerNamingNoAdvertisedSkill_PreloadsNothing()
    {
        var judge = new ScriptedJudge(_ => Answered("web-browsing", 0.99, ("web-browsing", 0.99)));

        var preload = await Preloader(judge).PreloadAsync(Request("busca algo", [Home]), CancellationToken.None);

        preload.Skills.ShouldBeEmpty();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Abstained);
    }

    private static SkillPreloader Preloader(IJudge judge, SkillPreloadSettings? settings = null) =>
        new(judge, settings ?? Settings, new FakeTimeProvider());

    private static SkillPreloadRequest Request(string text, IReadOnlyList<PromptSkill> skills, IEnumerable<ChatMessage>? history = null) =>
        new(text, skills, history ?? []);
}