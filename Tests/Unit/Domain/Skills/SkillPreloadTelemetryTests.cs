using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Prompts;
using Domain.Skills;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// One event per judgment, carrying what an operator reads a degradation off; none where no
// question was worth asking.
public class SkillPreloadTelemetryTests
{
    private static readonly PromptSkill Home = TestSkills.Home;

    private static SkillPreloadRequest Request(string text = "enciende la luz") =>
        new(text, [Home], [])
        {
            AgentId = "nabu",
            ChannelId = "voice",
            ConversationId = "conv-1"
        };

    [Theory]
    [InlineData(AbsenceReason.Deadline, SkillPreloadOutcomes.Deadline)]
    [InlineData(AbsenceReason.Error, SkillPreloadOutcomes.Error)]
    public async Task AnAbsence_PublishesOneEventWithItsOutcomeAndLatency(AbsenceReason reason, string outcome)
    {
        var published = new RecordingMetricsPublisher();
        var preloader = new SkillPreloader(
            new FixedJudge(new JudgmentOutcome.Absent(reason)), new SkillPreloadSettings(), new FakeTimeProvider(), published);

        await preloader.PreloadAsync(Request(), CancellationToken.None);

        var evt = published.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(outcome);
        evt.AgentId.ShouldBe("nabu");
        evt.Channel.ShouldBe("voice");
        evt.ConversationId.ShouldBe("conv-1");
        evt.DurationMs.ShouldNotBeNull();
        evt.Skills.ShouldBeEmpty();
        evt.InputTokens.ShouldBeNull();
    }

    [Fact]
    public async Task APreload_PublishesTheSkillsTheConfidencesTheTokensAndTheModel()
    {
        var published = new RecordingMetricsPublisher();
        var judgment = new Judgment("jev-1.13.0", new Dictionary<string, JudgmentAnswer>
        {
            [SkillPreloader.ChoiceQuestionId] = new ChoiceAnswer(Home.Name, 0.97, new Dictionary<string, double> { [Home.Name] = 0.97 }),
            [SkillPreloader.NeedsQuestionId(Home.Name)] = new NoulAnswer(0.95)
        }, new JudgmentUsage(2180, 60));
        var preloader = new SkillPreloader(
            new FixedJudge(new JudgmentOutcome.Answered(judgment)), new SkillPreloadSettings(), new FakeTimeProvider(), published);

        await preloader.PreloadAsync(Request(), CancellationToken.None);

        var evt = published.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(SkillPreloadOutcomes.Preloaded);
        evt.Skills.ShouldBe([Home.Name]);
        evt.Choice.ShouldBe(Home.Name);
        evt.ChoiceConfidence.ShouldBe(0.97);
        evt.Needs.ShouldNotBeNull()[Home.Name].ShouldBe(0.95);
        evt.InputTokens.ShouldBe(2180);
        evt.Model.ShouldBe("jev-1.13.0");
    }

    [Theory]
    [InlineData("none", 0.99, SkillPreloadOutcomes.None)]
    [InlineData("home-assistant", 0.6, SkillPreloadOutcomes.Abstained)]
    public async Task AnAnswerThatPreloadsNothing_StillPublishesItsOutcome(string choice, double confidence, string outcome)
    {
        var published = new RecordingMetricsPublisher();
        var judgment = new Judgment("jev-test", new Dictionary<string, JudgmentAnswer>
        {
            [SkillPreloader.ChoiceQuestionId] = new ChoiceAnswer(choice, confidence, new Dictionary<string, double> { [choice] = confidence })
        }, new JudgmentUsage(2000, 40));
        var preloader = new SkillPreloader(
            new FixedJudge(new JudgmentOutcome.Answered(judgment)), new SkillPreloadSettings(), new FakeTimeProvider(), published);

        await preloader.PreloadAsync(Request(), CancellationToken.None);

        published.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem().Outcome.ShouldBe(outcome);
    }

    [Fact]
    public async Task ASkippedLemonadeTurn_PublishesOneEventWithNoLatency()
    {
        var published = new RecordingMetricsPublisher();
        var preloader = new SkillPreloader(
            new FixedJudge(new JudgmentOutcome.Absent(AbsenceReason.Error)), new SkillPreloadSettings(), new FakeTimeProvider(), published);

        await preloader.PreloadAsync(Request() with { ConfigPatchModel = "lemonade/qwen3" }, CancellationToken.None);

        var evt = published.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(SkillPreloadOutcomes.SkippedLemonade);
        evt.DurationMs.ShouldBeNull();
    }

    [Fact]
    public async Task NothingWorthAsking_PublishesNothing()
    {
        var published = new RecordingMetricsPublisher();
        var preloader = new SkillPreloader(
            new FixedJudge(new JudgmentOutcome.Absent(AbsenceReason.Unconfigured)), new SkillPreloadSettings(), new FakeTimeProvider(), published);

        await preloader.PreloadAsync(Request(), CancellationToken.None);
        await preloader.PreloadAsync(new SkillPreloadRequest("hola", [], []), CancellationToken.None);
        await new SkillPreloader(new FixedJudge(new JudgmentOutcome.Absent(AbsenceReason.Error)),
                new SkillPreloadSettings { Enabled = false }, new FakeTimeProvider(), published)
            .PreloadAsync(Request(), CancellationToken.None);

        published.Published.ShouldBeEmpty();
    }

    [Fact]
    public void EveryOutcomeButNotAsked_HasAWireSpelling() =>
        Enum.GetValues<SkillPreloadOutcome>()
            .Where(o => o != SkillPreloadOutcome.NotAsked)
            .Select(SkillPreloader.WireOutcome)
            .ShouldAllBe(spelling => !string.IsNullOrWhiteSpace(spelling));

    private sealed class FixedJudge(JudgmentOutcome outcome) : IJudge
    {
        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline) => Task.FromResult(outcome);
    }
}