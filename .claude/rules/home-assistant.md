---
paths:
  - "McpServerHomeAssistant/**"
  - "Domain/Tools/HomeAssistant/**"
  - "Domain/Prompts/HomeAssistantPrompt.cs"
  - "Domain/Prompts/Sections/home_assistant_guide.md"
  - "Domain/Prompts/HomeWatchesSkill.cs"
  - "Domain/Prompts/HomeAssistantSkill.cs"
  - "Domain/Prompts/Skills/home-watches.md"
  - "Domain/Prompts/Skills/home-assistant.md"
  - "Domain/Prompts/HomeAssistantSetupSummary.cs"
---

# Home Assistant

Home Assistant is a household's: it runs in the tenant kit (`TenantKit/`) beside its server, at `http://<host>:8123` (`LAN_BIND_ADDRESS`). On first run:

1. Create the owner account through the browser onboarding flow.
2. Profile menu → **Security → Long-Lived Access Tokens** → create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `TenantKit/.env` and restart the kit's `mcp-homeassistant`.
4. For the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**; the vacuum appears as `vacuum.<name>`.

`McpServerHomeAssistant` reaches HA on the kit's network at `http://homeassistant:8123`; the agent reaches the server through its tunnel as `homeassistant` (`.claude/rules/own-servers.md`). The watch callback and announcement targets stay inside the kit, under the kit's own `Announce__Token`.

**Voice alarms** are events on a dedicated Local Calendar (prod: `calendar.assistant_alarms`) that an HA automation bridges to the voice announce endpoint. The prompt never hard-codes the calendar id — it says "the calendar the setup index lists as alarms". The `rest_command` + automation are provisioned by hand in the HA instance (`docs/home-assistant-bridges.md` has the YAML); without them an event is created and nothing rings.

## Action files (`Domain/Tools/HomeAssistant/Vfs/`)

- Each action an entity admits is an **action file** named for its service, no extension (`turn_on`, `music_assistant.play_media`), run as `./turn_on` or `turn_on`; `./` is the spelling taught, because a real shell needs it once exec runs in the sandbox. Executable-only: a read refuses it (`FsError.ExecutableOnly`), its usage is `--help`. `/ha` overrides no write at all (watches are written at `/watchers`). The old `<service>.sh` spelling answers 127 with the available names.
- Any leaf other than `state.json` parses as an action, so **whether one exists is the catalog's answer, never the parser's**.
- **Served actions** replace or add to HA's catalog where HA cannot do the job, declared as `HaServiceDefinition`s so glob/`--help`/exec resolve them like real services (`HaCatalogProvider` `extraServices`; one named like a published service **replaces** it, so an action file resolves to exactly one definition). The list is `McpServerHomeAssistant/Modules/ConfigModule.cs`, which the eval's fake mount builds from too.
- The exec tokeniser honours bash quoting (`HaFileSystem.Exec.cs`), and the prompt shows a JSON argument single-quoted; keep the two in step.

## The calendar's action files are served, not forwarded

HA's service catalog cannot drive a calendar (no uid on `get_events`, no `rrule`, no delete or update service), so `HaCalendarActions` serves `create_event`, `get_events` and `delete_event`, run by `HaCalendarEvents` over REST (`ListCalendarEventsAsync`) and WebSocket (`CreateCalendarEventAsync`, `DeleteCalendarEventAsync`). There is **deliberately no update**: a change is delete-by-uid plus create, which the prompt says to do silently.

Date-times cross as the caller's strings, never parsed into an instant: HA reads a naive one in its own zone and the container may sit in another, so the only arithmetic (an end one minute after a start, a listing window from now) keeps the string's shape (`HaDateTimeText`).

Testing: `FakeHomeAssistantSocket` and the eval's `FakeHomeAssistant` share `FakeCalendarStore` (snapshot key `AlarmsEventCountKey`); `HomeAssistantSeed` boots the real container with a Local Calendar.

## Every entity has `history`, served by the mount

- `HaHistoryActions.History` carries `AppliesToEveryEntity` — the one flag that puts an action in **every** entity directory, read-only classes included; `HaActionResolver` honours it before its domain rules, and `HomeAssistantSetupSummary` says it once on an `every entity:` line.
- **Home Assistant stamps everything in UTC whatever the home's zone**, so `HaCatalogProvider` reads the zone once (`IHomeAssistantClient.GetTimeZoneAsync`, `HaCatalog.HomeZone`) and **every instant the mount says goes through `HaDateTimeText.Stamp`** — `state.json`, history, summaries, statistics rows, a watch's `status.json`, echoed windows. The `home-assistant` skill tells the model the stamps are local and never to convert; the two stay in step. A zone that cannot be read leaves stamps as they came and buckets on UTC (`bucket_zone` says which). The seeded test container is `time_zone: UTC`, so only the unit test with Madrid stamps pins this.
- An empty window under `--every` is an empty summary with the retention note, never an error (`HaHistoryActionTests.NoChanges_UnderEvery_IsAnEmptySummary_WithTheRetentionNote`); a non-empty history with no number in it is an argument error.
- The first entry is the state at the window's start, stamped there (`HomeAssistantClientTests.ListHistoryAsync_FirstElement_IsTheStateAtTheWindowsStart_StampedThere`).
- Retention is 10 days by default (90 on prod) and nothing in the API says which, so the help and the empty answer say "unless this home raised it" rather than naming a bound. See `docs/adr/0037`.
- **`statistics` is the read that outlives retention** (`HaStatisticsActions.Statistics`, `HaStatistics`): every-entity but narrowed by `RequiresAttribute = "state_class"`, so the resolver takes the `HaEntityState`, not its id; the index says it once on an `every entity with state_class:` line, never on per-class lines. A sensor without a `state_class` gets one through HA's `customize:` (prod has that and `purge_keep_days: 90`).

## The setup index is a file, and the guide is two skills and a stub

- `/ha/setup-index.md` (`HomeAssistantSetupSummary`, rendered by `HaFileSystem` on every read) is the home's one-page account.
- `home_assistant_guide` (`Domain/Prompts/HomeAssistantPrompt.cs`) is the standing stub: scope, which mechanism a request is (calendar alarm, timer, schedule, watch), that an area id is read rather than derived, and to load the `home-assistant` skill and read the index in the same turn. When the host preloads the skill it makes that read too (`PreloadReads`, `SkillPreloadReads`).
- The doing rules are the `home-assistant` skill (`HomeAssistantSkill.cs`) and, for watches, the `home-watches` skill (`HomeWatchesSkill.cs`) — both shipped through `AddSkills`, loaded with `load_skill`.
- **A watch on a person's glucose is not the home's** wherever Sugary serves `glucose-watches`: the stub and the `home-watches` description say so, so the two descriptions divide by subject (the home's entities vs a person's sensor), and a home watch on the home's glucose entity is left for an agent with no Sugary (a household that turned Sugary off) or one that acts on the home. See `docs/adr/0039`, `.claude/rules/prompts.md`, `.claude/rules/sugary.md`.

## Watches are Home Assistant automations, written as files

- A watch is `/watchers/<id>/watch.json` with a read-only `status.json`, on `HaWatchFileSystem` (`ha-watches`, served after `ha` so a call naming no filesystem still reaches the home). `/ha/watches` names nothing. `/watchers` is a **union** (`.claude/rules/virtual-filesystem.md`): the home's watches declare `routeBy: /triggers/*/trigger` with `fallback: true`, beside Sugary's glucose watches. A home that cannot list its automations **fails the listing retryably, never answers empty**.
- A watch **is** a real automation (`IHomeAssistantClient.{List,Get,Upsert,Delete}Automation*`), id `assistant_watch_<id>`, rendered by `HaWatchAutomation.Render`; metadata the automation cannot carry is JSON in its description (`HaWatchMetadata`). **Only a prefixed automation with parsable metadata is a watch** — the alarm bridge and blueprints are invisible and untouchable through the subtree.
- **The automation's contract with the stack**: every prompt callback is `continue_on_error`; a `once` watch's final `automation.turn_off` is guarded on each prompt callback's `response_variable` being `< 400`, because a `rest_command` does not abort on an error status and the callback answers 503 when no agent is connected. Text crosses as Jinja rendered in a `variables` step and payloads are composed with `to_json`. The home's rest_command posts the rendered prompt and, as a literal, `promptIndex`; the callback (`WatchFiredEndpoint`, guarded by the announce token) reads them, answers 202/401/400/503 for the automation's trace to read, and buffers nothing.
- **A fire acts for the person the watch's delegation names** (`.claude/rules/delegations.md`, "Deferred turns"): the callback looks the watch up at fire time and asks the prompt **as the watch keeps it**, carrying what the home posted only as its report, never as what is asked (anything with the announce token can post); a home the callback cannot ask fires for nobody. `WatchDelegation.Of` describes a watch the same way on both ends (agent and HA server). A write carrying no delegation leaves the watch nobody's. `WatchDelegationStore` (`delegations_list` / `delegations_replace`) rewrites the config alone, so a spent one-shot is not re-armed.
- **Spent** = off with a fire on record **and** the agent last wrote it armed (`HaWatchAutomation.cs`): a refused fire stamps `last_triggered` on a watch that stays on, and a paused one must not read as spent (the guide says to remove spent watches).
- The creating agent comes from the call's `_meta` via `HaWatchFileSystem.For`, never an ambient; the view is a shallow copy with the caller swapped, never a second construction (`HaWatchFileSystemCallerViewTests`). A file naming no `deliverTo` takes the caller's origin channel and address, and no `userId` the caller's user, because the model is never told either; a replace naming neither keeps the watch's own. The default `deliverTo` of a fire is `Domain/delivery.json`.
- An announcement target is checked against the voice hub at write time; a hub error status is `VoiceHubRejectedException` (`HttpSatelliteCatalog`, mapped by `ToolError.CodeFor`): `authentication` for a token mismatch, `transient_dependency` otherwise.
- Decision `docs/adr/0038`; tests `HaWatchesTests`, `HaWatchEffectsTests`, `WatchFiredEndpointTests`, `HomeAssistantWatchFireTests`; eval `WatchScenarios` (`WatchCountKey`).

## Music Assistant (podcast episodes)

A podcast episode is playable **only** by its exact MA uri (`<provider>://podcast_episode/<id>`) — `music_assistant.play_media` given a title silently plays the show's newest episode and reports success — and only MA's own websocket API lists episodes (`IMusicAssistantClient`, `MusicAssistantClient`). `music_assistant.podcast_episodes` is a **virtual action** (`HaMusicActions.PodcastEpisodes`, intercepted by `HaFileSystem.ExecAsync`, run by `HaPodcastEpisodes`), registered only when `MusicAssistant:Token` is set. MA runs `network_mode: host`, so `mcp-homeassistant` reaches it over `host.docker.internal` (`extra_hosts: host-gateway`), not a container name.

## Music Assistant (restarting an episode)

`HaFileSystem.NormalizeMediaSeek` rewrites a `media_player.media_seek` of 0 to 1 second, because MA's `play_index` reads a 0 `seek_position` as "no seek" and resumes half a second behind the listener (MA 2.9.9); HA exposes no start-position field. **Keep that rewrite** as long as MA reads the field this way.

## The setup index says what an entity offers; the registry says what it hides

- **A choosing entity's index line carries its choices and the action that takes them** (`HomeAssistantSetupSummary`: ` — ./<service> --<flag>: a, b, c` for `activity_list`, `source_list`, `options`, only when the entity admits that action and the list is non-empty). Widen that table only for a list that is literally a flag's value.
- **An unavailable entity's choice lists come from where Home Assistant persists them, never from a memory** (`HaCatalogProvider.WithPersistedChoicesAsync`): the entity registry (`ListRegistryEntriesAsync`) for `source_list`/`options`, and for an `androidtv_remote` remote the config entry's options flow, opened, read and deleted unanswered (`ListAndroidTvAppsAsync`). The entry comes from the registry, **never from the integration's live entities** (a set off at boot leaves the integration in `setup_retry`). There is **deliberately no process-wide memory** of a list seen while the entity was on — it was empty after every restart with the set off.
- **An entity hidden in Home Assistant's registry never enters the catalog** (the area template renders `is_hidden_entity` and the provider drops those states). Hide an entity in HA when it exists for an automation rather than a person (the TV's wake button and its automation).
