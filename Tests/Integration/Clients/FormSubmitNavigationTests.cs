using System.Diagnostics;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.Judgments;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A click told to wait for a navigation waits for the page to change, not for its last picture: a
// form that posts to a confirmation is answered once that confirmation's document is
// there. Waiting for the load event made a submit cost thirty seconds whenever the event came late
// or was missed, and the eval's own materials form paid it now and then under a full suite.
[Collection(PlaywrightCollections.IsolatedSessionsC)]
public class FormSubmitNavigationTests(IsolatedSessionBrowserFixture fixture)
{
    private const string Site = "https://slow-confirmation.test";

    private static readonly TimeSpan _pictureDelay = TimeSpan.FromSeconds(20);

    private static async Task ServeAsync(IRoute route)
    {
        var path = new Uri(route.Request.Url).AbsolutePath;
        try
        {
            switch (path)
            {
                case "/form":
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        ContentType = "text/html",
                        Body = """
                            <!doctype html><html><body>
                            <form method="post" action="/done">
                              <label>Nombre <input name="nombre"></label>
                              <button type="submit">Enviar</button>
                            </form>
                            </body></html>
                            """
                    });
                    break;
                case "/done":
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        ContentType = "text/html",
                        Body = """
                            <!doctype html><html><body>
                            <h1>Recibido</h1><img src="/slow.png" width="200" height="200">
                            </body></html>
                            """
                    });
                    break;
                default:
                    // The picture that holds the load event back long after the document is there.
                    await Task.Delay(_pictureDelay);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                    break;
            }
        }
        catch (PlaywrightException)
        {
            // The session closed before the picture was answered.
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ASubmit_IsAnsweredOnceTheConfirmationIsThere_NotWhenItsPicturesLoad()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        await fixture.Browser.RouteOnContextAsync($"{Site}/**", ServeAsync);
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var nav = await fixture.Browser.NavigateAsync(
                new BrowseRequest(sessionId, $"{Site}/form") { Caller = JudgmentCaller.None });
            nav.Status.ShouldBe(BrowseStatus.Success);
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            var submit = Regex.Match(snapshot.Snapshot!, @"button ""Enviar"" \[ref=(e-\d+)\]").Groups[1].Value;
            submit.ShouldNotBeNullOrEmpty();

            var watch = Stopwatch.StartNew();
            var result = await fixture.Browser.ActionAsync(new WebActionRequest(
                sessionId, submit, WebActionType.Click, WaitForNavigation: true));
            watch.Stop();

            result.Status.ShouldBe(WebActionStatus.Success, result.ErrorMessage);
            result.NavigationOccurred.ShouldBeTrue();
            result.Url.ShouldBe($"{Site}/done");
            result.Snapshot.ShouldNotBeNull().ShouldContain("Recibido");
            watch.Elapsed.ShouldBeLessThan(_pictureDelay / 2);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}