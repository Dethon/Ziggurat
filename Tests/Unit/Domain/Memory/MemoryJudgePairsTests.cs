using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.Domain.Memory;

// The pair relation: one four-way choice per pair of a cluster, and the linked components of the
// same/updates links are what the merge model is called with.
public class MemoryJudgePairsTests
{
    private static readonly MemoryJudgmentContext _context = new("user1");

    private static MemoryEntry Memory(string id, string content) => new()
    {
        Id = id, UserId = "user1", Category = MemoryCategory.Fact, Content = content,
        Importance = 0.5, Confidence = 0.9, CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
    };

    private static readonly IReadOnlyList<MemoryEntry> _cluster =
    [
        Memory("mem_madrid", "Vive en Madrid"),
        Memory("mem_valencia", "Se ha mudado a Valencia"),
        Memory("mem_sister", "Su hermana Laura vive en Sevilla"),
        Memory("mem_brother", "Su hermano Pablo vive en Bilbao"),
        Memory("mem_valencia2", "Vive en Valencia desde septiembre")
    ];

    private static JudgmentOutcome Relations(params (string Pair, string Relation)[] answers) =>
        new JudgmentOutcome.Answered(new Judgment(
            "jev-test",
            answers.ToDictionary(a => a.Pair, a => (JudgmentAnswer)new ChoiceAnswer(a.Relation, 0.8, new Dictionary<string, double>()), StringComparer.Ordinal),
            new JudgmentUsage(500, 0)));

    private static MemoryJudge Judge(IJudge judge, RecordingMetricsPublisher? published = null) =>
        new(judge, new MemoryJudgmentSettings(), new FakeTimeProvider(), published);

    [Fact]
    public async Task TheLinkedComponents_AreWhatSameAndUpdatesConnect_SingletonsDropped()
    {
        var published = new RecordingMetricsPublisher();
        var judge = StubJudge.Answering(Relations(
            ("0-1", "updates"), ("0-2", "unrelated"), ("0-3", "unrelated"), ("0-4", "updates"),
            ("1-2", "unrelated"), ("1-3", "unrelated"), ("1-4", "same"),
            ("2-3", "distinct"), ("2-4", "unrelated"),
            ("3-4", "unrelated")));

        var verdict = await Judge(judge, published).RelateAsync(_cluster, _context, CancellationToken.None);

        verdict.Answered.ShouldBeTrue();
        verdict.Linked.ShouldHaveSingleItem().Select(m => m.Id).ShouldBe(["mem_madrid", "mem_valencia", "mem_valencia2"]);

        var evt = published.Published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(MemoryJudgmentKinds.Pairs);
        evt.MemoryIds.ShouldBe(_cluster.Select(m => m.Id));
        evt.Relations!["0-1"].ShouldBe("updates");
        evt.Relations["2-3"].ShouldBe("distinct");
        evt.Scores!["0-1"].ShouldBe(0.8);
        evt.Linked.ShouldHaveSingleItem().ShouldBe(["mem_madrid", "mem_valencia", "mem_valencia2"]);
        evt.InputTokens.ShouldBe(500);
    }

    [Fact]
    public async Task TwoSeparateLinks_AreTwoComponents()
    {
        var judge = StubJudge.Answering(Relations(
            ("0-1", "updates"), ("2-3", "same"),
            ("0-2", "unrelated"), ("0-3", "unrelated"), ("0-4", "unrelated"),
            ("1-2", "unrelated"), ("1-3", "unrelated"), ("1-4", "unrelated"),
            ("2-4", "unrelated"), ("3-4", "unrelated")));

        var verdict = await Judge(judge).RelateAsync(_cluster, _context, CancellationToken.None);

        verdict.Linked.Select(c => c.Select(m => m.Id).ToList()).ShouldBe([["mem_madrid", "mem_valencia"], ["mem_sister", "mem_brother"]]);
    }

    [Fact]
    public async Task NothingLinked_IsAnAnsweredVerdictWithNoComponents()
    {
        var judge = StubJudge.Answering(Relations(("0-1", "distinct")));

        var verdict = await Judge(judge).RelateAsync(_cluster.Take(2).ToList(), _context, CancellationToken.None);

        verdict.Answered.ShouldBeTrue();
        verdict.Linked.ShouldBeEmpty();
    }

    // A body that answers some of the pairs and not others says nothing about the pairs it left
    // out, and the gate and the check both treat an unanswered question as no evidence. Reading
    // the silence as "not linked" would quietly keep a cluster from the merge model it used to
    // reach — a failure away from the old behaviour rather than toward it.
    [Fact]
    public async Task APartiallyAnsweredJudgment_IsUnanswered_SoTheClusterGoesAsCosineMadeIt()
    {
        var judge = StubJudge.Answering(Relations(("0-1", "same")));

        var verdict = await Judge(judge).RelateAsync(_cluster, _context, CancellationToken.None);

        verdict.Answered.ShouldBeFalse();
        verdict.Linked.ShouldBeEmpty();
    }

    // The same silence, spelled as a choice nobody defined.
    [Fact]
    public async Task AChoiceOutsideTheFourRelations_IsUnanswered()
    {
        var judge = StubJudge.Answering(Relations(("0-1", "Same"), ("0-2", "unrelated")));

        var verdict = await Judge(judge).RelateAsync(_cluster.Take(3).ToList(), _context, CancellationToken.None);

        verdict.Answered.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AbsenceReason.Deadline)]
    [InlineData(AbsenceReason.Error)]
    [InlineData(AbsenceReason.Unconfigured)]
    public async Task NoAnswer_IsUnanswered(AbsenceReason reason)
    {
        var verdict = await Judge(StubJudge.Absent(reason)).RelateAsync(_cluster, _context, CancellationToken.None);

        verdict.Answered.ShouldBeFalse();
        verdict.Linked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabled_AsksNothing()
    {
        var judge = StubJudge.Answering(Relations(("0-1", "same")));
        var sut = new MemoryJudge(judge, new MemoryJudgmentSettings { Enabled = false }, new FakeTimeProvider());

        var verdict = await sut.RelateAsync(_cluster, _context, CancellationToken.None);

        verdict.Answered.ShouldBeFalse();
        judge.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASingleMemory_HasNoPairsAndAsksNothing()
    {
        var judge = StubJudge.Answering(Relations());

        var verdict = await Judge(judge).RelateAsync(_cluster.Take(1).ToList(), _context, CancellationToken.None);

        verdict.Answered.ShouldBeFalse();
        judge.Requests.ShouldBeEmpty();
    }
}