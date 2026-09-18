using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class ModalDismisser
{
    private static readonly IReadOnlyList<ModalPattern> _defaultPatterns =
    [
        new(
            Type: ModalType.CookieConsent,
            ContainerSelector:
            "[class*='cookie'], [id*='cookie'], [class*='consent'], [id*='consent'], [class*='gdpr'], [id*='gdpr'], [class*='onetrust'], [id*='onetrust']",
            ButtonSelectors:
            [
                "button[class*='accept'], button[id*='accept']",
                "button[class*='agree'], button[id*='agree']",
                "button[class*='allow'], button[id*='allow']",
                "a[class*='accept'], a[id*='accept']",
                "#onetrust-accept-btn-handler",
                ".onetrust-close-btn-handler",
                "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll",
                "[data-testid*='accept']",
                "[data-action*='accept']"
            ],
            ButtonTextPatterns:
            ["accept", "agree", "allow", "ok", "got it", "aceptar", "acepto", "entendido", "permitir"]
        ),

        new(
            Type: ModalType.AgeGate,
            ContainerSelector:
            "[class*='age'], [id*='age'], [class*='verify'], [id*='verify'], [class*='adult'], [id*='adult'], [class*='18'], [id*='18'], [class*='ageDisclaimer'], [class*='age-disclaimer']",
            ButtonSelectors:
            [
                "button[class*='enter'], button[id*='enter']",
                "button[class*='confirm'], button[id*='confirm']",
                "button[class*='yes'], button[id*='yes']",
                "button[class*='over18'], button[class*='Over18']",
                "a[class*='enter'], a[id*='enter']",
                "a[class*='yes'], a[id*='yes']",
                "[data-action*='enter']",
                "[data-action*='confirm']",
                "[data-label*='over18']"
            ],
            ButtonTextPatterns:
            ["yes", "enter", "confirm", "i am", "soy mayor", "si", "entrar", "over 18", "over 21", "i'm over"]
        ),

        new(
            Type: ModalType.Newsletter,
            ContainerSelector:
            "[class*='newsletter'], [id*='newsletter'], [class*='subscribe'], [id*='subscribe'], [class*='popup'], [class*='modal'], [class*='overlay']",
            ButtonSelectors:
            [
                "[class*='close'], [id*='close']",
                "[aria-label*='close'], [aria-label*='dismiss'], [aria-label*='cerrar']",
                "button[class*='dismiss']",
                ".modal-close, .popup-close, .close-button",
                "[data-dismiss='modal']",
                "button.close",
                "button:has(svg[class*='close'])",
                "[class*='icon-close']"
            ],
            ButtonTextPatterns: ["close", "no thanks", "dismiss", "not now", "maybe later", "cerrar", "no gracias"]
        ),

        new(
            Type: ModalType.Notification,
            ContainerSelector:
            "[class*='notification'], [id*='notification'], [class*='push'], [id*='push'], [class*='alert']",
            ButtonSelectors:
            [
                "button[class*='decline']",
                "button[class*='deny']",
                "button[class*='later']",
                "button[class*='no']",
                "[data-action*='decline']"
            ],
            ButtonTextPatterns: ["no", "later", "dismiss", "not now", "deny", "block", "no gracias", "ahora no"]
        )
    ];

    // How long to keep watching for a modal before concluding there is none. This is the price a
    // no-modal page pays, and the upper bound on how late an (async-injected) modal can appear and
    // still be dismissed. Chosen empirically (see ModalDismisserTests): a wall already present is
    // dismissed on the first poll regardless of this value — it only bounds the wait for one that
    // hasn't rendered yet. Missing a late wall rarely costs content (readability/selector extraction
    // strips overlays), so the window stays short to keep the common no-modal browse fast.
    private const int ModalDetectionWindowMs = 300;

    private const int ModalPollIntervalMs = 75;

    // What the browse envelope reports: the overlays that were closed. The measure is DismissAsync.
    public async Task<IReadOnlyList<ModalDismissed>> DismissModalsAsync(
        IPage page,
        CancellationToken ct) =>
        (await DismissAsync(page, ct))
            .Select(outcome => outcome.Dismissed)
            .OfType<ModalDismissed>()
            .ToList();

    // One outcome per overlay the last pass detected: how it was closed, or that it was left
    // standing. A page with no overlay answers an empty list — nothing to count.
    public async Task<IReadOnlyList<ModalOverlayOutcome>> DismissAsync(
        IPage page,
        CancellationToken ct)
    {
        var patterns = _defaultPatterns;

        // Re-attempt dismissal until something is dismissed or the window elapses. Each pass is fast
        // (immediate visibility/overlay checks, no blocking waits), and only acts on a real visible
        // overlay — so a content page with incidental modal-ish class names does nothing and a
        // no-modal page just polls cheaply until the window. This catches modals that render late
        // (async consent walls) on ANY page, and replaces the old unconditional 200ms settle + the
        // per-selector 3000/500ms WaitForAsync timeouts that every navigation paid with no modal.
        var sw = Stopwatch.StartNew();
        while (true)
        {
            // One round trip decides, for every pattern at once, whether a real overlay is on the
            // page. Only patterns that matched go on to the (much rarer) button-probing path.
            var hasOverlay = await DetectOverlayContainersAsync(page, patterns);
            var candidates = patterns.Where((_, i) => hasOverlay[i]).ToList();

            var results = await Task.WhenAll(candidates
                .Select(pattern => TryDismissPatternSafeAsync(page, pattern, ct)));
            var outcomes = candidates
                .Zip(results, (pattern, result) => result ?? ModalOverlayOutcome.LeftStanding(pattern.Type))
                .ToList();

            if (outcomes.Any(o => o.Dismissed is not null))
            {
                await SettleAfterDismissalAsync(page, ct);
                return outcomes;
            }

            if (sw.ElapsedMilliseconds >= ModalDetectionWindowMs)
            {
                // Nothing dismissable appeared within the window. Skip the Escape fallback: there is
                // no visible overlay, so it would only cost latency on the common no-modal page.
                // An overlay the last pass detected and neither path could close is the miss this
                // measure exists to count.
                return outcomes;
            }

            await Task.Delay(ModalPollIntervalMs, ct);
        }
    }

    // Brief wait for the close animation, then an Escape fallback for a sibling modal.
    private async Task SettleAfterDismissalAsync(IPage page, CancellationToken ct)
    {
        await Task.Delay(150, ct);
        try
        {
            await TryEscapeKeyAsync(page, ct);
        }
        catch
        {
            // Ignore escape key failures
        }
    }

    private async Task<ModalOverlayOutcome?> TryDismissPatternSafeAsync(
        IPage page,
        ModalPattern pattern,
        CancellationToken ct)
    {
        try
        {
            return await TryDismissPatternAsync(page, pattern, ct);
        }
        catch
        {
            // Modal dismissal is best-effort
            return null;
        }
    }

    private async Task<ModalOverlayOutcome?> TryDismissPatternAsync(
        IPage page,
        ModalPattern pattern,
        CancellationToken ct)
    {
        var urlBefore = page.Url;

        // Probe every button selector in one round trip. Per-selector this used to be an
        // IsVisibleAsync, a tagName evaluate, a possible getAttribute, and a TextContentAsync — and
        // Playwright re-resolves the selector on each. Across a pattern's ~9 selectors that is ~30
        // round trips and ~30 full-document traversals, which is what made dismissal cost seconds on
        // large pages once a container actually matched.
        var probes = await ProbeButtonsAsync(page, pattern.ButtonSelectors, urlBefore);

        for (var index = 0; index < pattern.ButtonSelectors.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var buttonSelector = pattern.ButtonSelectors[index];
            var probe = probes[index];
            if (probe.Outcome == ButtonProbeOutcome.NotActionable)
            {
                continue;
            }

            try
            {
                var button = page.Locator(buttonSelector).First;

                // The in-page probe could not evaluate this selector (a Playwright-only selector
                // engine such as :has()), so fall back to resolving it through Playwright.
                if (probe.Outcome == ButtonProbeOutcome.Unsupported)
                {
                    if (!await IsCurrentlyVisibleAsync(button) ||
                        await WouldCauseNavigationAsync(button, urlBefore))
                    {
                        continue;
                    }
                }

                var buttonText = probe.Outcome == ButtonProbeOutcome.Actionable
                    ? probe.Text
                    : await button.TextContentAsync(new LocatorTextContentOptions { Timeout = 500 });
                await button.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                // Verify we didn't navigate away - if we did, this wasn't a modal dismiss
                await Task.Delay(100, ct);
                if (page.Url != urlBefore && !IsSamePageNavigation(urlBefore, page.Url))
                {
                    await page.GoBackAsync(new PageGoBackOptions { Timeout = 5000 });
                    continue;
                }

                return new ModalOverlayOutcome(
                    pattern.Type,
                    ModalDismissalPath.Selector,
                    new ModalDismissed(pattern.Type, buttonSelector, buttonText?.Trim()));
            }
            catch
            {
                // Button not found or click failed, try next selector
            }
        }

        // Resolving GetByRole(..., Name: pattern) once per text pattern meant Playwright computed
        // the accessibility tree 18 times (9 patterns x button+link). Each of those measured
        // 250-480ms on a 1.8MB page, which is where dismissal spent 4.5-4.9s of every imdb.com
        // browse. One combined regex per role computes it twice instead, and Playwright still does
        // the accessible-name matching itself, so what counts as a match is unchanged; only the
        // number of resolutions is. Priority stays pattern-major, role-minor, as before.
        if (pattern.ButtonTextPatterns is { Count: > 0 })
        {
            var nameRegex = TextPatternRegexFor(pattern);

            // Resolved on first use, not up front: computing the accessibility tree is the expensive
            // part, and the overwhelmingly common case (a consent wall dismissed by a <button>) never
            // needs the link role at all. Order is unchanged — the roles are still consulted in
            // _textPatternRoles order within each pattern, exactly as the per-pattern loop did.
            var resolved = new List<(ILocator Locator, IReadOnlyList<string> Texts)>();

            async Task<IReadOnlyList<(ILocator Locator, IReadOnlyList<string> Texts)>> roleCandidatesAsync(int upTo)
            {
                while (resolved.Count <= upTo && resolved.Count < _textPatternRoles.Length)
                {
                    var locator = page.GetByRole(
                        _textPatternRoles[resolved.Count], new PageGetByRoleOptions { NameRegex = nameRegex });
                    try
                    {
                        resolved.Add((locator, await AccessibleNamesAsync(locator)));
                    }
                    catch
                    {
                        // A role that cannot be resolved contributes no candidates.
                        resolved.Add((locator, []));
                    }
                }

                return resolved;
            }

            foreach (var textPattern in pattern.ButtonTextPatterns)
            {
                ct.ThrowIfCancellationRequested();

                for (var roleIndex = 0; roleIndex < _textPatternRoles.Length; roleIndex++)
                {
                    var (locator, texts) = (await roleCandidatesAsync(roleIndex))[roleIndex];
                    var index = IndexOfTextMatch(texts, textPattern);
                    if (index < 0)
                    {
                        continue;
                    }

                    try
                    {
                        var candidate = locator.Nth(index);
                        if (!await IsCurrentlyVisibleAsync(candidate) ||
                            await WouldCauseNavigationAsync(candidate, urlBefore))
                        {
                            continue;
                        }

                        await candidate.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                        await Task.Delay(100, ct);
                        if (page.Url != urlBefore && !IsSamePageNavigation(urlBefore, page.Url))
                        {
                            await page.GoBackAsync(new PageGoBackOptions { Timeout = 5000 });
                            continue;
                        }

                        return new ModalOverlayOutcome(
                            pattern.Type,
                            ModalDismissalPath.Text,
                            new ModalDismissed(pattern.Type, $"text({textPattern})", textPattern));
                    }
                    catch
                    {
                        // Candidate went stale or refused the click, try the next one.
                    }
                }
            }
        }

        return null;
    }

    // Approximates each candidate's ACCESSIBLE NAME, which is what Playwright matched the role
    // locator on — not textContent, which is empty for exactly the controls this fallback exists to
    // reach: an <input type="submit"> named by its value, or an icon-only button named by
    // aria-label. Narrowing on textContent filtered those back out, so a consent wall whose only
    // control was named that way survived every browse. Still one round trip, like AllTextContents.
    //
    // Order follows the accessible-name computation closely enough for a substring match:
    // aria-labelledby wins (resolved in the element's own tree, so it works inside a shadow root),
    // then aria-label, then visible text, then the attribute-supplied names.
    private static async Task<IReadOnlyList<string>> AccessibleNamesAsync(ILocator locator)
    {
        try
        {
            return await locator.EvaluateAllAsync<string[]>(
                """
                els => els.map(el => {
                    const clean = v => String(v).replace(/\s+/g, ' ').trim();
                    const byIds = el.getAttribute('aria-labelledby');
                    if (byIds) {
                        const name = clean(byIds.split(/\s+/)
                            .map(id => el.getRootNode().getElementById(id))
                            .filter(Boolean)
                            .map(ref => ref.textContent || '')
                            .join(' '));
                        if (name) return name;
                    }
                    const label = el.getAttribute('aria-label');
                    if (label && label.trim()) return clean(label);
                    const text = clean(el.textContent || '');
                    if (text) return text;
                    for (const c of [el.value, el.getAttribute('title'), el.getAttribute('alt')]) {
                        if (c && String(c).trim()) return clean(c);
                    }
                    return '';
                })
                """);
        }
        catch
        {
            return [];
        }
    }

    private static readonly AriaRole[] _textPatternRoles = [AriaRole.Button, AriaRole.Link];

    // Playwright matches a role locator's Name case-insensitively as a substring, so an alternation
    // of the escaped patterns selects exactly the union the per-pattern calls used to select.
    // Keyed by pattern INSTANCE, not ModalType: a pattern constructed elsewhere that reuses a
    // default Type must build a regex from its own text list, not silently inherit the default's.
    private static readonly Dictionary<ModalPattern, Regex> _textPatternRegexes =
        new(
            _defaultPatterns
                .Where(p => p.ButtonTextPatterns is { Count: > 0 })
                .Select(p => new KeyValuePair<ModalPattern, Regex>(p, new Regex(
                    string.Join("|", p.ButtonTextPatterns!.Select(Regex.Escape)),
                    RegexOptions.IgnoreCase | RegexOptions.Compiled))),
            ReferenceEqualityComparer.Instance);

    internal static Regex TextPatternRegexFor(ModalPattern pattern) =>
        _textPatternRegexes.TryGetValue(pattern, out var cached)
            ? cached
            : new Regex(
                string.Join("|", pattern.ButtonTextPatterns!.Select(Regex.Escape)),
                RegexOptions.IgnoreCase);

    private static int IndexOfTextMatch(IReadOnlyList<string> texts, string textPattern)
    {
        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i].Contains(textPattern, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Guards against a pathological selector matching tens of thousands of nodes. Real consent
    // walls sit far earlier than this; the bound only stops one page from stalling the scan.
    private const int MaxContainersScanned = 5000;

    private enum ButtonProbeOutcome
    {
        NotActionable,
        Actionable,

        // querySelector could not parse the selector — it uses a Playwright-only selector engine
        // (e.g. :has()), so the decision has to be made through Playwright instead.
        Unsupported
    }

    private readonly record struct ButtonProbe(ButtonProbeOutcome Outcome, string? Text);

    // Decides, for a pattern's whole button-selector list in ONE round trip, which selectors resolve
    // to a currently visible element that would not navigate away — and captures its text while it
    // is there. Mirrors the checks the per-selector Playwright calls performed: Playwright treats an
    // element as visible when it has a non-empty bounding box and is not visibility:hidden, and
    // WouldCauseNavigationAsync only ever rejects anchors whose raw href points off the page.
    private static async Task<IReadOnlyList<ButtonProbe>> ProbeButtonsAsync(
        IPage page,
        IReadOnlyList<string> buttonSelectors,
        string urlBefore)
    {
        try
        {
            var raw = await page.EvaluateAsync<JsonElement>(
                """
                ([selectors, currentUrl]) => {
                    // querySelectorAll does not cross a shadow boundary, but the page.Locator calls
                    // this replaced did. A CMP rendered as a web component was invisible to the scan,
                    // and because a pattern that fails the gate is dropped entirely, the text
                    // fallback that WOULD have pierced never ran either. Roots are collected once per
                    // call rather than per selector, so the extra '*' traversal is paid once.
                    const roots = [document];
                    (function collect(root) {
                        root.querySelectorAll('*').forEach(e => {
                            if (e.shadowRoot) { roots.push(e.shadowRoot); collect(e.shadowRoot); }
                        });
                    })(document);
                    const queryAllDeep = sel => roots.flatMap(r => Array.from(r.querySelectorAll(sel)));

                    return selectors.map(selector => {
                    let el;
                    try { el = queryAllDeep(selector)[0]; }
                    catch { return { outcome: 2, text: null }; }
                    if (!el) return { outcome: 0, text: null };

                    const r = el.getBoundingClientRect();
                    if (r.width <= 0 || r.height <= 0) return { outcome: 0, text: null };
                    if (getComputedStyle(el).visibility === 'hidden') return { outcome: 0, text: null };

                    if (el.tagName.toLowerCase() === 'a') {
                        const href = el.getAttribute('href');
                        if (href) {
                            const lower = href.toLowerCase();
                            if (!lower.startsWith('javascript:') && !href.startsWith('#')) {
                                let absolute = null;
                                try { absolute = new URL(href); } catch { absolute = null; }
                                if (absolute) {
                                    const current = new URL(currentUrl);
                                    if (absolute.host !== current.host ||
                                        absolute.pathname !== current.pathname) {
                                        return { outcome: 0, text: null };
                                    }
                                } else {
                                    return { outcome: 0, text: null };
                                }
                            }
                        }
                    }

                    return { outcome: 1, text: el.textContent };
                    });
                }
                """,
                new object[] { buttonSelectors.ToArray(), urlBefore });

            return raw.EnumerateArray()
                .Select(entry => new ButtonProbe(
                    (ButtonProbeOutcome)entry.GetProperty("outcome").GetInt32(),
                    entry.GetProperty("text").ValueKind == JsonValueKind.Null
                        ? null
                        : entry.GetProperty("text").GetString()))
                .ToList();
        }
        catch
        {
            // The page could not be evaluated at all — let every selector take the Playwright path
            // rather than silently deciding there is nothing to dismiss.
            return buttonSelectors
                .Select(_ => new ButtonProbe(ButtonProbeOutcome.Unsupported, null))
                .ToList();
        }
    }

    // Decides, for every pattern in ONE round trip, whether the page currently shows a real visible
    // OVERLAY (fixed/absolute/sticky, on-screen, non-transparent) rather than ordinary content that
    // merely matched a generic container selector ([class*='age'], [class*='modal'], …).
    //
    // This used to be one CountAsync plus a Nth(i).EvaluateAsync per candidate, per pattern.
    // Playwright re-resolves the locator on each of those calls, so a single poll cost up to 44
    // WebSocket round trips AND 44 full-document querySelectorAll traversals — measured at 2.7s on
    // bbc.com and 4.9s on a 1.8MB imdb.com page. Doing the whole scan inside the page collapses it
    // to one round trip and one traversal per selector.
    //
    // Scanning stops at the first overlay per pattern, so the common no-overlay page is the only one
    // that walks its full match set — and it does so in-page, where each element costs microseconds.
    // The old code examined only the first 10 matches, which was a round-trip budget rather than a
    // correctness rule: a genuine banner sitting behind more than 10 same-class content elements was
    // silently never dismissed.
    private static async Task<IReadOnlyList<bool>> DetectOverlayContainersAsync(
        IPage page,
        IReadOnlyList<ModalPattern> patterns)
    {
        var selectors = patterns.Select(p => p.ContainerSelector ?? string.Empty).ToArray();

        try
        {
            return await page.EvaluateAsync<bool[]>(
                """
                ([selectors, maxScanned]) => {
                    // querySelectorAll does not cross a shadow boundary, but the page.Locator calls
                    // this replaced did. A CMP rendered as a web component was invisible to the scan,
                    // and because a pattern that fails the gate is dropped entirely, the text
                    // fallback that WOULD have pierced never ran either. Roots are collected once per
                    // call rather than per selector, so the extra '*' traversal is paid once.
                    const roots = [document];
                    (function collect(root) {
                        root.querySelectorAll('*').forEach(e => {
                            if (e.shadowRoot) { roots.push(e.shadowRoot); collect(e.shadowRoot); }
                        });
                    })(document);
                    const queryAllDeep = sel => roots.flatMap(r => Array.from(r.querySelectorAll(sel)));

                    return selectors.map(selector => {
                    if (!selector) return true;
                    let elements;
                    try { elements = queryAllDeep(selector); }
                    catch { return false; }
                    const limit = Math.min(elements.length, maxScanned);
                    for (let i = 0; i < limit; i++) {
                        const el = elements[i];
                        const r = el.getBoundingClientRect();
                        if (r.width <= 0 || r.height <= 0) continue;
                        const s = getComputedStyle(el);
                        if (s.visibility === 'hidden' || s.display === 'none' || parseFloat(s.opacity) === 0)
                            continue;
                        if (s.position === 'fixed' || s.position === 'sticky') return true;
                        // 'absolute' alone is not an overlay. Dropping the old 10-container cap was
                        // right — a real banner can sit behind more than ten same-class elements —
                        // but it also exposed every incidental absolutely positioned box on the
                        // page, and one of those opening a pattern hands the loose text fallbacks a
                        // licence to click. AgeGate's list contains "si", which substring-matches a
                        // site's own "Sign in". A real absolute overlay declares itself: it stacks
                        // above the content, or it covers a serious part of the viewport.
                        if (s.position === 'absolute') {
                            const z = parseInt(s.zIndex, 10);
                            if (Number.isFinite(z) && z >= 1) return true;
                            const viewport = window.innerWidth * window.innerHeight;
                            if (viewport > 0 && (r.width * r.height) / viewport >= 0.15) return true;
                        }
                    }
                    return false;
                    });
                }
                """,
                new object[] { selectors, MaxContainersScanned });
        }
        catch
        {
            // Detection is best-effort; a page that cannot be evaluated (navigating, closed) simply
            // has no dismissable overlay this pass.
            return selectors.Select(_ => false).ToArray();
        }
    }

    // Returns whether the locator currently resolves to a visible element, without blocking.
    // IsVisibleAsync reports the present DOM state immediately (unlike WaitForAsync, which polls up
    // to its timeout when the element is absent) and is already false for a locator that matches
    // nothing — so the CountAsync that used to precede it only doubled the round trips.
    private static async Task<bool> IsCurrentlyVisibleAsync(ILocator locator)
    {
        try
        {
            return await locator.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WouldCauseNavigationAsync(ILocator element, string currentUrl)
    {
        try
        {
            var tagName = await element.EvaluateAsync<string>("el => el.tagName.toLowerCase()");

            if (tagName == "a")
            {
                var href = await element.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href))
                {
                    if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        href == "#" ||
                        href.StartsWith("#"))
                    {
                        return false;
                    }

                    if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
                    {
                        var currentUri = new Uri(currentUrl);
                        if (absoluteUri.Host != currentUri.Host ||
                            absoluteUri.AbsolutePath != currentUri.AbsolutePath)
                        {
                            return true;
                        }
                    }
                    else if (!href.StartsWith("#"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            // If we can't determine, assume it's safe
            return false;
        }
    }

    private static bool IsSamePageNavigation(string urlBefore, string urlAfter)
    {
        // Consider it same-page if only the fragment/hash changed
        if (Uri.TryCreate(urlBefore, UriKind.Absolute, out var uriBefore) &&
            Uri.TryCreate(urlAfter, UriKind.Absolute, out var uriAfter))
        {
            return uriBefore.GetLeftPart(UriPartial.Query) == uriAfter.GetLeftPart(UriPartial.Query);
        }

        return false;
    }

    private async Task TryEscapeKeyAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(200, ct);
    }
}