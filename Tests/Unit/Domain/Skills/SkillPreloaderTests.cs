using System.Text.Json.Nodes;
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
    private static readonly SkillPreloadSettings _settings = new() { DeadlineMs = 600 };

    private static readonly PromptSkill _home = TestSkills.Skill("home-assistant", "Turning lights, climate and media on or off.");
    private static readonly PromptSkill _timers = TestSkills.Skill("countdown-timers", "Setting, reading or cancelling a countdown.");
    private static readonly PromptSkill _vault = TestSkills.Skill("obsidian-vault", "Creating or editing a note in the vault.");

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

        var preload = await preloader.PreloadAsync(Request("enciende la luz del salón", [_home, _timers]), CancellationToken.None);

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

        var preload = await Preloader(judge).PreloadAsync(Request("hola, ¿qué tal?", [_home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.None);
        preload.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_NothingOverTheBar_PreloadsNothingAndSaysAbstained()
    {
        var judge = new ScriptedJudge(_ => Answered("home-assistant", 0.74, ("home-assistant", 0.83)));

        var preload = await Preloader(judge).PreloadAsync(Request("dime cuando termine la lavadora", [_home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Abstained);
        preload.Skills.ShouldBeEmpty();
        preload.Judgment.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preload_ASkillAlreadyLoaded_IsNeitherAskedAboutNorPreloaded()
    {
        var judge = new ScriptedJudge(_ => Answered("home-assistant", 0.99, ("home-assistant", 0.99), ("countdown-timers", 0.01)));
        var history = new[] { new ChatMessage(ChatRole.User, "hola"), ALoadOf("home-assistant") };

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [_home, _timers], history), CancellationToken.None);

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
        var history = new[] { SkillLoadTool.AsLoaded([_home], "preload-1").First() };

        await Preloader(judge).PreloadAsync(Request("apaga la luz", [_home, _timers], history), CancellationToken.None);

        judge.Asked.ShouldHaveSingleItem().Questions["skill"].ShouldBeOfType<ChoiceQuestion>()
            .Criteria.Keys.ShouldBe(["countdown-timers", "none"]);
    }

    [Fact]
    public async Task Preload_EverySkillLoaded_AsksNothing()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));
        var history = new[] { ALoadOf("home-assistant"), ALoadOf("countdown-timers") };

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [_home, _timers], history), CancellationToken.None);

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
        var preloader = Preloader(judge, _settings with { Enabled = false });

        var preload = await preloader.PreloadAsync(Request("apaga la luz", [_home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_ALemonadeTurn_AsksNothingAndSaysSo()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("must not be asked"));

        var preload = await Preloader(judge).PreloadAsync(
            Request("apaga la luz", [_home]) with { ConfigPatchModel = "lemonade/qwen3" }, CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.SkippedLemonade);
        preload.Latency.ShouldBeNull();
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_AnUnconfiguredJudge_IsTheFeatureOff()
    {
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(AbsenceReason.Unconfigured));

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [_home]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.NotAsked);
    }

    [Theory]
    [InlineData(AbsenceReason.Deadline, SkillPreloadOutcome.Deadline)]
    [InlineData(AbsenceReason.Error, SkillPreloadOutcome.Error)]
    public async Task Preload_AnAbsentAnswer_IsAnEmptyPreloadCarryingItsReason(AbsenceReason reason, SkillPreloadOutcome outcome)
    {
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(reason));

        var preload = await Preloader(judge).PreloadAsync(Request("apaga la luz", [_home]), CancellationToken.None);

        preload.Outcome.ShouldBe(outcome);
        preload.Skills.ShouldBeEmpty();
        preload.Judgment.ShouldBeNull();
        preload.Latency.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preload_TheQuestions_SayRequestAndNeverAssumeASpeaker()
    {
        var judge = new ScriptedJudge(_ => Answered("none", 0.99));

        await Preloader(judge).PreloadAsync(Request("busca una receta", [_home, _vault]), CancellationToken.None);

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

        await Preloader(judge).PreloadAsync(Request("busca en mi portátil", [_home, outpost]), CancellationToken.None);

        var asked = judge.Asked.ShouldHaveSingleItem();
        var criteria = asked.Questions["skill"].ShouldBeOfType<ChoiceQuestion>().Criteria;
        criteria["home-assistant"].ShouldBe(_home.Description);
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
        var preloader = new SkillPreloader(judge, _settings, clock);

        var pending = preloader.PreloadAsync(Request("apaga la luz", [_home]), CancellationToken.None);
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

        var preload = await Preloader(judge).PreloadAsync(Request("busca algo", [_home]), CancellationToken.None);

        preload.Skills.ShouldBeEmpty();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Abstained);
    }

    // A skill's body can say "read this file in the same turn as me"; a preload does that read
    // too, through the reader the request carries, so the model's first call is the action.
    [Fact]
    public async Task Preload_ASkillDeclaringAReadInTheSameTurn_ReadsItThroughTheRequestsReader()
    {
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("home-assistant"));
        var index = new JsonObject { ["filePath"] = "/ha/setup-index.md", ["content"] = "1: ## Current Home Assistant setup" };
        var asked = new List<string>();
        var request = Request("enciende la luz del salón", [TestSkills.HomeWithIndex, _timers]) with
        {
            Reader = (path, _) =>
            {
                asked.Add(path);
                return Task.FromResult<JsonNode?>(index);
            }
        };

        var preload = await Preloader(judge).PreloadAsync(request, CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        asked.ShouldBe(["/ha/setup-index.md"]);
        var read = preload.Reads.ShouldHaveSingleItem();
        read.Skill.ShouldBe("home-assistant");
        read.Path.ShouldBe("/ha/setup-index.md");
        read.Result.ShouldBeSameAs(index);
    }

    // The reads are made on the turn's token, not the judge's deadline — a read is the mount
    // rendering what it holds. But the first model call waits on the whole preload, so a mount
    // that hangs held the turn with no first token and nothing spoken, which is exactly what the
    // deadline exists to prevent. The reads get a bound of their own.
    [Fact]
    public async Task Preload_AReadThatHangs_StillPreloadsTheSkill_WithoutTheRead()
    {
        var clock = new ArmedClock();
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("home-assistant"));
        var hanging = new TaskCompletionSource<JsonNode?>();
        var request = Request("enciende la luz del salón", [TestSkills.HomeWithIndex, _timers]) with
        {
            Reader = (_, ct) => hanging.Task.WaitAsync(ct)
        };

        var pending = Preloader(judge, clock: clock).PreloadAsync(request, CancellationToken.None);
        await clock.AdvancePastAsync(TimeSpan.FromMilliseconds(_settings.ReadsBudgetMs));

        var preload = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe(["home-assistant"]);
        preload.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_ASkillDeclaringNoRead_AsksTheReaderNothing()
    {
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("countdown-timers"));
        var asked = 0;
        var request = Request("pon un temporizador", [TestSkills.HomeWithIndex, _timers]) with
        {
            Reader = (_, _) =>
            {
                asked++;
                return Task.FromResult<JsonNode?>(new JsonObject());
            }
        };

        var preload = await Preloader(judge).PreloadAsync(request, CancellationToken.None);

        preload.Skills.Select(s => s.Name).ShouldBe(["countdown-timers"]);
        preload.Reads.ShouldBeEmpty();
        asked.ShouldBe(0);
    }

    // A read that fails, refuses or has nowhere to run is the skill alone: the body still tells
    // the model to read, and it will. Never a lost preload, never an error envelope in the
    // conversation as if the model had asked for it.
    [Fact]
    public async Task Preload_AReaderThatThrows_PreloadsTheSkillWithoutTheRead()
    {
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("home-assistant"));
        var request = Request("enciende la luz", [TestSkills.HomeWithIndex]) with
        {
            Reader = (_, _) => throw new InvalidOperationException("mount is down")
        };

        var preload = await Preloader(judge).PreloadAsync(request, CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe(["home-assistant"]);
        preload.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_AReaderAnsweringAnErrorEnvelope_PreloadsTheSkillWithoutTheRead()
    {
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("home-assistant"));
        var refused = new JsonObject { ["ok"] = false, ["errorCode"] = "not_found", ["message"] = "no such file" };
        var request = Request("enciende la luz", [TestSkills.HomeWithIndex]) with
        {
            Reader = (_, _) => Task.FromResult<JsonNode?>(refused)
        };

        var preload = await Preloader(judge).PreloadAsync(request, CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preload_WithoutAReader_PreloadsTheSkillWithoutTheRead()
    {
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure("home-assistant"));

        var preload = await Preloader(judge).PreloadAsync(Request("enciende la luz", [TestSkills.HomeWithIndex]), CancellationToken.None);

        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Reads.ShouldBeEmpty();
    }

    private static SkillPreloader Preloader(
        IJudge judge, SkillPreloadSettings? settings = null, TimeProvider? clock = null) =>
        new(judge, settings ?? _settings, clock ?? new FakeTimeProvider());

    private static SkillPreloadRequest Request(string text, IReadOnlyList<PromptSkill> skills, IEnumerable<ChatMessage>? history = null) =>
        new(text, skills, history ?? []);
}