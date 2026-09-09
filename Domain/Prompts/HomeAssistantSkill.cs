namespace Domain.Prompts;

// The doing rules of the home, loaded when a request is about a device, its past, an alarm on the
// calendar or music on a player. Which mechanism a request is — a timer, an alarm, a watch, a
// schedule — and that an area id is read rather than derived stay in the home guide, because the
// model decides those before it loads anything; everything it needs while acting is here
// (docs/adr/0039).
public static class HomeAssistantSkill
{
    public const string Name = "home-assistant";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Doing anything in the home through `/ha`: switching, setting or reading a device (\"turn on the AC\", \"what is the thermostat at\"), its past (\"how was her glucose overnight\"), a future alarm or reminder on the alarms calendar (\"wake me at seven\", \"move or remove the trash alarm\"), music, radio or a podcast on a room's player. Not for anything ringing right now (that is `/timers`, whatever it was created as), a countdown, a watch or a scheduled task. The entity layout, the exec workflow, how a result is read, history and statistics, alarm events, Music Assistant playback.";

    public const string Body =
        """
        The setup index (`/ha/setup-index.md`) is read in the same turn as this skill: it names every
        entity, the actions each class admits and the rooms a voice can reach. Re-read it when a name
        does not resolve.

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

        1. Find the entity in the setup index you read; `domain__filesystem__glob` under `/ha/entities/<class>` or
           `/ha/areas/<room>` only for what the index does not settle. Do NOT glob to discover actions:
           the setup index lists them per class, and action files live in the entity
           directory, so a glob of `/ha/entities/<class>/*.sh` returns nothing.
        2. Inspect when you need an attribute as input: `domain__filesystem__file_read` on
           `/ha/.../state.json`.
        3. Learn an action's arguments: `domain__filesystem__exec` of `<service>.sh --help`. The `.sh` files are
           action stubs, not scripts — don't `file_read` them; `--help` prints the field list.
        4. Act: `domain__filesystem__exec` from the entity directory, e.g.
           `domain__filesystem__exec(path="/ha/entities/light/kitchen_(kitchen)", command="turn_on.sh --brightness_pct 60")`.

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
        setup index lists as alarms (e.g. `assistant_alarms_(assistant-alarms)` — the object id keeps
        its underscores and only the friendly half is hyphenated, so read the name rather than
        normalising one half to the other). That directory serves three action
        files: `create_event.sh`, `get_events.sh` and `delete_event.sh`. From the entity directory:
        `domain__filesystem__exec(command="create_event.sh --summary \"Take out the trash\"
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

        To see, change or cancel alarms: `domain__filesystem__exec get_events.sh` lists every event with its `uid`
        (no arguments covers the week ahead; `--days N` or `--start_date_time`/`--end_date_time`
        set the window). Cancel with `domain__filesystem__exec delete_event.sh --uid <uid>` — the uid comes from that
        listing, never from memory; to drop one occurrence of a recurring alarm add
        `--recurrence_id <the listing's recurrence_id>`. There is no update action: to change an
        alarm's time or message, delete it by uid and create the new one. That is internal —
        state the new time and never narrate the delete — and never create a second event beside
        the old one as a way of "moving" it.

        Snooze: the new one-shot event after a dismissed alarm keeps the dismissed alarm's summary
        and description.

        ### Music playback

        Music plays through Music Assistant (MA). The MA player for a room is the `media_player`
        whose `state.json` attributes include `app_id: music_assistant` and `mass_player_type`.
        Other media_players (TVs, etc.) also list `music_assistant.*.sh` actions, but MA calls on
        them do nothing — when a room has more than one player, read `state.json` and pick the MA
        one. Default the target to the **speaking room**'s player (the room the request came
        from) unless another room is named; "everywhere" => run it on every room's MA player.

        - Tracks, artists, albums, radio: play directly by name from the player directory:
          `domain__filesystem__exec music_assistant.play_media.sh --media_id "miles davis"` — add
          `--media_type artist|album|track|radio` to disambiguate. Free-text names resolve
          through the streaming providers.
        - Playlists ("my playlist", "songs I like", "música favorita", any saved list): NEVER guess
          the name. Playlist titles only resolve against the user's MA library, and the words the
          user used to describe the list are almost never its stored title — a request for
          "música favorita" resolves to a title like "Liked Songs dethonv". The rule is mechanical,
          not a judgement call about the phrasing: whenever you pass `--media_type playlist`, the
          `--media_id` value MUST be a title you read from a `browse_media.sh` listing
          in this same turn. List first:
          `domain__filesystem__exec browse_media.sh --media_content_id playlists --media_content_type music_assistant`
          then play the exact title it returned:
          `domain__filesystem__exec music_assistant.play_media.sh --media_id "<exact title>" --media_type playlist`.
          Inventing or translating a title (e.g. "Mi música favorita") does not fail cleanly — it
          comes back as a bare HA 500 that says nothing about what went wrong.
        - Podcasts: a SHOW plays by name, but a specific EPISODE never does. `play_media` looks a
          name up across tracks, albums, playlists, artists, radio and shows — never episodes — so an
          episode title resolves to the show and starts its **newest episode**, and reports success
          while doing it. An episode plays only by its exact uri. Get that uri first:
          `domain__filesystem__exec music_assistant.podcast_episodes.sh --podcast "<show name>" --match "<words from the episode>"`
          then play what it returned:
          `domain__filesystem__exec music_assistant.play_media.sh --media_id "<the episode's uri>"`.
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
          `domain__filesystem__exec media_seek.sh --seek_position 1` on the player. Playing the uri again does NOT
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
        """;

    public static readonly SkillText Text = new(Name, Description, Body);

    // The trigger claim: a request of this kind loads the skill. Cited by every home and music
    // scenario, so a skill nobody loads shows as a red description rather than a red body.
    public static readonly PromptClaim LoadsForAHomeRequest =
        new("home-assistant.loads-for-a-home-request",
            "A request to switch, set or read a device, ask about its past, set or change an alarm, or play something loads the home-assistant skill before the home is touched.");

    // Every falsifiable statement the prose above makes, declared in full rather than only where a
    // scenario exists: the rules nothing tests yet are the ones most worth writing down, and the
    // exemption list beside the suite is the backlog.
    public static readonly PromptClaim EntityNamedAsListed =
        new("home-assistant.entity-named-as-listed",
            "An entity directory is used with the exact name a listing returned, never a bare id or a guessed friendly-name suffix.");

    public static readonly PromptClaim ArgumentsComeFromHelp =
        new("home-assistant.arguments-come-from-help",
            "An action's arguments are read from its --help rather than guessed, and a bad-argument exit is fixed by re-reading it rather than by repeating the same shape.");

    public static readonly PromptClaim ExitCodeIsTheConfirmation =
        new("home-assistant.exit-code-is-the-confirmation",
            "The exit code confirms an action; state.json is never re-read afterwards to check that it worked.");

    public static readonly PromptClaim ExitCodesAreNeverVoiced =
        new("home-assistant.exit-codes-are-never-voiced",
            "A spoken reply states success or failure in one short clause and never voices an exit code or stderr text.");

    public static readonly PromptClaim AlarmCarriesTargetAndInsistent =
        new("home-assistant.alarm-carries-target-and-insistent",
            "A calendar alarm's description carries target and insistent; without insistent it is a one-shot announce rather than an alarm.");

    public static readonly PromptClaim SnoozeIsANewEvent =
        new("home-assistant.snooze-is-a-new-event",
            "A snooze after a dismissed alarm is a new one-shot event at the requested offset with the same summary and description.");

    public static readonly PromptClaim AlarmIsCancelledByUid =
        new("home-assistant.alarm-is-cancelled-by-uid",
            "An alarm is cancelled by listing the calendar for its uid and deleting by that uid, never left in place.");

    public static readonly PromptClaim AlarmIsChangedByDeleteAndCreate =
        new("home-assistant.alarm-is-changed-by-delete-and-create",
            "A change to an alarm is a delete by uid and a fresh create, never a second event beside the old one.");

    public static readonly PromptClaim MusicPlaysOnTheMusicAssistantPlayer =
        new("home-assistant.music-plays-on-the-music-assistant-player",
            "Music plays on the media_player whose state says it is a Music Assistant player, defaulting to the speaking room's.");

    public static readonly PromptClaim PlaylistIsBrowsedBeforeItIsPlayed =
        new("home-assistant.playlist-is-browsed-before-it-is-played",
            "A playlist is played by an exact title read from a browse listing in the same turn, never by the words the user used.");

    public static readonly PromptClaim EpisodePlaysOnlyByItsUri =
        new("home-assistant.episode-plays-only-by-its-uri",
            "A specific podcast episode is looked up first and played by its exact uri, because a name resolves to the show.");

    public static readonly PromptClaim TheWebIsNoMediaFallback =
        new("home-assistant.web-is-no-media-fallback",
            "A name the library cannot resolve is reported, never hunted online — nothing a web search returns is playable on a player.");

    public static readonly PromptClaim RestartIsASeek =
        new("home-assistant.restart-is-a-seek",
            "Starting an item over is a seek to the beginning; playing it again resumes where it was left.");

    public static readonly PromptClaim RelativeSeekReadsThePositionFirst =
        new("home-assistant.relative-seek-reads-the-position-first",
            "A rewind or skip-forward reads media_position and seeks to that value plus or minus the offset, never to a bare offset and never as a timer.");

    public static readonly PromptClaim ThePastIsReadFromHistory =
        new("home-assistant.past-is-read-from-history",
            "A question about the past or a trend of a value is answered from history.sh's recorded changes or statistics.sh's compiled rows, never from state.json or repeated reads of it.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        LoadsForAHomeRequest,
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
        ThePastIsReadFromHistory
    ];
}