# Glucose in Sugary, and one `/watchers` for every watch

Status: ready-for-agent
Branch: `sugary-glucose`, from `master`

## Problem Statement

Her glucose sensor (FreeStyle Libre, shared through LibreLinkUp) reaches the agent only through the
home's Home Assistant integration: one household, one person, a sensor entity the agent reads with
`./history`. Sugary, where she logs her meals and insulin, knows nothing about the curve those meals
and doses make. A household without Home Assistant has no glucose at all, and the app cannot show
her the reading, the day's curve or how her last two weeks went.

Glucose alarms live as Home Assistant automations under `/ha/watches`, so they exist only where a
home runs HA, and they can only watch what HA sees.

## Solution

**Sugary reads the sensor itself.** Each person links their own LibreLinkUp follower account in the
Sugary app. Sugary polls it about once a minute, keeps every reading in her log, and keeps the
history from then on. A one-off operator import brings in what a household's Home Assistant already
recorded. The app has a Glucose tab: the current reading with its trend and age, a curve over a
day, a week, two weeks or ninety days, and the statistics a diabetes clinic reads — time in range,
below and above range, mean, GMI and CV. The agent reads the same readings as tables under
`/sugary` (`glucose/<YYYY-MM>.parquet`), asks them with `query`, and analyses them in the sandbox
with duckdb or polars, beside her meals and doses.

**Every watch lives at `/watchers`.** A watch is still a `watch.json` file with triggers, effects,
`once`, `enabled` and `deliverTo`, at `/watchers/<id>/watch.json`. Which server keeps it depends on
its triggers: a glucose trigger makes it Sugary's, anything else stays a Home Assistant automation.
The agent gains no tool and no new kind of file: the HA watch subtree moves from `/ha/watches` to
`/watchers`, and `/ha/watches` is gone.

This works because:

1. **A server may serve several filesystems.** The file tools (`fs_*`) take the filesystem they
   act on, and a server publishes each filesystem's operations in its resource. HA serves `ha` and
   `ha-watches`; Sugary serves `sugary` and `sugary-watches`.
2. **A mount point may be a union.** Two filesystems published at one mount point, each declaring
   itself a union member, are merged by the agent generically: listings combine, an existing entry
   is answered by the member that holds it, and a new entry is routed by a claim the member
   publishes — the values it owns at a JSON pointer in the written file, or "everything nobody
   claims". The agent has no watch-specific code.

**Glucose watches** fire on a threshold crossing, a rate of change, a predicted low or high, or the
sensor's state (readings stopped, the link failing, the sensor ending). Their effects are a
**prompt** the agent runs and delivers, as a home watch's is, and an insistent **announcement** on
the household's satellites, rung through the voice relay exactly as a timer rings — and, with no
target or every satellite offline, rung as an insistent alert on the owner's chat and Telegram, as a
target-less timer is. A glucose watch is the business of the person whose sensor it watches, the
person who wrote it, and whoever holds "Reads everyone's Sugary log"; nobody else in the household
lists it.

## User Stories

1. As a person with a Libre sensor, I want to link my LibreLinkUp follower account in Sugary, so that my readings reach my log without Home Assistant.
2. As a person linking an account, I want to be told plainly when the credentials are refused, the account has no connection, or Abbott wants terms accepted in the app, so that I know what to fix.
3. As a person whose follower account sees several people, I want to choose which connection is me, so that someone else's readings never land in my log.
4. As a person, I want my credentials kept sealed and never shown back, so that a database copy does not leak my LibreView password.
5. As a person, I want to unlink my account, so that Sugary stops reading my sensor and forgets the credentials (my readings stay).
6. As a person, I want Sugary to keep every reading from the moment I link, so that my history grows past LibreLinkUp's twelve hours.
7. As a person, I want the app to show my current reading, its trend arrow and how old it is, so that a glance tells me where I am.
8. As a person, I want a curve over 24 h, 7 d, 14 d and 90 d with my target band, so that I can see patterns.
9. As a person, I want time in range, time below (< 70, < 54), time above (> 180, > 250), mean, GMI, CV and the share of the period with readings, so that I see what my clinic sees.
10. As a person, I want to set my target range and whether I read mg/dL or mmol/L, so that the app speaks my units.
11. As a person, I want the app to say when the link is failing and since when, so that a gap in the curve is never a mystery.
12. As the operator, I want to import a person's glucose history from a household's Home Assistant, so that the ninety days HA kept are not lost.
13. As the operator, I want an import to skip what the log already has, so that running it twice changes nothing.
14. As a person asking the agent, I want it to read my glucose beside my meals and doses, so that it can tell me what a meal did.
15. As a person asking the agent for an analysis, I want it to work the readings in the sandbox with duckdb or polars, so that it can chart and compute properly.
16. As a reader an owner granted everyone's log, I want the agent to read another person's glucose under their folder, so that I can help them.
17. As a person, I want to tell the agent "warn me if my sugar drops under 70", so that a glucose watch is written without my knowing any file.
18. As a person, I want "wake me in the bedroom if it drops under 60 at night", so that a low at night rings on the bedroom satellite until I stop it.
19. As a person without satellites, I want an urgent glucose alarm to ring on my phone's chat and Telegram, so that it is still heard.
20. As a person, I want to be warned when my glucose is falling fast or predicted to go low soon, so that I can act before the low.
21. As a person, I want to be warned when my sensor stops sending readings, so that a silent sensor is never mistaken for a calm night.
22. As a person, I want a LibreLinkUp outage to be told apart from a silent sensor, so that a provider hiccup does not ring every bedroom as "no readings".
23. As a person, I want to be warned before my sensor ends, so that I have a new one ready.
24. As a partner granted her log, I want a watch on her glucose that warns me, so that I can help when she's low.
25. As a person, I want my glucose watches hidden from household members who were not granted my log, so that my health stays mine.
26. As a person, I want home watches and glucose watches listed together at `/watchers`, so that "what are you watching for me?" has one answer.
27. As a person, I want to pause, change and remove a glucose watch the way I do a home watch, so that nothing new needs learning.
28. As a person, I want a glucose watch to fire once per crossing and to honour "for N minutes", so that one noisy reading does not alarm me.
29. As a person, I want a restart of the stack not to forget a watch's state, so that a restart neither re-fires nor loses a pending alarm.
30. As a person, I want imported and back-filled readings never to fire a watch, so that history arriving late does not ring old alarms.
31. As a household with Home Assistant, I want my existing home watches to keep working at `/watchers`, so that the move costs me nothing.

## Implementation Decisions

### Several filesystems per server (ticket 01)

- `AddFileSystemTools<TBackend>()` and `AddFileSystemResource<TBackend>()` may be called once per
  backend type on one server. Tools are registered once per operation the union of the server's
  backends overrides; every `fs_*` call carries `filesystem` (the published name) and is dispatched
  to that backend; an operation the named backend does not override answers `unsupported_operation`.
  A call naming no filesystem goes to the server's first registered filesystem, so an outpost binary
  or a kit server from before keeps working.
- The resource publishes the filesystem's **operations** (`operations`: the `FileSystemOperations`
  keys its backend overrides). Discovery uses them per mount; a resource with none (an older server)
  falls back to the server's advertised tools, as today.
- `McpFileSystemBackend` sends `filesystem` on every call, not only the blob calls.
- `FileSystemServerConformanceTests` becomes per filesystem: published operations = overridden
  operations, and the server's tools = their union.
- The `Describe*` text of a shared tool is the first filesystem's; the agent filters raw `fs_*`
  tools out anyway, and a domain tool's per-mount prose comes from the mount description.

### Union mounts (ticket 02)

- A resource may declare `union`: `{ "routeBy": "<JSON pointer with * for array items>", "claims":
  [values] }` or `{ "routeBy": "…", "fallback": true }`. Only members that all declare `union` share a
  mount point; a non-union mount point keeps first-wins and the shadow log.
- `UnionFileSystem` (Domain, generic) sits in the registry where the second member arrives. It is
  an `ICallerBoundBackend` that binds every member to the caller. Its capabilities are the union of
  its members' operations; its description joins theirs; `ShellReach`, `Workspace` and
  `IsLandingTarget` are null/false (a member declaring otherwise is refused as a union member).
- **Top-level entries belong to one member.** Glob and search merge members' answers (sorted, then
  capped as today); info/read/edit/remove/move-out of `<entry>/…` go to the member whose listing
  holds `<entry>`. A member that refuses a listing (`permission_denied` for a nobody caller) adds
  nothing; one that fails (unreachable, transient) adds nothing and its name is reported in the
  glob result's defaulted `unavailable` list, so "no watches" is never said for "Sugary didn't
  answer".
- **A new entry is routed by claims.** A create of `<entry>/<file>` where no member holds
  `<entry>` reads the content at `routeBy`; every value found must be claimed by one member, which
  takes the write; no claimed value sends it to the fallback member; values claimed by two members,
  or by one member alongside unclaimed values, are refused `invalid_argument` naming which value
  belongs where. Content that is not JSON, or with nothing at the pointer, goes to the fallback. A
  create of an entry another member holds goes to that holder.
- `VfsVirtualPathConformanceTests` and the bridge test cover a union mount.

### Home watches move to `/watchers` (ticket 03)

- The watch subtree leaves `HaFileSystem` for its own backend, `HaWatchFileSystem` (name
  `ha-watches`, mount point `/watchers`, `union: {routeBy: "/triggers/*/trigger", fallback: true}`),
  whose paths are `<id>/watch.json` and `<id>/status.json`. Behaviour, automation rendering,
  metadata, delegation and the fire callback are unchanged; only the coordinates move.
- `/ha/watches` is not a path any more: `HaVfsPath` stops parsing it and `HaTree` stops listing it.
- `DelegatedWrites` and `WatchDelegation` key on `ha-watches`.
- The setup index's watches line lists the home's watches at `/watchers`; the HA guide stub and
  the `home-watches` skill say `/watchers`; snapshots move; `WatchScenarios` match `/watchers`.

### Glucose from LibreLinkUp (tickets 04–07)

- **Client.** `LibreLinkUpClient` (in Sugary): `product: llu.android`, `version: 4.16.0`, a
  `User-Agent` (none is a Cloudflare 403 page before the service, found on prod 2026-11-18),
  `Account-Id` = lowercase hex SHA-256 of the login's `user.id`, bearer token cached until its
  `expires` (about 180 days) and re-logged only on 401/expiry — never per poll. A login answering
  `redirect: true, region` logs in again at `api-<region>.libreview.io` and the link keeps the region.
  Body `status` 2 = credentials refused, 4 = a step to accept in the LibreLinkUp app, 920 = version
  too old (logged as an error for the operator). HTTP 429/430 honour `Retry-After` (backoff, never a
  tighter loop). `FactoryTimestamp` (UTC) is the instant of record; `Timestamp` (local, no offset) is
  ignored. `TrendArrow` 1–5 = falling fast, falling, steady, rising, rising fast; anything else unknown.
  A `FakeLibreLinkUp` (Tests) serves the wire for integration tests.
- **The link** (`GlucoseLink`, `IPersonal`, one per person): region, email, sealed password, the
  chosen `patientId`, unit (`mgdl`/`mmol`), target low/high (default 70/180), and its state: last
  success, last failure kind and since when. The password is sealed with AES-GCM under
  `Sugary:SealingKey` (a secret: `.env` placeholder, compose `${SUGARY_SEALING_KEY}`), associated
  data the owner's ids; it is never returned by any route. Linking validates by logging in and
  listing connections; one connection is chosen automatically, several are offered to choose.
- **The poller** (`GlucosePoller`, a `BackgroundService` shaped like `PenReminderLoop`) reads every
  live link once a minute (`Sugary:Glucose:PollSeconds`, appsettings), staggered, at most a few at a
  time: `/graph` gives the current reading and about twelve hours of 5-minute points. Readings
  (`GlucoseReading`, `IPersonal`: instant, mg/dL, trend, `source` = `current`|`graph`|`import`) are
  keyed by `(UserId, At)`; a graph point within 2.5 minutes of an existing reading is skipped.
  Sensor facts (`GlucoseSensor`: serial, activation instant) are kept as seen. After a poll the
  person's watches are evaluated (ticket 11).
- **The mount.** `/sugary/glucose/<YYYY-MM>.parquet` (one row per reading: `read_at`, `mg_dl`,
  `mmol_l`, `trend`, `source`), `/sugary/sensors.parquet`, and the tables `glucose` and `sensors` in
  `query`; `read_at_local` like every instant. The README says what each column is, that values are
  mg/dL (mmol/L = mg/dL / 18.016), her target range and unit, and gives the clinic's statistics as
  SQL a model can copy: time in ranges, mean, GMI (3.31 + 0.02392 × mean mg/dL), CV (sd / mean), the
  share of the period covered, and the rule that a statistic over less than 70 % coverage is said with
  its coverage. `people/<folder>/` carries theirs under the grant. The README no longer sends a glucose
  question to the Home Assistant mount.
- **The app** (ticket 08): a Glucose tab, routes under `/api/glucose` bound to the person as every
  route is. The link form, unit and range in its settings; the current reading with trend arrow and
  age (stale past 15 minutes said so); the curve with the target band; the statistics for the chosen
  period from a server route (one implementation, `GlucoseStatistics`, unit-tested against hand
  figures). Every word in the app's two languages; icons drawn, never typed.

### Import from Home Assistant (ticket 09)

- `SugaryCommand import-glucose --ha <url> --token <file> --entity <sensor id> --tenant <tid> --user
  <uid> [--days N]` reads the recorder's history in ten-day windows (`minimal_response`), then the
  long-term statistics' hourly means for the days before the history's first, writes readings with
  `source = import` (hourly means marked `import-hourly`, a lower resolution the README names), skips
  any within 2.5 minutes of an existing reading, and reports counts. Imported readings never fire.

### Glucose watches (tickets 10–13)

- **Files.** `SugaryWatchFileSystem` (name `sugary-watches`, mount `/watchers`, `union: {routeBy:
  "/triggers/*/trigger", claims: ["glucose", "glucose_trend", "glucose_forecast", "sensor"]}`),
  `<id>/watch.json` (read/create/edit/remove) and read-only `<id>/status.json`. Ids are per
  household. Fields: `name`, `triggers`, `conditions` (only `{"condition": "time", "after", "before",
  "weekday"}`), `effects`, `once`, `enabled`, `deliverTo`, `userId`, and per trigger `person` (a
  `people/` folder name; default the caller). `actions` effects and any other field are refused by
  name.
- **Who.** The subject is the person whose sensor it watches; watching another's needs the
  `ReadsSugaryLogs` grant at write. Listed for its subject, its writer and the grant's holders; a
  caller naming nobody is refused, as `/sugary` is.
- **Triggers** (mg/dL; mmol/L converted by the model, the skill says how):
  - `{"trigger": "glucose", "below"|"above": n, "for": {"minutes": m}}` — fires on the crossing into
    the condition after it has held `for`; again only after it left.
  - `{"trigger": "glucose_trend", "falling"|"rising": rate}` — mg/dL per minute over the last 15
    minutes of readings (at least 3), crossing semantics.
  - `{"trigger": "glucose_forecast", "below"|"above": n, "within": {"minutes": m}}` — linear fit of
    the last 20 minutes (at least 4 readings) projected `within` ahead, crossing semantics.
  - `{"trigger": "sensor", "state": "no_readings", "for": …}` — LibreLinkUp answered and the newest
    reading is older than `for`. `"state": "link_failing", "for": …` — Sugary could not get an answer
    (refused, unreachable, rate-limited) for `for`. `"state": "ending", "within": {"hours": h}` —
    the sensor's end (activation + its lifetime, 14 days unless the device says otherwise) is within h.
    A link failure never counts as `no_readings`.
- **Evaluation.** After each poll, for the polled person's watches only, on the newest reading
  only and only if it is under 10 minutes old. Per watch per trigger, `GlucoseWatchState` keeps
  whether the condition holds, since when, and whether this episode fired, in the database, so a
  restart neither re-fires nor drops a pending `for`. Back-filled graph points and imports change no
  state. A watch written while the condition already holds records it as holding and fired, so it
  fires at the next crossing — the reply says the current value (the skill).
- **Prompt effect** (ticket 12): composed by `FirePlanning` as a schedule's fire is —
  `Principal` = the watch's `userId`, `HouseholdId`, `Origin` Watch with the watch id, the report
  (trigger, value, trend, when) beside the prompt — and emitted on Sugary's channel. The agent signs a
  delegation for a write to `sugary-watches` (`DelegatedWrites`), Sugary keeps it with the watch,
  answers `delegations_list`/`delegations_replace` (`AddDelegationStore`), and the verifier knows the
  new delegated kind. A fire no agent took leaves a `once` watch armed and is logged.
- **Announce effect** (ticket 13): `{"kind": "announce", "text", "target", "insistent"}` as a home
  watch's, rung through `IHouseholdVoices.For(tenant)` (Sugary added to the relay's callers, its
  compose given `VOICEHUB__BASEURL` and `MCP__AUDIENCE`), `AnnounceKind.Alarm`, the target validated
  at write by `ISatelliteCatalog`. No target, every satellite `offline`, or the hub unavailable rings
  an insistent channel alert on the subject's and writer's channels: `ChannelAlerts` and
  `settle_alert` move out of the timers server into shared hosting, keyed by an alert request rather
  than `ArmedTimer`, and Sugary hosts them on its channel. A satellite alarm is stopped on the
  satellite or by `/timers ./dismiss` (the hub's dismiss is per household, not per kind — ticket
  13 verifies); a channel alert by its Dismiss.
- **Guidance** (ticket 14): a `glucose-watches` skill served by Sugary (shape, triggers, units,
  effects, who), the `home-watches` skill on `/watchers`, the Sugary README's glucose section, the
  mount descriptions, CONTEXT.md's **Watch** and **Effect** no longer home-only, rules files
  (`home-assistant.md`, `sugary.md`, `virtual-filesystem.md`, `mcp-hosting.md`), and eval scenarios
  for glucose watches and glucose analysis on a seeded log.

## Testing Decisions

- Test through public seams, test-first, as the repo does: the registrar and union through
  `FileSystemServerConformanceTests`, `VfsVirtualPathConformanceTests` and a union unit suite
  over in-memory members; the LibreLinkUp client and poller against `FakeLibreLinkUp` over HTTP; the
  store and migrations through `MigrationsMatchTheModelTests`; the trigger engine as a pure function
  of readings, state and clock (`FakeTimeProvider`); the fire through a real inbox as
  `WatchFiredEndpointTests` and scheduling do; the relay's new caller in `RelayedCalls` tests; the app
  with its vitest suites; prompts through snapshots and the changed-tier eval.
- No test reads user secrets; nothing calls Abbott.

## Out of Scope

- Libre's own alarm settings, LibreView reports, Dexcom or any other CGM.
- Writing anything to LibreLinkUp.
- A glucose watch that acts on the home (`actions`): write a home watch on an HA glucose entity for that.
- Insulin advice from readings beyond what Jonas already does.

## Further Notes

- Prod has no active glucose watch on HA (user, 2026-11-18), so nothing is migrated; the HA
  integration may stay for the home's dashboards.
- Prod deploy needs `SUGARY_SEALING_KEY` in `.env`, each person's link made in the app, and the
  import run once for her with the prod HA's token.
