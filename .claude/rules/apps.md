---
paths:
  - "McpServerApps/**"
  - "Domain/Apps/**"
  - "Tests/Integration/Fixtures/AppsServer.cs"
  - "Tests/Integration/McpServerTests/*Apps*"
  - "Tests/Unit/Domain/Apps/**"
  - "Tests/Unit/McpServerApps/**"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
  - "DockerCompose/caddy/Caddyfile"
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
  app's root — the action files, `check`, `errors`, `.checks` — for what later tickets produce.
- **Action files run on the mount** (a catalog, as `/timers`): `publish` (validates the manifest and its
  `root`, commits the working tree if it changed, records `HEAD` as served, answers the link), `history
  [--limit N]` (newest first, the served one marked `*`/`(served)`), `rollback <rev>` (moves the served
  pointer in the metadata only; history is never rewritten). Unknown is exit 127 naming the actions.
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
  members); the preview, its author alone. Anyone else, an unknown label, a non-GET, an app never
  published (on its published origin) gets **one 404, byte for byte**, under `AppContentPolicy.Closed`.
- **App sessions** (`AppSessions`): a host-only `__Host-app-session` cookie names one, which opens one
  `AppOrigin` (published or preview, never both) until it expires, keeping the accounts `sid` it was made
  from. **Held in memory**, indexed by person (`Account`: household and user), `EndAll(person)` for
  `SessionsEnded`: a Redis announcement missed while the server is down would leave a stored session
  alive, while memory loses both at once; a restart costs a silent re-sign-in.
- **What is served**: published, the served commit's files under that commit's manifest `root`, read
  from git as bytes (`AppRepository.EntryAsync`, object named on stdin); preview, the working tree's
  under the tree's manifest (no links followed). A directory without `/` is a 308 to it; a navigation
  to a missing path is the root `index.html` (client routes); `.`/`..`/`.git` segments are 404.
- **Every answer** carries `AppContentPolicy.For(manifest, Cdns)` (the origin's own manifest: commit or
  tree) — `style-src` allows inline styles as the spec says; `manifest-src`/`worker-src 'self'` so the
  generated files load — plus `nosniff`, `no-cache`, `no-referrer`. `Cdns` is appsettings.
- **The server's own files under `/_app/`** (`AppOriginFiles`), which no app file can shadow: the web
  manifest (name, short name, colour, `id`/`start_url`/`scope` `/`), the icon (`SpaceIcon`, the
  space icon's drawing, shared with WebChat's `/icon.svg`), the worker (network first, the cache only
  offline, never `/_vfs/` or `/_errors`; `Service-Worker-Allowed: /`), its registration and the client
  library **stub** (ticket 06). Every `.html` gets the three tags first in `<head>`, the manifest link
  `crossorigin="use-credentials"` since the manifest is behind the session.
- **A deleted app's origin**, asked of nobody: navigations get a 410 "This app was removed" page, its
  `/_app/sw.js` a worker that clears its caches, unregisters and reloads its windows. Its id stays
  taken (`BuiltAppStore.IdTaken`), so no later app inherits that origin's storage.
- **`./publish` pins outside code** (`Publishing/OutsideCodePinning`, AngleSharp): every page under the
  served root whose outside script, module preload or stylesheet lacks an exact version or `integrity`,
  or whose import map names an outside module (or prefix) without an integrity entry, refuses the
  publish naming each. Imports inside JS modules are not read.
- **Local name**: `apps.localhost` (smoke override, `scripts/run-local.sh` on 6019): loopback by name and a
  secure context in Chromium on http or Caddy's local CA, which the `__Host-` cookie and the worker need.

## Tests

`McpAppsServerTests` and `AppsPublishingTests` drive the server in process through its MCP tools
(`AppsServer`, after `SugaryServer`, both listeners open); `AppsServingTests` its pages listener by Host
with sessions the test opens (`SessionFor`), `AppsServingInABrowserTests` the same in Chromium on
`apps.localhost` (secure context, worker, manifest, no policy violation, removed worker);
`AppManifestTests`, `BuiltAppIdTests`, `AppOriginTests`, `AppContentPolicyTests`, `OutsideCodePinningTests` the pure rules;
`HouseholdAgentsTests` and `HouseholdFeaturesAdminTests` the feature; the deployment and server tables
(`McpServerRegistrations`, `DeploymentConformanceTests`) know the service and its volume.
