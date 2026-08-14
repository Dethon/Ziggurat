using System.Diagnostics;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// WaitForDomStabilityAsync runs on every navigation and is pure waiting, so its schedule is a
// straight latency/coverage trade. It used to sleep before its first reading, which made three
// sleeps — a hard 600ms floor — the price of even a fully static page, while capping total patience
// at 1200ms for a page still rendering. These tests pin both ends: a settled page must clear fast,
// and a page still mutating must be waited out rather than sampled early.
[Collection(PlaywrightCollections.Timing)]
public class DomStabilityWaitTests(QuietBrowserFixture fixture) : IAsyncLifetime
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
    [SkippableFact]
    public async Task WaitForDomStabilityAsync_SettledPage_ReturnsWellUnderTheOldFloor()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var fastest = await LatencyBudget.FastestAsync(async () =>
        {
            var page = await _context!.NewPageAsync();
            await page.SetContentAsync("<!doctype html><html><body><p>static content</p></body></html>");

            var sw = Stopwatch.StartNew();
            await PlaywrightWebBrowser.WaitForDomStabilityAsync(page, CancellationToken.None);
            sw.Stop();

            await page.CloseAsync();
            return sw.ElapsedMilliseconds;
        });

        fastest.ShouldBeLessThan(550);
    }

    // The coverage half of the trade: content injected repeatedly after DOMContentLoaded must be
    // waited out, not sampled while it is still arriving. Anything that speeds up the settled case
    // must not buy it by giving up on pages that are still rendering.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task WaitForDomStabilityAsync_PageStillRendering_WaitsUntilItSettles()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body><div id='host'></div></body></html>");
        await page.EvaluateAsync(
            """
            () => {
                let n = 0;
                const timer = setInterval(() => {
                    const row = document.createElement('p');
                    row.className = 'late';
                    row.textContent = 'late row ' + n;
                    document.getElementById('host').appendChild(row);
                    if (++n >= 6) clearInterval(timer);
                }, 150);
            }
            """);

        await PlaywrightWebBrowser.WaitForDomStabilityAsync(page, CancellationToken.None);

        // All six late rows land by ~900ms. If the wait returned early the page would still be
        // mutating and the caller would serialise a partial DOM.
        (await page.Locator("p.late").CountAsync()).ShouldBe(6);
    }

    // Text replaced in place changes no node count, so a structure-only stability signal would miss
    // it. What gets extracted is the text, so the wait has to see this.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task WaitForDomStabilityAsync_TextRewrittenInPlace_IsTreatedAsStillRendering()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body><p id='slot'>x</p></body></html>");
        await page.EvaluateAsync(
            """
            () => {
                let n = 0;
                const timer = setInterval(() => {
                    document.getElementById('slot').textContent = 'loaded article body '.repeat(++n);
                    if (n >= 5) clearInterval(timer);
                }, 150);
            }
            """);

        await PlaywrightWebBrowser.WaitForDomStabilityAsync(page, CancellationToken.None);

        (await page.Locator("#slot").TextContentAsync())
            .ShouldBe(string.Concat(Enumerable.Repeat("loaded article body ", 5)));
    }

    // The counterpart: a page whose only ongoing activity is cosmetic — a spinner's inline style, a
    // rotating class — must be allowed to settle. Churn like this never stops on ad-heavy pages, and
    // waiting it out to the cap bought nothing, because none of it reaches the extracted markdown.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task WaitForDomStabilityAsync_OnlyCosmeticChurn_SettlesWithoutWaitingOutTheCap()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var fastest = await LatencyBudget.FastestAsync(async () =>
        {
            var page = await _context!.NewPageAsync();
            await page.SetContentAsync(
                "<!doctype html><html><body><p>article body</p><div id='spinner'></div></body></html>");
            await page.EvaluateAsync(
                """
                () => {
                    let deg = 0;
                    setInterval(() => {
                        const s = document.getElementById('spinner');
                        deg = (deg + 7) % 360;
                        s.style.transform = 'rotate(' + deg + 'deg)';
                        s.className = 'spin-' + deg;
                    }, 30);
                }
                """);

            var sw = Stopwatch.StartNew();
            await PlaywrightWebBrowser.WaitForDomStabilityAsync(page, CancellationToken.None);
            sw.Stop();

            await page.CloseAsync();
            return sw.ElapsedMilliseconds;
        });

        fastest.ShouldBeLessThan(700);
    }
}