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

## Camoufox

The `camoufox` service is an anti-detect Firefox for scraping, reached at `ws://camoufox:9377/browser`; config in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

**Bumping `Microsoft.Playwright` is a two-sided change.** The connect handshake demands an exact client/server minor match — a mismatch fails hard with HTTP 428 `Playwright version mismatch`. `DockerCompose/camoufox/Dockerfile` must move in lockstep: both the `mcr.microsoft.com/playwright:vX.Y.0-noble` base and `playwright-core@X.Y.0`. Camoufox's bundled Firefox carries an older juggler protocol, so a newer playwright-core can send fields it rejects; `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653) — their own Python library just pins `playwright<1.61`). Both `patch-*.js` scripts are anchor-checked and **exit 1 when their anchor disappears**, so a bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` after any bump.
