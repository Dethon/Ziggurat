using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.Domain.Memory;

// The gate: three yes/no questions about the current message, and the extractor is skipped only
// when all three are at or below the bar. Everything else, including no answer, extracts.
public class MemoryJudgeGateTests
{
    private static readonly MemoryJudgmentContext Context = new("user1", "nabu", "conv-1");

    private static readonly IReadOnlyList<ChatMessage> Window =
    [
        new(ChatRole.User, "pon música"),
        new(ChatRole.Assistant, "¿Qué te apetece?"),
        new(ChatRole.User, "algo tranquilo")
    ];

    private static MemoryJudge Judge(IJudge judge, RecordingMetricsPublisher? published = null, MemoryJudgmentSettings? settings = null) =>
        new(judge, settings ?? new MemoryJudgmentSettings(), new FakeTimeProvider(), published);

    [Fact]
    public async Task EveryQuestionAtOrBelowTheBar_Skips()
    {
        var judge = StubJudge.Nouls(("fact", 0.05), ("preference", 0.1), ("instruction", 0.02));

        var verdict = await Judge(judge).GateAsync(Window, Context, CancellationToken.None);

        verdict.Skip.ShouldBeTrue();
        verdict.Scores["preference"].ShouldBe(0.1);
    }

    [Fact]
    public async Task AnyQuestionAboveTheBar_Extracts()
    {
        var judge = StubJudge.Nouls(("fact", 0.05), ("preference", 0.11), ("instruction", 0.02));

        var verdict = await Judge(judge).GateAsync(Window, Context, CancellationToken.None);

        verdict.Skip.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AbsenceReason.Deadline)]
    [InlineData(AbsenceReason.Error)]
    [InlineData(AbsenceReason.Unconfigured)]
    public async Task NoAnswer_Extracts(AbsenceReason reason)
    {
        var verdict = await Judge(StubJudge.Absent(reason)).GateAsync(Window, Context, CancellationToken.None);

        verdict.Skip.ShouldBeFalse();
    }

    // A question the judge did not answer is not a low score.
    [Fact]
    public async Task AQuestionLeftUnanswered_Extracts()
    {
        var judge = StubJudge.Nouls(("fact", 0.05), ("preference", 0.05));

        var verdict = await Judge(judge).GateAsync(Window, Context, CancellationToken.None);

        verdict.Skip.ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_AsksNothing()
    {
        var judge = StubJudge.Nouls(("fact", 0.0), ("preference", 0.0), ("instruction", 0.0));
        var published = new RecordingMetricsPublisher();

        var verdict = await Judge(judge, published, new MemoryJudgmentSettings { Enabled = false })
            .GateAsync(Window, Context, CancellationToken.None);

        verdict.Skip.ShouldBeFalse();
        judge.Requests.ShouldBeEmpty();
        published.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheStateIsTheWindowAsFields_ContextThenCurrent()
    {
        var judge = StubJudge.Nouls(("fact", 0.0), ("preference", 0.0), ("instruction", 0.0));

        await Judge(judge).GateAsync(Window, Context, CancellationToken.None);

        var request = judge.Requests.ShouldHaveSingleItem();
        request.State.ToJsonString().ShouldBe(JsonSerializer.Serialize(new
        {
            context = new[] { new { role = "user", text = "pon música" }, new { role = "assistant", text = "¿Qué te apetece?" } },
            current = "algo tranquilo"
        }));
        request.Questions.Keys.ShouldBe(["fact", "preference", "instruction"], ignoreOrder: true);
        request.Questions.Values.ShouldAllBe(q => q is NoulQuestion);
    }

    [Fact]
    public async Task AnAnsweredCall_PublishesOneEventSayingWhetherItSkipped()
    {
        var published = new RecordingMetricsPublisher();
        var judge = StubJudge.Nouls(("fact", 0.05), ("preference", 0.1), ("instruction", 0.02));

        await Judge(judge, published).GateAsync(Window, Context, CancellationToken.None);

        var evt = published.Published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(MemoryJudgmentKinds.Gate);
        evt.Answered.ShouldBeTrue();
        evt.Skipped.ShouldBe(true);
        evt.Scores!["fact"].ShouldBe(0.05);
        evt.UserId.ShouldBe("user1");
        evt.AgentId.ShouldBe("nabu");
        evt.ConversationId.ShouldBe("conv-1");
        evt.InputTokens.ShouldBe(100);
        evt.Model.ShouldBe("jev-test");
        evt.DurationMs.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnAbsentCall_PublishesOneEventSayingSo()
    {
        var published = new RecordingMetricsPublisher();

        await Judge(StubJudge.Absent(AbsenceReason.Deadline), published).GateAsync(Window, Context, CancellationToken.None);

        var evt = published.Published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(MemoryJudgmentKinds.Gate);
        evt.Answered.ShouldBeFalse();
        evt.Skipped.ShouldBeNull();
        evt.Scores.ShouldBeNull();
    }

    // An unconfigured judge is the feature off, not a thing to count.
    [Fact]
    public async Task AnUnconfiguredJudge_PublishesNothing()
    {
        var published = new RecordingMetricsPublisher();

        await Judge(StubJudge.Absent(AbsenceReason.Unconfigured), published).GateAsync(Window, Context, CancellationToken.None);

        published.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyWindow_AsksNothing()
    {
        var judge = StubJudge.Nouls(("fact", 0.0), ("preference", 0.0), ("instruction", 0.0));

        var verdict = await Judge(judge).GateAsync([], Context, CancellationToken.None);

        verdict.Skip.ShouldBeFalse();
        judge.Requests.ShouldBeEmpty();
    }
}