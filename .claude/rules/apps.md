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
  - "Domain/Apps/PublishedApps.cs"
  - "Accounts/Admin/HouseholdAppsClient.cs"
  - "Tests/Integration/McpServerTests/AppSpacesOverTheWireTests.cs"
  - "Infrastructure/Clients/Apps/**"
  - "Tests/E2E/WebChat/AppSpacesE2ETests.cs"
  - "Domain/Prompts/Skills/apps.md"
  - "Domain/Prompts/AppsSkill.cs"
  - "Domain/DTOs/Channel/AppTurn.cs"
  - "Domain/Monitor/AppTurns.cs"
  - "Tests/Integration/Agents/AppTurnAgentTests.cs"
  - "Tests/Unit/Domain/Monitor/ChatMonitorAppTurnTests.cs"
---

# Built apps: the apps server and `/apps`

Spec: `.scratch/app-spaces/spec.md`. A **built app** is a web app an agent built, kept as a git repository
by the apps server (`McpServerApps`, container `mcp-apps`); its **app manifest** is its `app.json`, its
**preview** its working tree.

## The server

- **A shared server, never in the tenant kit** (`sharedServers.apps`), withheld by the household's **`apps`
  feature**, off by default and the operator's alone: an owner's features save strips it
  (`HouseholdFeaturesAdmin`), since the server spends the deployment's shared storage (`admin-operator.md`,
  "The apps feature").
- **Each call acts for the call token's person** (`Principal.Of(caller.Conversation)?.Account`); nobody or
  the guest is `permission_denied` on every operation, never an empty list.
- **Only the household's own agents create or change a manifest**: the turn's `AgentId` must be in the
  `agent-names` view (`ICallHousehold.AgentNamesAsync`); no accessor is a refusal, never a fallback to the
  registered catalogue (a person's registered agent resolves for its owner alone).
- **The git directory is never inside the working tree** (a writable git dir runs code here). Git runs
  through `AppRepository` alone: argument lists, no system/global config, hooks or signing, empty
  `--template=`, a deadline, `add --all --force` with conversions unset, a model's revision after
  `--end-of-options`. The volume (`<household>/<id>/`: `app.meta.json`, `git/`, `tree/`, `checks/`,
  `errors`, `asks`) is laid out on `BuiltAppStore`; a deleted app leaves a `.removed/<id>` tombstone, and asks'
  images wait in `.uploads/` beside the households.
- **Ids** (`BuiltAppId`): cryptographic, unique across households; the repository and the link
  (`<slug>-<id>[-preview].<AppsDomain>`) are named by id, so a rename keeps links. `AppsDomain` (compose
  `x-apps-domain`, shared by `mcp-apps` and `caddy`) is never under the session cookie's domain. A quota
  refusal names the limit and its value.
- **The `apps` skill** (`Domain/Prompts/Skills/apps.md`) is served by this server, so it reaches exactly the
  agents offered `/apps`; `AppsSkillTests` holds every name it teaches to the code.
- **Create, rename, delete, publish and rollback run one at a time per household**
  (`BuiltAppStore.OneAtATimeAsync`).

## The mount

- **One filesystem, `apps`** (`AppsFileSystem`), declared by overriding
  (`FileSystemServerConformanceTests`). It lists **the caller's own apps** alone; another member's are not
  found, shared or not. A slug is at most `AppsFileSystem.SlugLength` (one DNS label with the preview
  suffix), unique per author.
- **Creating an app is writing `/<slug>/app.json`**, and `AppManifest.Parse` judges it whole **on every way
  in** (create, overwrite, edit applied in memory first); a blob write, copy or move onto it is refused, and
  it goes only with its directory. `connect` entries are bare `https` origins.
- Moving `/<slug>` to `/<new-slug>` renames the app; every other move stays within one app, and copies go
  only between the caller's own apps.
- **Refused writes**: under `node_modules/` (hint: build in the sandbox, copy the output), any `.git`
  segment (hidden from listings too), and the reserved root names (the action files, `errors`, `.checks`).
- **Action files** are a closed catalog (`AppActions`; unknown is exit 127): `publish`, `history`,
  `rollback` (moves the served pointer only; history is never rewritten), `check` ("Checks"). A publish of
  an app space asking beyond its approval (reads, origins or servers) says what waits on an owner, read after the household lock; spaces
  unreadable or refused say it could not be checked, and the publish stands.
- **Deleting an app the household shares as an app space is refused** `permission_denied`, naming the space;
  spaces unreadable delete nothing (`transient_dependency`), refused delete nothing (`permission_denied`).
  `AppSharing` is the one spaces reader for the delete, the publish and an origin's access.

## Serving built apps

- **Two listeners** (`AppListeners`: `/mcp` 8080, origins 8081). `AppPagesListener` keys on the arrival
  socket, never Host; the pages listener serves app origins alone, never `/mcp`. Caddy's `*.{$APPSDOMAIN}`
  site has **no `forward_auth`**, and nothing else of the deployment is on that domain
  (`DeploymentConformanceTests`).
- **The app is the Host's label, by id** (`AppOrigin.Parse`); the slug is ignored.
- **Who opens what** (`AppPages.AccessAsync`, settled for pages, reads, asks and reports alike by
  `AppPages.SettleAsync`): the published origin to its author and, while an app space, every member of its
  household; the preview to its author alone. **The viewer is settled before the app is looked up**, so a
  sessionless page goes to sign in whether or not the app exists. Anyone who may not open it, an unknown
  label, a non-GET (but the `/_vfs/*`, `/_ask` and `/_errors` POSTs), an unpublished app: **one 404, byte for byte**,
  under `AppContentPolicy.Closed`.
- **App sessions** (`AppSessions`): a host-only `__Host-app-session` cookie opens one `AppOrigin` (never
  published and preview both) until `Sessions:Lifetime`, keeping the accounts `sid` and the latest `apps`
  assertion. **In memory**, indexed by person: a stored session could outlive a missed revocation; a restart
  costs a re-sign-in.
- **What is served**: published, the served commit's files under that commit's manifest `root`; preview, the
  working tree under the tree's manifest, no links followed. `.`/`..`/`.git` segments are 404; a navigation
  to a missing path is the root `index.html`.
- **Every answer carries `AppContentPolicy.For(manifest, Cdns, pinned)`** for the origin's own manifest (an
  app space's narrowed to its approval) — **what was checked is what runs**: `script-src`, `style-src` and
  `font-src` name no CDN host whole, only the pinned code's package-version directories on a `Cdns` origin
  (`PinnedCode.Sources`; a path that could end a source or directive is refused whole); another package or
  version is refused. Published uses the commit's `BuiltApp.Pinned[commit]`, recorded at publish (none
  recorded loads no outside code); the preview finds it per answer, so a check sees a refused import.
  `img-src` keeps `Cdns` whole (an image runs nothing). Inline import maps by hash (`InlineImportMaps`) are
  the one inline script; every answer also carries `nosniff` and `no-referrer`. Residual: a pinned address
  that redirects lands anywhere on that CDN host (integrity still holds).
- **`./publish` pins outside code** (`OutsideCodePinning`): an outside script, module preload, stylesheet or
  import-map entry without an exact version and `integrity` refuses the publish, naming each; a GitHub
  (`/gh/`) address pins a whole commit, never a tag (`PinnedCode.HasExactVersion`). Imports inside
  JS modules are not read, since the policy refuses any outside the pinned set.
- **`/_app/` is the server's** (`AppOriginFiles`: manifest, icon, worker with `Service-Worker-Allowed: /`,
  registration, client library), never shadowed by an app file; every `.html` gets its tags first in
  `<head>`, the manifest link `crossorigin="use-credentials"` since the manifest is behind the session.
  That link comes first, so a manifest of the app's own is never the one installed.
- **The icon** (`/_app/icon.svg`) is `SpaceIcon`'s frame with its corner badge, around the manifest's `icon`
  (an SVG the origin serves, `AppIcon`: put in as a data-URI picture, never markup) or else the accent
  colour's drawing. SVG only, one drawing for every size. `./publish` refuses an `icon` that is no file
  under the served root (a link counts as none) or no whole `<svg>` document of 256 KiB at most; a preview
  whose file is not there yet draws the accent's. `./check` saves it as it installs (`icon-512.png`,
  `icon-64.png`), drawn by its own browser from the working tree, so the agent sees it with no rasterizer.
- **The worker**: a published origin's answers carry `X-App-Version` (served commit, base policy, this
  build), embedded in the worker, which keeps the app's files **cache first under that version** — a
  publish, a narrowed approval or a new build is new worker bytes, and the new worker drops every other
  `app-files-*` cache. **A navigation reaches the server first**; the kept page answers only when the
  network fails; a page served at another version loads from the network until the new worker takes over.
  The preview is network first. `/_vfs/`, `/_ask`, `/_errors`, `/_app/sign-in` and `/_app/sw.js` are never cached.
  Residual: a shell opened offline after a narrowing runs the old policy until it is back online.
- **A deleted app's origin**: navigations a 410, `/_app/sw.js` a worker that clears, unregisters and
  reloads. Its id stays taken (`BuiltAppStore.IdTaken`), so no later app inherits its storage.

## App spaces

An **app space** is a household space sharing a published app (`HouseholdSpace.AppId`; owners' side in
`admin-owner-pages.md`, the hub's in `chat-hub.md`).

- **The household's `spaces` section is the one truth**; the apps server keeps no copy. `mcp-apps` may read
  that view, `app-asks` and `agent-names` alone (`InternalCallers`), on the call token for the mount and on **the
  viewer's own `apps` assertion** for an origin (`CallHousehold.OfPerson`), kept a minute or until a save is
  heard.
- **Access**: the author always; anyone else must be of the app's household (`tid` equal), on the published
  origin, holding an assertion (a check's session never), with the app in the section. **Unreadable is
  unchecked** (a page's 503, a read's `transient_dependency`), never the 404 and never open; **a refusal is
  closed**.
- **A disabled member** is closed by their sessions ending (`SessionsEnded` → `EndAll`) or, if that is
  missed, by the next renewal answering nobody.
- **The approval holds** (`HouseholdSpace.Approved`, an `AppReach`: `reads`, `connect` and the servers its
  `asks` may use): while shared, the published origin grants **every viewer, its author too**, only the
  served manifest's reach `Within` it (`AppPages.ServedAsync` narrows once, for both policies, the reads and
  an ask's servers); a read `Beyond` it is
  `permission_denied` naming an owner's approval. No approval grants nothing. **Accounts never closes an
  author's own app**: spaces unreadable or refused open it under its whole manifest (their own data only).
- **The listing** `GET /internal/published-apps` (`PublishedAppsListing`) and **a person's apps** `GET
  /internal/persons-apps` (`PersonsAppsListing`) sit on the MCP listener outside `/mcp`'s gate, never routed
  by Caddy nor answered on the pages listener. Each believes only an assertion the accounts service signs
  for its own channel: `AppChannels.PublishedApps` naming an `owner` (`HouseholdAppsClient`; 401 else, 403 a
  member), `AppChannels.PersonsApps` for the chat hub's session refresh alone (`PersonsAppsClient`,
  `APPSURL`). Each answers the assertion's household alone, `no-store`; a person's apps give a shared id no
  more than its link and name, and read no spaces. `apps-browser` reaches this listener, so the assertion is
  all that closes them.

## App asks

Spec: `.scratch/app-asks/spec.md`; wire in `Domain/Apps/AppAsks`; the client library's `apps.ask` and
`apps.AskError` (the envelope, as `ReadError`). **The apps server is also a channel** (dual-role,
gate-on-live, `AddAgentTurns`): the agent dials it as the `apps` channel endpoint at the address it serves
as `sharedServers.apps` (`AgentAppSettingsTests`), with no default agent. What the agent believes of an
ask and does with it is `.claude/rules/channels.md` (`ChannelMessageNotification.AppTurn`).

- **`POST /_ask` on the origin** (`AgentAsks`), `{ "text", "schema"?, "conversation"? }` parsed strictly (`AppAskRequest`: an
  unknown field refuses it whole, `Asks:MaxTextLength`; `schema` is `{ name, schema }`, the name
  `[A-Za-z0-9_-]{1,64}`, the schema a JSON object of at most `Asks:MaxSchemaBytes` as written, never
  rewritten or checked for strict mode's rules): **settled exactly as a read** — from a page of that origin alone, the viewer
  before the app, the one 404 before the body is looked at — and answered `no-store`.
- **Images** (`apps.ask(text, { files })`): the ask becomes a `multipart/form-data` of exactly one `ask` field
  (the same JSON) and a `files` part per image (`AppAsks.AskField`, `FilesField`); anything else in the form,
  more than `Asks:MaxImages`, one over `Asks:MaxImageBytes` or empty, or one whose **bytes** are no type in
  `Asks:ImageTypes` (`AppAskImages.Sniff`: JPEG, PNG, WebP, GIF; the page's declared type is never believed,
  and the sniffed one is what the model is sent; the code's default takes none, the shipped file names the
  four) is 400 `invalid_argument` **before any turn, before counting**. The request body is bounded to the
  JSON plus the images' bounds. Kept only once the ask is counted and its conversation settled, in the upload
  store (`AskUploads`, `<volume>/.uploads`, under a name no household id takes) **for the app's household**
  under the ask's conversation, named on the message's `Attachments` in the order sent; **taken back**
  (`UploadStore.Remove`) when nobody was listening; swept after the shared `attachmentRetention`. The agent
  fetches them on this channel signed for the turn's person, so only the app's household is ever handed one;
  a follow-up sends none and its history hydrates the first ask's. Nothing checks the model can see images
  (the apps server reads no catalogue, and the agent does not check a third time): one that cannot is sent
  them anyway.
- **The message**: `AgentId` the building agent from `app.meta.json`, the session's `apps` assertion
  (renewed first if due) — or, from a check's session, **its call token as `CheckCallToken` in the
  assertion's place**, never both (Checks, below) — a label
  principal `("apps", user id, Account)`, a conversation (below), and an `AppTurn` with the origin's own manifest's
  instructions and `asks` (the servers its tools may come from, **by the names they are configured under**:
  a shared server's `sharedServers` key, a household server's own name, never a host and port; shape judged
  as `TunnelCredentialProtocol.IsServerName`) — the served commit's, the preview the tree's; on
  a shared app, those `Within` its approval, for every viewer, its author too (an unapproved server is left
  out, never the ask refused). The page's schema is the message's
  `ResponseFormat` (strict, as written: ADR 0044), and the same manifest's **`model`** (a model id's shape
  alone, `vendor/name`) its `ConfigPatch.Model`; neither is sent when absent. Whether the agent may be
  patched to that model is the agent's (`channels.md`): off its `patchableModels` the ask runs on its own. Sent by `AgentTurns` under its own
  key, waited on for `Asks:Timeout` (a check's, no longer than its session has left); the page giving up or the wait running out cancels the turn **naming
  the agent** (the agent groups by conversation and agent).
- **Conversations** (`AskConversations` over `OpenConversations`, keyed by app id and viewer `Account`,
  carrying the building agent): an ask naming none opens one; one naming a `conversation` is a turn on it
  **only if this app and viewer opened it, within `Asks:FollowUpWindow` of its last turn, and the app's
  building agent is still the one it was opened on** (the agent keeps its history under conversation and
  agent; a changed one holds none, so the app starts afresh). Anything else — never minted, another app's
  or viewer's (published and preview are one app), past the window, after a restart (**in memory by
  design**), evicted at the table's ceiling (`AskConversations.Ceiling`: the one longest without a turn
  goes) — is one answer, 410 `conversation_expired`, **before any turn**, checked after the ask's every
  other refusal, since continuing restarts the window. Each ask carries its own `schema`: a follow-up with none is answered in text.
  The ceiling is deployment-wide, so one household's apps can push another's conversations out; the cap
  bounds how fast (an ask past it opens none).
- **The daily cap** (`AskCounts`, `AskCaps`): asks are counted **per app** — every viewer, published and
  preview alike, sharing one count; another app or household's app its own — over a **rolling 24 hours**,
  by the minute sent in (a minute stops counting a day after it began). An ask is counted **once nothing
  else refuses it, before the conversation is looked at**, under a per-app lock (two asks never both take
  the last slot), and **given back** if its conversation is refused or nobody was listening (never
  delivered; a conversation it opened is forgotten too, `OpenConversations.Forget`): an ask past the cap
  opens no conversation, and one never delivered keeps none. Counts live at `<app>/asks` (the minutes still
  counting, JSON), durable across restarts, gone with the app, never listed, committed or counted toward
  the quota. **The limit is `AskCaps.LimitAsync`'s alone**, read at each ask from the household's `app-asks`
  view (`ICallHousehold.AppAsksAsync`, on the viewer's `apps` assertion; a check's on its call's own household, `call.Household`, carried on `ViewerProof.CheckCall`),
  kept a minute or until a save is heard: the app's own cap, else the household's `dailyCap`, else
  `Asks:DailyCap` (200) (`AppAsksSection.LimitOf`). **Never lifted**: unreadable is 503
  `transient_dependency` (`AppAsks.CapUnreadable`), refused 422 `permission_denied` (`CapRefused`), both
  before counting or sending, never the deployment's value. Owners set the caps on `/admin/apps`
  (`admin-owner-pages.md`, "Apps"). A check's asks count as anyone's.
- **`instructions`** (`AppManifest`): a repository-relative `.md` path, shape judged on every way in. Its
  file is read at publish (`./publish` refuses none, a link, empty, not UTF-8 or over
  `AppAsks.MaxInstructionsBytes`) and again at each ask (the preview never published). The bound is a
  constant, not a setting: 16,000 bytes keep the `app_instructions` section within its 4,000-token budget
  (`AppAsksTests`).
- **Answers**: 200 `{ conversation, text }` (the last message's text; the conversation opened or continued), or, asked with a schema,
  `{ conversation, value }` (that text parsed as JSON whole, `AppAsks.ReadReply`; never both keys); else the
  tools' envelope — 400 `invalid_argument` (body), 422 `invalid_reply` (a reply to a schema that is no JSON,
  not retryable; **its words ride as `reply` on the preview alone**, `AppAsks.InvalidReply`, clipped, since
  the published origin's viewers may be any member), 422 `permission_denied` (no instructions, a refusal:
  `RefusalAnswers.CodeOf`'s sentence as the message, `unchecked` as `transient_dependency`), 422 `transient_dependency` for a failed
  turn whose words are the agent's exception (logged; shown on the preview alone, below) or no answer (an ask ended on its refused
  calls with no text is one), 410 `conversation_expired` (not retryable: ask without it), 429
  `quota_exceeded` past the daily cap (not retryable; names the limit, and `freesAt` when an ask is next
  counted, absent for a cap of none: `AppAsks.Capped`), 503 nobody polling, 504 `timeout`. **On the preview
  alone** a failed turn's own words (the agent's exception, often a provider's body: a strict schema or a
  model it refused) ride as `failure` (`AppAsks.Failed`, clipped as `reply`), so its author can fix it; the
  published origin's viewers never read them, and the log keeps them either way.

## App sign-in

`AppViewers`; wire in `Domain/Apps/AppSignIn`; the accounts side in `accounts.md`, "The app sign-in".

- **A sessionless page** is a 302 to `{PublicBaseUrl}/accounts/app-sign-in` with a fresh state, also set as
  `__Host-app-sign-in`; the origin is the Host with the proxy's `X-Forwarded-Proto`. Empty `PublicBaseUrl`
  sends nobody: the 404.
- **The callback** `/_app/sign-in`: the state must equal the cookie **before the code is redeemed** (a code
  sent to another browser is neither believed nor spent); the code is redeemed **for this origin**
  (`IAppSignIns.RedeemAsync`) and **the verified assertion's claims alone** (`AppChannels.Apps`) name the
  person, household and `sid`; then 303 to `AppSignIn.SamePath(path)`. A refused code is 400 and never
  redirected again; an outage or an unverifiable assertion 503. Cookies are `Secure; HttpOnly; Path=/;
  SameSite=Lax`, no `Domain`.
- **Renewal** after `Sessions:AssertionKept` (at most half the accounts `AssertionLifetime`,
  conformance-checked), via `IAppSignIns.RenewAsync`: nobody, a suspended household, or another person or
  `sid` **ends the session**; an outage or unverifiable answer **is a 503 and ends nothing**.
  `EndedAppSessionsListener` ends every session of a person on `SessionsEnded`.

## The app read API

A built app reads the household as its viewer (wire in `Domain/Apps/AppReads`; the client library is
`AppOriginFiles.Library`, whose refusal is `apps.ReadError` carrying the envelope).

- **`POST /_vfs/<operation>` on the origin** (`AppPages.ReadAsync`), one JSON object of the tool's arguments
  (≤ 64 KiB), **from a page of that origin alone** (the `Origin` authority is the Host: sibling apps are
  same-site and carry this cookie). Settled as a page is, then forwarded (`AgentReads`) with **the reads of
  the origin's own manifest** — the served commit's, so a tree edit widens nothing until published, and no
  more than approved while shared — the building agent from `app.meta.json`, and the session's assertion,
  renewed first if due. Answered `no-store`; an agent unreachable or not believing this server is a 503
  `transient_dependency`.
- **The agent's route** `POST /internal/app-reads/<operation>` (`AppReadsApi`, never routed by Caddy)
  believes **the apps server's own credential** (`MCP_SECRET_APPS`; the agent API's secret opens nothing
  here) for the app, and for who reads **the viewer's `apps` assertion** (a person) or, from a check, the
  agent's own call token (`AppReads.CheckCallTokenHeader`), never both; no other header. Then, per read
  (`AppReader`), the household's standing as a turn's (`HouseholdStanding`: a refusal `permission_denied`,
  unreadable `transient_dependency`), the registry, and `AppRead`. 200 the tool's JSON or the bytes, 422 the
  envelope, 401 not believed — what `AgentReads` passes on.
- **What is read** (`AppRead`): each operation the file tool's own call (`VfsCall.ToolAsync`), the blob the
  bridge's byte read (`vfsBridge:maxFileBytes`). `permission_denied`: an operation outside
  `AppReads.Operations` (nothing that writes is reachable), a mount point (resolved, else the first segment)
  not in `reads` — **checked per request**, since the cached registry is shared by every app of that agent
  and viewer — and a tool the agent is not offered or whose whitelist would ask (`ReadRegistry.Offered`,
  `Permission`). A malformed argument is `invalid_argument` (`ToolError.CodeFor`); a read mount missing
  while a household server is offline is `transient_dependency` naming it; an agent the household no longer
  has refuses every read.
- **The cache** (`ReadRegistryCache`): one registry per (household, agent, person), single-flight, leased
  per read; a stale one (`IsStaleAsync`) is rebuilt, one idle `appReads:registryIdle` let go, an evicted one
  disposed when its last lease ends, a failed build never kept.
- **Wiring** (`AGENTURL`, the credential pair, the route off the proxy) is conformance-checked.

## Checks

`./check` (`AppChecks`) opens the preview in a headless Chromium of its own as the turn's person, in the
`apps-browser` container, never in `mcp-apps` (whose image has the Playwright driver alone).

- **What went wrong is in its output** (`AppActions.CheckSummary`): per viewport, the counts, then failed
  assertions, console errors (each led by where it came from, the preview's own by path: Chromium names a failed
  load's address nowhere else), failed requests, policy violations, refused reads and failed asks, a line each (first line,
  clipped, capped); a viewport that saw exactly what an earlier one saw names it instead. The agent reads
  `report.json` for the rest, never to learn what failed.
- **A refused read is not a failure**: a `/_vfs/*` read on the preview answered 422 (the tools' envelope)
  is a `refusedReads` entry (operation, path, code, message), and Chromium's console line for that address
  is dropped, since the page may well have shown it as it should. Any other answer of 400 or worse there
  (no session, nobody answering) stays a failed request.
- **Nor is a failed ask**: a `/_ask` on the preview answered 400 or worse **with the tools' envelope** (any
  status: 400, 410, 422, 429, 503, 504) is a `failedAsks` entry (status, the ask's `text` when sent as JSON,
  code, message, hint, and the preview's `reply` or `failure`), its console line dropped, a summary line
  `failed ask: <code>: <message>` with what the agent said or the reply beside it; an answer that is no
  envelope (the origin's 404) stays a failed request. Neither changes the exit code.
- **A check's asks are real turns** (`AppTurnAgentTests`, `AppsAsksTests`): the page asks as the check's
  person on its call token, against the tree's manifest and instructions, counted toward the app's cap,
  each waited on no longer than the check has left (`Checks:Settle` and every viewport reloading the page
  mean an ask made on load is made once per viewport).

- **It reads and asks as the call's own token** (`FileSystemCaller.CallToken`; `CallHouseholdConformanceTests` keeps
  every other reader out), on an app session of its own (`AppSessions.OpenCheck`, with the call's
  `call.Household` for its asks' cap; a call proving no household runs no check): the preview only, never
  renewed, ended with the check. The agent believes the token as **its own signature for the apps server,
  naming a person** (`CheckCallVerifier`: its `SigningKeyStore`, audience
  `TokenAudiences.CallTo(sharedServers.apps)`; the guest, nobody, another server's, or both headers are
  401 for a read; for an ask, both together, or any of those, is refused `not-verified`,
  `person-assertions.md`, "The one exception"). Nothing is minted, so `Checks:Deadline` is at most the agent's `callTokens:refreshMargin`
  (`DeploymentConformanceTests`).
- **The browser** (`CheckBrowser`): full Chromium (the headless shell ignores the secure-origin flag), **one
  per check**, **always in Chromium's sandbox** — no `--no-sandbox` fallback; a host that cannot sandbox
  fails the check — with app origins mapped to the pages listener by a host-resolver rule, never DNS. An
  empty `Checks:Browser:Endpoint` launches it here; set, it connects to the Playwright server.
- **`apps-browser`** (`DeploymentConformanceTests` pins it): `run-server --unsafe` (without it the server
  drops `args` and `chromiumSandbox`) on **port 10080, a Chromium-restricted port**, so no page can dial the
  launcher; **no secret, setting, volume, port or capability**, read-only; on a network with `mcp-apps`
  alone, **`internal: true`** — no route to the LAN, the host or the internet, nothing shared with Redis,
  the agent or accounts. Its seccomp profile is Docker's default plus namespace `clone`/`unshare` and
  `chroot`; a host refusing a user namespace is fixed on the host, never with `--no-sandbox`.
- **The egress proxy is the browser's one way out** (`CheckEgress`): **CONNECT only**, the target resolved
  **once** and refused unless **every** address is public (`EgressAddresses.IsPublic`, default deny), then
  **dialled at a judged address, never the name again** (no DNS rebinding); a redirect's hop is a new
  CONNECT. A raw listener (`Listeners.Egress`) outside the MCP gate and the pages listener, bound on
  `mcp-apps`'s address facing the browser server, so no other container gets out through it. The bypass list
  sends app origins direct and loopback through the proxy (the later rule wins, so a `.localhost` apps
  domain still goes direct).
- **What the page reaches**: a context route lets through the preview's origin, `Cdns` and the tree's
  `connect` alone (else `blocked`), and **nothing is kept of a page that is not the preview** — screenshot,
  snapshot, console and assertions only while the main frame is on it. The policy is never bypassed.
  Assertion files (`checks/*.js`) are read here as text and evaluated in the page, so nothing of the app
  runs outside the page.
- **Output** goes under `BuiltAppStore.ChecksDirectory` (never committed, served or counted), swapped in
  whole as `latest/`, shown read-only at `/<slug>/.checks/latest/`. Concurrency limits (`PerHousehold`,
  `Total`) refuse `permission_denied`, never queue; the deadline closes the browser.

## Runtime errors

What a page met in somebody's browser (wire in `Domain/Apps/AppErrors`), for the agent to read.

- **The client library reports** uncaught errors, rejections and failed reads to `POST /_errors`; a thrown
  value that is no Error is named by its type alone, so what the app held never leaves the page.
- **`POST /_errors`** (`AppErrorReports`): from a page of that origin alone, settled as a read (the one
  404). **The body is the page's word only for what went wrong**, parsed strictly (`AppErrorReport.Parse`);
  **the server stamps the rest** (time, user agent, viewer, origin, `retryable` from this side's taxonomy);
  `Errors:PerSessionPerMinute`, then 429.
- **Only the author's own report keeps the page's text.** A member's is **reduced** (`AppErrorReduction`):
  kind, a standard error name, frames and route only where they name a file the origin serves (in the
  origin's own spelling), a read's operation, mount point (only one the manifest reads) and code, and
  `reduced` (`AppErrorReduction.Note`). No message, stack, user agent or hint: a bounded numeric channel, so
  an error carries nothing past the approved reach.
- **Kept** at `<app>/errors` (`AppErrorLog`: newest first, the newest `Errors:Kept`, written unescaped since
  it is never served; never committed or counted; a deleted app's report is dropped), shown read-only at
  `/<slug>/errors`.
- **The operator sees** an `AppRuntimeError` `ErrorEvent` under **the app's own household** — a member's
  without their text. **A check's report is answered 204 and kept nowhere** (`report.json` already shows
  it).
