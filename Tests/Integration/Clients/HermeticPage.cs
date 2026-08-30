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

    public static async Task PrepareAsync(PlaywrightWebBrowser browser, string sessionId, string markup)
    {
        await browser.RouteOnContextAsync(AnchorUrl, route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<!DOCTYPE html><html><head><title>anchor</title></head><body></body></html>"
        }));

        var nav = await browser.NavigateAsync(new BrowseRequest(sessionId, AnchorUrl));
        nav.Status.ShouldBe(BrowseStatus.Success);

        await InjectAsync(browser, sessionId, markup);
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