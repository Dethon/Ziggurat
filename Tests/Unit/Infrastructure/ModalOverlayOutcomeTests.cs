using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The event is the measure: what a detected overlay ended as, in the spellings the dashboard
// groups by. A judgment's confidence and latency ride only on the outcomes a judge produced.
public class ModalOverlayOutcomeTests
{
    [Fact]
    public void ToEvent_LeftStanding_CarriesTheKindAndNoSelector()
    {
        var evt = ModalOverlayOutcome.LeftStanding(ModalType.CookieConsent).ToEvent();

        evt.Kind.ShouldBe(ModalKinds.Cookie);
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.LeftStanding);
        evt.Selector.ShouldBeNull();
        evt.ButtonText.ShouldBeNull();
        evt.Confidence.ShouldBeNull();
        evt.DurationMs.ShouldBeNull();
    }

    [Theory]
    [InlineData(ModalDismissalPath.Selector, ModalDismissalOutcomes.Selector)]
    [InlineData(ModalDismissalPath.Text, ModalDismissalOutcomes.Text)]
    public void ToEvent_ACheapPath_NamesWhatItClicked(ModalDismissalPath path, string expected)
    {
        var outcome = new ModalOverlayOutcome(
            ModalType.Newsletter, path, new ModalDismissed(ModalType.Newsletter, "text(no thanks)", "No thanks"));

        var evt = outcome.ToEvent();

        evt.Kind.ShouldBe(ModalKinds.Newsletter);
        evt.Outcome.ShouldBe(expected);
        evt.Selector.ShouldBe("text(no thanks)");
        evt.ButtonText.ShouldBe("No thanks");
    }

    [Fact]
    public void ToEvent_AJudgment_CarriesThePickItsConfidenceAndItsLatency()
    {
        var outcome = new ModalOverlayOutcome(
            ModalType.AgeGate,
            ModalDismissalPath.Judgment,
            new ModalDismissed(ModalType.AgeGate, "judgment(1)", "Soy mayor de 18 años"),
            Confidence: 0.93,
            JudgmentLatency: TimeSpan.FromMilliseconds(325));

        var evt = outcome.ToEvent();

        evt.Kind.ShouldBe(ModalKinds.Age);
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.Judgment);
        evt.Selector.ShouldBe("judgment(1)");
        evt.Confidence.ShouldBe(0.93);
        evt.DurationMs.ShouldBe(325);
    }
}