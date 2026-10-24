using Domain.Contracts;
using Domain.Judgments;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A bot check that answers 200, runs a script that sends the page on to a follow-up address, and
// has the server refuse that one while leaving behind what lets the next visit through. Reddit
// does this to every fresh browser on some addresses: the first browse of a session read "You've
// been blocked by network security" and every later one read the thread. The browser sees only
// the shape, so the test builds the shape: the page the browse asked for, then the page's own
// script landing it on an error the server answers once.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class SelfNavigatedErrorBrowseTests(IsolatedSessionBrowserFixture fixture)
{
    private const string ChallengePage =
        "<!DOCTYPE html><html><head><title>Checking</title></head><body><p>One moment.</p>"
        + "<script>location.replace(location.pathname + '?solution=1');</script></body></html>";

    private const string RealPage =
        "<!DOCTYPE html><html><head><title>The thread</title></head><body><article><h1>The thread</h1>"
        + "<p>The comments a reader came for, long enough to be the page's content.</p></article></body></html>";

    private const string BlockPage =
        "<!DOCTYPE html><html><head><title></title></head><body><p>You have been blocked.</p></body></html>";

    [SkippableFact]
    public async Task APageThatSendsItselfOntoAnErrorOnce_IsBrowsedAgainAndReadsWhatTheSecondVisitServes()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var host = $"https://challenge-{Guid.NewGuid():N}.test";
        var refusals = 0;
        await fixture.Browser.RouteOnContextAsync($"{host}/**", route =>
            route.Request.Url.Contains("solution=")
                ? Interlocked.Increment(ref refusals) == 1
                    ? FulfillAsync(route, 403, BlockPage)
                    : FulfillAsync(route, 200, RealPage)
                : FulfillAsync(route, 200, ChallengePage));

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var result = await fixture.Browser.NavigateAsync(
                new BrowseRequest(sessionId, $"{host}/thread") { Caller = JudgmentCaller.None });

            result.Status.ShouldBe(BrowseStatus.Success);
            result.Content.ShouldNotBeNull().ShouldContain("The comments a reader came for");
            result.HttpStatus.ShouldBe(200);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // The bound: a page whose script lands on an error every time is asked for twice and no more,
    // and the error it settled on is the status the browse reports, not the 200 the first document
    // committed with.
    [SkippableFact]
    public async Task APageThatAlwaysSendsItselfOntoAnError_IsAskedForTwiceAndReportsTheError()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var host = $"https://challenge-{Guid.NewGuid():N}.test";
        var asked = 0;
        await fixture.Browser.RouteOnContextAsync($"{host}/**", route =>
        {
            if (route.Request.Url.Contains("solution="))
            {
                return FulfillAsync(route, 403, BlockPage);
            }

            Interlocked.Increment(ref asked);
            return FulfillAsync(route, 200, ChallengePage);
        });

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var result = await fixture.Browser.NavigateAsync(
                new BrowseRequest(sessionId, $"{host}/thread") { Caller = JudgmentCaller.None });

            result.HttpStatus.ShouldBe(403);
            result.Content.ShouldNotBeNull().ShouldContain("You have been blocked");
            asked.ShouldBe(2);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private static Task FulfillAsync(IRoute route, int status, string body) =>
        route.FulfillAsync(new() { Status = status, ContentType = "text/html", Body = body });
}