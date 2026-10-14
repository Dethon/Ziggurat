# 02 — The app served by the Sugary server

**What to build:** The Sugary PWA moves into the repository unchanged and is served by the Sugary server, with its HTTP API on routes other than `/mcp`, gated by the access code the standalone app uses (a deployment secret). She can open the app, add foods by hand, from her foods or by barcode, save a meal, edit portions, record the outcome, manage her foods and download the CSV tables. Estimating from a photo or description is not wired yet and says so. See the spec's "The Sugary server".

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] The app loads from the Sugary server and asks for the access code; every API route refuses a request without it.
- [x] Meals, foods, barcode lookup (her saved foods first, then Open Food Facts) and the CSV downloads behave as in the standalone app; the standalone API and export tests carry over as behaviour tests at this boundary.
- [x] A meal saved in the app appears in `/sugary` on the next read.
- [x] The estimate actions are visibly unavailable rather than failing.
- [x] The PWA builds as part of the server's image; it shares no code with WebChat or the dashboard.

## Comments

**Built (2026-10-14).** The standalone PWA lives at `Sugary.Client/` (React, TypeScript, Vite,
unchanged except as below), and `McpServerSugary` serves it and its HTTP API on every route but
`/mcp`, over the SQLite store ticket 01 built. The image builds the app in a `node:22-alpine` stage
and copies it into `/app/wwwroot`. Everything app-side is in `McpServerSugary/App/`, registered by
`AddSugaryApp` (from `ConfigureSugary`) and mapped by `MapSugaryApp` (from `Program.cs`, after
`MapMcp("/mcp")`).

**The HTTP API surface** (all under `/api`, ported from the standalone endpoints as they were):

| Route | Gate | What |
|---|---|---|
| `GET/POST/DELETE /session` | open | state, sign in with the code (rate-limited, 10/min shared), sign out |
| `GET /meals?skip&take`, `POST /meals`, `PUT/DELETE /meals/{id}`, `GET /meals/{id}/photo` | cookie | the log; save, edit portions, record the outcome, delete |
| `GET /foods`, `POST /foods`, `PUT/DELETE /foods/{id}` | cookie | her foods, most recently eaten first, with uses and last use |
| `GET /products/{barcode}` | cookie | her saved foods first, then Open Food Facts (`OpenFoodFactsUrl`, appsettings) |
| `GET /export/{meals,items,foods}.csv?tz&from&to` | cookie | the downloads, rendered by ticket 01's `SugaryTables`, named `sugary-*.csv` as the import reads them |
| `POST /estimates`, `POST /labels` | cookie | **503 "not available yet"** (see below) |
| anything else under `/api` | open | 404, never the app |

Any other path without a file name serves `index.html`; only `/assets/*` is cached for good.

**Where tickets 04 and 07 plug in.** `McpServerSugary/App/UnavailableEndpoints.cs` maps the two
routes the app already calls with the standalone multipart forms (`/estimates`: `photos`,
`description`, `clarifications`, `counted`, `language`; `/labels`: `photo`, `language`), and
`SessionEndpoints` reports `estimates: false, labels: false` on `GET /api/session`. Ticket 04
replaces the `/estimates` stub with the channel round trip and turns `estimates` on; ticket 07 does
the same for `/labels`. The app needs no change for either: it reads the two flags and, when they
are on, shows the photo/description screen and calls the label route exactly as the standalone app
did. Sugary's own OpenRouter client, prompt, schemas and range floor were not ported here.

**Decisions to know about.**

- **The access code is `SUGARYAPP__ACCESSCODE`**, a section of its own (`SugarySettings.SugaryApp`)
  because `.env` is handed to every container. Placeholder in `DockerCompose/.env`, wired in the
  `mcp-sugary` service, bound through `BindSettings`. The standalone app called it
  `AUTH__ACCESSCODE`; the value carries over, the name does not.
- **An unset code refuses every sign-in**, as an unset shared secret does everywhere here. The
  standalone app ran open in Development and refused to start in Production; neither carries over.
  `required` on `/api/session` is therefore always true (kept so the app is unchanged). Its
  comparison is `SharedSecret.Matches`, after trimming the typed code.
- **The cookie gate guards the API only.** `/mcp` keeps the deployment secret, and the cookie does
  not open it (tested). The app shell loads without a cookie, since it is the app that asks for the
  code.
- **Cookie keys live beside the database** (`/data/keys`), so a redeployed container accepts the
  cookies it issued. Ticket 01's "nothing written to disk" test now ignores that directory, which
  the host writes on start and no read touches.
- **Unavailable is shown, not failed.** With `estimates` off, the meal screen replaces the photo,
  description and Estimate button with a notice and keeps My foods, Packaged food and Add by hand.
  With `labels` off, the packaged sheet still reads a barcode from a photo (the only barcode route
  over plain HTTP) and, when the photo has none, says label reading is not available instead of
  calling the server. New strings exist in English and Spanish; the i18n is the standalone's (a
  dictionary per language chosen in Settings, defaulting to the browser's), unchanged.
- **Small changes to the app besides that:** the build writes into `../McpServerSugary/wwwroot`,
  the dev proxy points at `localhost:6017`, and the service worker no longer answers navigations
  to `/mcp` with the app.
- **Open Food Facts is stubbed at its HTTP boundary** in tests (WireMock), so the real client, its
  user agent and its busy/unreachable answers are what is tested. An unreachable database or a
  timeout also answers 502 "could not be reached", where the standalone app let a timeout through
  as a 500.
- **Tests.** `Tests/Integration/McpServerTests/SugaryApp{Access,Serving}Tests.cs` and
  `Sugary{Meal,Food,Product,Export}ApiTests.cs` boot the real server (the `SugaryServer` fixture
  now also maps the app's routes and gives a cookie-jar `Browser()`/`SignedInAsync()`) and call it
  as the app does; the standalone meal, food, barcode, auth and export tests are carried over
  there, and its Open Food Facts parsing tests as `Tests/Unit/McpServerSugary/`. Saving a meal or
  a food in the app and reading `/sugary` on the next MCP call is tested. The PWA's 33 vitest tests
  pass as moved. Checked by hand: the image builds and serves the app, and a meal added by hand
  through the real UI is saved and listed.

**Deferred / not done.**

- **No HTTPS in front yet.** The app is on plain HTTP at `:6017` with no Caddy site. Installing it to
  a phone and scanning with the live camera both need HTTPS (the standalone app sat behind her own
  proxy). The hostname is a deployment decision, so it is left for the switch-over (ticket 12).
- No frontend test covers the unavailable states: the vitest suite is pure logic with no DOM, and
  adding a DOM test stack for two conditionals did not seem worth it. Checked by hand in a browser.
- `/estimates` and `/labels` exist only as stubs; meal photos are kept as the standalone app kept
  them (one JPEG thumbnail per meal), nothing more.

