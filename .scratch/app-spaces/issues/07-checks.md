# 07: Checks

Spec: `.scratch/app-spaces/spec.md` ("Checks").

**What to build:** The agent sees what it built before the person does. `./check [--viewport WxH]... [--path <route>]` in an app's directory opens the preview in a headless Chromium inside the apps server's image. It signs in as the person whose turn ran the check, with an app session the server makes from the call's verified principal. Screenshots and a report land in `/apps/<slug>/.checks/latest/`, where `file_read` shows the PNGs as images. The browser reaches app origins at the apps server itself. Data reads go through `/_vfs/*`, so they return data once 06 lands.

**Blocked by:** 03

**Status:** ready-for-agent

- [x] One PNG per requested viewport. The default viewports are a phone and a desktop.
- [x] `report.json` records console messages, failed requests, content-policy violations, an accessibility snapshot, and each assertion file under `checks/` with its pass or fail and message.
- [x] Assertion files run inside the page only. Nothing of the app runs outside the browser.
- [x] Concurrent checks beyond the per-household or total quota are refused, not queued. A check past its deadline is stopped and its report says so.
- [x] Tests run real Chromium in process: a screenshot is present, a console error is reported, a CSP violation is reported, and one assertion passes while another fails.

## Comments

**Landed (branch `app-spaces`).** `./check [--viewport WxH]... [--path /route]` on the mount
(`AppsFileSystem.CheckAsync`), run by `McpServerApps/Checks/AppChecks`; output in `<app>/checks/latest/`,
shown read-only at `/<slug>/.checks/latest/` (`<W>x<H>.png`, `report.json`). Contracts in
`.claude/rules/apps.md` ("Checks"), with pointers in `service-identity.md`, `person-assertions.md` and
`mcp-hosting.md`.
Tests: `AppChecksTests` (9, real Chromium on `apps.test`: two default screenshots and a report; console
error, uncaught throw, 404 and an `img-src` violation; assertions pass, throw and fail to parse; reads as
the turn's person through `AppReadsAgent` and Sugary; a navigation onto an internal address blocked, and an
allowed origin's redirect there never shown; both
quotas, held open by a stub agent; the deadline; viewports, route and read-only output), two cases in
`AppReadsApiTests` (the check's token believed; six refusals), `CallHouseholdConformanceTests` (a new rule),
`DeploymentConformanceTests.TheAppsServersCheckDeadline_EndsBeforeTheCallsTokenDoes`, and updates to
`McpAppsServerTests` (the `check` action is listed) and `FileSystemServerConformanceTests`.

**How a check's reads authenticate (the design choice).** Nothing is minted. The `./check` call's own
call token goes back to the agent, which believes it as its own signature:
- `CallTurn.VerifiedToken` exposes the token the gate verified. The registrar passes it as
  `FileSystemCaller.CallToken`, and only the apps mount reads it; `CallHouseholdConformanceTests` pins that.
- The check opens its own app session for the author on the preview (`AppSessions.OpenCheck`). Its proof
  is `ViewerProof.CheckCall`, beside the sign-in's `ViewerProof.Assertion`. It is never renewed, it expires
  at the deadline, and it is ended when the check ends.
- `AgentReads` sends that token in `X-Ziggurat-Check-Call-Token` (`AppReads.CheckCallTokenHeader`) instead
  of an assertion.
- The agent verifies it with `CheckCallVerifier`, which checks:
  - the agent's own `SigningKeyStore`;
  - audience `CallTo(sharedServers.apps)`;
  - the wall clock;
  - that it names a person.

  A guest, nobody, another server's token, a token out of date, or a token sent beside an assertion is
  refused with 401. "A claim nobody signed is not believed" holds: the token is the agent's own, bound to
  the apps server, and names the turn's person.
- The token arrives with at least `callTokens:refreshMargin` (30 s) of life left. `Checks:Deadline` is
  therefore 30 s and is conformance-checked to stay within it.
- The ask-for-an-assertion alternative was set aside: an accounts route signing an `apps` assertion on a
  call token would make the accounts service sign on another issuer's word.

**Choices the spec left open:**
- *Full Chromium, one per check.* Chromium's headless shell ignores
  `--unsafely-treat-insecure-origin-as-secure`. A probe showed it: `isSecureContext` false and no cookie
  sent. So the check launches `Channel = "chromium"` (new headless). The flag is per process, so each check
  gets its own browser, and closing it is how the deadline stops everything. App origins resolve to the
  pages listener through `--host-resolver-rules=MAP *.<domain> 127.0.0.1:<pages>`. The loaded origin is
  `http://<label>.<domain>`, the real name over plain http, and the report names the https origin people use.
- *Assertion files* are `checks/*.js` at the tree's root, read as text and evaluated in the page as an
  async function body. A return passes, with the returned value as the message. A throw fails, with the
  error's message. A file that does not parse fails. They run once per viewport, after the screenshot and
  the aria snapshot. The CSP is never bypassed.
- *Reach.* A context route lets through only the preview origin, `Cdns` and the tree manifest's `connect`.
  A request anywhere else is aborted and reported `blocked`. The route sees only a request's first URL,
  though, so an allowed origin can still redirect the browser anywhere, and the redirected request does go
  out. So nothing is kept unless the main frame is on the preview's origin: the screenshot, the snapshot,
  the console entries and the assertions. A viewport that left says so in its `error`. The test for this
  was red before the fix: a CDN's 302 to an internal server was screenshotted. Agent-written JS therefore
  cannot show the agent a page from the compose network. It can still make the browser send a GET there,
  blind. The service worker stays allowed; its fetches are the page's own, held to the same policy.
- *Exit code* is 1 when an assertion failed, the deadline was hit or the browser would not run. Console
  errors alone exit 0, with a count per viewport in the answer.
- *Limits*: `Checks:PerHousehold` 1, `Total` 2, `Deadline` 30 s, `Settle` 5 s (the longest wait for a quiet
  network before the screenshot). A refusal is `permission_denied` naming the limit and value. Viewports:
  at most 4, each side 240 to 3840 px, scale 1, viewport-sized screenshots. Each report list is capped at
  200 entries.
- *Output lives beside `git/` and `tree/`* (`BuiltAppStore.ChecksDirectory`), so publish never commits it
  and the preview never serves it. It is swapped in whole per run. It does not count toward
  `BytesPerRepository`, which is a change from ticket 01's "whole app directory".
- *Found on the way:* when the deadline closed the browser, the evaluation in flight failed before
  `IsConnected` turned false, so the stop was reported as a failed assertion. The catches now key on the
  deadline's own token (the deadline test caught this).

**Known gaps:**
- *Chromium runs without its sandbox* (`--no-sandbox`, Playwright's default). In the built image, launching
  with `chromiumSandbox: true` fails under Docker's default seccomp ("Chromium sandboxing failed"). Agent
  code therefore runs in a browser whose renderer escape would reach the apps server's container. It is
  bounded by the route above and by the code being the household's own agent's. Enabling the sandbox needs
  a seccomp profile or user-namespace allowance for `mcp-apps`, which was not done here.
- The agent's `file_read` showing a check's PNG as an image was not exercised end to end. The server's byte
  read of the PNG was, and that is what `file_read` streams.
- `./check` holds its exec call for up to the deadline (30 s).

**Not verified:** the smoke tier, E2E and the full suite were not run. The `mcp-apps` image was built
(`base-sdk` rebuilt first). Inside it, a probe with the bundled driver and the production flags launched
full Chromium as uid 1654 and as an arbitrary uid with no home: a secure context, a registered worker and
an aria snapshot. The arbitrary uid failed at first ("chrome_crashpad_handler: --database is required"),
which is why the image now sets `HOME=/tmp`. The .NET server itself was not started in the container.

**Deploying this needs:** no new secret or setting. Rebuild `base-sdk` first, since Domain, Infrastructure
and Mcp.Hosting changed. Then rebuild `agent` (it now believes a check's token) and `mcp-apps` (Chromium,
about 2 GB now). `mcp-apps` gains `init: true`. Checks read data only once the agent runs this build;
before that, a check's reads answer 503 and show as failed requests.

