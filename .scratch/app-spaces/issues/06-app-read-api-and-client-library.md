# 06: App read API and client library

Spec: `.scratch/app-spaces/spec.md` ("The app read API (the agent)", "Serving built apps").

**What to build:** A built app reads the household's data as its viewer. The served client library offers `apps.read`, `apps.glob`, `apps.info`, `apps.search`, `apps.query` and blob reads on virtual paths, with the tools' result shapes. These go to `/_vfs/*` on the app's origin. The apps server authenticates the viewer by the app session and forwards the read to a new internal agent route. It sends both the agent API's secret, which carries the app's id, building agent and `reads`, and the viewer's current `apps` assertion, which carries the person and household. The agent answers through the `VfsCall` meaning layer, over a read registry for (household, building agent, viewer) that is cached briefly.

**Blocked by:** 04, 05

**Status:** ready-for-agent

- [x] Two viewers of one app each get their own `/sugary` rows.
- [x] A read outside the manifest's `reads`, any operation that changes anything, and a read the building agent's whitelist would ask approval for each get the tools' permission envelope.
- [x] A request without the secret, or without a believable `apps` assertion, is refused. The agent believes no other header.
- [x] A withheld feature or an offline own server answers as a turn would, with the tools' unavailable envelope for offline.
- [x] A building agent the household no longer has refuses every read and names the agent.
- [x] The bridge's per-file byte ceiling applies.
- [x] The registry cache is rebuilt when the household's servers change.
- [x] Over-the-wire tests follow `VfsBridgeOverTheWireTests`, against in-process MCP servers.

## Comments

**Landed (branch `app-spaces`).** The wire is spelled once in `Domain/Apps/AppReads` (`/_vfs/<operation>` on
the origin, `/internal/app-reads/<operation>` on the agent, `AppReadRequest`/`AppReadScope`).
- *Client library*: `AppOriginFiles.Library` (`/_app/apps.js`, injected) and `AppOriginFiles.Module`
  (`/_app/apps.mjs`), one expression: `read`, `glob`, `info`, `search`, `query`, `blob`, `ReadError`.
- *Apps server*: `AppPages.ReadAsync` and `AgentReads`.
- *Agent*: `Agent/App/AppReadsApi`, `Infrastructure/Agents/AppReader`, `ReadRegistryCache`, and
  `Domain/Tools/FileSystem/Bridge/AppRead` over the new `VfsCall.ToolAsync` (the gate is now `VfsCall.Unasked`).
- *Standing*: the turn's rule moved out of `ConversationGroup` into `Domain/Monitor/HouseholdStanding`
  (`StandingRefusal` public), and both use it.
- *Registry*: `ReadRegistry` gained `Permission` (the whitelist), `Offered` (`FileSystemToolFeature.Offers`,
  now also the exec tool's) and `Call(viewer, maxFileBytes)`.

Contracts are in `.claude/rules/apps.md` ("The app read API"), with pointers in `virtual-filesystem.md`,
`sandbox.md`, `person-assertions.md` and `channels.md`.

Tests:
- `AppReadsApiTests` (27, over the wire against Sugary, text servers and a roles probe in process): two
  viewers' own rows; every operation's tool shape; undeclared mount (with two apps sharing one registry);
  eight non-read operations; whitelist and offered-tool refusals; withheld feature; offline own server and
  its return; the byte ceiling; removed agent, cold and after a cached read and added back; seven
  unbelieved requests; identity headers never read; suspended, disabled and unreadable standing; per-read roles.
- `ReadRegistryCacheTests` (4): sharing, a rebuild on server change, a lease held across eviction, idle eviction.
- `AppReadTests` (2), `AppsReadsTests` (4, the origin forwarding to the real route), a Chromium case in
  `AppsServingInABrowserTests`, `InjectorModuleTests.ConfigureAgents_AnswersAppReads`, and
  `DeploymentConformanceTests.TheAppsServer_ReadsThroughTheAgent_WithTheCredentialTheAgentHoldsForIt`.

**Decisions and deviations:**
- *The credential is the apps server's own MCP credential, not the agent API's secret*, against the
  ticket's and spec's letter (the spec line is amended). The agent already holds it as
  `MCP__SECRETS__mcp-apps` for the server `sharedServers.apps` names, and checks it the way `RelayedCalls`
  checks a shared server. `AGENTAPI__SHAREDSECRET` is also held by SexyTime's server and opens
  `/api/agents*`, `/tunnel` and `CALLERS__agent`. Reusing it would have let SexyTime's server read any
  household's data with a person's `apps` assertion, and handed the apps server those routes. This way no
  new secret exists.
- *Status codes*: 200 is a value (the tool's JSON or a blob's bytes), 422 the tools' envelope (every
  refusal: gate, standing, removed agent, the tool's own error), 401 a caller not believed. On the origin,
  503 is the unavailable envelope when the agent is unreachable or did not believe the apps server, and
  the one 404 is anyone who may not open the app.
- *Envelope codes*: an undeclared mount, a non-read, a whitelist-gated or unoffered tool, a suspended
  household, a disabled member and a removed building agent are all `permission_denied`. A standing that
  cannot be read is `transient_dependency`.
- *The ceiling* (`vfsBridge:maxFileBytes`) bounds `blob`, the bridge's whole-file read. Text reads, globs,
  searches and queries are bounded by the tools' own windows and caps.
- *Mounts*: what `VfsCall` serves, so the sandbox (a shell mount) and outposts are never readable, whatever
  `reads` names. `reads` is checked against the resolved mount point, else the path's first segment, per
  request, since the cached registry is shared by every app of one agent and viewer.
- *The cache key* is (household, agent, user). Each read is signed as the identity its own assertion
  verified, with its roles now. `appReads:registryIdle` is 2 min. Single-flight builds, a lease per read,
  a stale check on every lease, disposal only after the last lease, a failed build never kept.
- *Library shape*: a classic script, so `globalThis.apps` exists before the app's own scripts (a module tag
  is deferred). It also ships as an ES module at `/_app/apps.mjs`, the same object.
  `search(query, {directoryPath | filePath, ...})` mirrors the tool's arguments. The origin's 404 reaches
  app code as `authentication`.
- *The worker* already passed `/_vfs/` by; the library also fetches `no-store`.

**Known imprecision:** a declared mount that is not mounted while any household server is offline
answers `transient_dependency` naming the offline servers. This includes a mount withheld by a feature,
which would otherwise answer `not_found` as a turn does: a server's mount point is known only once it is
reached.

**Not verified:** images were not built. The smoke tier, the E2E stack and the full suite were not run.
Tunnel back-calls cannot reach the route (`BackCallTargets` grants fixed paths only), and a host run's
`sharedServers.apps` (`localhost:6018`) resolves to the `localhost` credential the apps server presents.
Both were checked by reading the code alone.

**Deploying this needs:** no new secret. Rebuild `base-sdk` first (Domain changed), then `agent` and
`mcp-apps`. `AGENTURL: http://agent:8080` is in compose already. Reads answer only once the agent runs
this build.

