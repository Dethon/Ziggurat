---
paths:
  - "McpServerHomeAssistant/**"
  - "Domain/Tools/HomeAssistant/**"
  - "Domain/Prompts/HomeAssistantPrompt.cs"
---

# Home Assistant

Home Assistant runs at `http://<host>:8123` (published on all interfaces). On first run:

1. Create the owner account through the browser onboarding flow.
2. Profile menu → **Security → Long-Lived Access Tokens** → create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart `mcp-homeassistant`.
4. For the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**; the vacuum appears as `vacuum.<name>`.

The agent reaches HA in-network at `http://homeassistant:8123` via `McpServerHomeAssistant`. For voice alarms/reminders it creates events on a dedicated Local Calendar (prod: `calendar.assistant_alarms`, "Assistant Alarms" — the prompt never hard-codes the id, it says "the calendar the setup index lists as alarms") that an HA automation bridges to the voice announce endpoint; the `home_assistant_guide` prompt (`Domain/Prompts/HomeAssistantPrompt.cs`) teaches the idiom, and the one-time `rest_command` + automation provisioning lives in the HA instance itself — `docs/home-assistant-alarm-bridge.md` has the YAML. Without that automation an event is created and nothing rings.

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
entity with no numeric state, or beside `--limit`. **The recorder stamps every change in UTC
whatever the home's zone** (`recorder/history/__init__.py` formats `last_changed` from a UTC
timestamp), so buckets cannot follow the stamps' offset: `HaCatalogProvider` reads the home's zone
once from `GET /api/config` (`IHomeAssistantClient.GetTimeZoneAsync`, `HaCatalog.HomeZone`) and
`HaHistory` aligns buckets to that clock, so a day bucket opens at the home's midnight; a zone it
cannot read or resolve leaves the catalog whole and buckets on UTC, logged once as a warning, and
the payload's `bucket_zone` says which. An empty window under `--every` is an empty summary with
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
