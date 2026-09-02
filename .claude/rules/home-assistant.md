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

The agent reaches HA in-network at `http://homeassistant:8123` via `McpServerHomeAssistant`. For voice alarms/reminders it creates events on a dedicated Local Calendar (prod: `calendar.alarms`, "Alarms" — the prompt never hard-codes the id, it says "the calendar the setup index lists as alarms") that an HA automation bridges to the voice announce endpoint; the `home_assistant_guide` prompt (`Domain/Prompts/HomeAssistantPrompt.cs`) teaches the idiom, and the one-time `rest_command` + automation provisioning lives in the HA instance itself — `docs/home-assistant-alarm-bridge.md` has the YAML. Without that automation an event is created and nothing rings.

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
