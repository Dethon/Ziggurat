---
paths:
  - "Observability/**"
  - "Dashboard.Client/**"
  - "Infrastructure/Metrics/**"
  - "Domain/DTOs/Metrics/**"
  - "Domain/Metrics/**"
---

# Observability Architecture

## Publishing

- **`IMetricsPublisher.Publish` is fire-and-forget**: it cannot fail, block or be observed. Do not "fix" it back into a `Task` (`docs/adr/0002-metrics-publishing-is-fire-and-forget.md`). The transport that can throw is `IMetricSink` (`RedisMetricSink`, `HttpMetricSink`, in `Infrastructure/Metrics/`), and `BufferedMetricsPublisher` is the only publisher a host registers.
- Becoming a metrics-publishing host is **one call**, `services.AddMetricsPublishing(serviceName)` (sink, buffered publisher and `HeartbeatService` together), so no host resolves a bare sink as its publisher or publishes without a heartbeat; `MetricsRegistrationContractTests` boots each host's registration.
- Measure a span with the scope `publisher.MeasureLatency(stage, conversationId, agentId, model)` (`LatencyScope`), not a stopwatch; it publishes on an early return too, so open it *after* any guard that can return first.
- Optional publisher parameters coalesce once to `NoOpMetricsPublisher.Instance` where stored; no type null-checks before publishing.

## Collection

Events reach the Redis Pub/Sub channel `metrics:events`. A household's own server reaches no deployment Redis: given `METRICS__URL` (`MetricsFeedConfig`) it posts to its metrics back-call (`HttpMetricSink`) and the relay publishes it with `MetricEvent.Origin` set to that server's synthetic address (`.claude/rules/tunnel.md`, "Back-calls"); a deployment service's events carry no `Origin`. `MetricsCollectorService` aggregates into Redis and forwards live events to `/hubs/metrics`; `MetricsQueryService` serves the reads. The dashboard loads history over REST and live updates over SignalR.

## Households

Every event names the household it was for in `MetricEvent.TenantId`; null is the **deployment's own** (a keep-alive, a relay refusal, a deployment service's heartbeat, a probed container). Who says:

- **A kit server never does.** The relay stamps a back-call's event with its tunnel's verified household (and `Origin`) over whatever the server posted (`BackCallGatewayTests`), so a household cannot bill its use to another.
- **What acts for a household carries a publisher for it**, not a household per call: `AgentMetricsPublisher` (`AgentSpec.TenantId`), a conversation group's `metricsPublisher.ForHousehold(tenant)`, recall and extraction's `MemoryExtractionRequest.TenantId`, Telegram dictation's sender's account, the chat client's `DictationScope.TenantId`.
- **A model call's payer says it** (`ModelPayer.TenantId`): Jev's usage off `JudgmentCaller.Payer` (a shared server's from the call's verified identity), the memory models' `PaidChatClients`; dreaming publishes through the household of the walk it is on.
- **A shared server's failed tool call says it from the verified call**: `CallToolErrorFilter` publishes a `ToolCallFailed` `ErrorEvent` (`Service` the server's audience) under the household of `CallTurn.Identity`, **never a household the `_meta` names**; a call for nobody, a refused call token (`CallTokenGate`) and a server's background errors are the deployment's (`CallToolErrorFilterTests`).
- **A built app that broke in a person's browser says it from the app**: the apps server publishes an `AppRuntimeError` `ErrorEvent` under the household its own metadata names, never one the page sent (`.claude/rules/apps.md`, "Runtime errors").

**An agent is keyed by its id** (`MetricEvent.AgentId`), which a rename leaves alone, with `AgentName` beside it as a label (stamped by `AgentMetricsPublisher` from `AgentSpec.MetricsAgentName`, and by memory recall and extraction); `MetricsQueryService` labels each id with its latest name.

**A person is keyed by their account** (`TokenUsageEvent.UserId`, `ContextTruncationEvent.UserId`), `Sender` a label. Every model call names the account it is billed to: a turn's off its context (`ChatMessage.GetAccountId()`), a request no turn is behind on the message (`SetAccountId`), Jev's on `JudgmentCaller.UserId`. The totals' `tokens:byUser:` field is the account (the sender for a call with none). A label is never the id nor a `ServiceSenders` word (`PersonLabels`, `PersonNames`).

**Roll-ups are kept per scope** (`MetricKeys`): a household's id, or `deployment`. Daily keys are `metrics:{family}:{scope}:{yyyy-MM-dd}`, recent errors `metrics:errors:recent:{scope}`, every scope written in `metrics:scopes`. The household migration's `MetricRollups` step tells a pre-household key by its three segments, so **a family name never holds `:`**. A read is handed back as the household of its key, not its body.

**Every read takes `?household=`**: an id, `deployment`, or absent for everybody's; `/api/metrics/households` lists the scopes.

**The dashboard's household filter** (`HouseholdFilter`, picked in `HouseholdPicker`): `MetricsApiService` joins `household=` onto each read, and since **the hub sends everybody's pushes**, `MetricsHubBinder` drops one whose household (`MetricEvent.TenantId`, `ServiceHealthUpdate.Household`; null is `deployment`) is not the picked one, asked when the push is applied. Choices are the scopes joined with the household list (`HouseholdsAdminService.ListAsync`). A change reloads every family over its own page's range (`DataLoadEffect.ReloadAsync`, under the push hold). Not saved.

**Health is keyed by household too**: a roster member is the bare service name for the deployment's (`metrics:health:{service}`) and `{household}:{service}` for a household's server (`ServiceHealthRegistry.Member`/`Parse`); `ServiceHealthResult`/`ServiceHealthUpdate` carry `Household`, and the client keys a tile on service and household.

**Lemonade's use** is `LemonadeUsageEvent { Use, Amount, Channel }`, counted under the household's totals as `lemonade:transcription:ms`, `lemonade:speech:chars` and `lemonade:embedding:count` (`LemonadeTotals`), read as `/api/metrics/lemonade` and `/api/metrics/lemonade/by-household`. Each use is counted at the one place every caller goes through: `MeteredAudioTranscriber` around the transcription client every transcribing host registers, `MeteredTextToSpeech` (outermost on voice's `ITextToSpeech`; characters, once audio starts arriving), `RecordEmbedded` beside each memory embedding. The speech typist is never counted.

## Who reaches the dashboard

Only the operator: the dashboard host's Caddy block puts `forward_auth` to the session check (`?role=operator`) in front of everything, a signed-out page going to sign-in on the main host and back (the dashboard's origin is one of the accounts service's `ALLOWEDORIGINS`) (`DeploymentConformanceTests.TheDashboard_AdmitsOnlyTheOperatorsSession`), and `OperatorGate` asks again before routing: the assertion must verify against `Accounts:JwksUrl` for channel `dashboard` with the `operator` role, else 401/403; **the plain headers are never read** (`.claude/rules/person-assertions.md`). So `localhost:5003` straight answers 401; the dashboard E2E signs an assertion with the accounts test key and serves its JWKS from the test process.

- The manifest is a file of the web root (`Dashboard.Client/wwwroot/manifest.webmanifest`), **never a route**: the browser fetches it with no assertion (`DashboardManifestTests`).
- **One kind of request is nobody's**: a file the client's web root holds, asked for by something that is not a page being opened (no `Sec-Fetch-Mode: navigate`, no `text/html` in `Accept`, not under `/api` or `/hubs`) — Caddy's `forward_auth @checked`, the gate's `ClientFile` — because an installed dashboard replaces its service worker with signed-out fetches. `OperatorGateTests.TheClientsOwnFiles_AreServedSignedOut_AndNothingElse` and the smoke tier hold it. A hub start refused 401 reopens the page (`ISignIn`) rather than retrying.
- Do not add a site-level `request_header -X-Ziggurat-*` strip: Caddy orders it after `forward_auth` and it would blank what `copy_headers` set.

**`/api/usage`** (`HouseholdUsage.Path`) is an owner's usage, asked only by the accounts service's admin API (`.claude/rules/admin-owner-pages.md`, "Usage"). `OperatorGate` holds that exact path to an assertion for **`AppChannels.Usage`** with the `owner` role (a channel no proxy block names) and keeps the owner (`UsageOwner`) for the route, which answers that household and **reads no `?household=`**; everything else keeps the dashboard channel and operator role.

`/accounts/admin/operator/*` on that host is not the dashboard's: the household list page (`/households`) calls it same-origin, and the proxy asks again for the admin channel and passes it to the accounts service (`.claude/rules/admin-operator.md`, "The household list"). E2E: `DashboardAdminE2EFixture` (accounts service + Caddy mirroring the host); `DashboardE2EFixture` keeps the metric pages on the bare container.

## The dashboard's live connection

`MetricsLiveConnection` alone owns being live: bind, start retrying until it succeeds, publish the status, catch up — the last two also on the transport's own reconnect. The layout calls connect and catches nothing.

- **The seam is `IMetricsHubConnection`**, one generic receive verb keyed by wire method name plus three lifecycle events. Never hand-write a named registration method.
- **`MetricsRetryPolicy` is the one schedule** and never returns stop; it drives both automatic reconnection and the first-start loop (which automatic reconnection never covers).
- The started latch records a start that **succeeded**, never one attempted.
- **`ConnectionStore` is the only source of connection status** (connecting, live, reconnecting; no permanent disconnected). A failed page-load request is not a lost connection. The indicator is in the layout and keeps `.connection-status`, which the overview's E2E reads.
- **`MetricsCatchUp` keeps the user's group-by, metric and time choices**: it walks the family table for the range each family already holds. The first epoch skips it unless the first load failed.
- **A page load stamps only the families that page draws** (`DataLoadEffect.LoadAsync`; `MetricFamilyTable.OverviewFamilies` for the Overview); every family is still reloaded over its own page's range.
- **A time pill is a rolling window** (`TimeWindows`: 24h, 7d, 30d), sent as `?days=N` and resolved by `MetricDateRange.Last` against the server's clock on every read, so a reload or catch-up reads the window as it stands then. `?from=`/`?to=` dates stay for whole-day callers (`/api/usage`, Lemonade). Daily keys outlive the 30-day window by a day.
- **The summary is added up from events** (`MetricsQueryService.GetSummaryAsync`), never the totals hash: the hash counts whole days and kept cost in whole ten-thousandths of a dollar.
- **Only the latest load writes** (`MetricFamily.LoadEventsAsync`, `OverviewFigures`).
- **Every event list is appended through `EventWindow.Append`**, bounded at the load's length or `EventWindow.Cap`.
- **The catch-up holds pushes while it runs**: `MetricsHubBinder.HoldPushes` queues, `ReleaseHeldPushesAsync` replays against the snapshot, skipping what it already holds. Any change to the catch-up must keep both ends of that pairing. The binder is otherwise only the push-to-store mapping.

## Health tiles

`ServiceHealthRegistry` scores the roster by last registration, not health; reachability is the TTL'd `metrics:health:<service>` key. `HttpHealthProbeService` probes `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response as up; a target re-registers every cycle, so a down service stays a red tile and a retired one ages off after `Retention` (7 days).

**Every service is a tile** (user, 2026-11-12): the deployment's servers that publish no metrics (Idealista, scheduling, timers, Sugary, accounts) are `HttpProbes` targets; every household server on a tunnel but the Lemonade forwarder posts its heartbeat on its metrics back-call, registering `AddMetricsPublishing` only when `METRICS__URL` is set (`TenantKitConformanceTests`); a household's helper containers are probed by the server beside them (`MetricsFeedConfig.Helpers`, `METRICS__HELPERS__<name>=<url>`, `HelperHealthProbes`) and beat under their own name on its feed. **A household's own Lemonade box is a tile of that household's** (user, 2026-11-13), read from what its Keys page saved, never the `.env`: the agent's `LemonadeBoxHealthProbes` asks each box's `/health` on the pool its turns ride (a kit's box through its forwarder's tunnel) and beats `lemonade-chat-host` under the household; the deployment's own box (`LemonadeChat:ApiUrl`) stays Observability's probe.

## Household privacy

**A household's own fire is recorded without its prompt.** `ScheduleExecutionEvent.Prompt` is empty for every household but the home (`ConversationGroup.IsHomeAsync`; unreadable counts as not the home): the Schedules page is the operator's, and a household's schedule is as private as the conversation that wrote it (user's decision, 2026-11-05); its id, duration, outcome and error still show. **A household's memory candidate is recorded without its words**: `MemoryJudgmentEvent.Candidate` is the home's alone (`.claude/rules/memory-architecture.md`); its scores, category and verdict still show.
