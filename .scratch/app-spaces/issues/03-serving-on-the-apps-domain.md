# 03: Serving built apps on the apps domain

Spec: `.scratch/app-spaces/spec.md` ("Serving built apps").

**What to build:** A built app's author opens its published origin and its preview origin in a browser and gets the app. Each origin has its own content policy, web manifest and service worker, so it installs as a PWA of its own. The apps domain is a per-deployment compose value. Caddy has one `*.<apps-domain>` block with a wildcard certificate through the existing Cloudflare DNS-01 snippet, sending everything to the apps server with no `forward_auth`. In this ticket access is by an app session the apps server holds (seeded by tests). The sign-in that creates sessions is ticket 04.

**Blocked by:** 01

**Status:** ready-for-agent

- [x] The server parses `<slug>-<id>` and `<slug>-<id>-preview` from the Host header by the id. A renamed slug's old link still resolves, and an unknown label is a 404.
- [x] The published origin serves the served commit's `root`. The preview origin serves the working tree to the author alone. Everyone else gets the same 404 as an unknown app.
- [x] Every response carries the policy built from the manifest: `default-src 'none'`; `script-src`, `style-src` and `font-src` as `'self'` plus the configured CDN list; `img-src 'self' data: blob:` plus CDNs and `connect`; `connect-src 'self'` plus `connect`; `frame-ancestors 'none'`; `base-uri 'none'`; `form-action 'none'`.
- [x] Every served HTML document has the manifest link, the service worker registration and the client library injected.
- [x] A generated web manifest is served from the app's name and colour, with an icon drawn from the colour as the space icon is and `start_url`/`scope` set to `/`.
- [x] A generated service worker is served that caches the app's own files and never caches `/_vfs/*`.
- [x] A deleted app's origin answers navigations with a "this app was removed" page, and its service worker with one that unregisters itself.
- [x] `./publish` refuses HTML that loads an outside script or stylesheet without an exact version and an `integrity` attribute, and an import map that names an outside module without an integrity entry.
- [ ] Caddy and deployment conformance tests cover the apps-domain block and its environment entry. The smoke and E2E stacks use a local wildcard name.

## Comments

**Landed (branch `app-spaces`).** The apps server has a second listener (`AppListeners.Pages`, 8081; `/mcp`
stays on 8080), split by arrival socket (`McpServerApps/Serving/AppPagesListener.cs`), serving app origins
by Host (`AppPages`): `Domain/Apps/AppOrigin` reads the id from `<slug>-<id>[-preview]`. Sessions are
`AppSessions`, in memory. Policy `Domain/Apps/AppContentPolicy`, generated files `AppOriginFiles`
under `/_app/`, pinning `McpServerApps/Publishing/OutsideCodePinning` (AngleSharp, now a direct
reference). The space icon moved to `Domain/DTOs/WebChat/SpaceIcon`, which WebChat's `/icon.svg` and
the apps server both draw from. Caddy: one `*.{$APPSDOMAIN}` site → `mcp-apps:8081`, no `forward_auth`.
Contracts in `.claude/rules/apps.md` ("Serving built apps").
Tests: `AppsServingTests` (by Host, sessions seeded), `AppsServingInABrowserTests` (Chromium on
`apps.localhost`), `AppOriginTests`, `AppContentPolicyTests`, `OutsideCodePinningTests`, `SpaceIconTests`,
a refusal case in `AppsPublishingTests`, three cases in `DeploymentConformanceTests`.

**Choices the spec left open:**
- *Session store: memory*, indexed by person (`Account`), each session bound to one `AppOrigin` (published
  or preview) with the accounts `sid` kept. `SessionsEnded` is pub/sub, so a server that was down misses it;
  a Redis-kept session would outlive that announcement, memory loses both together. Ticket 04 calls
  `EndAll(person)` from its listener and opens sessions with `Open(...)`.
- *The policy* adds `manifest-src 'self'` and `worker-src 'self'` (the ticket's list would block the
  generated manifest) and `style-src 'unsafe-inline'` per the spec (the ticket's criterion omits it).
- *Server paths* live under `/_app/` (manifest, icon, worker with `Service-Worker-Allowed: /`, its
  registration, the client stub `apps.js`), which no app file shadows. The manifest link is
  `crossorigin="use-credentials"`: the manifest sits behind the session, and without it Chromium fetched it
  signed out (the browser test fails that way).
- *The worker is network-first*, the cache only offline, so a removed page, a new version or ticket 04's
  sign-in redirect is never hidden behind a kept copy.
- *A deleted app leaves a tombstone* (`<storage>/.removed/<id>`): its origin answers navigations 410 with
  the removed page, and `/_app/sw.js` with the self-unregistering worker, to anyone (no session). The id is
  never minted again. Non-navigation requests there get the common 404.
- *Serving details*: a directory without `/` is a 308 to it; a navigation to a missing path is the
  root's `index.html` (client-side routes); `.`/`..`/`.git` segments are 404. The published origin answers
  404 until the first publish, and uses the served commit's manifest (root, policy, name); the preview
  uses the tree's.
- *Pinning*: an exact version is `x.y.z[-pre|+build]` after `@` or as a path segment; protocol-relative
  addresses are outside; an import map's prefix mapping (`"lib/": "https://…/"`) is refused outright, since
  no integrity entry can cover it. Only pages under the served root are checked; `import` statements inside
  JS modules are not read.
- *The local name is `apps.localhost`*: loopback by name, and a secure context in Chromium on plain http
  (verified in `AppsServingInABrowserTests`: `isSecureContext`, a `__Host-` cookie set via CDP, the
  worker controlling the page) as well as behind Caddy's local CA. The smoke override sets it on `caddy` and
  `mcp-apps`; `scripts/run-local.sh` serves it on 6019.
- *The apps domain* is one compose anchor (`x-apps-domain`) merged into `mcp-apps` and `caddy`. A
  conformance test holds it outside the session cookie's domain.
- *Fixed on the way:* the shipped `appsettings.json` lacked the required `AppsDomain` (ticket 01), which
  failed `ShippedAppSettingsBindingTests`; it now ships `""`, filled by compose.

**Not met:** the E2E half of the last criterion. `WebChatStack` hosts no apps server, so it has no apps
name to set. The local name is chosen and documented for ticket 10's collection (and ticket 02's sandbox
case), which adds the server to that stack. The smoke tier was not run: `SmokeStack.Hosts` now skips the
wildcard site, a change made from reading the code alone.

**Deploying this needs:** a real `APPSDOMAIN` in `docker-compose.yml`'s `x-apps-domain` (a domain apart
from the deployment's), with its DNS on Cloudflare and `CF_API_TOKEN` able to answer its DNS-01 challenge
for the wildcard. Rebuild `base-sdk` first (Domain changed), then `mcp-apps`, `webui` and `caddy`. Then
restart (not reload) Caddy and compare the running Caddyfile with the host's. The `mcp-apps` image was not
built here: the in-process server, the real `Program.cs` on the host, and `caddy validate` in the shipped
caddy image were exercised. Until ticket 04 nobody holds an app session, so every app origin answers 404.

