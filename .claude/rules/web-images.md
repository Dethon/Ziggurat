---
paths:
  - "Domain/Tools/Web/ViewImageTool.cs"
  - "Domain/Tools/Web/ImageRef.cs"
  - "Domain/Tools/Web/WebBrowseTool.cs"
  - "Domain/Contracts/IWebBrowser.cs"
  - "McpServerWebSearch/McpTools/McpViewImageTool.cs"
  - "McpServerWebSearch/McpTools/McpWebBrowseTool.cs"
  - "Infrastructure/Clients/Browser/*Image*"
  - "Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs"
  - "Infrastructure/HtmlProcessing/**"
---

# Web Browsing: page images

See also: `web-browsing.md` (tools, overlays, tabs), `browsing-as-person.md` (logins, `asMe`), `camoufox.md` (the browser container).

## Images

`web_browse` writes an entry into the markdown body where each image sits — its ref and the best label the page gives it — and `view_image` fetches by ref through the same page. See `docs/adr/0033`.

**Image refs are their own namespace.** `i-1`, `i-2`, never mixed into the snapshot's `e-` refs; a tool handed the other kind refuses it by name. Both live in the browse session and die with it; a ref from a dead session says to browse the page again.

**Entries are positional, so they cost body budget.** They sit in the text `maxLength` and `offset` paginate over, so an image-heavy page shifts the text window, and the envelope reports how many images lie beyond it (`imagesBeyondWindow`).
- `HtmlConverter.Truncate` must never cut an entry in half — it backs up past a partial one as it backs up to a newline (which it does when the last newline falls past 70% of the target; the target is `maxLength` minus the suffix).
- A ref the model can see is always one it can use: an entry longer than the whole window goes out over budget rather than being backed up to nothing, since a window that consumes nothing can never be paged past.
- So the body's length says nothing about where the next window starts: a truncated envelope carries `nextOffset`, where the cut landed; paging by `maxLength` skips exactly what the back-up left, including entries `imagesBeyondWindow` promised.

**The size filter needs rendered dimensions.** Under ~100px on either side an image is a spacer, an icon or a tracking pixel: no entry, no ref, unreachable. Markup `width`/`height` lie, so ask the page. `StripDomNoiseAsync` keeps `src` for images that pass and strips it for everything else.

**A fixture image must actually load, or the measurement is a race.** An image at an address that 404s renders at its CSS size only until the request fails, then collapses to its alt text — under 100px, so the filter drops it and a restamp erases the ref (why `SessionUniqueRefTests` failed only under full-suite load). Use a `data:` URI or Playwright's `RouteAsync` with real bytes, and await `onload` before measuring. The images in `PageImageMeasurementTests` are single-pass and latently fragile — don't add a second pass to one without giving it a picture that loads.

**A renderer that flattens must still list its pictures.** `ConvertTableToMarkdown` writes each cell as flat `TextContent` so a row stays on one line, and an `<img>` contributes no text, so cells append their entries via `ListImagesWithin` (as the anchor case does) after the cell's own words — without it every table picture vanished (Wikimedia's featured pictures answered as a page with no images). Both table-walk selectors are scoped to the table's own rows and the row's own cells (`Closest(...) == ...`): `QuerySelectorAll` is descendant-wide, and a table framed inside a cell was rendered twice, repeating a ref. Any future renderer that flattens a subtree owes the same two things — list what it flattened, and don't render a nested one twice.

**Labels fall back before going blank**: alt → figcaption → title → enclosing link text → filename → rendered dimensions. A list of entries all called "image" is not a menu.

**One picture, one label — by construction.** `ImageLabel.From` (`Infrastructure/HtmlProcessing`) is the only code that turns what a page offers into a label; the entry builder and `view_image`'s fetch both feed it a facts record, so the entry and the note replacing a fetched picture cannot disagree. The fetch script computes no label — it answers facts (raw strings, stamped dimensions); a JavaScript copy of the ladder once disagreed on Unicode whitespace and anchoring, now named unit facts in `ImageLabelTests`. The 500-character cap is a safeguard, not an editor (120 cut real alts mid-sentence; a real label should never meet it), and the cut backs off to a text-element boundary, so a label ending in an emoji never leaves as a lone surrogate half.

**`view_image`'s cap of 8 is spelled three times**: `ViewImageTool.MaxPerCall`, the tool description, and the MCP parameter description. The two prose copies are hand-synced with the constant.

**Bytes as served first, pixels only when no fetch can obtain them.** The descent — wire fetch, canvas read, context request, element screenshot, each trading fidelity for reach — is owned by `ImageAcquisition` (`Infrastructure/Clients/Browser`), one probe call per rung behind `IImagePageProbe` (Playwright-backed in production), decided in C# and unit-tested against a faked probe (`ImageAcquisitionTests`); read the module for the ordering. What it cannot say for itself:
- Same-origin fetches carry the page's credentials, cross-origin goes **anonymous**: the big image CDNs send ACAO `*`, which a **credentialed** request is rejected against. Don't "fix" cross-origin to send credentials — that trap already broke one implementation.
- Only wire rasters (one constant in the module, media type trimmed once) leave as served — as-served `image/svg+xml` made the vision provider refuse the whole request with HTTP 400.
- A tainted canvas is not a refusal but two more rungs, because CORS binds script inside the page and nothing else (`docs/adr/0033`).
- SiteRefused remains only for an image the browser itself will not show; a dead link answers its own NeverLoaded wall rather than inviting retries.

**`view_image` takes a list, capped at 8.** Over the cap, the first eight are fetched and the rest named in the envelope — partial success, so a greedy call progresses. The cap counts images, not bytes; eight full-size images can still fail as an oversized request, and that is accepted (`docs/adr/0033`).

**The bytes cross at the bridge, not here.** The MCP server returns a protocol image block and knows nothing of Redis, conversation ids or tool call ids. `QualifiedMcpTool` lifts the `DataContent` into `IReadImageStore` and substitutes the envelope, so history stays text-only and hydration treats a page image exactly like a `file_read` image — same picture cap, forget past it, placeholder. Do not use this server's Redis connection to shortcut that; it carries metrics and the handoff's state, never page images.

**One call can answer with several pictures, so a key carries an index.** `IReadImageStore` holds one image per key and `file_read` depends on that, so `view_image`'s pictures are keyed `<callId>#<n>`. **`n` counts blank-line-separated paragraphs that start with `{`, not pictures and not content blocks** — the call's own envelope leads and takes index 0, and a refused image takes a slot with no bytes. A `{`-paragraph inside a foreign text block takes a slot on both sides, so "fixing" either side to count `AIContent` blocks would silently re-offset every key. `McpImageLift` and `ReadImageHydration` compute the number independently on either side of the wire; `PageImageRoundTripTests` drives both over a real `ToolResponse.Create` result to stop them drifting (they did: every image hydrated as "no longer in view").

**Every refusal names its wall**: no vision on the model, dead session ref, not-an-image ref, site refused the fetch, a dead-link image (no retry invited — distinct from a CDN guarding pixels the page displays), past the per-call cap, bytes already forgotten, and the two stale-ref walls (`web-browsing.md`, Tabs). One generic "unavailable" leaves the model unable to tell retryable from permanent. `ImageFetchStatus` carries `Success` plus exactly the five a fetch can reach — not-an-image, site-refused, dead-link and the two stale-ref ones; the other four are answered off the fetch path (dead session and the cap before one is attempted, no-vision and forgotten bytes agent-side afterwards). Don't add a status for those: a status nothing can produce is a refusal nobody can receive.

**The no-vision wall is answered agent-side, not here.** The capability catalogue and the turn's resolved model live hub-side, so `McpViewImageTool` passes `modelAcceptsImages: true` unconditionally and hydration leaves the note instead. `ViewImageTool`'s own refusal is unreachable through MCP and fires only for a direct domain-tool caller — kept because the tool is what is tested, not dead code. The cost: a vision-less turn still fetches and stores the bytes.
