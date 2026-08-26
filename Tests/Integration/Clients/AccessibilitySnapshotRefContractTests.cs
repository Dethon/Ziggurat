using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A ref is a promise: web_action resolves it with
// locator.WaitForAsync(WaitForSelectorState.Visible, Timeout = 15_000), so any ref handed to the agent for
// an element Playwright will not call visible is a guaranteed 15-second dead wait ending in
// "Operation timed out." All three web_action calls in four weeks of production failed at 15.0s.
//
// Playwright's Visible means a NON-EMPTY bounding box: width > 0 AND height > 0. The snapshot's own
// visibility test must not be looser than that, or it advertises refs that cannot be acted on.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class AccessibilitySnapshotRefContractTests(IsolatedSessionBrowserFixture fixture) : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(fixture.WsEndpoint))
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Firefox.ConnectAsync(fixture.WsEndpoint!);
        _context = await _browser.NewContextAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // already gone
            }
        }

        _playwright?.Dispose();
    }

    [Trait("Category", "External")]
    [SkippableTheory]
    [InlineData("width:120px;height:0", "zero height")]
    [InlineData("width:0;height:32px", "zero width")]
    [InlineData("position:fixed;width:0;height:0", "zero-size fixed")]
    public async Task CaptureAsync_ElementPlaywrightWillNotSeeAsVisible_IsNotGivenARef(
        string style, string description)
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            $"<button id='unreachable' style='{style};overflow:hidden;padding:0;border:0'>Hidden action</button>" +
            "<button id='real'>Real action</button>" +
            "</body></html>");

        var service = new AccessibilitySnapshotService();
        var result = await service.CaptureAsync(page, null, "ref-contract");

        // Whatever the snapshot advertises, Playwright must agree it is visible.
        var unreachableIsVisibleToPlaywright =
            await page.Locator("#unreachable").IsVisibleAsync();
        unreachableIsVisibleToPlaywright.ShouldBeFalse(
            $"precondition: Playwright must consider the {description} element invisible");

        (await page.Locator("#unreachable[data-ref]").CountAsync()).ShouldBe(
            0, $"a {description} element must not be advertised as an actionable ref");
        (await page.Locator("#real[data-ref]").CountAsync()).ShouldBe(
            1, "a normally sized button must still get a ref");
        result.RefCount.ShouldBe(1);
    }
}