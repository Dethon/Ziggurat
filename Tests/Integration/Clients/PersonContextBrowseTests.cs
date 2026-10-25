using Domain.Contracts;
using Domain.Judgments;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A person's browser context against real Camoufox: their cookies stay theirs. Each case names
// people of its own, so it shares no context with any other case on the backend.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class PersonContextBrowseTests(IsolatedSessionBrowserFixture fixture)
{
    private const string Site = "https://person-site.test";

    // /login sets a cookie from script; /whoami prints what the page's cookies are; /launch opens
    // /whoami in a new window from a button.
    private static Task ServeAsync(IRoute route)
    {
        var path = new Uri(route.Request.Url).AbsolutePath;
        var body = path switch
        {
            "/login" => "<script>document.cookie='session=signed-in; path=/'</script><p>logged in</p>",
            "/launch" => "<button onclick=\"window.open('/whoami')\">Open account</button>",
            _ => "<p id='c'>none</p><script>document.getElementById('c').textContent='cookies:'+(document.cookie||'none')</script>"
        };
        return route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = $"<!DOCTYPE html><html><head><title>{path}</title></head><body>{body}</body></html>"
        });
    }

    private async Task<BrowseResult> BrowseAsync(string sessionId, string path, string? person)
    {
        await fixture.Browser.RouteOnContextAsync($"{Site}/**", ServeAsync, person);
        var result = await fixture.Browser.NavigateAsync(
            new BrowseRequest(sessionId, $"{Site}{path}") { Caller = JudgmentCaller.None, Person = person });
        result.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
        return result;
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ACookieSetInAPersonsContext_IsAbsentFromTheAnonymousContextAndAnotherPersons()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var fran = $"fran-{Guid.NewGuid():N}";
        var ana = $"ana-{Guid.NewGuid():N}";
        try
        {
            await BrowseAsync(sessionId, "/login", fran);

            (await BrowseAsync(sessionId, "/whoami", fran)).Content.ShouldContain("session=signed-in");
            (await BrowseAsync(sessionId, "/whoami?anon", null)).Content.ShouldNotContain("session=signed-in");
            (await BrowseAsync(sessionId, "/whoami?ana", ana)).Content.ShouldNotContain("session=signed-in");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // Refs need no flag: a ref stamped in a person's tab routes back to it, and the popup its
    // button opens is the person's too — it reads their cookies.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ARefInAPersonsTab_RoutesBackToIt_AndItsPopupStaysInTheirContext()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var fran = $"fran-{Guid.NewGuid():N}";
        try
        {
            await BrowseAsync(sessionId, "/login", fran);
            await BrowseAsync(sessionId, "/launch", fran);
            // An anonymous tab browsed after it becomes current, so the click below is routed by
            // its ref alone.
            await BrowseAsync(sessionId, "/whoami?anon", null);

            var snapshot = await fixture.Browser.SnapshotAsync(
                new SnapshotRequest(sessionId, ForUrl: $"{Site}/launch", Person: fran));
            snapshot.ErrorMessage.ShouldBeNull();
            var buttonRef = snapshot.Snapshot!.Split('\n')
                .First(l => l.Contains("Open account"))
                .Split("[ref=")[1].Split(']')[0];

            var clicked = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, buttonRef, WebActionType.Click));

            clicked.Status.ShouldBe(WebActionStatus.Success);
            clicked.Url.ShouldNotBeNull().ShouldContain("/whoami");
            var popupText = await fixture.Browser.EvaluateOnSessionAsync<string>(
                sessionId, "() => document.body.innerText");
            popupText.ShouldContain("session=signed-in");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}