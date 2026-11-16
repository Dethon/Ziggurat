using Domain.Contracts;
using Domain.Judgments;
using Infrastructure.Clients.Browser;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The popup grace on a real browser: a claim that holds only inside a 1.5 s window, so it runs on
// the backend nothing else is driving (PlaywrightCollectionLayoutTests). The window itself is pinned
// on a fake clock in TabProtocolTests; this is the browser reporting a real popup inside it.
[Collection(PlaywrightCollections.Timing)]
public class PopupGraceTests(QuietBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AClickThatOpensANewTab_AnswersFromThePopup()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // The click answers from the popup when the browser reports it inside the 1.5 s grace, and
        // a machine running a second suite beside this one reported it later — where the popup is
        // still adopted and made current, but the click has already answered from its own page.
        // So the answer is the best of a few clicks; one that never answers from the popup is a
        // grace that does not work.
        var answered = await LatencyBudget.HoldsOnceAsync(async () =>
        {
            var sessionId = $"test-{Guid.NewGuid():N}";
            try
            {
                await BrowseAsync(sessionId, "https://example.com/");
                await InjectAsync(sessionId,
                    """
                    <button id="open" onclick="window.open('https://example.org/')">Open elsewhere</button>
                    """);
                var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
                var buttonRef = System.Text.RegularExpressions.Regex
                    .Match(snapshot.Snapshot!, @"button ""Open elsewhere"" \[ref=(e-\d+)\]").Groups[1].Value;
                buttonRef.ShouldNotBeNullOrEmpty();

                var result = await fixture.Browser.ActionAsync(new WebActionRequest(
                    sessionId, buttonRef, WebActionType.Click));

                result.Status.ShouldBe(WebActionStatus.Success);
                if (!result.NavigationOccurred)
                {
                    return false;
                }

                // The action answers from where the site actually took the model, not with a stale
                // diff of the page left behind.
                result.Url.ShouldNotBeNull().ShouldContain("example.org");
                result.Snapshot.ShouldNotBeNull();

                // The popup joined the pool as the tab the next ref-less call addresses…
                (await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId)))
                    .Url.ShouldNotBeNull().ShouldContain("example.org");

                // …and the opener lives on beside it, still addressable by its URL.
                (await fixture.Browser.SnapshotAsync(
                        new SnapshotRequest(sessionId, ForUrl: "https://example.com/")))
                    .ErrorMessage.ShouldBeNull();
                return true;
            }
            finally
            {
                await fixture.Browser.CloseSessionAsync(sessionId);
            }
        });

        answered.ShouldBeTrue("no click answered from the popup it opened");
    }

    private async Task BrowseAsync(string sessionId, string url)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, url) { Caller = JudgmentCaller.None });
        nav.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
    }

    private async Task InjectAsync(string sessionId, string markup)
    {
        await fixture.Browser.EvaluateOnSessionAsync(
            sessionId,
            $"() => {{ document.body.innerHTML = {System.Text.Json.JsonSerializer.Serialize(markup)}; }}");
    }
}