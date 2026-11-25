---
paths:
  - "McpServerApps/**"
  - "Domain/Apps/**"
  - "Tests/Integration/Fixtures/AppsServer.cs"
  - "Tests/Integration/McpServerTests/*Apps*"
  - "Tests/Unit/Domain/Apps/**"
  - "Tests/Unit/McpServerApps/**"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
  - "Accounts/Apps/**"
  - "Accounts/Internal/AppSignInRedemption.cs"
  - "DockerCompose/caddy/Caddyfile"
  - "Agent/App/AppReadsApi.cs"
  - "Infrastructure/Agents/AppReader.cs"
  - "Infrastructure/Agents/ReadRegistryCache.cs"
  - "Domain/Tools/FileSystem/Bridge/AppRead.cs"
  - "Tests/Integration/Fixtures/AppReadsAgent.cs"
  - "Tests/Integration/Agents/AppReadsApiTests.cs"
  - "Tests/Integration/Agents/ReadRegistryCacheTests.cs"
  - "Domain/Security/CheckCallVerifier.cs"
  - "Tests/Integration/McpServerTests/AppChecksTests.cs"
  - "Tests/Integration/Fixtures/PlaywrightBrowserServer.cs"
  - "DockerCompose/apps-browser/**"
  - "DockerCompose/seccomp/ziggurat-apps-browser.json"
---

# Built apps: the apps server and `/apps`

Spec: `.scratch/app-spaces/spec.md`. A **built app** is a web app an agent built, kept as a git
repository by the apps server (`McpServerApps`, container `mcp-apps`); plain "app" means an app behind
the proxy. Its **app manifest** is its `app.json`; its **preview** is its working tree.

## The server

- **A shared server, never in the tenant kit.** Listed in the agent's `sharedServers.apps`, named by the
  jonas template, and withheld from every definition by the household's **`apps` feature**
  (`FeaturesSection.Apps`), which is **off by default and the operator's alone**: an owner's features
  save strips `apps` from what it sent and from its `loaded`, then keeps the stored value
  (`HouseholdFeaturesAdmin`), because the server spends the deployment's shared storage.
- **Each call acts for the call token's person** (`Principal.Of(caller.Conversation)?.Account`, as
  Sugary's mount); their household is the account's tenant. A call naming nobody, or the guest, is
  refused `permission_denied` on every operation, never shown an empty list.
- **Only the household's own agents create or change a manifest**: the turn's `AgentId` must be in the
  household's `agent-names` view, read through `ICallHousehold.AgentNamesAsync` on the call token
  (`InternalCallers.Apps`, `CALLERS__mcp-apps`). No fallback to a registered catalogue: no household
  accessor is a refusal. A person's registered agent resolves for its owner alone, so nobody else could
  read through an app it built.
- **The volume** (`./volumes/apps`, chowned by `mcp-apps-volume`): `<household>/<id>/` holds
  `app.meta.json` (`BuiltApp`: id, slug, household, author, building agent, served commit), `git/` and
  `tree/`; `.removed/<id>` beside the households is a deleted app's tombstone. **The git directory is never inside the working tree**: everything in the tree is the agent's
  to write, and a writable git directory runs code (hooks, config) on this server. Git runs through
  `AppRepository` only: arguments as a list, no system or global config, no hooks, no signing, a fixed
  identity, `--template=` empty, a deadline. What is committed is what was written: `add --all --force` (a tree's `.gitignore` names `dist/`) and `git/info/attributes` unsetting text, filter and ident conversion; a revision from the model is a hash prefix or `HEAD~N` and is
  passed after `--end-of-options`.
- **Ids** (`Domain/Apps/BuiltAppId`): six lowercase alphanumerics from a cryptographic generator, unique
  across every household (the hostname names none). The repository is named by id, never slug, so a
  renamed app keeps its links; the link is `https://<slug>-<id>.<AppsDomain>/`, the preview's `<slug>-<id>-preview`. `AppsDomain` is
  per deployment (compose `x-apps-domain`, merged into `mcp-apps` and `caddy` so both read one value; a
  placeholder until the domain exists; never under the session cookie's domain); quotas
  (`Quotas:AppsPerHousehold`, `Quotas:BytesPerRepository`, the whole app directory) are the server's
  appsettings. A refusal names the limit and its value.
- **Create, rename, delete, publish and rollback run one at a time per household**
  (`BuiltAppStore.OneAtATimeAsync`), so a quota or a slug is never decided on a listing another call is
  changing.

## The mount

- **One filesystem, `apps`**, declared by overriding (`AppsFileSystem`): glob, info, read, search, text
  create and edit, delete, move, copy, blob read and write, exec. Never a hand-written `fs_*` tool;
  `FileSystemServerConformanceTests` pins the set.
- **It lists the caller's own apps**, one directory per slug; another member's are not found, shared or
  not. A slug is the slug rule, at most `AppsFileSystem.SlugLength` (the preview label must stay one DNS
  label), unique per author.
- **Creating an app is writing `/<slug>/app.json`.** The manifest (`AppManifest.Parse`) is judged
  whole, every broken rule named in its own words, and **checked on every way in**: create, overwrite and
  edit (applied in memory with `TextEditTool.Apply`, stored only if it passes); a blob write, copy or move
  onto it is refused, and it cannot be deleted or moved away (removing the directory deletes the app).
  `reads` are checked for shape only (`/` + a slug); `connect` entries are bare `https` origins.
- **Text create takes any extension**, since a sandbox command's UTF-8 file arrives as a text create;
  the text read and search open the shared text list plus web extensions. Binary build output arrives
  through the ranged blob write a copy streams through.
- **Refused writes**: anything under a `node_modules/` segment (its own hint: build in the sandbox, copy
  the output), any `.git` segment (also hidden from listings), and the names the server keeps at an
  app's root — the action files (`check` among them), `errors`, `.checks`.
- **Action files run on the mount** (a catalog, as `/timers`): `publish` (validates the manifest and its
  `root`, commits the working tree if it changed, records `HEAD` as served, answers the link), `history
  [--limit N]` (newest first, the served one marked `*`/`(served)`), `rollback <rev>` (moves the served
  pointer in the metadata only; history is never rewritten), `check [--viewport WxH]... [--path /route]`
  (below). Unknown is exit 127 naming the actions.
- Moving `/<slug>` to `/<new-slug>` renames the app; other moves stay within one app; copies may go
  between the caller's apps.

## Serving built apps

- **Two listeners** (`AppListeners`: `/mcp` 8080, app origins 8081; the image clears
  `ASPNETCORE_HTTP_PORTS`). `AppPagesListener` keys on the arrival socket, never Host, and on the pages
  listener serves app origins and nothing else, `/mcp` included. Caddy's one `*.{$APPSDOMAIN}` site sends
  everything to `mcp-apps:8081` with **no `forward_auth`**, its wildcard certificate through
  `tls_{$CADDY_TLS:cloudflare}`; nothing else of the deployment is on that domain (`DeploymentConformanceTests`).
- **The app is the Host's label, by id** (`Domain/Apps/AppOrigin.Parse`): `<slug>-<id>` or `<slug>-<id>-preview`;
  the slug is shape-checked and ignored, so a renamed app's old link opens it.
- **Who opens what** (`AppPages`): the published origin, its author (ticket 09 adds an app space's
  members); the preview, its author alone. **The viewer is settled before the app is looked up**: a page
  opened with no app session is sent to sign in whether or not its app exists; a signed-in viewer who
  may not open it, an unknown label, a non-GET (but a read's POST to `/_vfs/*`, below), anything but a page opened with no session, an app never
  published (on its published origin) gets **one 404, byte for byte**, under `AppContentPolicy.Closed`.
- **App sessions** (`AppSessions`): a host-only `__Host-app-session` cookie names one, which opens one
  `AppOrigin` (published or preview, never both) until it expires (`Sessions:Lifetime`, 12 h), keeping
  the accounts `sid` it was made from and the latest `apps` assertion. **Held in memory**, indexed by
  person (`Account`: household and user): a Redis announcement missed while the server is down would
  leave a stored session alive, while memory loses both at once; a restart costs a silent re-sign-in.
- **What is served**: published, the served commit's files under that commit's manifest `root`, read
  from git as bytes (`AppRepository.EntryAsync`, object named on stdin); preview, the working tree's
  under the tree's manifest (no links followed). A directory without `/` is a 308 to it; a navigation
  to a missing path is the root `index.html` (client routes); `.`/`..`/`.git` segments are 404.
- **Every answer** carries `AppContentPolicy.For(manifest, Cdns)` (the origin's own manifest: commit or
  tree) — `style-src` allows inline styles as the spec says; `manifest-src`/`worker-src 'self'` so the
  generated files load; a page's own inline import maps by their hashes (`InlineImportMaps`), the one inline
  script that runs — plus `nosniff`, `no-cache`, `no-referrer`. `Cdns` is appsettings.
- **The server's own files under `/_app/`** (`AppOriginFiles`), which no app file can shadow: the web
  manifest (name, short name, colour, `id`/`start_url`/`scope` `/`), the icon (`SpaceIcon`, the
  space icon's drawing, shared with WebChat's `/icon.svg`), the worker (network first, the cache only
  offline, never `/_vfs/` or `/_errors`; `Service-Worker-Allowed: /`), its registration and the client
  library (below). Every `.html` gets the three tags first in `<head>`, the manifest link
  `crossorigin="use-credentials"` since the manifest is behind the session.
- **A deleted app's origin**, asked of nobody: navigations get a 410 "This app was removed" page, its
  `/_app/sw.js` a worker that clears its caches, unregisters and reloads its windows. Its id stays
  taken (`BuiltAppStore.IdTaken`), so no later app inherits that origin's storage.
- **`./publish` pins outside code** (`Publishing/OutsideCodePinning`, AngleSharp): every page under the
  served root whose outside script, module preload or stylesheet lacks an exact version or `integrity`,
  or whose import map names an outside module (or prefix) without an integrity entry, refuses the
  publish naming each. Imports inside JS modules are not read.
- **Local name**: `apps.localhost` (smoke override, `scripts/run-local.sh` on 6019): loopback by name, and on
  plain http a secure context in Chromium, which the `__Host-` cookie and the worker need (verified on http
  only; through the smoke stack's Caddy on its untrusted local CA a browser may refuse the worker).

## App sign-in

How an app session is made (`AppViewers`; the wire spelled once in `Domain/Apps/AppSignIn`; the
accounts side in `accounts.md`, "The app sign-in"):

- **Sessionless page → the main host.** 302 to `{PublicBaseUrl}/accounts/app-sign-in?returnUrl=<this
  address>&state=<fresh>`, the state also set as `__Host-app-sign-in` (10 min). The origin is the Host
  with `X-Forwarded-Proto` (the proxy's) else the listener's scheme. Empty `PublicBaseUrl` (`PUBLICBASEURL`,
  equal to the accounts service's, conformance-checked) sends nobody: the 404.
- **The callback** `GET /_app/sign-in?code=&path=&state=` (under `/_app/`, before any session check):
  the state must equal the cookie (**checked before the code is redeemed**, so a code sent to another
  browser in a link is neither believed nor spent); the code is redeemed with this server's own
  credential for **this origin** (`IAppSignIns.RedeemAsync`); the answer's assertion is verified for
  `AppChannels.Apps` and **its claims alone** name the person, household and `sid`. Then the session is
  opened, its cookie set, the state cookie cleared, and 303 to `AppSignIn.SamePath(path)` (one leading
  `/`). A refused code is 400 and never redirected again (no loop between hosts); an outage, or an
  assertion that does not verify, 503.
- **Cookies**: `Secure; HttpOnly; Path=/; SameSite=Lax`, no `Domain` (`__Host-`). Lax because the
  callback is a navigation another site started.
- **Renewal**: once an app session's assertion has been held `Sessions:AssertionKept` (5 min, at most
  half the accounts `AssertionLifetime`, conformance-checked), the next request asks the session refresh
  on `apps` (`IAppSignIns.RenewAsync`), as the chat hub does per turn. Nobody (revoked, disabled), a
  suspended household, or an assertion naming another person or `sid` **ends that app session**; an
  outage or an unverifiable answer **serves 503 and ends nothing**. So a revoked session opens nothing
  after at most `AssertionKept`, announcement or not.
- **Ending**: `EndedAppSessionsListener` hears `SessionsEnded` on `accounts:sessions:ended`
  (`REDISCONNECTIONSTRING`) and calls `EndAll(person)` — every app session of theirs, never asked
  session by session. Empty Redis (a host run, a test) hears nothing; renewal still ends them.
- **Wiring**: the same `CALLERS__mcp-apps` credential as the agent-names view, `ACCOUNTS__JWKSURL`
  (`AddAssertionVerifier`), `PUBLICBASEURL`, Redis; `mcp-apps` depends on `redis`. A host run reads them
  from `appsettings.Local.json`, the accounts service its `CALLERS__mcp-apps` from `run-local.sh`/launch.

## The app read API

A built app reads the household as its viewer (the wire spelled once in `Domain/Apps/AppReads`):

- **The client library** (`AppOriginFiles.Library`, `/_app/apps.js`): a classic script injected first,
  so `globalThis.apps` exists before any of the app's scripts; `/_app/apps.mjs` is the same object as an
  ES module (default and named exports). `read(filePath, {offset, limit})`, `glob(basePath, pattern)`,
  `info(path)`, `search(query, {directoryPath | filePath, …})`, `query(path, sql, {maxRows})` answer the
  file tools' JSON; `blob(path)` a `Blob`. A refusal throws `apps.ReadError` carrying the envelope
  (`errorCode`, `message`, `retryable`, `hint`, `envelope`) and the `path`; the origin's 404 is
  `authentication`, anything else unanswered `transient_dependency`. Dependency-free, one expression.
- **`POST /_vfs/<operation>` on the origin** (`AppPages.ReadAsync`), the body the tool's arguments (one
  JSON object, at most 64 KiB): the viewer and app settled exactly as for a page — nobody, or a viewer
  who may not open the app, is the one 404 — then handed to the agent (`AgentReads`) with **the reads of
  the origin's own manifest** (the served commit's on the published origin, so a tree edit widens
  nothing until published; the tree's on the preview), the building agent from `app.meta.json`, and the
  assertion the app session holds, renewed first if it was due. The answer goes back as it came, under the
  origin's policy, `no-store`; an agent unreachable, or one that did not believe this server, is a 503
  `transient_dependency` envelope.
- **The agent's route** `POST /internal/app-reads/<operation>` (`AppReadsApi`, on the compose network
  alone, never routed by Caddy): believes **the apps server's own credential** — the one the agent holds
  for the apps server it dials (`sharedServers.apps` → `MCP__SECRETS__mcp-apps` = `MCP_SECRET_APPS`, the
  apps server's `MCP__SHAREDSECRET`), so no secret exists for this alone and the agent API's secret,
  which SexyTime's server holds, opens nothing here — for the app (id, building agent, reads), and **the
  viewer's `apps` assertion** (`VerifyTurnAsync` for `AppChannels.Apps`, a person) for who reads — or,
  from a check, the agent's own call token in `AppReads.CheckCallTokenHeader` ("Checks"), never both. No
  other header is read. Then, per read: the household's standing (`HouseholdStanding`, the turn's rule:
  suspended or disabled is `permission_denied`, unreadable `transient_dependency`), the registry
  (`ReadRegistryCache` over `AgentReadRegistries`), and `AppRead` over the registry's `VfsCall`. 200 the
  tool's JSON or the bytes, 422 the envelope, 401 not believed.
- **What is read** (`AppRead`, Domain): each operation the file tool's own call (`VfsCall.ToolAsync`,
  the tool's result shape on virtual paths), the blob the bridge's byte read (`VfsCall.ReadAsync`, so
  `vfsBridge:maxFileBytes` applies). Refused with `permission_denied`: an operation not in
  `AppReads.Operations` (nothing that changes anything is reachable), a path whose mount point — the
  resolved one, else the path's first segment — is not in `reads` (checked per request: the cached
  registry is shared by every app of that agent and viewer), and a tool the building agent is not offered
  or whose whitelist would ask (`ReadRegistry.Permission`, `Offered`). A mount the app reads that is not
  mounted while a household server is offline is `transient_dependency` naming the offline servers (a
  server's mount point is known only once reached). A building agent the household no longer has refuses
  every read, naming it.
- **The cache** (`ReadRegistryCache`): one registry per (household, agent, person), built once
  (single-flight), leased per read; every lease asks `IsStaleAsync` and a stale one is rebuilt; one nobody
  leased for `appReads:registryIdle` (2 min, agent appsettings) is let go; an evicted one is disposed only
  when its last lease ends; a failed build is never kept. Disposed with the host.
- **Wiring**: `AGENTURL: http://agent:8080` on `mcp-apps` (empty answers every read unavailable; a host
  run's `appsettings.Local.json` names 5000), conformance-checked with the credential pair and the route
  off the proxy.

## Checks

`./check` (`Checks/AppChecks`, the mount's `CheckAsync`): the preview opened in a headless Chromium of
its own as the person whose turn ran it; in the deployment that browser runs in the `apps-browser`
container, never in `mcp-apps`.

- **Arguments**: `--viewport WxH` (repeatable, at most 4, each side 240–3840; default a phone `390x844`
  and a desktop `1280x800`), `--path /route` (default `/`). Anything else is exit 2. The tree's
  `app.json` must parse, or exit 1 with nothing checked.
- **Who it reads as**: the call's own token (`call.VerifiedToken`, handed to the mount as
  `FileSystemCaller.CallToken`; `CallHouseholdConformanceTests` keeps every other reader out). The check
  opens an app session itself (`AppSessions.OpenCheck`, `ViewerProof.CheckCall`): the preview alone, never
  renewed, expiring at the deadline, ended when the check does and by `EndAll`. Its `/_vfs/*` reads carry
  the token in `AppReads.CheckCallTokenHeader` instead of an assertion (`AgentReads`), and the agent
  believes it as **its own signature for the apps server it dials, naming a person**
  (`CheckCallVerifier`: the agent's `SigningKeyStore`, audience `TokenAudiences.CallTo(sharedServers.apps)`,
  the wall clock; the guest, nobody, another server's or both headers at once are 401). Nothing is minted
  for a check: the token arrives with at least the agent's `callTokens:refreshMargin` (30 s) left, so
  `Checks:Deadline` is at most that (`DeploymentConformanceTests`).
- **The browser** (`Checks/CheckBrowser`, one options object for both ways of getting it): full Chromium
  (`Channel = "chromium"`, new headless; the headless shell ignores the secure-origin flag), **one per
  check**, **always in Chromium's own sandbox** (`ChromiumSandbox = true`; no fallback to `--no-sandbox`:
  a host that cannot sandbox fails the check "the browser could not run"), launched with
  `--host-resolver-rules=MAP *.<AppsDomain> <Checks:Browser:Pages>` (the app is reached at this server's
  pages listener, never through DNS; empty is `127.0.0.1:<Listeners.Pages>`) and
  `--unsafely-treat-insecure-origin-as-secure=http://<preview host>` (the `__Host-` cookie and the worker
  need a secure context; the flag is per process, hence one browser each, and closing it is the
  deadline). The session cookie is set host-only over CDP. **`Checks:Browser:Endpoint` empty launches it
  here** (a host run, the tests); **set, it connects** to a Playwright browser server and sends the same
  options in `x-playwright-launch-options`, and the server launches the browser for that connection
  alone and closes it with the connection.
- **`apps-browser`** (`DockerCompose/apps-browser`, compose; `DeploymentConformanceTests` pins all of it):
  `node:22-bookworm-slim` + `playwright-core@<Microsoft.Playwright's version>` (the handshake refuses a
  minor mismatch) + full Chromium alone, running `run-server --unsafe` (without `--unsafe` the server
  drops `args` and `chromiumSandbox`) as `node` on **port 10080, one of Chromium's restricted ports**, so
  no page, nor a compromised renderer's network stack, can dial the server that would launch anything.
  It holds **no secret, setting, volume, port or capability** (`cap_drop: ALL`, `no-new-privileges`,
  `read_only` with `/tmp` and `/home/node` in memory, `stdin_open` since the server exits when stdin
  closes, `init`), and sits on the `apps-browser` network with `mcp-apps` alone: it reaches `mcp-apps`
  (8081, and 8080 behind the server's secret), the internet and through NAT the host's LAN and published
  ports (Caddy, Lemonade; Redis is on loopback), and shares no network with Redis, the agent or the
  accounts service. `mcp-apps` reaches it at `CHECKS__BROWSER__ENDPOINT:
  ws://apps-browser:10080/` and is reached by it at `CHECKS__BROWSER__PAGES: mcp-apps:8081` (topology
  values; the service's DNS name, which `compose run` does not give). **The sandbox's syscall filter**
  (`DockerCompose/seccomp/ziggurat-apps-browser.json`) is Docker 29.8.0's default (moby/profiles
  `seccomp/v0.2.3`, as the sandbox's) plus `clone`/`unshare` for any namespace (the kernel still refuses
  any but a user namespace without `CAP_SYS_ADMIN` over it) and `chroot` (the default's needs
  `CAP_SYS_CHROOT`, which is dropped); `mount`, `setns`, `bpf` and the rest of `SYS_ADMIN`'s grant stay
  refused. `chrome://sandbox` there says "adequately sandboxed" (verified on Docker Desktop's WSL2; a
  host whose AppArmor or `kernel.apparmor_restrict_unprivileged_userns` refuses the container a user
  namespace fails every check, and is fixed on the host, never with `--no-sandbox`). The `mcp-apps` image carries the
  Playwright driver only (made executable in its publish stage), no browser.
- **What the page reaches**: a context route lets through only the preview's origin, `Cdns` and the tree
  manifest's `connect`; a request anywhere else (a navigation onto the deployment's network included) is
  aborted and reported `blocked`. The route sees only a request's first URL, so an allowed origin can
  still redirect the browser anywhere: **nothing is kept of a page that is not the preview** — the
  screenshot and snapshot are taken and kept only while the main frame is on the preview's origin, an
  assertion runs only then (one that ends elsewhere fails "left the preview"), console entries are
  recorded only then, and the viewport's `error` says it left. The policy is never bypassed, so its
  violations are what the app would meet.
- **Per viewport**, a context of its own: load, then up to `Checks:Settle` (5 s) for the network to go
  quiet (`settled`), then the screenshot (the viewport, scale 1), the aria snapshot of `body`, then each
  **assertion file** — `checks/*.js` at the tree's root, sorted, read here as text and evaluated in the
  page as the body of an async function: returning passes (its value is the message), throwing fails
  (the error's message), not parsing fails. Nothing of the app runs outside the page.
- **Output**: a run directory (stale ones swept as the next check starts) under `BuiltAppStore.ChecksDirectory` (`<app>/checks/`, beside `git/` and
  `tree/`, never committed or served, not counted in the repository's bytes), swapped in whole as
  `latest/` when done. The mount shows it read-only at `/<slug>/.checks/latest/` (`<W>x<H>.png` per
  viewport, `report.json`); the PNGs come through the byte read, so `file_read` shows them. `report.json`:
  app, origin, path, times, `deadline`, `deadlineHit`, `error` (the browser would not run), and per
  viewport `screenshot`, `settled`, `error` (navigation), `console` (type, text, location; `uncaught` for
  a page error), `failedRequests` (status ≥ 400 or the failure), `policyViolations` (from
  `securitypolicyviolation`), `accessibility`, `assertions` (file, passed, message); lists capped at 200.
- **The answer**: a line per viewport (console errors, failed requests, violations, assertions) and where
  the files are; exit 1 when an assertion failed, the deadline was hit or the browser would not run.
- **Limits** (`Checks` in appsettings): `PerHousehold` (1) and `Total` (2) concurrent checks, refused
  `permission_denied` naming the limit and value, never queued; `Deadline` (30 s) stops the check, closing
  its browser, and the report and answer say so.

## Tests

`McpAppsServerTests` and `AppsPublishingTests` drive the server in process through its MCP tools
(`AppsServer`, after `SugaryServer`, both listeners open); `AppsServingTests` its pages listener by Host
with sessions the test opens (`SessionFor`, an assertion signed by `TestAssertions`), `AppsSignInTests`
the sign-in against a real `AccountsServer` signing with the test key on its clock (handed to the apps
server's `TimeProvider`), Redis for the ending, `AppsServingInABrowserTests` the same in Chromium on
`apps.localhost` (secure context, worker, manifest, no policy violation, removed worker, the sign-in);
`AppManifestTests`, `BuiltAppIdTests`, `AppOriginTests`, `AppSignInTests`, `AppContentPolicyTests`, `OutsideCodePinningTests`,
`InlineImportMapsTests` the pure rules; `AppsShippedSettingsTests` the shipped CDN list;
`HouseholdAgentsTests` and `HouseholdFeaturesAdminTests` the feature; `AppReadsApiTests` the agent's
route over the wire (`AppReadsAgent`, `TextFileServer`, Sugary), `ReadRegistryCacheTests` the cache,
`AppsReadsTests` the origin's forwarding to the real route, a Chromium case the library; `AppChecksTests` the check
in real Chromium on `apps.test` (screenshots, console, failed requests, violations, assertions, reads as the
turn's person through `AppReadsAgent`, the blocked navigation, quotas held open by a stub agent, the
deadline), launched in process and once on a local `run-server --unsafe` from the bundled driver
(`PlaywrightBrowserServer`), a dead endpoint, and `chrome://sandbox` both ways; `CheckBrowserTests` that
`Checks:Browser:Pages` reaches the `MAP` rule; `AppReadsApiTests` also the check's call token; the deployment and server tables
(`McpServerRegistrations`, `DeploymentConformanceTests`) know the service and its volume.
