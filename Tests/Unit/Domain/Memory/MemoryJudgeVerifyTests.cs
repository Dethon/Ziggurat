using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.Domain.Memory;

// The check: four yes/no questions against the window plus the candidate, stored only when each
// clears its bar. An instruction is a request, so it is judged on `supported` alone.
public class MemoryJudgeVerifyTests
{
    private static readonly MemoryJudgmentContext _context = new("user1");

    private static readonly IReadOnlyList<ChatMessage> _window = [new(ChatRole.User, "¿qué tiempo hace el martes?")];

    private static ExtractionCandidate Candidate(string content, MemoryCategory category = MemoryCategory.Fact) =>
        new(content, category, 0.5, 0.9, [], null);

    private static MemoryJudge Judge(IJudge judge, RecordingMetricsPublisher? published = null) =>
        new(judge, new MemoryJudgmentSettings(), new FakeTimeProvider(), published);

    [Fact]
    public async Task ACandidateBelowTheBarOnAnyOneQuestion_IsDropped_AndTheDropCarriesContentAndScores()
    {
        var published = new RecordingMetricsPublisher();
        var judge = StubJudge.Nouls(("supported", 0.9), ("about_user", 0.9), ("durable", 0.4), ("not_a_question", 0.9));

        var verdict = await Judge(judge, published).VerifyAsync(_window, Candidate("Preguntó por el tiempo el martes"), _context, CancellationToken.None);

        verdict.Store.ShouldBeFalse();
        var evt = published.Published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(MemoryJudgmentKinds.Verify);
        evt.Dropped.ShouldBe(true);
        evt.Candidate.ShouldBe("Preguntó por el tiempo el martes");
        evt.Category.ShouldBe("Fact");
        evt.Scores.ShouldBe(new Dictionary<string, double>
        {
            ["supported"] = 0.9, ["about_user"] = 0.9, ["durable"] = 0.4, ["not_a_question"] = 0.9
        });
    }

    [Fact]
    public async Task ACandidateAtTheBarOnAllFour_IsStored()
    {
        var published = new RecordingMetricsPublisher();
        var judge = StubJudge.Nouls(("supported", 0.5), ("about_user", 0.5), ("durable", 0.5), ("not_a_question", 0.5));

        var verdict = await Judge(judge, published).VerifyAsync(_window, Candidate("Se levanta a las siete"), _context, CancellationToken.None);

        verdict.Store.ShouldBeTrue();
        published.Published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem().Dropped.ShouldBe(false);
    }

    [Fact]
    public async Task AnInstruction_IsJudgedOnSupportedAlone()
    {
        var judge = StubJudge.Nouls(("supported", 0.8), ("about_user", 0.6), ("durable", 0.49), ("not_a_question", 0.1));

        var verdict = await Judge(judge).VerifyAsync(
            _window, Candidate("Instrucción: tratarle siempre de tú", MemoryCategory.Instruction), _context, CancellationToken.None);

        verdict.Store.ShouldBeTrue();
    }

    [Fact]
    public async Task AnInstructionNobodyStated_IsStillDropped()
    {
        var judge = StubJudge.Nouls(("supported", 0.2), ("about_user", 0.9), ("durable", 0.9), ("not_a_question", 0.9));

        var verdict = await Judge(judge).VerifyAsync(
            _window, Candidate("Instrucción: no poner música", MemoryCategory.Instruction), _context, CancellationToken.None);

        verdict.Store.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AbsenceReason.Deadline)]
    [InlineData(AbsenceReason.Error)]
    [InlineData(AbsenceReason.Unconfigured)]
    public async Task NoAnswer_Stores(AbsenceReason reason)
    {
        var verdict = await Judge(StubJudge.Absent(reason)).VerifyAsync(_window, Candidate("anything"), _context, CancellationToken.None);

        verdict.Store.ShouldBeTrue();
    }

    // A question the judge did not answer is not a low score: the other three still decide.
    [Fact]
    public async Task AQuestionLeftUnanswered_DoesNotDrop()
    {
        var judge = StubJudge.Nouls(("supported", 0.9), ("about_user", 0.9), ("not_a_question", 0.9));

        var verdict = await Judge(judge).VerifyAsync(_window, Candidate("Trabaja en Globex"), _context, CancellationToken.None);

        verdict.Store.ShouldBeTrue();
    }

    [Fact]
    public async Task TheStateIsTheWindowPlusTheCandidate()
    {
        var judge = StubJudge.Nouls(("supported", 0.9), ("about_user", 0.9), ("durable", 0.9), ("not_a_question", 0.9));

        await Judge(judge).VerifyAsync(_window, Candidate("Trabaja en Globex"), _context, CancellationToken.None);

        var request = judge.Requests.ShouldHaveSingleItem();
        request.State["current"]!.GetValue<string>().ShouldBe("¿qué tiempo hace el martes?");
        request.State["candidate"]!.GetValue<string>().ShouldBe("Trabaja en Globex");
        request.Questions.Keys.ShouldBe(["supported", "about_user", "durable", "not_a_question"], ignoreOrder: true);
    }

    [Fact]
    public async Task Disabled_AsksNothingAndStores()
    {
        var judge = StubJudge.Nouls(("supported", 0.0), ("about_user", 0.0), ("durable", 0.0), ("not_a_question", 0.0));
        var sut = new MemoryJudge(judge, new MemoryJudgmentSettings { Enabled = false }, new FakeTimeProvider());

        var verdict = await sut.VerifyAsync(_window, Candidate("anything"), _context, CancellationToken.None);

        verdict.Store.ShouldBeTrue();
        judge.Requests.ShouldBeEmpty();
    }
}