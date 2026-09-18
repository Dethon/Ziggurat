using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Judgments;
using Domain.Tools.Web;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.Domain.Tools.Web;

// The judgment at the dismisser's seam, against a fake contract: what is asked, in what shape,
// and what the rule makes of the answers. The click itself is the browser test's.
public class ModalJudgeTests
{
    private static readonly IReadOnlyList<ModalControl> CookieWall =
    [
        new(0, "button", "Configurar"),
        new(1, "button", "Aceptar todo"),
        new(2, "button", "Rechazar todo")
    ];

    private static readonly IReadOnlyList<ModalControl> SiteNavigation =
    [
        new(0, "link", "Iniciar sesión"),
        new(1, "button", "Buscar"),
        new(2, "button", "Menú")
    ];

    private static readonly ModalJudgmentSettings Settings = new();

    [Fact]
    public async Task Cookie_AConfidentReject_IsPickedOverTheAccept()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, "2", 0.9), (ModalJudge.AcceptQuestionId, "1", 0.95));

        var pick = await Judge(judge).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.Picked);
        pick.Index.ShouldBe(2);
        pick.Confidence.ShouldBe(0.9);
    }

    [Fact]
    public async Task Cookie_NoRejectOnTheWall_FallsBackToTheAccept()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, ModalJudge.NoneChoice, 0.9), (ModalJudge.AcceptQuestionId, "1", 0.8));

        var pick = await Judge(judge).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.Picked);
        pick.Index.ShouldBe(1);
    }

    [Fact]
    public async Task Cookie_BothUnderTheBar_ClicksNothing()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, "2", 0.5), (ModalJudge.AcceptQuestionId, "1", 0.59));

        var pick = await Judge(judge).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.None);
        pick.Index.ShouldBeNull();
        pick.Confidence.ShouldBe(0.59);
    }

    [Fact]
    public async Task APagesOwnNavigation_BothNone_ClicksNothingHoweverConfident()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, ModalJudge.NoneChoice, 0.99), (ModalJudge.AcceptQuestionId, ModalJudge.NoneChoice, 0.99));

        var pick = await Judge(judge).PickAsync(ModalType.CookieConsent, SiteNavigation, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.None);
        pick.Index.ShouldBeNull();
    }

    [Fact]
    public async Task APickNamingNoControlThatWasSent_ClicksNothing()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, "7", 0.9), (ModalJudge.AcceptQuestionId, "7", 0.9));

        var pick = await Judge(judge).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.None);
    }

    [Fact]
    public async Task ANewsletter_IsAskedOneQuestion_ACookieWallTwo_EachInOneRequest()
    {
        var newsletter = Choices((ModalJudge.DeclineQuestionId, "0", 0.9));
        var cookie = Choices((ModalJudge.RejectQuestionId, "2", 0.9), (ModalJudge.AcceptQuestionId, "1", 0.9));

        await Judge(newsletter).PickAsync(ModalType.Newsletter, [new ModalControl(0, "button", "Quizás más tarde")], CancellationToken.None);
        await Judge(cookie).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        newsletter.Requests.ShouldHaveSingleItem().Questions.Keys.ShouldBe([ModalJudge.DeclineQuestionId]);
        cookie.Requests.ShouldHaveSingleItem().Questions.Keys.ShouldBe([ModalJudge.RejectQuestionId, ModalJudge.AcceptQuestionId]);
        cookie.Requests[0].Questions.Values.ShouldAllBe(q => q is ChoiceQuestion);
    }

    [Theory]
    [InlineData(ModalType.AgeGate, ModalJudge.EnterQuestionId)]
    [InlineData(ModalType.Notification, ModalJudge.DenyQuestionId)]
    public async Task TheOtherKinds_AreAskedTheirOneQuestion(ModalType kind, string questionId)
    {
        var judge = Choices((questionId, "0", 0.9));

        var pick = await Judge(judge).PickAsync(kind, [new ModalControl(0, "button", "Soy mayor de 18 años")], CancellationToken.None);

        judge.Requests.ShouldHaveSingleItem().Questions.Keys.ShouldBe([questionId]);
        pick.Index.ShouldBe(0);
    }

    [Fact]
    public async Task MoreControlsThanTheCap_SendsTheFirstCapInDocumentOrder()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, "3", 0.9), (ModalJudge.AcceptQuestionId, "3", 0.9));
        var controls = Enumerable.Range(0, 25).Select(i => new ModalControl(i, "button", $"Control {i}")).ToList();

        await Judge(judge, Settings with { MaxControls = 20 }).PickAsync(ModalType.CookieConsent, controls, CancellationToken.None);

        var request = judge.Requests.ShouldHaveSingleItem();
        var sent = request.State["controls"]!.AsArray();
        sent.Count.ShouldBe(20);
        sent.Select(c => c!["index"]!.GetValue<int>()).ShouldBe(Enumerable.Range(0, 20));
        var criteria = ((ChoiceQuestion)request.Questions[ModalJudge.RejectQuestionId]).Criteria;
        criteria.Keys.ShouldBe([.. Enumerable.Range(0, 20).Select(i => i.ToString()), ModalJudge.NoneChoice]);
    }

    [Fact]
    public async Task TheState_CarriesTheKindAndTheControlsAndNothingElse()
    {
        var judge = Choices((ModalJudge.RejectQuestionId, "2", 0.9), (ModalJudge.AcceptQuestionId, "1", 0.9));

        await Judge(judge).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        var state = judge.Requests.ShouldHaveSingleItem().State;
        state.Select(p => p.Key).ShouldBe(["overlay_kind", "controls"]);
        state["overlay_kind"]!.GetValue<string>().ShouldBe("cookie");
        var control = state["controls"]!.AsArray()[2]!.AsObject();
        control.Select(p => p.Key).ShouldBe(["index", "role", "name"]);
        control["name"]!.GetValue<string>().ShouldBe("Rechazar todo");
        var criteria = ((ChoiceQuestion)judge.Requests[0].Questions[ModalJudge.AcceptQuestionId]).Criteria;
        criteria["2"].ShouldBe("button \"Rechazar todo\"");
    }

    [Fact]
    public async Task AnAnswerAfterTheDeadline_IsAbsent_AndNothingIsClicked()
    {
        var clock = new FakeTimeProvider();
        var settings = Settings with { DeadlineMs = 1000 };
        // The judge answers a confident pick, but only after the deadline has passed it by.
        var judge = new StubJudge(request =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return Answered((ModalJudge.RejectQuestionId, "2", 0.95), (ModalJudge.AcceptQuestionId, "1", 0.95));
        });

        var pick = await new ModalJudge(judge, settings, clock).PickAsync(ModalType.CookieConsent, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.Absent);
        pick.Index.ShouldBeNull();
        pick.Latency.ShouldBe(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task AJudgeThatHonoursTheDeadline_IsCancelledAtIt()
    {
        var clock = new FakeTimeProvider();
        var judge = new StubJudge(_ => throw new InvalidOperationException("not reached"));
        var honouring = new CancellationObservingJudge(clock);

        var pending = new ModalJudge(honouring, Settings with { DeadlineMs = 1000 }, clock)
            .PickAsync(ModalType.Newsletter, [new ModalControl(0, "button", "×")], CancellationToken.None);
        await honouring.Asked.Task;
        clock.Advance(TimeSpan.FromMilliseconds(1001));

        var pick = await pending;
        pick.Status.ShouldBe(ModalPickStatus.Absent);
        judge.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AbsenceReason.Unconfigured)]
    [InlineData(AbsenceReason.Error)]
    public async Task AnAbsentJudge_ClicksNothing(AbsenceReason reason)
    {
        var pick = await Judge(StubJudge.Absent(reason)).PickAsync(ModalType.AgeGate, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.Absent);
    }

    [Fact]
    public async Task AGenericOverlay_IsNotAsked()
    {
        var judge = StubJudge.Absent();

        var pick = await Judge(judge).PickAsync(ModalType.Generic, CookieWall, CancellationToken.None);

        pick.Status.ShouldBe(ModalPickStatus.NotAsked);
        judge.Requests.ShouldBeEmpty();
    }

    private static ModalJudge Judge(IJudge judge, ModalJudgmentSettings? settings = null) =>
        new(judge, settings ?? Settings, new FakeTimeProvider());

    private static StubJudge Choices(params (string Id, string Choice, double Confidence)[] answers) =>
        new(Answered(answers));

    private static JudgmentOutcome Answered(params (string Id, string Choice, double Confidence)[] answers) =>
        new JudgmentOutcome.Answered(new Judgment(
            "jev-test",
            answers.ToDictionary(
                a => a.Id,
                a => (JudgmentAnswer)new ChoiceAnswer(a.Choice, a.Confidence, new Dictionary<string, double> { [a.Choice] = a.Confidence }),
                StringComparer.Ordinal),
            new JudgmentUsage(100, 0)));

    // A judge that waits on the deadline it was handed, as the real client does, and answers the
    // absence the cancellation means.
    private sealed class CancellationObservingJudge(FakeTimeProvider clock) : IJudge
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
        {
            Asked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, clock, deadline);
            }
            catch (OperationCanceledException)
            {
                return new JudgmentOutcome.Absent(AbsenceReason.Deadline);
            }

            throw new InvalidOperationException("not reached");
        }
    }
}