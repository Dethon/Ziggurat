using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Regression guard for the "Target page, context or browser has been closed" crash.
//
// A page that emits an uncaught JS error with no source location used to crash the entire
// Camoufox/Playwright server process: the Firefox backend forwarded location=undefined to
// playwright-core's PageError dispatcher, which dereferenced/validated location.url synchronously
// inside an EventEmitter callback, throwing an uncaught exception that exited Node. The fix lives
// in the Camoufox image (DockerCompose/camoufox/patch-playwright.js).
//
// This test owns its trigger instead of borrowing a flaky real-world page (it originally
// reproduced against aemet.es): a cross-origin <script src="data:..."> that throws produces a
// masked "Script error." with no location — exactly the condition that crashed the server. It then
// asserts the server is still alive and serving. Against an unpatched image this fails (the
// connection drops); against the patched image the page error is delivered harmlessly.
[Collection(PlaywrightCollections.SharedBrowser)]
public class CamoufoxPageErrorCrashTests(PlaywrightWebBrowserFixture fixture)
{
    private const string LocationlessErrorPage =
        "<!doctype html><html><body><p>trigger</p>" +
        "<script src=\"data:text/javascript,throw new Error('locationless')\"></script>" +
        "</body></html>";

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task LocationlessPageError_DoesNotCrashTheBrowserServer()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Firefox.ConnectAsync(fixture.WsEndpoint!);
        try
        {
            var disconnected = false;
            browser.Disconnected += (_, _) => disconnected = true;

            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            // Loading the hostile page must not take the server down with it.
            try
            {
                await page.SetContentAsync(LocationlessErrorPage);
            }
            catch (PlaywrightException)
            {
                // Even if this call races the (now-guarded) page error, the server must survive —
                // proven by the positive assertions below.
            }

            await Task.Delay(1500);

            // The server is still alive: the connection held, and a fresh page still works.
            disconnected.ShouldBeFalse();
            browser.IsConnected.ShouldBeTrue();

            var survivor = await browser.NewContextAsync();
            var survivorPage = await survivor.NewPageAsync();
            await survivorPage.SetContentAsync("<p id='ok'>alive</p>");
            (await survivorPage.InnerTextAsync("#ok")).ShouldBe("alive");
        }
        finally
        {
            try
            { await browser.CloseAsync(); }
            catch (PlaywrightException) { /* already gone */ }
        }
    }
}