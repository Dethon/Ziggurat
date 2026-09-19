using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Clients;

// The measure, end to end through a real navigation: one ModalDismissalEvent per overlay the
// dismisser detected, naming how it ended, and nothing at all for a page without one. The browser
// here is the test's own — it carries the recording publisher — connected to the collection's
// Camoufox, and every page is route-fulfilled so no third party stands between the test and the
// wall it is measuring.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class ModalDismissalTelemetryTests(IsolatedSessionBrowserFixture fixture) : IAsyncLifetime
{
    private readonly RecordingMetricsPublisher _published = new();
    private PlaywrightWebBrowser? _browser;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(fixture.WsEndpoint))
        {
            return;
        }

        _browser = new PlaywrightWebBrowser(wsEndpoint: fixture.WsEndpoint, metricsPublisher: _published);
        await _browser.EnsureInitializedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_AWallNoPatternCloses_PublishesLeftStandingForItsKind()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        // No accept-ish class, and "Rechazar todo" is in no word list: the two cheap paths both
        // come up empty, so the wall is detected and left where it is.
        var url = await ServeAsync(
            "<div id='cmp' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999'>" +
            "Usamos cookies. <button class='cmp-secondary'>Rechazar todo</button></div><p>Main content</p>");

        var result = await _browser!.NavigateAsync(new BrowseRequest(Guid.NewGuid().ToString(), url));

        result.Status.ShouldBe(BrowseStatus.Success);
        result.DismissedModals.ShouldBeEmpty();
        var evt = _published.Published.OfType<ModalDismissalEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(ModalKinds.Cookie);
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.LeftStanding);
        evt.Selector.ShouldBeNull();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_AWallAWordCloses_PublishesTheTextPath()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var url = await ServeAsync(
            "<div id='cmp' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999'>" +
            "We use cookies. <button class='cmp-primary' " +
            "onclick=\"document.getElementById('cmp').style.display='none'\">Accept</button></div><p>Main content</p>");

        var result = await _browser!.NavigateAsync(new BrowseRequest(Guid.NewGuid().ToString(), url));

        result.DismissedModals.ShouldNotBeNull().ShouldHaveSingleItem().Selector.ShouldBe("text(accept)");
        var evt = _published.Published.OfType<ModalDismissalEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(ModalKinds.Cookie);
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.Text);
        evt.Selector.ShouldBe("text(accept)");
        evt.ButtonText.ShouldBe("accept");
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_APageWithNoOverlay_PublishesNothing()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var url = await ServeAsync("<h1>Hello</h1><p>Plain page with no modals.</p>");

        var result = await _browser!.NavigateAsync(new BrowseRequest(Guid.NewGuid().ToString(), url));

        result.Status.ShouldBe(BrowseStatus.Success);
        _published.Published.OfType<ModalDismissalEvent>().ShouldBeEmpty();
    }

    // Each case gets its own address, so a route registered for one cannot answer another.
    private async Task<string> ServeAsync(string body)
    {
        var url = $"https://modal-telemetry-{Guid.NewGuid():N}.test/";
        await _browser!.RouteOnContextAsync(url, route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = $"<!DOCTYPE html><html><head><title>wall</title></head><body>{body}</body></html>"
        }));
        return url;
    }
}