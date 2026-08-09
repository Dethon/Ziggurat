using System.Diagnostics;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Speed + correctness guards for ModalDismisser.
//
// The dismisser runs on EVERY navigation. It used to cost 3.4s on a page with no modals (a 3000ms
// container WaitForAsync timeout) and 15-17s on content-rich pages, where overly-generic substring
// selectors ([class*='age'], [class*='modal'], [class*='cookie']) false-match real article elements
// and then ~29 button/text probes each block 500ms. That latency hit production browsing and the
// integration test suite alike. These tests pin the fast path AND prove real modals still dismiss,
// using hermetic SetContentAsync pages (no network) against the shared Camoufox backend.
[Collection("PlaywrightWebBrowserIntegration")]
public class ModalDismisserTests(PlaywrightWebBrowserFixture fixture) : IAsyncLifetime
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
    public async Task DismissModalsAsync_NoModalPage_CompletesQuickly()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body><h1>Hello</h1><p>Plain page with no modals.</p></body></html>");

        var dismisser = new ModalDismisser();
        var sw = Stopwatch.StartNew();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);
        sw.Stop();

        result.ShouldBeEmpty();
        // Bounds the no-modal cost to roughly the detection window (~300ms) — guards against a
        // regression back toward the old multi-second blocking waits.
        sw.ElapsedMilliseconds.ShouldBeLessThan(800);
    }

    // Pins the empirically-chosen detection window (~300ms): a consent overlay that renders shortly
    // after load (async CMP behaviour) — here ~120ms — is still caught. If the window is shortened
    // below that, this fails; that is the speed/coverage knob made explicit.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_OverlayInjectedWithinWindow_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body><p>content</p></body></html>");
        await page.EvaluateAsync(
            """
            (delay) => {
                setTimeout(() => {
                    const d = document.createElement('div');
                    d.id = 'late-banner';
                    d.className = 'cookie-consent';
                    d.style.cssText = 'position:fixed;top:0;left:0;right:0;height:60px;background:#ddd;z-index:9999';
                    const b = document.createElement('button');
                    b.className = 'accept-cookies';
                    b.textContent = 'Accept';
                    b.onclick = () => d.remove();
                    d.appendChild(b);
                    document.body.appendChild(d);
                }, delay);
            }
            """,
            120);

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#late-banner").IsVisibleAsync()).ShouldBeFalse();
    }

    // A large DOM is where overlay detection got expensive: the scan resolved its container locator
    // once per candidate (CountAsync, then Nth(i).EvaluateAsync for i in 0..9, for each of 4
    // patterns), and Playwright re-runs querySelectorAll for every one of those calls. That is ~44
    // full-document traversals per poll and ~176 across the detection window — invisible on a toy
    // page, but measured at 2.7s on bbc.com and 4.9s on a 1.8MB imdb.com page in production.
    // Detection must cost a bounded number of round trips regardless of document size.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_LargeContentPage_StaysWithinDetectionWindow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var bulk = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"<div class='card-{i} modal-body popup-inner overlay-tile cookie-note'>" +
            $"<span class='close-icon'>row {i}</span></div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body>" + bulk + "</body></html>");

        var dismisser = new ModalDismisser();
        var sw = Stopwatch.StartNew();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);
        sw.Stop();

        result.ShouldBeEmpty();
        sw.ElapsedMilliseconds.ShouldBeLessThan(800);
    }

    // The text-pattern fallback, which runs only when no button SELECTOR matched. Here the dismiss
    // control carries no close/dismiss-ish class or aria-label, so it is reachable by accessible
    // name alone. Guards the path that resolves roles by name — the expensive one, since Playwright
    // computes the accessibility tree to satisfy it.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_OverlayDismissibleOnlyByButtonText_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='signup' class='newsletter-wall' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#eee;z-index:9999'>" +
            "Subscribe to our newsletter " +
            "<button class='signup-reject' " +
            "onclick=\"document.getElementById('signup').style.display='none'\">No thanks</button>" +
            "</div><p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.Newsletter);
        (await page.Locator("#signup").IsVisibleAsync()).ShouldBeFalse();
    }

    // A control named ONLY by aria-labelledby: its textContent is empty and it carries no
    // aria-label/value/title/alt, so Playwright's role locator finds it by its accessible name
    // while a narrowing that approximates the name from the element's own attributes filters it
    // back out — and the wall survives every browse. The approximation must resolve the reference
    // the way the accessible-name computation does.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_ButtonNamedOnlyByAriaLabelledby_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='signup' class='newsletter-wall' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#eee;z-index:9999'>" +
            "<span id='signup-reject-label'>No thanks</span>" +
            "<button class='signup-reject' aria-labelledby='signup-reject-label' " +
            "onclick=\"document.getElementById('signup').style.display='none'\"></button>" +
            "</div><p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.Newsletter);
        (await page.Locator("#signup").IsVisibleAsync()).ShouldBeFalse();
    }

    // The companion to the speed guard: the same large, noisy DOM with one genuine fixed-position
    // consent overlay in it must still be found and dismissed. Proves the cheaper scan did not buy
    // its speed by looking at fewer elements.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_RealBannerBuriedInLargeContentPage_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var bulk = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"<div class='card-{i} modal-body popup-inner overlay-tile cookie-note'>" +
            $"<span class='close-icon'>row {i}</span></div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" + bulk +
            "<div id='cookie-banner' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;background:#ddd;padding:20px;z-index:9999'>" +
            "We use cookies. " +
            "<button class='accept-cookies' " +
            "onclick=\"document.getElementById('cookie-banner').style.display='none'\">Accept</button>" +
            "</div></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#cookie-banner").IsVisibleAsync()).ShouldBeFalse();
    }

    // The text fallback narrows Playwright's accessible-name matches using textContent, which is
    // empty for a control named by value/aria-label/title/alt. Those are exactly the controls the
    // fallback exists for — a selector-matched one would never have reached it.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_ConsentButtonNamedByValue_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='cmp-wall' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999'>" +
            "Usamos cookies. " +
            // input[type=submit], so its accessible name comes from value and its textContent is "".
            // The class carries no accept-ish token, so no ButtonSelector matches it either.
            "<input type='submit' class='cmp-btn-primary' value='Aceptar todo' " +
            "onclick=\"document.getElementById('cmp-wall').style.display='none';return false;\">" +
            "</div><p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#cmp-wall").IsVisibleAsync()).ShouldBeFalse();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_ConsentButtonNamedByAriaLabel_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='signup' class='newsletter-wall' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#eee;z-index:9999'>" +
            "Subscribe " +
            // Icon-only control: named by aria-label, textContent empty, and the label is "No thanks"
            // rather than a close/dismiss token, so the ButtonSelectors miss it too.
            "<button class='sg-icon' aria-label='No thanks' " +
            "onclick=\"document.getElementById('signup').style.display='none'\"><svg></svg></button>" +
            "</div><p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.Newsletter);
        (await page.Locator("#signup").IsVisibleAsync()).ShouldBeFalse();
    }

    // The in-page scan replaced Playwright locators with document.querySelectorAll, which does not
    // pierce open shadow roots. A CMP rendered as a web component (Usercentrics and friends) then
    // fails the overlay gate, and because the gate drops the whole pattern the text fallback — which
    // WOULD have pierced — never runs either.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_ConsentWallInsideOpenShadowRoot_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body><div id='cmp-host'></div><p>Main content</p></body></html>");
        await page.EvaluateAsync(
            """
            () => {
                const host = document.getElementById('cmp-host');
                const root = host.attachShadow({ mode: 'open' });
                const wall = document.createElement('div');
                wall.id = 'shadow-wall';
                wall.className = 'cookie-consent';
                wall.style.cssText =
                    'position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999';
                const btn = document.createElement('button');
                btn.className = 'cmp-accept-all';
                btn.textContent = 'Accept all';
                btn.onclick = () => wall.remove();
                wall.appendChild(btn);
                root.appendChild(wall);
            }
            """);

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        var stillThere = await page.EvaluateAsync<bool>(
            "() => !!document.getElementById('cmp-host').shadowRoot.getElementById('shadow-wall')");
        stillThere.ShouldBeFalse();
    }

    // Dropping the 10-container cap was right — a real banner can sit behind more than ten
    // same-class elements — but it also means the gate now sees every incidental absolutely
    // positioned element on the page. With the overlay predicate unchanged, one lazy-loaded image
    // placeholder opens the AgeGate pattern, whose text list contains "si" — which substring-matches
    // the page's own "Sign in" button. The scan stays unbounded; what counts as an overlay tightens.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_IncidentalAbsoluteElement_DoesNotClickPageControls()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        // More than ten rows, so the placeholder sits past the index the old 10-container cap
        // stopped at — the cap is what used to hide this, and removing it was still right.
        var bulk = string.Concat(Enumerable.Range(0, 15).Select(i =>
            $"<div class='page-body image-{i}'>row {i}</div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" + bulk +
            // Small, absolutely positioned, no stacking context, and only incidentally matching
            // [class*='age'] (via "im-age-"). Pinned away from the button so it cannot shield it:
            // an overlapping placeholder makes the click time out and hides the very bug under test.
            "<div class='image-placeholder' " +
            "style='position:absolute;top:0;left:600px;width:300px;height:200px'></div>" +
            "<button id='signin' onclick=\"document.title='CLICKED'\">Sign in</button>" +
            "</body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldBeEmpty();
        (await page.TitleAsync()).ShouldNotBe("CLICKED");
    }
}