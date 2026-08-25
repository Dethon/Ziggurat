---
paths:
  - "McpServerWebSearch/**"
  - "Domain/Tools/Web/**"
  - "Domain/Prompts/WebBrowsingPrompt.cs"
  - "Domain/Contracts/IWebBrowser.cs"
  - "Infrastructure/Clients/Browser/**"
  - "DockerCompose/camoufox/**"
---

# Web Browsing Architecture

McpServerWebSearch exposes the `web_*` browse tools over `PlaywrightWebBrowser`, a WebSocket to Camoufox. The accessibility snapshot assigns interactive element refs (`e-1`, `e-2`, …) that `web_action` then addresses, pages are kept alive per session with cookie persistence, and cookie banners, newsletters and age gates are auto-dismissed.

## Images

`web_browse` writes an entry into the markdown body where each image sits — its ref and the best label the page gives it — and `view_image` fetches by ref through the same page. See `docs/adr/0033`.

**Image refs are their own namespace.** `i-1`, `i-2`, never mixed into the `e-` refs the snapshot assigns. A tool handed the other kind refuses it by name rather than failing to find it. Both live in the browse session and die with it; a ref from a dead session says to browse the page again rather than erroring vaguely.

**Entries are positional, so they cost body budget.** They sit in the text that `maxLength` and `offset` paginate over, which means an image-heavy page shifts the text window. `HtmlConverter.Truncate` must never cut an entry in half — it backs up past a partial one the same way it backs up to a newline — and the envelope reports how many images lie beyond the window. A ref the model can see is always a ref it can use.

**The size filter needs rendered dimensions.** Under ~100px on either side an image is a spacer, an icon or a tracking pixel: no entry, no ref, unreachable. Markup `width`/`height` lie, so ask the page. `StripDomNoiseAsync` keeps `src` for images that pass — it strips it for everything else, as before.

**Labels fall back before going blank**: alt → figcaption → title → enclosing link text → filename → rendered dimensions. A list of entries all called "image" is not a menu.

**`view_image` takes a list, capped at 8.** Over the cap, the first eight are fetched and the rest named in the envelope — partial success, so a greedy call progresses. The cap counts images, not bytes; eight full-size images can still fail as an oversized request, and that is accepted (`docs/adr/0033`).

**The bytes cross at the bridge, not here.** The MCP server returns a protocol image block and knows nothing of Redis, conversation ids or tool call ids. `QualifiedMcpTool` lifts the `DataContent` out into `IReadImageStore` and substitutes the envelope, so the history stays text-only and hydration treats a page image exactly like a `file_read` image — same 20-message distance, same forget-on-exit, same placeholder. Do not give this server a Redis connection to shortcut that.

**Every refusal names its wall**: no vision on the model, dead session ref, not-an-image ref, site refused the fetch, past the per-call cap, bytes already forgotten. One generic "unavailable" leaves the model unable to tell a retryable failure from a permanent one.

## Camoufox

The `camoufox` service is an anti-detect Firefox for scraping, reached at `ws://camoufox:9377/browser`; config in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

**Bumping `Microsoft.Playwright` is a two-sided change.** The connect handshake demands an exact client/server minor match — a mismatch fails hard with HTTP 428 `Playwright version mismatch`. `DockerCompose/camoufox/Dockerfile` must move in lockstep: both the `mcr.microsoft.com/playwright:vX.Y.0-noble` base and `playwright-core@X.Y.0`. Camoufox's bundled Firefox carries an older juggler protocol, so a newer playwright-core can send fields it rejects; `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653) — their own Python library just pins `playwright<1.61`). Both `patch-*.js` scripts are anchor-checked and **exit 1 when their anchor disappears**, so a bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` after any bump.
