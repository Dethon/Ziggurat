using Domain.Contracts;
using Domain.Judgments;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A page whose own script holds its thread answers no call made on it, and page.content() has no
// timeout of its own: on 2026-11-27 a browse of a torrent index waited on one for 35 minutes,
// holding its tab and the turn that asked, until the session's idle prune closed the page.
[Collection(PlaywrightCollections.IsolatedSessionsC)]
public class HungPageBrowseTests(IsolatedSessionBrowserFixture fixture)
{
    private const string HangingPage =
        "<!DOCTYPE html><html><head><title>Searching</title></head><body><p>Searching for it.</p>"
        + "<script>setTimeout(() => { for (;;) {} }, 0);</script></body></html>";

    private const string LivePage =
        "<!DOCTYPE html><html><head><title>Results</title></head><body><article><h1>Results</h1>"
        + "<p>What a reader came for, long enough to be the page's content.</p></article></body></html>";

    [SkippableFact]
    public async Task APageWhoseScriptHoldsItsThread_EndsTheBrowseAsUnresponsive_AndTheSessionBrowsesOn()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var hung = $"https://hung-{Guid.NewGuid():N}.test";
        var live = $"https://live-{Guid.NewGuid():N}.test";
        await fixture.Browser.RouteOnContextAsync($"{hung}/**", route => FulfillAsync(route, HangingPage));
        await fixture.Browser.RouteOnContextAsync($"{live}/**", route => FulfillAsync(route, LivePage));

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var result = await fixture.Browser.NavigateAsync(
                    new BrowseRequest(sessionId, $"{hung}/search") { Caller = JudgmentCaller.None })
                .WaitAsync(TimeSpan.FromMinutes(3));

            result.Status.ShouldBe(BrowseStatus.Error, result.ErrorMessage);
            result.ErrorMessage.ShouldNotBeNull().ShouldContain("stopped responding");

            var next = await fixture.Browser.NavigateAsync(
                    new BrowseRequest(sessionId, $"{live}/") { Caller = JudgmentCaller.None })
                .WaitAsync(TimeSpan.FromMinutes(1));
            next.Status.ShouldBe(BrowseStatus.Success, next.ErrorMessage);
            next.Content.ShouldNotBeNull().ShouldContain("What a reader came for");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private static Task FulfillAsync(IRoute route, string body) =>
        route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = body });
}