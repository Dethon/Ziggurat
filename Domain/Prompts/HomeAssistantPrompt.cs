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

        ### Alarms & reminders

        To set an alarm or reminder, use the `calendar.create_event` service on the
        alarms calendar (`calendar.assistant_alarms`) — do NOT use `/schedules` for
        human alarms. From that entity directory:
        `exec(command="create_event.sh --summary \"Take out the trash\"
              --start_date_time \"2026-06-19 21:30:00\"
              --description \"{\\\"target\\\":{\\\"room\\\":\\\"Kitchen\\\"},\\\"insistent\\\":{\\\"gapSeconds\\\":30,\\\"maxRepeats\\\":5}}\"")`

        - `summary` is the spoken message.
        - `start_date_time` is the local wall-clock time. Resolve relative requests
          ("tomorrow at 7", "next Monday at 9") to an absolute date-time yourself; HA
          interprets it in its own timezone (with DST), so you never compute UTC.
        - `rrule` makes it recurring (e.g. `--rrule "FREQ=DAILY"` for every day,
          `FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR` for weekdays).
        - `description` is a JSON object with two keys: `target` ({satelliteId |
          satelliteIds | room | all}) and `insistent` (an object with optional
          `gapSeconds`, `maxRepeats`, `maxDurationSeconds`; use `{}` for all defaults).
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

        To change or cancel: list with `exec get_events.sh ...`, then
        `exec delete_event.sh ...` / `exec update_event.sh ...` on the event.

        Snooze: when the message context says the user just dismissed an alarm and they ask to
        snooze or be reminded again ("five more minutes"), create a new one-shot event on the
        alarms calendar at the requested offset with the same summary and description.

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

    public static readonly PromptClaim AreaSlugIsReadNotDerived =
        new("home.area-slug-is-read-not-derived",
            "An area id passed to an action is the slug read from the setup index, never one derived from the display name.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        ExactlyWhatWasAsked,
        EntityNamedAsListed,
        ArgumentsComeFromHelp,
        ExitCodeIsTheConfirmation,
        ExitCodesAreNeverVoiced,
        AlarmCarriesTargetAndInsistent,
        SnoozeIsANewEvent,
        MusicPlaysOnTheMusicAssistantPlayer,
        PlaylistIsBrowsedBeforeItIsPlayed,
        EpisodePlaysOnlyByItsUri,
        TheWebIsNoMediaFallback,
        RestartIsASeek,
        AreaSlugIsReadNotDerived
    ];
}