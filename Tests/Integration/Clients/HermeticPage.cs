using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Integration.Clients;

// A route-fulfilled anchor page for the browser tests that only need a document to inject markup
// onto. These used to navigate example.com, which made every one of them lean on a live third
// party for a blank canvas: a bare test run must stay green with the network's third parties
// unreachable, and the anchor is the one place that dependency hid.
internal static class HermeticPage
{
    public const string AnchorUrl = "https://hermetic-anchor.test/";

    // The route goes on the context, and the context outlives every case: the backend is refcounted
    // across the whole run, so registering per call stacked one more handler for the same URL on it
    // every time, none of them ever removed. They all answer identically, so nothing was wrong with
    // the page — but Playwright consults them on every request the context makes, and dozens of
    // dead handlers is work every navigation in the run pays for and no case asked for.
    //
    // One registration per browser, then. Keyed on the instance rather than a bare flag because a
    // pool that hands back a different browser must get its own route — and memoised as the task
    // rather than as a flag set afterwards, so callers racing into the first registration await
    // that one instead of starting a second beside it.
    private static readonly Dictionary<PlaywrightWebBrowser, Task> _routed = [];

    public static async Task PrepareAsync(PlaywrightWebBrowser browser, string sessionId, string markup)
    {
        await EnsureRouteAsync(browser);

        var nav = await browser.NavigateAsync(new BrowseRequest(sessionId, AnchorUrl));
        nav.Status.ShouldBe(BrowseStatus.Success);

        await InjectAsync(browser, sessionId, markup);
    }

    private static Task EnsureRouteAsync(PlaywrightWebBrowser browser)
    {
        lock (_routed)
        {
            if (!_routed.TryGetValue(browser, out var registration))
            {
                registration = browser.RouteOnContextAsync(AnchorUrl, route => route.FulfillAsync(new()
                {
                    ContentType = "text/html",
                    Body = "<!DOCTYPE html><html><head><title>anchor</title></head><body></body></html>"
                }));
                _routed[browser] = registration;
            }

            return registration;
        }
    }

    // The markup goes in and the annotator measures what it rendered, so the write has to be laid
    // out before the measurement reads it. Setting innerHTML only dirties layout; nothing computes
    // a box until something asks for one. On a warm container the next round trip is slow enough
    // to hide that, and every case here passed for it — on a cold one the measure arrived first
    // and read zeros, and an image the case had sized in pixels filtered out as furniture.
    // Reading offsetHeight forces the reflow in the same evaluation that wrote the markup.
    public static async Task InjectAsync(PlaywrightWebBrowser browser, string sessionId, string markup)
    {
        await browser.EvaluateOnSessionAsync<int>(
            sessionId,
            $$"""
              () => {
                  document.body.innerHTML = {{JsonSerializer.Serialize(markup)}};
                  return document.body.offsetHeight;
              }
              """);
    }
}