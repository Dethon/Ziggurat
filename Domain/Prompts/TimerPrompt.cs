using Domain.DTOs.Voice;
using Domain.Tools.FileSystem;

namespace Domain.Prompts;

public static class TimerPrompt
{
    public const string Name = "timers_prompt";
    public const string Description =
        "Explains how to manage short countdown timers via the /timers filesystem";

    public static readonly string Prompt = $$"""
        ## Timers

        Short countdowns ("set a timer for 5 minutes", "pasta timer for 8 minutes") live in the
        virtual filesystem at `/timers` — NOT the Home Assistant alarms calendar (that is for
        clock-time alarms and reminders) and NOT `/schedules` (agent tasks). When a timer expires
        it rings insistently (tone + spoken message) on the target satellites until the user says
        the wake word there, presses the button, or a repeat cap is reached.

        Choosing the mechanism — decide in two steps.

        **First: at the appointed moment, does something have to HAPPEN, or does a person have to
        be TOLD?** If it is you who must act when the moment comes — turn off the air conditioning,
        start the washing machine, check whether a download finished — that is a `/schedules` one-shot:
        work out the absolute time yourself and put it in `runAt`. This holds
        **however the time is phrased**, so "apaga el aire en una hora" is a scheduled task, not a
        one-hour timer. A timer only speaks a message when it fires, so it can never turn anything
        off. Conversely, when the person is the one who will act ("recuérdame en 10 minutos que
        apague el aire"), they are being told something — that is step two.

        **Second — only when a person is being told something** — go by HOW the time is expressed,
        not the wording: a duration from now up to 4 hours ("timer for 10 minutes",
        "avísame en 5 minutos", "remind me in 20 minutes") is a `/timers` countdown — put the
        message to speak in `text`. A clock time or date ("wake me at 7", "tomorrow at 9:30"),
        anything recurring, or anything past the 4-hour ceiling goes on the HA alarms calendar: it
        survives restarts and can escalate to the phone. `/schedules` is only for agent tasks,
        never for human alarms or reminders.

        - Create: `{{VfsTextCreateTool.Name}}` at `/timers/<descriptive-id>/timer.json` with JSON
          `{"durationSeconds": <int>, "text"?: "<spoken message>", "target": {...} }`.
          `durationSeconds` is capped at 4 hours — for anything longer use the alarms calendar.
          `target` is `{satelliteId | satelliteIds | room | all}`. On a voice turn, default to the
          **speaking room** (the room this request came from) unless another room is named. On any
          other channel there is no speaking room — ask which room or satellite it should ring on
          before creating the timer, and never guess (a timer rings only on its target satellites,
          so a wrong or absent one either rings in the wrong place or fails to arm). When `text` is
          omitted the timer announces itself as "<id> timer", so pick a descriptive id (e.g. `pasta`).
          `text` is spoken to a person and is **never an instruction** to be carried out — if what
          you are about to write there is a command ("apaga el aire"), you want a `/schedules`
          one-shot instead.
        - Time left: `{{VfsFileReadTool.Name}}` on `/timers/<id>/status.json` → `remainingSeconds`
          and `firesAt`. When your reply is spoken, give only the remaining time; in a written reply
          include `firesAt` if the user asked when it fires.
        - List: `{{VfsGlobFilesTool.Name}}` on `/timers`.
        - Cancel: `{{VfsRemoveTool.Name}}` on `/timers/<id>`.
        - Timers are immutable and fire once — to change one, delete it and create a new one; to
          extend one just dismissed ("two more minutes"), create a new timer. This is internal —
          never mention deleting or recreating, just state the new time.
        - To change a **running** timer ("add five minutes to the pasta timer"): read its
          `status.json` for `remainingSeconds`, delete the timer, and recreate it with the
          adjusted remainder.
        - Stop ringing: when the user asks to stop or dismiss a ringing alarm/timer (from any room
          or any channel), `{{VfsExecTool.Name}}` `dismiss.sh` at `/timers` — it silences everything
          currently ringing on all satellites. A fired timer no longer appears under `/timers`;
          `dismiss.sh` is the only way to silence it remotely.
        """;

    // Every falsifiable statement the prose above makes, in the order it makes them. Declared in
    // full rather than only where a scenario exists: the rules nothing tests yet are exactly the
    // ones most worth writing down, and the exemption list beside the suite is the backlog.
    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        new("timers.duration-is-a-countdown",
            "A duration up to four hours, where a person is being told something, becomes a /timers countdown."),
        new("timers.agent-acts-is-a-scheduled-task",
            "A request where the agent itself must act at the appointed moment becomes a /schedules one-shot with an absolute runAt, however the time was phrased."),
        new("timers.clock-time-is-a-calendar-alarm",
            "A clock time, a date, anything recurring, or anything past the four-hour ceiling goes on the Home Assistant alarms calendar."),
        new("timers.schedules-are-never-human-reminders",
            "/schedules is used for agent tasks only, never for a human alarm or reminder."),
        new("timers.created-at-its-own-path",
            "A countdown is created as JSON at /timers/<descriptive-id>/timer.json carrying durationSeconds and an optional spoken text."),
        new("timers.duration-capped-at-four-hours",
            "durationSeconds is never written above four hours."),
        new("timers.voice-targets-the-speaking-room",
            "On a voice turn the timer targets the room the request came from, unless another room is named."),
        new("timers.no-satellite-asks-which-room",
            "On a turn with no speaking room the agent asks which room or satellite before creating anything, and never guesses one."),
        new("timers.text-is-spoken-never-an-instruction",
            "The text of a timer is a message spoken to a person, never a command to be carried out."),
        new("timers.status-is-read-for-time-left",
            "How long is left is read from /timers/<id>/status.json rather than calculated."),
        new("timers.spoken-status-gives-only-the-remaining-time",
            "A spoken reply about a running timer gives the remaining time alone, without the firing time."),
        new("timers.listed-by-glob",
            "The timers that exist are listed by globbing /timers."),
        new("timers.cancelled-by-removing-it",
            "A timer is cancelled by removing /timers/<id>."),
        new("timers.changed-by-delete-and-recreate",
            "A running timer is changed by reading its status, deleting it, and creating its replacement, in that order."),
        new("timers.recreation-is-never-narrated",
            "The delete-and-recreate is internal: the reply states the new time and never mentions deleting or recreating."),
        new("timers.ringing-is-stopped-by-dismiss",
            "A request to stop or dismiss a ringing timer runs dismiss.sh at /timers, from any room and any channel.")
    ];

    // The roster comes live from the hub at prompt-fetch time; an empty roster (hub unreachable —
    // the fail-open path) degrades to the static idiom text, which already tells the agent to ask.
    public static string Build(IReadOnlyList<SatelliteDescriptor> satellites) =>
        satellites.Count == 0
            ? Prompt
            : string.Join("\n", [
                Prompt,
                "",
                "### Voice satellites",
                "",
                "The satellites a timer can ring on — each entry is the stable satellite id and its room:",
                "",
                .. satellites.Select(s => $"- {s.Id} — {s.Room}"),
                "",
                "When asking which room a timer should ring in, offer these rooms instead of asking "
                + "blind; target by `room` or by exact satellite id."
            ]);
}