---
paths:
  - "McpServerHomeAssistant/**"
  - "Domain/Tools/HomeAssistant/**"
  - "Domain/Prompts/HomeAssistantPrompt.cs"
  - "Domain/Prompts/HomeWatchesSkill.cs"
  - "Domain/Prompts/HomeAssistantSkill.cs"
  - "Domain/Prompts/HomeAssistantSetupSummary.cs"
---

# Home Assistant

Home Assistant runs at `http://<host>:8123` (published on all interfaces). On first run:

1. Create the owner account through the browser onboarding flow.
2. Profile menu → **Security → Long-Lived Access Tokens** → create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart `mcp-homeassistant`.
4. For the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**; the vacuum appears as `vacuum.<name>`.

The agent reaches HA in-network at `http://homeassistant:8123` via `McpServerHomeAssistant`. For voice alarms/reminders it creates events on a dedicated Local Calendar (prod: `calendar.assistant_alarms`, "Assistant Alarms" — the prompt never hard-codes the id, it says "the calendar the setup index lists as alarms") that an HA automation bridges to the voice announce endpoint; the `home_assistant_guide` prompt (`Domain/Prompts/HomeAssistantPrompt.cs`) teaches the idiom, and the one-time `rest_command` + automation provisioning lives in the HA instance itself — `docs/home-assistant-bridges.md` has the YAML. Without that automation an event is created and nothing rings.

The HA VFS engine is `Domain/Tools/HomeAssistant/Vfs/*.cs`.

## The calendar's action files are served, not forwarded

HA's service catalog cannot drive a calendar: `calendar.get_events` answers without the `uid` an
event is addressed by (its response is filtered to start/end/summary/description/location/status),
`calendar.create_event` takes no `rrule`, and there is **no delete or update service** — those exist
only as WebSocket commands (`calendar/event/create|delete|update`). A real turn hit this: asked to
move an alarm, the agent found `update_event.sh` missing and created a second event beside the first.

So `HaCalendarActions` declares `create_event`, `get_events` and `delete_event` as served actions
(the podcast pattern), always registered, and `HaCatalogProvider` **replaces** the catalog's
same-named definitions with them — an action file resolves to exactly one definition.
`HaCalendarEvents` runs them through three `IHomeAssistantClient` members: `ListCalendarEventsAsync`
(REST `GET /api/calendars/{entity}?start&end`, the one read that carries uids), and
`CreateCalendarEventAsync` / `DeleteCalendarEventAsync` (one WebSocket connection per command:
`auth_required` → `auth` → `auth_ok`, then an id-correlated `result` frame). There is deliberately
no update: a change is delete-by-uid plus create, which the prompt says to do silently.

Date-times cross as the caller's strings. HA reads a naive one in its own zone and the container
may sit in another, so parsing into an instant here would move an alarm by the difference; the only
arithmetic (an end one minute after a start, a listing window from now) keeps the string's shape.

Testing: `FakeHomeAssistantSocket` (Tests/Integration/Fixtures) speaks the protocol over a shared
`FakeCalendarStore`; the eval's `FakeHomeAssistant` serves the calendars endpoint from the same
store, points the client's base url at the socket (REST still goes through the handler), and
counts events in its snapshot (`AlarmsEventCountKey`) so a create or a delete is a declared change.
`HomeAssistantSeed` boots the real container with a Local Calendar config entry for the round trip.

The exec tokeniser honours bash quoting (`\"` and `\\` inside double quotes, a literal single-quoted
string, an unquoted backslash escape). Before it did, `--description "{\"target\":…}"` reached HA
as `{\target\:…}` and the bridge could not read the alarm's target. The prompt now shows the
description single-quoted.

## Every entity has `history.sh`, served by the mount

The service catalog cannot read the recorder — the history endpoint (`GET /api/history/period/
{start}`) is REST only — so `HaHistoryActions.History` is a served action (the calendar pattern)
with `AppliesToEveryEntity`, the one flag that puts an action in **every** entity directory under
its bare name, read-only classes included. `HaActionResolver` honours the flag before its
domain rules; `HomeAssistantSetupSummary` says such an action once, on an `every entity:` line,
and keeps it off the per-class lines. `HaHistory` runs it through `IHomeAssistantClient.
ListHistoryAsync` (asked with `minimal_response&no_attributes`, so a change is state plus
instant). Window strings cross as written (naive = HA's zone), like the calendar's; the shared
arithmetic is `HaDateTimeText`. Plain listing keeps the latest `--limit` changes; `--every N`
buckets numeric states per N minutes (min/max/mean/last/samples) and is an argument error on an
entity with no numeric state, or beside `--limit`. **Home Assistant stamps everything in UTC
whatever the home's zone** (`recorder/history/__init__.py` formats `last_changed` from a UTC
timestamp, and a state's `last_changed`/`last_updated` come the same way), so `HaCatalogProvider`
reads the home's zone once from `GET /api/config` (`IHomeAssistantClient.GetTimeZoneAsync`,
`HaCatalog.HomeZone`) and **every instant the mount says goes through `HaDateTimeText.Stamp` on
that clock**: `state.json`'s stamps, a listing's changes, a summary's buckets, a statistics row,
a watch's `status.json`, the window an action echoes. A real turn read a glucose `last_updated`
of `02:13+00:00` beside a turn prefix saying 04:13 local and called a thirty-second-old reading
two hours old; the skill now tells the model the stamps are local and never to convert. Buckets
align to the same clock, so a day bucket opens at the home's midnight; a zone it cannot read or
resolve leaves the catalog whole, keeps the stamps as they came and buckets on UTC, logged once as
a warning, and the payload's `bucket_zone` says which. The model-facing JSON uses relaxed escaping
so an offset's `+` is not written `\u002B`. An empty window under `--every` is an empty summary with
the retention note, never an error. The seeded test container is `time_zone: UTC`, so only the unit test with Madrid
stamps pins this. The first entry is the state at the window's start,
stamped there (pinned by `HomeAssistantClientTests.ListHistoryAsync_FirstElement_IsTheStateAtTheWindowsStart_StampedThere` against the real recorder). What
comes back is bounded by the recorder's retention (10 days by default, 90 on prod); nothing in
the API says which, so the help and the empty answer say "unless this home raised it" rather
than naming a bound the model would treat as fact. See `docs/adr/0037`.

**`statistics.sh` is the read that outlives retention.** Long-term statistics (hourly mean/min/max,
or state/sum/change for a total) are WebSocket-only (`recorder/statistics_during_period`), compiled
at :12 each hour for every sensor with a `state_class`, and kept for good. `HaStatisticsActions.
Statistics` is the same every-entity served action narrowed by `RequiresAttribute = "state_class"`,
so it appears in exactly the directories that have rows behind it — the resolver now takes the
`HaEntityState`, not its id, for that reason — and the index says `every entity with state_class:
statistics.sh`. `HaStatistics` runs it through `ListStatisticsAsync` (`--days`, `--period
5minute|hour|day|week|month`, `--limit`); `HaWindow` resolves both reads' windows. Rows render only
the measures the sensor's kind has. `FakeHomeAssistantSocket` answers the command from its
`Statistics` map; the real-container test imports rows with `recorder.import_statistics` and
reads them back. A sensor that lacks a `state_class` (LibreLink's glucose did) gets one through
HA's `customize:`; prod has that and `purge_keep_days: 90` since 2026-09-04.

## The setup index is a file, and the guide is two skills and a stub

`/ha/setup-index.md` is the one-page account of the home (`HomeAssistantSetupSummary`, rendered
by `HaFileSystem` on every read from the catalog, the watches and the satellite roster), so it is
as old as the read rather than as old as the conversation. The served `home_assistant_guide`
(`Domain/Prompts/HomeAssistantPrompt.cs`) is the standing stub: scope, which mechanism a request
is (calendar alarm, timer, schedule, watch), that an area id is read rather than derived, and the
instruction to load the `home-assistant` skill and read the index in the same turn before any
home task. The doing rules — layout, workflow, reading results, history, alarms, music — are the
`home-assistant` skill (`Domain/Prompts/HomeAssistantSkill.cs`), which says to re-read the index
when a name does not resolve; the doing rules of a watch are the `home-watches` skill
(`Domain/Prompts/HomeWatchesSkill.cs`), the one home task that loads its own skill instead. Both
are shipped by the server through `AddSkills` and loaded by the model with `load_skill`. See
`docs/adr/0039` and `.claude/rules/prompts.md`.

## Watches are Home Assistant automations, written as files

`/ha/watches/<id>/watch.json` is the mount's one writable subtree (`HaFileSystem.Watches.cs`;
`HaVfsPath` has the four watch kinds; `HaTree` lists them from ids the fs fetches live, only for a
glob that can reach them). A watch **is** a real automation written through the config API
(`IHomeAssistantClient.{List,Get,Upsert,Delete}Automation*`): `HaWatchSpec` parses the file and
names the field it refuses, `HaWatchAutomation.Render` turns it into the automation — id
`assistant_watch_<id>`, alias = name, `mode: single`, triggers/conditions verbatim, the effects in
order, and for `once` a final `automation.turn_off` on `{{ this.entity_id }}` guarded by a template
condition over each prompt callback's `response_variable` (`watch_fire_<n>.status < 400`), because
a `rest_command` does not abort the sequence on an error status and an unguarded turn_off would
spend the watch on a 503 nobody received; every callback call is `continue_on_error`, once or not,
because a stack that is down does raise in the home and the effects after the prompt must still
fire — and `Project`
reads one back; the metadata the automation cannot carry (creating agent, effects as authored, once,
deliverTo, userId, createdAt, and the on/off the agent last wrote) is JSON in its description
(`HaWatchMetadata`). A one-shot is spent only when it is off with a fire on record **and** the
agent last wrote it armed: a refused fire stamps `last_triggered` on a watch that stays on, and a
pause after it would otherwise read as spent and be removed. Only a prefixed
automation with parsable metadata is a watch; the alarm bridge and blueprints are invisible to the
subtree and untouchable through it. `enabled` is the entity's on/off, synced with
`automation.turn_on/off` after every write; `status.json` is rendered, read-only. Text fields cross
as Jinja and are rendered by the home inside a `variables` step, so the rest_command payloads are
composed with `to_json` and a quote in a prompt cannot break them. The creating agent comes from
`CallerContext` (entered by the call-tool filter from the request's `_meta`); a file naming no
`deliverTo` takes the caller's origin channel and address, because the model is never told which
channel a turn came from, and a file naming no `userId` takes the caller's user for the same
reason (a fire attributed to nobody would run as the sender label "watch"); a replace naming
neither keeps the watch's own. A watch written paused whose entity the home has not loaded within
the store's five polls is armed, and the write's `Note` says so. A prefixed automation whose
description is not the metadata is not a watch and is logged by id once per listing. The hub answering an
error status at that check is `VoiceHubRejectedException` (Infrastructure's `HttpSatelliteCatalog`
throws it, `ToolError.CodeFor` maps it by status), refused as `authentication` for a token
mismatch and `transient_dependency` otherwise. The default `deliverTo` a fire falls back to is `Domain/delivery.json`.
The prompt effect's callback (`McpServerHomeAssistant/Services/WatchFiredEndpoint.cs`, guarded by
the announce token, 202/401/400/503 as the automation's trace reads them) and the bridges document
are in `docs/home-assistant-bridges.md`; the decision is `docs/adr/0038`. Testing: `HaWatchesTests`
and `HaWatchEffectsTests` over `FakeHaClient`'s automation store; `WatchFiredEndpointTests` at the
HTTP boundary over a real inbox; `HomeAssistantFixture` seeds the rest_command towards a listener
it owns and `HomeAssistantWatchFireTests` drives a real crossing through it; the eval's fake home
serves the config API and counts watches (`WatchCountKey`), with `WatchScenarios` per effect kind.

## Music Assistant (podcast episodes)

Almost everything media goes through HA's `music_assistant.*` services. One thing cannot: **listing a
podcast's episodes**. `media_player.browse_media` has no podcast branch and raises `BrowseError`,
`search_media` / `music_assistant.search` never return episodes, and `get_library` only holds saved
items. That matters because `music_assistant.play_media` resolves a name across tracks/albums/
playlists/artists/radio/**shows** — never episodes — so an episode title silently plays the show's
newest episode and still reports success.

So a podcast episode is playable **only** by its exact MA uri (`<provider>://podcast_episode/<id>`),
and only MA's own websocket API can produce it. `IMusicAssistantClient` /
`Infrastructure/Clients/MusicAssistant/MusicAssistantClient.cs` talks to `ws://<ma>/ws` directly
(no HTTP command endpoint exists); it authenticates with a long-lived MA token, and accumulates
frames flagged `partial: true`.

The capability reaches the agent as `music_assistant.podcast_episodes.sh` in every media_player
directory. It is a **virtual action**: `HaMusicActions.PodcastEpisodes` is a synthetic
`HaServiceDefinition` injected via `HaCatalogProvider(extraServices:)` so glob/`--help`/exec resolve
it like a real service, and `HaFileSystem.ExecAsync` intercepts it and runs `HaPodcastEpisodes`
instead of calling HA. It is registered only when `MusicAssistant:Token` is set, so a deployment
without MA never advertises an action that cannot work.

MA runs `network_mode: host`, so `mcp-homeassistant` reaches it over `host.docker.internal`
(`extra_hosts: host-gateway`), not a container name.

## Music Assistant (restarting an episode)

MA keeps a **resume point** per podcast episode and audiobook, and `play_index` reads its
`seek_position` argument as falsy-or-set: a 0 means "no seek requested", so it substitutes
`resume_position_ms - 500` and the stream restarts half a second behind where the listener already
was (`music_assistant/controllers/player_queues.py`, MA 2.9.9). Both routes into it are affected —
`music_assistant.play_media` always resumes, and `media_player.media_seek` forwards straight to
`play_index` — and HA's `music_assistant.play_media` exposes no start-position field, so nothing the
agent can say means "second zero".

`HaFileSystem.NormalizeMediaSeek` therefore rewrites a `media_player.media_seek` of 0 to 1 second:
truthy for MA, inaudible to a listener. Keep that rewrite as long as MA reads the field this way —
without it "play it from the beginning" silently becomes "jump back half a second".

## The setup index says what an entity offers; the registry says what it hides

"Turn on the TV and put Crunchyroll" took ten model turns on a 36-second task, seven of them
spent learning that `remote.turn_on --activity <app>` opens an app and which apps exist. Two
things fix that:

- **A choosing entity's index line carries its choices and the action that takes them.**
  `HomeAssistantSetupSummary` appends ` — <service>.sh --<flag>: a, b, c` for the lists that ARE
  an action's argument (`activity_list` → `turn_on --activity`, `source_list` → `select_source
  --source`, `options` → `select_option --option`), only when the entity admits that action and
  the list is non-empty. The header says the segment stops at the dash. Widen the table only for
  a list that is literally a flag's value — a climate's mode lists would say a lot for a call the
  model already knows.
- **An unavailable entity's choice lists come from where Home Assistant persists them, not from
  a memory and not from anything loaded.** The set goes `unavailable` seconds into standby and
  the states endpoint then serves the entity with a name and its features and no lists — at the
  one moment the apps are asked for. `HaCatalogProvider.WithPersistedChoicesAsync` puts them
  back, only for what the states endpoint left blank, so a home with everything on reads nothing:
  - one **entity registry** read (`config/entity_registry/get_entries`,
    `IHomeAssistantClient.ListRegistryEntriesAsync`) for every unavailable entity serving no
    choice list. The registry persists `source_list` and `options` as capabilities, current from
    the last state write, and the entity gets exactly the choice lists it holds. The same entry
    names the entity's platform and config entry.
  - a `remote`'s `activity_list` is **no capability** (and the Android TV Remote's media player
    leaves `source_list` empty), so a remote the registry puts on `androidtv_remote` gets its apps
    from the one place HA keeps them, the config entry's options — whose only window is the
    entry's options flow, opened, read at its first form (each app labelled `Name (key)`) and
    deleted unanswered, so nothing is saved or reloaded (`ListAndroidTvAppsAsync`). The flow
    opens whether or not the entry is loaded.
  The entry comes from the registry and never from the integration's live entities: a set that
  is off when the home starts leaves `androidtv_remote` in `setup_retry` (its setup raises
  `ConfigEntryNotReady`) until the set is next seen, and a template's `integration_entities`
  lists nothing for it — a real index built four minutes after a restart listed the TV with no
  apps for exactly that reason, and the model spent nine turns finding Plex. A read that fails
  leaves the entities as served and the catalog whole, logged. There is deliberately no
  process-wide memory of a list seen while the entity was on: it was empty after every restart
  with the set off, and it never fired on prod anyway, because a live disconnect is served
  without the `restored` flag it keyed on. The eval's fake home serves the flow and a bare
  unavailable stub the way prod does, and its socket's `Registry` names the remote's entry.
- **An entity hidden in Home Assistant's registry never enters the catalog.** The states endpoint
  serves hidden entities regardless, so `HaCatalogProvider`'s area template also renders the
  `is_hidden_entity` list and the provider drops those states before the catalog is built — no
  tree entry, no index line, no path resolves. Hide an entity in HA (the entity's settings →
  Visibility) when it exists for an automation rather than for a person: the TV's wake button and
  the automation that presses it were the first two.
