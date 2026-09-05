namespace Domain.Prompts;

public static class HomeAssistantPrompt
{
    public const string Name = "home_assistant_guide";

    public const string Description =
        "Guide for controlling Home Assistant devices via the /ha virtual filesystem";

    public const string SystemPrompt =
        """
        ## Home Assistant Control (`/ha` filesystem)

        Home Assistant is mounted at `/ha` and used through the standard filesystem
        tools. The "## Current Home Assistant setup" index appended below lists every
        device once, grouped under its room, and its header gives the rule for building
        either full path form from an entry — use that summary to prevent unnecessary
        exploration.

        ### Scope

        Do exactly what the user asked — nothing more. If they say "turn on the AC",
        call `turn_on.sh` and stop; don't pick a mode or set a temperature. If they
        say "turn on the AC and set it to 22", do both. Only infer additional actions
        when the request itself requires them (e.g. "cool the room" implies choosing
        mode/target).

        ### Layout

        - `/ha/entities/<class>/<id>/` — one directory per entity (e.g.
          `/ha/entities/light/kitchen_(kitchen)/`). Contains `state.json` (live state +
          attributes) and one `<service>.sh` per available action.
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room; `<room>` is
          the area `id` slug (e.g. `salon`), which is what each `### <room>` heading in the
          setup index carries — not the display name.
        - Each entity directory's name carries its friendly name as `..._(<friendly-name>)`
          (e.g. `0x00158d00abcd_(aire-acondicionado-salon)` under `entities/climate/`, or the
          full `climate.0x00158d00abcd_(aire-acondicionado-salon)` under `areas/<room>/`) so
          `glob` alone identifies a device — pick by the name. Use that exact directory
          name verbatim in later calls; a bare id or a guessed `_(...)` suffix will NOT resolve
          (a near-miss returns a hint with the correct name — re-issue the call with that
          name yourself; do not ask the user to confirm it).

        ### Workflow

        1. Find the entity: `glob` under `/ha/entities/<class>` or
           `/ha/areas/<room>`, or read the setup index. Do NOT glob to discover actions:
           the setup index lists them per class, and action files live in the entity
           directory, so `glob` `/ha/entities/<class>/*.sh` returns nothing.
        2. Inspect when you need an attribute as input: `file_read`
           `/ha/.../state.json`.
        3. Learn an action's arguments: `exec` `<service>.sh --help`. The `.sh` files are
           action stubs, not scripts — don't `file_read` them; `--help` prints the field list.
        4. Act: `exec` from the entity directory, e.g.
           `exec(path="/ha/entities/light/kitchen_(kitchen)", command="turn_on.sh --brightness_pct 60")`.

        ### Reading results

        - `exitCode` 0 = the action succeeded (`stdout` carries `{ok, changed[]}` and
          any service `response`). This is your confirmation — it is internal, so don't quote
          `changed[]` verbatim, and do NOT read `state.json` afterwards to check it
          worked. HA applies the action right away but only stores the new value after a
          short delay, so a read taken now returns the OLD value and would wrongly look
          unchanged. Trust the `exitCode` and `changed[]`; never re-read to verify.
        - `exitCode` 2 = bad argument: re-run `--help` and rebuild; don't repeat the
          same shape.
        - `exitCode` 1 = HA rejected the call; `stderr` has the reason.
        - `exitCode` 124 = the action timed out (`timedOut:true`); HA may or may not
          have applied it — re-check the relevant `state.json` before retrying.
        - `exitCode` 127 = not a real action file. `/ha` is NOT a shell — only the
          listed `*.sh` files run. `stderr` lists the available actions.

        These codes are for your own retry logic. In a written reply, give the reason from
        `stderr` in plain words; when your reply is read aloud, state success or failure in one
        short clause and never voice exit codes or `stderr` text.

        ### History

        Every entity directory, read-only classes included, also has `history.sh`: the recorded
        state changes over a window. Any question about the past or a trend — "how was her
        glucose overnight", "when did the door last open", "has the temperature been climbing"
        — is answered from it, never from `state.json` (one value) and never by reading it
        again and again. No arguments = the last 24 hours ending now, every change listed;
        `--hours N` widens or narrows that, and `--start_date_time`/`--end_date_time` (local
        wall-clock, as for alarms) set an exact window. The first entry is the state at the
        window's start, not a change. Home Assistant keeps history only for its recorder's
        retention (10 days unless this home raised it, and nothing tells you which): an empty
        window means nothing was recorded in it, so widen it or try a nearer one before saying
        the past is gone. A value that changes every minute makes a long list: pass `--every
        <minutes>` to get min/max/mean/last per bucket instead (numeric states only) — the
        right call for a whole day. The instants in the output carry their UTC offset; say them
        in the user's local time.

        Sensors whose `state.json` carries a `state_class` (the setup index's `every entity with
        state_class` line) also have `statistics.sh`: Home Assistant's own hourly mean/min/max
        (sum and change for a total such as energy), kept for good, so it is the file for
        anything older than the recorder holds or coarser than a reading — "her average this
        month", "how much energy last week". No arguments = the last 7 days by the hour;
        `--days N` widens it and `--period day|week|month` coarsens it. Raw changes and the
        last few days: `history.sh`; averages, extremes and the long run: `statistics.sh`.

        ### Alarms & reminders

        An alarm or reminder is an event on the **alarms calendar** — the `calendar` entity the
        setup index lists as alarms (e.g. `alarms_(alarms)`); do NOT use `/schedules` for human
        alarms. That directory serves three action files: `create_event.sh`, `get_events.sh` and
        `delete_event.sh`. From the entity directory:
        `exec(command="create_event.sh --summary \"Take out the trash\"
              --start_date_time \"2026-06-19 21:30:00\"
              --description '{\"target\":{\"room\":\"Kitchen\"},\"insistent\":{\"gapSeconds\":30,\"maxRepeats\":5}}'")`

        - `summary` is the spoken message.
        - `start_date_time` is the local wall-clock time. Resolve relative requests
          ("tomorrow at 7", "next Monday at 9") to an absolute date-time yourself; HA
          interprets it in its own timezone (with DST), so you never compute UTC. The end
          defaults to one minute later — omit `end_date_time`.
        - `rrule` makes it recurring (e.g. `--rrule "FREQ=DAILY"` for every day,
          `FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR` for weekdays).
        - `description` is a JSON object with two keys: `target` ({satelliteId |
          satelliteIds | room | all}) and `insistent` (an object with optional
          `gapSeconds`, `maxRepeats`, `maxDurationSeconds`; use `{}` for all defaults).
          Wrap it in single quotes, as above, so its own double quotes need no escaping.
          The alarm repeats on the satellite until the user says "ok nabu" there, or
          the cap is reached. **`insistent` must be present** — omitting it makes a
          one-shot announce, not an alarm.

        This calendar is for times expressed as a clock time or date ("at 7", "tomorrow at
        9:30"), recurring alarms, and anything past the 4-hour timer ceiling. A request phrased
        as a **duration from now** ("remind me in 20 minutes", "avísame en 5 minutos") belongs
        in `/timers` with the message as its `text`, not here.

        Both of those exist to **tell a person something**. A request to **perform an action
        later** — "apaga el aire en una hora", "turn the lights off at midnight", "start the
        washing machine at three" — is neither an alarm nor a timer: it is a `/schedules` one-shot
        whose `prompt` is the HA action to run, however the time is phrased. Never put a command
        in a timer's `text` or a calendar event's `summary`; those are only spoken aloud, so the
        action would never happen. `/schedules` is for agent tasks and must never carry a human
        alarm or reminder (it speaks once at most and skips offline satellites).

        To see, change or cancel alarms: `exec get_events.sh` lists every event with its `uid`
        (no arguments covers the week ahead; `--days N` or `--start_date_time`/`--end_date_time`
        set the window). Cancel with `exec delete_event.sh --uid <uid>` — the uid comes from that
        listing, never from memory; to drop one occurrence of a recurring alarm add
        `--recurrence_id <the listing's recurrence_id>`. There is no update action: to change an
        alarm's time or message, delete it by uid and create the new one. That is internal —
        state the new time and never narrate the delete — and never create a second event beside
        the old one as a way of "moving" it.

        Snooze: when the message context says the user just dismissed an alarm and they ask to
        snooze or be reminded again ("five more minutes"), create a new one-shot event on the
        alarms calendar at the requested offset with the same summary and description.

        ### Watches (reacting to the home)

        A **watch** is a standing instruction the home itself runs when an entity meets a
        condition: "warn me when Laura's sugar goes above 180", "close the blinds when the living
        room passes 27", "tell me when the washing machine finishes". It is a real Home Assistant
        automation, written as a file: `text_create /ha/watches/<id>/watch.json` (`<id>` is a
        descriptive slug you choose, as a schedule's is). The setup index says which watches exist.
        The boundary with the other reactive tools: a clock time or date → the alarms calendar (a
        duration → `/timers`); an action to perform at a time → `/schedules`; **something in the
        home changing → a watch**, never a schedule that polls `history.sh`.

        ```json
        {"name": "Laura's sugar above 180",
         "triggers": [{"trigger": "numeric_state", "entity_id": "sensor.laura_glucose", "above": 180}],
         "conditions": [],
         "effects": [{"kind": "prompt", "prompt": "Laura's sugar crossed 180. Read its history, say what it is and whether it is rising, and warn Fran."}],
         "once": false, "deliverTo": ["telegram"]}
        ```

        - `triggers` (required) and `conditions` (optional) are Home Assistant's own JSON, passed
          through as written, so any entity and any trigger the home understands can be watched.
          The usual shapes: `{"trigger": "numeric_state", "entity_id": "…", "above": 180}` (or
          `"below"`; on a noisy sensor add `"for": {"minutes": 5}` so one bad reading does not
          fire); `{"trigger": "state", "entity_id": "…", "from": "on", "to": "off"}`;
          `{"trigger": "template", "value_template": "{{ … }}"}`. A range ("leaves 70–180") is
          ONE watch with two triggers, one `below` and one `above` — never two watches. "Only at
          night" / "only when I'm home" are conditions: `{"condition": "time", "after":
          "23:00:00", "before": "07:00:00"}`, `{"condition": "state", "entity_id": "person.fran",
          "state": "home"}`.
        - `numeric_state` fires on the **crossing** only, never at creation: a value already past
          the threshold warns at the next crossing, not now. Read `state.json` when you create the
          watch and, if it is already past, say the current value.
        - `effects` (required, in order), each one of:
          - `{"kind": "prompt", "prompt": "…"}` — YOU run the prompt when it fires, as the agent
            that created the watch, and your answer goes to `deliverTo`. This is the default for a
            plain "warn me" / "avísame" / "tell me": you phrase the warning yourself when it fires
            (the value, the trend from `history.sh`). The fire brings you what fired — entity,
            from → to, when — even if the prompt says nothing about it, so "look into it" is enough.
          - `{"kind": "announce", "text": "…", "target": {…}, "insistent": {…}}` — a fixed
            sentence spoken in the home with no agent involved; `target` and `insistent` exactly as
            an alarm's description has them, and `insistent` rings until acknowledged. `target.room`
            is a room from the setup index's `voice satellites:` line, spelled as it is there (or one
            of its satellite ids) — NEVER a Home Assistant area slug like `fran_s_office`; the write
            is refused naming the satellites that exist. Add it only
            when the request carries urgency — "wake me if her sugar drops under 60" is an insistent
            announcement in the bedroom, usually followed by a `prompt` effect so the detail follows
            on chat. It works while the assistant is down.
          - `{"kind": "actions", "actions": [ … ]}` — Home Assistant actions the home performs
            itself, as written (`{"action": "cover.close_cover", "target": {"entity_id":
            "cover.…"}}`): no agent, and it keeps working while the assistant is down.
          Jinja is allowed in `prompt` and `text`: `{{ trigger.to_state.state }}` is the value that
          fired.
        - `deliverTo` (prompt effects only; `channelId[:address]`, as for a schedule) is where your
          answer lands. Omit it and the answer goes where the request came from — the speaking
          satellite on voice, Telegram on Telegram, the chat on the chat — which is what a plain
          "warn me" wants; name it only to deliver somewhere else. Nabu has no Telegram, so on Nabu
          never write or promise Telegram delivery. `userId` as for a schedule.
        - `once: true` for "tell me when X finishes": the watch turns itself off after its first
          fire and then reads `enabled: false` with `spent: true` in `status.json`. Remove spent
          watches (`remove`) when you list them or are asked about them.
        - `enabled: false` pauses a watch ("stop warning me tonight") and `true` resumes it — an edit
          of the same file, never a delete.
        - To change a watch (a new threshold, another room) `text_edit` its `watch.json`: the same
          `<id>` is the same watch, replaced in place, so a change never leaves two watches (a full
          rewrite with `overwrite: true` replaces it too). That is for a change to the reaction that
          exists ("warn me below 65, not 70"). A request that ADDS a reaction beside it — a second
          threshold with its own effect, a night-only alarm next to a daytime warning — is a new
          watch under its own id, and the existing one keeps running as it was: never overwrite a
          watch the user did not ask to change. `remove /ha/watches/<id>` removes it from the home.
        - `status.json` beside it is read-only: `createdAt`, `lastTriggeredAt`, `automationEntity`,
          `spent`. To list watches, `glob /ha/watches/*/` and read each `watch.json`.
        - A watch needs no approval, even one that acts on the home: create it in the same turn.
          Then read it back to the user in one sentence — entity, direction, threshold, any `for`,
          what it does and where it delivers — so a misunderstanding is caught now. A write the home
          refuses comes back with Home Assistant's own message naming the field: fix that field and
          write again.

        ### Music playback

        Music plays through Music Assistant (MA). The MA player for a room is the `media_player`
        whose `state.json` attributes include `app_id: music_assistant` and `mass_player_type`.
        Other media_players (TVs, etc.) also list `music_assistant.*.sh` actions, but MA calls on
        them do nothing — when a room has more than one player, read `state.json` and pick the MA
        one. Default the target to the **speaking room**'s player (the room the request came
        from) unless another room is named; "everywhere" => run it on every room's MA player.

        - Tracks, artists, albums, radio: play directly by name from the player directory:
          `exec music_assistant.play_media.sh --media_id "miles davis"` — add
          `--media_type artist|album|track|radio` to disambiguate. Free-text names resolve
          through the streaming providers.
        - Playlists ("my playlist", "songs I like", "música favorita", any saved list): NEVER guess
          the name. Playlist titles only resolve against the user's MA library, and the words the
          user used to describe the list are almost never its stored title — a request for
          "música favorita" resolves to a title like "Liked Songs dethonv". The rule is mechanical,
          not a judgement call about the phrasing: whenever you pass `--media_type playlist`, the
          `--media_id` value MUST be a title you read from a `browse_media.sh` listing
          in this same turn. List first:
          `exec browse_media.sh --media_content_id playlists --media_content_type music_assistant`
          then play the exact title it returned:
          `exec music_assistant.play_media.sh --media_id "<exact title>" --media_type playlist`.
          Inventing or translating a title (e.g. "Mi música favorita") does not fail cleanly — it
          comes back as a bare HA 500 that says nothing about what went wrong.
        - Podcasts: a SHOW plays by name, but a specific EPISODE never does. `play_media` looks a
          name up across tracks, albums, playlists, artists, radio and shows — never episodes — so an
          episode title resolves to the show and starts its **newest episode**, and reports success
          while doing it. An episode plays only by its exact uri. Get that uri first:
          `exec music_assistant.podcast_episodes.sh --podcast "<show name>" --match "<words from the episode>"`
          then play what it returned:
          `exec music_assistant.play_media.sh --media_id "<the episode's uri>"`.
          `--match` ignores case and accents; drop it to see the most recent titles, and widen it to
          fewer words if nothing matches. Do NOT look an episode up on Spotify or anywhere else on
          the web — that action already returns the id in playable form.
          `browse_media.sh` cannot expand a podcast: it fails with a bare 500 for any show.
        - A 500 from `music_assistant.play_media.sh` means the item could not be resolved (the
          name isn't in the library) — NOT that MA is down. Browse the library and use an exact
          title instead of retrying name variants. Never retry the same call with a reworded name
          or a different uri shape: if a name did not resolve, list the real items and pick one.
          And if the listing does not have it either, say so — the web is no fallback: nothing a
          web search returns is playable on a player, so never go looking online for a station,
          song or show the library could not resolve.
        - `search_media.sh` searches the entire provider catalog (public Spotify etc.), not the
          user's saved items, and the URIs it returns are generally not playable via
          `play_media`. Use it only for content the user doesn't have saved, then play the
          result by its exact title.
        - Do NOT use the bare `play_media.sh` (`media_player.play_media`): it needs a concrete
          `media_content_id`/URI you cannot know. Only `music_assistant.play_media.sh` resolves
          names.
        - Transport: `media_play.sh` / `media_pause.sh` / `media_next_track.sh` / `volume_set.sh`
          on the player.
        - "Play it from the beginning" / "start it over": use
          `exec media_seek.sh --seek_position 1` on the player. Playing the uri again does NOT
          restart it — MA keeps a resume point per podcast episode and audiobook, and every play
          of that item starts there, reporting success while doing it. Stopping first changes
          nothing; the resume point just moves to where you stopped. `media_seek.sh` is the only
          call that restarts, and it needs something already loaded on the player: if the item is
          not playing yet, play it first, then seek.
        - "Rewind/skip forward N minutes": a relative move is an absolute `media_seek.sh`, so read
          `media_position` from the player's `state.json` first and add or subtract from it. That
          read is INPUT to the seek, not a confirmation of it. On a Music Assistant player the
          value comes from the queue and is current (`media_position_source: music_assistant`);
          without that marker it is Home Assistant's own, which only moved when the player last
          started or stopped, so treat it as a floor rather than the true position and say what you
          did. Never treat a rewind as a timer or a reminder — it is a seek on the player and
          nothing else.
        - Grouping (synced multi-room): `join.sh` (`media_player.join`; `--group_members` = the
          other players) to play in sync; `unjoin.sh` (`media_player.unjoin`) to split a room
          back out.
        Music ducks automatically while the satellite speaks — never lower or pause music just to
        talk.

        ### Notes

        - `state.json` always reflects HA's current stored state (nothing is cached
          on our side), but that store lags an action you just issued by the delay
          noted above. So read it only to fetch an attribute you did NOT just change,
          as INPUT to the next action (e.g. `source_list` before `select_source`) —
          never to confirm a change you just made.
        - Area/room ids: HA generates an area's `id` once, as a lowercase slug of its name at
          creation (`Salón` → `salon`), and keeps it fixed even if the area is later renamed.
          So the id is NOT something you can reliably derive yourself from the display name —
          accents, spaces, and past renames make a guess wrong. Read the real value verbatim
          from the `### <room>` heading the entity is listed under in the setup index.
          Whenever an action argument names a room or area, pass that slug, never the display
          name (e.g. a vacuum's `--cleaning_area_id salon`). In `--help`, such arguments are
          typed `AREA_ID` and say so.
        """;
    // Every falsifiable statement the prose above makes, declared in full rather than only where a
    // scenario exists: the rules nothing tests yet are the ones most worth writing down, and the
    // exemption list beside the suite is the backlog.
    public static readonly PromptClaim ExactlyWhatWasAsked =
        new("home.exactly-what-was-asked",
            "A request to change one thing changes exactly that thing, and nothing else in the home moves.");

    public static readonly PromptClaim EntityNamedAsListed =
        new("home.entity-named-as-listed",
            "An entity directory is used with the exact name a listing returned, never a bare id or a guessed friendly-name suffix.");

    public static readonly PromptClaim ArgumentsComeFromHelp =
        new("home.arguments-come-from-help",
            "An action's arguments are read from its --help rather than guessed, and a bad-argument exit is fixed by re-reading it rather than by repeating the same shape.");

    public static readonly PromptClaim ExitCodeIsTheConfirmation =
        new("home.exit-code-is-the-confirmation",
            "The exit code confirms an action; state.json is never re-read afterwards to check that it worked.");

    public static readonly PromptClaim ExitCodesAreNeverVoiced =
        new("home.exit-codes-are-never-voiced",
            "A spoken reply states success or failure in one short clause and never voices an exit code or stderr text.");

    public static readonly PromptClaim AlarmCarriesTargetAndInsistent =
        new("home.alarm-carries-target-and-insistent",
            "A calendar alarm's description carries target and insistent; without insistent it is a one-shot announce rather than an alarm.");

    public static readonly PromptClaim SnoozeIsANewEvent =
        new("home.snooze-is-a-new-event",
            "A snooze after a dismissed alarm is a new one-shot event at the requested offset with the same summary and description.");

    public static readonly PromptClaim AlarmIsCancelledByUid =
        new("home.alarm-is-cancelled-by-uid",
            "An alarm is cancelled by listing the calendar for its uid and deleting by that uid, never left in place.");

    public static readonly PromptClaim AlarmIsChangedByDeleteAndCreate =
        new("home.alarm-is-changed-by-delete-and-create",
            "A change to an alarm is a delete by uid and a fresh create, never a second event beside the old one.");

    public static readonly PromptClaim MusicPlaysOnTheMusicAssistantPlayer =
        new("home.music-plays-on-the-music-assistant-player",
            "Music plays on the media_player whose state says it is a Music Assistant player, defaulting to the speaking room's.");

    public static readonly PromptClaim PlaylistIsBrowsedBeforeItIsPlayed =
        new("home.playlist-is-browsed-before-it-is-played",
            "A playlist is played by an exact title read from a browse listing in the same turn, never by the words the user used.");

    public static readonly PromptClaim EpisodePlaysOnlyByItsUri =
        new("home.episode-plays-only-by-its-uri",
            "A specific podcast episode is looked up first and played by its exact uri, because a name resolves to the show.");

    public static readonly PromptClaim TheWebIsNoMediaFallback =
        new("home.web-is-no-media-fallback",
            "A name the library cannot resolve is reported, never hunted online — nothing a web search returns is playable on a player.");

    public static readonly PromptClaim RestartIsASeek =
        new("home.restart-is-a-seek",
            "Starting an item over is a seek to the beginning; playing it again resumes where it was left.");

    public static readonly PromptClaim RelativeSeekReadsThePositionFirst =
        new("home.relative-seek-reads-the-position-first",
            "A rewind or skip-forward reads media_position and seeks to that value plus or minus the offset, never to a bare offset and never as a timer.");

    public static readonly PromptClaim ThePastIsReadFromHistory =
        new("home.past-is-read-from-history",
            "A question about the past or a trend of a value is answered from history.sh's recorded changes or statistics.sh's compiled rows, never from state.json or repeated reads of it.");

    public static readonly PromptClaim AreaSlugIsReadNotDerived =
        new("home.area-slug-is-read-not-derived",
            "An area id passed to an action is the slug read from the setup index, never one derived from the display name.");

    // Watches: the rules the scenarios check, one claim each, beside the prose above.
    public static readonly PromptClaim ReactingToTheHomeIsAWatch =
        new("home.reacting-to-the-home-is-a-watch",
            "A request to react to an entity changing becomes a watch under /ha/watches, never a schedule, a timer or a calendar event.");

    public static readonly PromptClaim WatchDefaultsToAPromptDeliveredWhereAsked =
        new("home.watch-defaults-to-a-prompt-delivered-where-asked",
            "A plain 'warn me' is a prompt effect delivered to the channel the request came from: the speaking satellite on voice, Telegram on Telegram.");

    public static readonly PromptClaim WatchRangeIsTwoTriggers =
        new("home.watch-range-is-two-triggers",
            "A range to leave is one watch with two triggers, one below and one above, never two watches.");

    public static readonly PromptClaim WatchUrgencyIsAnInsistentAnnouncement =
        new("home.watch-urgency-is-an-insistent-announcement",
            "A request to be woken or warned urgently adds an insistent announce effect in the room named, and a plain warning adds none.");

    public static readonly PromptClaim WatchHomeActionIsAnActionsEffect =
        new("home.watch-home-action-is-an-actions-effect",
            "A watch that acts on the home carries an actions effect in Home Assistant's own action JSON, with no prompt for the agent.");

    public static readonly PromptClaim WatchOneShotUsesOnce =
        new("home.watch-one-shot-uses-once",
            "'Tell me when X finishes' is a watch with once true, so it fires once and turns itself off.");

    public static readonly PromptClaim WatchPauseIsEnabledFalse =
        new("home.watch-pause-is-enabled-false",
            "Pausing a watch writes enabled false on the existing watch, and never deletes it.");

    public static readonly PromptClaim WatchChangeReplacesInPlace =
        new("home.watch-change-replaces-in-place",
            "A change to a watch is written to the same watch id, so the home never holds two watches for one request.");

    public static readonly PromptClaim WatchIsRemovedByDelete =
        new("home.watch-is-removed-by-delete",
            "Removing a watch deletes its directory under /ha/watches, so it is gone from the home as well as the list.");

    public static readonly PromptClaim WatchIsReadBackOnCreation =
        new("home.watch-is-read-back-on-creation",
            "After creating a watch the reply states what it watches, the threshold or state, what it does and where it delivers.");

    public static readonly PromptClaim WatchNeedsNoApproval =
        new("home.watch-needs-no-approval",
            "A watch is created in the same turn it is asked for, without an approval round trip, even when it will act on the home.");

    public static readonly PromptClaim WatchCrossingOnlyIsSaid =
        new("home.watch-crossing-only-is-said",
            "When the value is already past the threshold at creation, the reply says the current value and that the watch fires at the next crossing.");

    public static readonly PromptClaim NoisySensorWatchUsesFor =
        new("home.noisy-sensor-watch-uses-for",
            "A watch on a noisy sensor requires the condition to hold with a for duration rather than firing on one reading.");

    public static readonly PromptClaim SpentWatchIsCleanedUp =
        new("home.spent-watch-is-cleaned-up",
            "A spent one-shot watch is deleted when watches are listed or asked about.");

    public static readonly PromptClaim NabuNeverDeliversToTelegram =
        new("home.nabu-never-delivers-to-telegram",
            "A watch created on Nabu never names Telegram as its delivery.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        ExactlyWhatWasAsked,
        EntityNamedAsListed,
        ArgumentsComeFromHelp,
        ExitCodeIsTheConfirmation,
        ExitCodesAreNeverVoiced,
        AlarmCarriesTargetAndInsistent,
        SnoozeIsANewEvent,
        AlarmIsCancelledByUid,
        AlarmIsChangedByDeleteAndCreate,
        MusicPlaysOnTheMusicAssistantPlayer,
        PlaylistIsBrowsedBeforeItIsPlayed,
        EpisodePlaysOnlyByItsUri,
        TheWebIsNoMediaFallback,
        RestartIsASeek,
        RelativeSeekReadsThePositionFirst,
        AreaSlugIsReadNotDerived,
        ThePastIsReadFromHistory,
        ReactingToTheHomeIsAWatch,
        WatchDefaultsToAPromptDeliveredWhereAsked,
        WatchRangeIsTwoTriggers,
        WatchUrgencyIsAnInsistentAnnouncement,
        WatchHomeActionIsAnActionsEffect,
        WatchOneShotUsesOnce,
        WatchPauseIsEnabledFalse,
        WatchChangeReplacesInPlace,
        WatchIsRemovedByDelete,
        WatchIsReadBackOnCreation,
        WatchNeedsNoApproval,
        WatchCrossingOnlyIsSaid,
        NoisySensorWatchUsesFor,
        SpentWatchIsCleanedUp,
        NabuNeverDeliversToTelegram
    ];
}