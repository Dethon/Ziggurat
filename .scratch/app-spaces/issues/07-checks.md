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
- ~~*Chromium runs without its sandbox*~~ Closed by the follow-up below: the browser left `mcp-apps` and
  runs sandboxed in `apps-browser`.
- The agent's `file_read` showing a check's PNG as an image was not exercised end to end. The server's byte
  read of the PNG was, and that is what `file_read` streams.
- `./check` holds its exec call for up to the deadline (30 s).

**Not verified:** the smoke tier, E2E and the full suite were not run. The `mcp-apps` image was built
(`base-sdk` rebuilt first). Inside it, a probe with the bundled driver and the production flags launched
full Chromium as uid 1654 and as an arbitrary uid with no home: a secure context, a registered worker and
an aria snapshot. The arbitrary uid failed at first ("chrome_crashpad_handler: --database is required"),
which is why the image now sets `HOME=/tmp`. The .NET server itself was not started in the container.

**Deploying this needs:** no new secret or setting. Rebuild `base-sdk` first, since Domain, Infrastructure
and Mcp.Hosting changed. Then rebuild `agent` (it now believes a check's token), `mcp-apps` and
`apps-browser` (the follow-up below). Checks read data only once the agent runs this build; before that, a
check's reads answer 503 and show as failed requests.

**Follow-up: the browser left `mcp-apps` (branch `app-spaces`).** Chromium ran in the apps server's own
container with `--no-sandbox`, so agent-written JS ran unsandboxed beside `MCP_SECRET_APPS`, Redis and every
household's repositories. Now:
- *The seam* is `Checks:Browser` (`CheckBrowserSettings`): `Endpoint` empty launches the browser in
  process (a host run, the tests); set, `CheckBrowser.OpenAsync` connects to a Playwright browser server
  and sends the very `BrowserTypeLaunchOptions` it would have launched with, as JSON in
  `x-playwright-launch-options`. `Pages` is where that browser reaches the pages listener (empty:
  `127.0.0.1:<Listeners.Pages>`). Compose sets both as topology values: `ws://apps-browser:10080/` and
  `mcp-apps:8081`. A `--host-resolver-rules` `MAP` to a hostname works: Chromium resolves the target
  through Docker's DNS. It does need the service's DNS name, which `compose run` does not give.
- *The server* is `run-server --unsafe` in `DockerCompose/apps-browser` (`node:22-bookworm-slim`,
  `playwright-core@1.64.1`, full Chromium alone). It launches one browser per connection, so "one per
  check, closing it is the deadline" still holds: after a deadline no Chromium process was left. Without
  `--unsafe` (`filterLaunchOptions`, 1.64.1) the server drops `args` and `chromiumSandbox`. `--unsafe`
  also lets a client pick `executablePath`. So the server listens on **10080, one of Chromium's
  restricted ports**: a page's `WebSocket` and `fetch` to it are refused inside Chromium's network stack,
  while a plain client connects. That was probed in the container: the page was refused and a CDN
  answered 200.
- *The sandbox is on.* `chromiumSandbox: true` in both modes. Docker's default filter refuses namespaces
  without `CAP_SYS_ADMIN`, so `DockerCompose/seccomp/ziggurat-apps-browser.json` is Docker 29.8.0's default
  (moby/profiles `seccomp/v0.2.3`) with `clone`/`unshare` let through for any namespace and `chroot`
  unconditionally. Each was found necessary:
  - With `clone`/`unshare` allowed only for flags including `CLONE_NEWUSER`, the renderer crashed.
  - Without `chroot`, the browser would not start, because `cap_drop: ALL` removes the default's
    `CAP_SYS_CHROOT` rule.

  `mount`, `setns` and `bpf` stay refused, with no `SYS_ADMIN` and never `privileged`. In the hardened
  container (`cap_drop: ALL`, `no-new-privileges`, `read_only`, uid 1000, `CapEff` 0), `chrome://sandbox`
  read "You are adequately sandboxed" (PID and network namespaces, seccomp-BPF). No Chromium process in
  a running check had `--no-sandbox`.
- *What it can reach:* the `apps-browser` network only, which it shares with `mcp-apps` alone. That gives
  it `mcp-apps` 8081, and 8080 behind the server's secret. Through the bridge's NAT it also reaches the
  internet, the host's LAN and the host's published ports: Caddy's 443 and Lemonade on its LAN address,
  but not Redis, which is published on loopback only. The check's route still bounds what a page may
  request, and the CDNs and `connect` origins need the internet. It holds nothing: no secret, setting,
  volume, port or capability. It is on no network with Redis, the agent or the accounts service. That is
  shown by the compose file and pinned by `DeploymentConformanceTests`; no probe tried those services.
- *Images:* `mcp-apps` went from 2.01 GB to 760 MB; it keeps only the Playwright driver, made executable
  at publish. `apps-browser` is 1.42 GB.
- *Tests:*
  - `AppChecksTests` gained three cases: a check through a local `run-server --unsafe` from the bundled
    driver (`PlaywrightBrowserServer`) reaching the pages listener by `localhost`, secure context included;
    a dead endpoint, which reports "the browser could not run"; and `chrome://sandbox` launched here and
    on a server. `CheckBrowserTests` (unit) pins that `Pages` reaches the `MAP` rule, which the browser
    cases cannot tell apart from the loopback default.
  - `DeploymentConformanceTests` pins the service:
    - no env, `env_file`, volume, port, device or added capability; `cap_drop: ALL`; not privileged;
    - exactly `no-new-privileges` and its filter;
    - on its own network, shared with `mcp-apps` alone;
    - the endpoint and pages values;
    - Chromium installed in that image alone;
    - every Playwright server image pinned to `Microsoft.Playwright`'s version, camoufox included.

  `DeploymentFiles` now reads `cap_add`, `cap_drop`, `security_opt` and `privileged`.
- *Proved in Docker* with a throwaway harness: the real apps server module and `AppChecks`, built on the
  `mcp-apps` image, on that image's own Playwright driver, running as uid 1654 and again as an arbitrary
uid with no home (4242, `HOME=/`), so `HOME=/tmp` was dropped. Both default viewports came back with screenshots,
  the aria snapshot, and an assertion reading `isSecureContext` true. The `mcp-apps` image has no
  `/ms-playwright` and no `chrome` anywhere. The real compose definition started the server from the
  repository root (the relative seccomp path resolved). The full stack, smoke tier and E2E were not run.
- *Deploying:*
  - Build `apps-browser` and rebuild `mcp-apps` (`up --build` does both).
  - `mcp-apps` loses `init: true` (it starts no browser now) and joins the new `apps-browser` network.
  - **Verify the sandbox on prod**: `chrome://sandbox` from a client on that network, or the process
    arguments during a check. Prod enforces AppArmor `docker-default`, and a kernel refusing the container
    a user namespace (`kernel.apparmor_restrict_unprivileged_userns=1` on Ubuntu 24.04 and later) fails
    every check with "the browser could not run". That is fixed on the host, for example with an AppArmor
    profile allowing `userns` for the container, never with `--no-sandbox`.

**Follow-up: the check browser reaches no LAN (branch `app-spaces`).** `apps-browser` sat on an ordinary NAT
network, so it reached the host's LAN and published ports, and the per-page route judged only a request's
first URL: a `connect` origin the author controls could answer 307 and Chromium followed it to `192.168.x.x`
with method and body. Now the fix is the network's:
- The `apps-browser` network is `internal: true` (no route out of the host). Its one way out is
  `McpServerApps/Checks/CheckEgress`, a CONNECT-only proxy in the apps server on `Listeners.Egress` (8082),
  bound on `mcp-apps`'s own address facing the browser server, so no other `jackbot` container can use it.
- The proxy resolves the target once, refuses it unless every address it answers with is public
  (`EgressAddresses`: default deny, global unicast minus private, loopback, link-local, CGNAT, documentation,
  multicast and reserved ranges, IPv4-in-IPv6 forms judged as IPv4), and dials one of those addresses, never
  the name again. A redirect's next hop is a fresh CONNECT, judged the same.
- The browser gets `--proxy-server=http://mcp-apps:8082` and `--proxy-bypass-list=*.<AppsDomain>;<-loopback>`
  (`CHECKS__BROWSER__PROXY`); app origins still go direct to the pages listener by the `MAP` rule.
- *Choice:* this proxy over squid's `dst` ACLs, because squid judges a name on its DNS cache and may resolve
  again to connect (a zero-TTL rebinding window), and it would be a fourth image to pin.
- Tests: `CheckEgressTests` (arranged network below the judgment: a public origin tunnelled at its judged
  address; a redirect's private next hop refused by address, name, mixed answer, mapped IPv6, with nothing
  dialled; a rebinding name asked once; plain http refused), `EgressAddressesTests`, `CheckBrowserTests`
  (the flags), `AppChecksTests.ACheckBehindTheEgressProxy_DialsNoPrivateAddress_EvenOneTheRouteAllows`
  (Chromium: red first, it dialled loopback twice), and `DeploymentConformanceTests.TheChecksBrowsersOnlyWayOut_IsTheAppsServersEgressProxy`.

