---
paths:
  - "DockerCompose/camoufox/**"
  - "Infrastructure/Clients/Browser/Playwright*"
  - "Infrastructure/Clients/Browser/Modal*"
  - "Infrastructure/Clients/Browser/AccessibilitySnapshot*"
  - "McpServerWebSearch/Settings/**"
  - "McpServerWebSearch/appsettings*"
---

# Web Browsing: the Camoufox container

See also: `web-browsing.md` (tools, overlays, tabs), `browsing-as-person.md` (logins, the handoff's live view), `web-images.md` (page images).

## Camoufox

The `camoufox` service is an anti-detect Firefox for scraping, reached at `ws://camoufox:9377/browser`; config in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`). Two of its files serve the login handoff (`browsing-as-person.md`): `patch-screencast-ack.js` keeps the live view from freezing on its first frame, and `server.js` turns off the USB WebAuthn transport so a passkey ask is refused at once; rebuild the image after changing either.

**Bumping `Microsoft.Playwright` is a two-sided change.** The connect handshake demands an exact client/server minor match — a mismatch fails hard with HTTP 428 `Playwright version mismatch`. `DockerCompose/camoufox/Dockerfile` moves in lockstep with the .NET projects' version: both the `mcr.microsoft.com/playwright:vX.Y.Z-noble` base and `playwright-core@X.Y.Z`. Camoufox's bundled Firefox carries an older juggler protocol, so a newer playwright-core can send fields it rejects:
- `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653); their Python library pins `playwright<1.61`).
- `patch-interception.js` strips the `bypassServiceWorker` 1.64 added to `Network.setRequestInterception`, without which every route and every context restored from storage state failed.

Every `patch-*.js` script is anchor-checked and **exits 1 when its anchor disappears**, so a bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` after any bump. `CamoufoxPlaywrightVersionTests` pins the two sides' minors to each other.

**The browser is pinned by the library version.** Each `@camoufox/camoufox` release is stamped with the browser build released beside it (`dist/data-files/browser-pin.json`), and `camoufox fetch` installs that build, so updating Camoufox is bumping the one npm version in the Dockerfile; `CamoufoxPlaywrightVersionTests` refuses the old `camoufox-js`, which took whatever release was newest at build time. The pairing also keeps the library's fingerprint properties in step with the browser's — `camoufox-js` against a 156 build died at launch with `UnknownProperty: navigator.product in config`. `npx camoufox active` in the image names the build. The pin can be a prerelease, and today is: 156.0.1-beta.34.

**One device for the life of the deployment.** Camoufox draws a fresh identity per launch unless handed one, and a site that sees a logged-in session arrive from a new machine after every deploy challenges or drops it. `fingerprint.js` draws an fpgen fingerprint on the first launch over an empty `camoufox-identity` volume and saves it to `/data/fingerprint.json`; `server.js` hands it to every launch, and every per-identity draw (fonts, voices, audio seed, WebGL) is seeded from it, so the file is the device. A file that will not parse keeps the container down rather than drawing a new one. `check-fingerprint.js` runs at image build against `fingerprint-fixture.json.gz` (saved by the package the pin was introduced with): a bump that refuses it, or presents it differently from one launch to the next, fails the build. Anonymous and person contexts share the device — cookies, not fingerprints, separate them. `CamoufoxFingerprintPinTests` launches twice over one volume on the reused `camoufox:latest` image and runs once per image id (a `ziggurat-camoufox-pin-<id>.passed` stamp in the temp directory), so rebuild that image after touching this directory — the rebuild is also what runs it again.

**uBlock Origin runs, and only builds from 156.0.1-beta.32 on can carry it.** Earlier Camoufox installed add-ons once per window, so each Playwright page reloaded uBlock's background page and a request could wait on a `webRequest` listener that had gone away ([camoufox#185](https://github.com/daijro/camoufox/issues/185)): every request stalls partway through a few parallel browses — a channel suspended in `WebRequest.sys.mjs`, `data:` pages still rendering — until the browser restarts — in `Tests/Integration/Clients/`, a run of 30s "Request timed out" browses. Moving the pin backwards past beta.32 means `exclude_addons: ['UBO']` in `server.js`. The add-on itself floats (AMO's latest at image build) and fetches its filter lists on every launch, so the first few seconds after a restart browse unfiltered.

**Our scripts run in an isolated world.** Since Camoufox 152.0.4-beta.29 every `EvaluateAsync` lands in an isolated world (a `mw:` prefix reaches the page's own, and only with `main_world_eval` on at launch). The DOM is shared, but page-made objects are seen through Xrays: indexing a `Uint8Array` throws, so `PlaywrightImageProbe`'s wire rung reads bytes with `FileReader`. A script that silently returns nothing after a browser bump is the first thing to check here.
