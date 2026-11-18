# 10: Glucose watches as files at /watchers

**What to build:** `GlucoseWatch` storage (household id, subject, writer, spec JSON, enabled, once, delegation slot, createdAt) and migration; `SugaryWatchFileSystem` (`sugary-watches`, mount `/watchers`, union claims `glucose`, `glucose_trend`, `glucose_forecast`, `sensor`) registered beside `sugary`. `GlucoseWatchSpec` parses and refuses by field name (spec's fields, `time` conditions only, no `actions`); `person` resolves a `people/` folder under the grant; `deliverTo`/`userId` filled from the caller like a home watch; listed for subject, writer and grantees; nobody refused. `status.json`: createdAt, lastFiredAt, current condition per trigger, spent. Announce targets validated at write via `ISatelliteCatalog` (ticket 13 wires the relay; until then a target is refused as unavailable). Spec: "Glucose watches — Files", "Who".

**Blocked by:** 02, 06

Status: done

- [x] Write/read/edit/remove/glob through the union beside a fallback member; ids unique per household.
- [x] Every refused field names itself; another person's sensor without the grant is `permission_denied`.
- [x] A non-grantee in the household does not list another's glucose watch.

## Comments

Done in 3d43b728d. What was built, and what ticket 11 needs:

- **Shared coordinates.** `Domain/Tools/Watches/Watchers.cs` holds `MountPoint` (`/watchers`), `RouteBy`
  (`/triggers/*/trigger`) and the two file names; `HaWatchFileSystem.MountPointPath` is gone (the setup
  index, `PromptStalenessTests` and `FakeHomeAssistantTests` read `Watchers.MountPoint`), `HaWatchPath`'s
  file names point at it. Sugary's backend parses its paths with `HaWatchPath` (same four kinds).
- **`GlucoseWatchSpec`** (`Domain/Tools/Sugary/Watches/`): `name`, `triggers`, `conditions`, `effects`,
  `once`, `enabled`, `deliverTo`, `userId`; anything else refused by name. Triggers are typed records
  (`GlucoseLevelTrigger(Bound, MgPerDl, For?)`, `GlucoseTrendTrigger(Direction, MgPerDlPerMinute)`,
  `GlucoseForecastTrigger(Bound, MgPerDl, Within)`, `SensorTrigger(State, For?, Within?)`, each with
  `Person`), conditions `GlucoseTimeCondition(After?, Before?, Weekdays?)` (wall times, `after > before`
  meaning across midnight is the engine's to honour), effects `GlucosePromptEffect` /
  `GlucoseAnnounceEffect(Text, Target?, Insistent?)`. Every refusal names its field
  (`triggers[1].below must be a number`, `conditions[0].weekday …`, `effects[0].kind 'actions' … home watch`).
  Unknown keys inside a trigger, a condition or an effect are refused too. Durations are
  `{hours|minutes|seconds}` objects only (no `"HH:MM:SS"` string), rendered back in the largest whole unit
  (`{"minutes":60}` reads back as `{"hours":1}`).
- **Beyond the ticket: a level outside 20–600 is refused** naming the field and saying mmol/L × 18 is mg/dL,
  since `below: 3.9` would be a low alarm that never rings. Rates are only checked positive.
- **Store** (`McpServerSugary/Store/GlucoseWatch.cs`, migration `20261118020726_GlucoseWatches`, class renamed
  from `_2026…`): `GlucoseWatches` key `(TenantId, Id)`, index `(TenantId, SubjectUserId)`; `GlucoseWatchStates`
  key `(TenantId, WatchId, TriggerIndex)`, FK to the watch with cascade. Both are `IHousehold` (tenant filter
  through `SugaryStore.For(account)`); visibility is explicit in `SqliteGlucoseWatches.ListAsync`.
  `DeliverTo` is a JSON list in text. `.claude/rules/sugary.md` ("Who owns a row") says so.
- **The Domain contract** (`Domain/Contracts/IGlucoseWatches.cs`, record `StoredGlucoseWatch` and
  `GlucoseTriggerCondition` in `Domain/DTOs/Sugary/StoredGlucoseWatch.cs`):

  ```csharp
  Task<IReadOnlyList<StoredGlucoseWatch>> ListAsync(Account reader, bool everyone, CancellationToken ct);
  Task<StoredGlucoseWatch?> FindAsync(Account reader, string id, CancellationToken ct);      // household-wide
  Task<bool> AddAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct);       // false: id taken
  Task<bool> ReplaceAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct);   // clears its states
  Task<bool> RemoveAsync(Account reader, string id, CancellationToken ct);                   // states too
  Task<IReadOnlyList<GlucoseTriggerCondition>> ConditionsAsync(Account reader, string id, CancellationToken ct);
  ```
- **The backend** `SugaryWatchFileSystem` (`sugary-watches`, `/watchers`, claims the four kinds), registered
  on Sugary after `SugaryFileSystem`; read/info/glob/create/edit/delete and the byte read (`ReadBlobAsync`
  overridden so a refusal is not a retryable failure). Caller view by shallow copy (`_caller`, `_reader`,
  `_household`; `SugaryWatchFileSystemCallerViewTests`). `status.json`: `createdAt`, `lastFiredAt` (household
  zone), `enabled`, `spent`, `subject` (display name), `person` (folder, null for the reader), and per
  trigger `{trigger, holds, since}` from the state rows (`holds` null until judged).

Decisions (pinned in `SugaryWatchFileSystemTests`):

- **The subject is the stored `SubjectUserId`, never re-derived from a reader.** The stored spec keeps no
  `person`; a read renders it per reader (left out for the subject, the subject's `people/` folder for
  anyone else), so writing back what was read keeps the subject. A trigger naming nobody is the existing
  watch's subject on a replace, the writer's own sensor on a new watch. Naming one's own folder is one's own
  sensor, with or without the grant. All triggers must name one subject, else refused naming the indices.
- **Writer is the creator and stays** across others' edits, so whoever set it up keeps seeing it.
  `AgentId`, `CreatedAt`, `LastFiredAt`, `Delegation` are kept on replace; `deliverTo`/`userId` the file
  leaves out are the existing watch's, else the caller's (origin channel; the principal's user id).
- **An id somebody else keeps out of the caller's view is `already_exists` even with `overwrite`**, saying
  nothing more about it. The converse cannot be caught here: a non-grantee writing a *home* watch with an id
  a hidden glucose watch holds is routed to the home (the union saw no holder), so the two members can each
  hold that id; whoever sees both then reaches whichever member the union asks first.
- **Watching another's sensor needs the grant at every write**, so a writer whose grant was withdrawn still
  lists their watch but cannot edit it (`permission_denied`); they can still remove it.
- **`Spent`** = it was spent and the write leaves it off; a write turning it on arms it again.
- **Announce target**: optional (none rings on channels, ticket 13); when present, checked through
  `IHouseholdVoices.For(tenant).Satellites` (unknown satellite or room → `not_found` naming the field and the
  satellites; hub unavailable → `transient_dependency` hinting to leave the target out;
  `VoiceHubRejectedException` 401/403 → `authentication`). No target asks no voice server.
- **Folder names** follow `SugaryFileSystem`'s rule (others only, twins numbered in join order), copied into
  `SugaryPeopleFolders` because that file is being changed elsewhere; `SugaryPeopleFoldersTests` asks both
  the same question so they cannot drift. Fold `SugaryFileSystem.OthersAsync` onto it when that work lands.
- **`userId` is not checked**: the file may name anyone as who a fire runs as. Ticket 12 should decide
  (likely: only the writer, or the subject) when the delegation is wired; `Delegation` is stored null now.
- `TheMount_OffersReadingAndNothingThatWrites` now pins the log's published operations and the watches'
  separately; the server's tool set is their union (so Sugary has `fs_create`/`fs_edit`/`fs_delete` now).

For ticket 11 (the engine):

- Read watches with the factory context, `IgnoreQueryFilters()`, `Where(w => w.TenantId == person.TenantId
  && w.SubjectUserId == person.UserId && w.Enabled)` (index there), and add the engine to the rules'
  `IgnoreQueryFilters()` list. Parse `Spec` with `GlucoseWatchSpec.Parse` (triggers in order; the index is
  the state's `TriggerIndex`; persons are absent — the subject is the column).
- Own `GlucoseWatchStates` (upsert per `(TenantId, WatchId, TriggerIndex)`: `Holding`, `HoldingSince`,
  `FiredThisEpisode`). A rewrite (`ReplaceAsync`) deletes them, so "written while the condition holds →
  holding and fired" means: a watch with no state rows is judged once without firing, its states recorded
  as fired where they hold.
- On a fire set `LastFiredAt`; for `once`, set `Spent = true` and `Enabled = false` on the columns only (the
  read overlays `Enabled` from the column onto the spec, so `watch.json` then reads `"enabled": false`).
- Conditions' times are wall times on the household's clock; the mount has none at poll time, so the engine
  needs the household's zone (or the server's) itself.

Tests: `GlucoseWatchSpecTests` (55 cases), `SugaryWatchFileSystemTests` (30, through a union beside an
in-memory fallback), `SugaryWatchFileSystemCallerViewTests`, `SugaryPeopleFoldersTests`,
`SugaryGlucoseWatchesTests` (integration over MCP on the real store: shown to whom it concerns, id taken,
states cleared on rewrite and removal), conformance rows (`sugary-watches` in every table, `/watchers`,
its union) and `MigrationsMatchTheModelTests`. Red first: the conformance rows failed until the server
registered the backend; the hidden-id and listing guards were proven by mutation (each turned its test red).
`Tests.Unit` 7526/7527 (the one failure, `ChatMonitoringTests.AMonitorThatKeepsFailing_IsRestartedOnADoublingDelay`,
is the known load flake ticket 03 saw; it passes alone); Sugary/VFS/HA/watch/deployment integration 781 passed;
`FakeHomeAssistantTests`, `PromptStaleness`, `VfsBridge` 195 passed. Eval not run.
