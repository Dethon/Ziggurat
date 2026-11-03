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
    // The script sends the page on, and the blocker after it holds the parse until the page's follow-up
    // has been asked for (Challenge): when the browser issues a script's navigation is its own
    // business, and a busy one issued it after the page looked settled and had been read. Held, the
    // page cannot look ready before its navigation is in flight, so the browse sees it go.
    private const string ChallengePage =
        "<!DOCTYPE html><html><head><title>Checking</title></head><body><p>One moment.</p>"
        + "<script>location.replace(location.pathname + '?solution=1');</script>"
        + "<script src=\"/challenge-blocker.js\"></script></body></html>";

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
        var challenge = new Challenge();
        await fixture.Browser.RouteOnContextAsync($"{host}/**", route =>
            challenge.Answer(route, () => route.Request.Url.Contains("solution=")
                ? Interlocked.Increment(ref refusals) == 1
                    ? FulfillLateAsync(route, 403, BlockPage)
                    : FulfillLateAsync(route, 200, RealPage)
                : FulfillAsync(route, 200, ChallengePage)));

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var result = await fixture.Browser.NavigateAsync(
                new BrowseRequest(sessionId, $"{host}/thread") { Caller = JudgmentCaller.None });

            var seen = $"at {result.Url} after {refusals} refusals, {result.Status} {result.HttpStatus}: {result.ErrorMessage}";
            result.Status.ShouldBe(BrowseStatus.Success, seen);
            result.Content.ShouldNotBeNull().ShouldContain("The comments a reader came for", customMessage: seen);
            result.HttpStatus.ShouldBe(200, seen);
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
        var challenge = new Challenge();
        await fixture.Browser.RouteOnContextAsync($"{host}/**", route => challenge.Answer(route, () =>
        {
            if (route.Request.Url.Contains("solution="))
            {
                return FulfillLateAsync(route, 403, BlockPage);
            }

            Interlocked.Increment(ref asked);
            return FulfillAsync(route, 200, ChallengePage);
        }));

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var result = await fixture.Browser.NavigateAsync(
                new BrowseRequest(sessionId, $"{host}/thread") { Caller = JudgmentCaller.None });

            var seen = $"at {result.Url} after {asked} asks, {result.Status}: {result.Content?[..Math.Min(80, result.Content.Length)]}";
            result.HttpStatus.ShouldBe(403, seen);
            result.Content.ShouldNotBeNull().ShouldContain("You have been blocked");
            asked.ShouldBe(2, seen);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // Holds each challenge's blocker until that challenge's follow-up has been asked for, then lets the
    // parse finish; every other request is answered as the test says.
    private sealed class Challenge
    {
        private int _blockers;
        private int _followUps;

        public async Task Answer(IRoute route, Func<Task> otherwise)
        {
            if (route.Request.Url.Contains("challenge-blocker.js"))
            {
                var mine = Interlocked.Increment(ref _blockers);
                await Eventually.Until(() => Volatile.Read(ref _followUps) >= mine, "the challenge's follow-up to be asked for");
                await route.FulfillAsync(new() { Status = 200, ContentType = "text/javascript", Body = "" });
                return;
            }

            if (route.Request.Url.Contains("solution="))
            {
                Interlocked.Increment(ref _followUps);
            }

            await otherwise();
        }
    }

    private static Task FulfillAsync(IRoute route, int status, string body) =>
        route.FulfillAsync(new() { Status = status, ContentType = "text/html", Body = body });

    // The follow-up is answered slower than the page takes to look settled, as a challenge
    // server under load does: a browse that read the page while that navigation was still in
    // flight answered with the challenge itself, or with an error from a read the navigation
    // tore down — both only when the suite was busy enough to delay the answer by chance.
    private static async Task FulfillLateAsync(IRoute route, int status, string body)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await FulfillAsync(route, status, body);
    }
}