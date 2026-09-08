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

        Whatever is ringing right now on a satellite — a timer or an alarm, whichever it was
        created as — is silenced through `/timers`, never through the calendar or the home. Before
        you create, read, change, cancel or silence a timer, or silence a ringing alarm, load the
        `countdown-timers` skill: the file's shape, the target rules, status, listing, the
        change-by-recreate rule and the dismiss action are there.
        """;

    // The choosing rules' claims: which mechanism a request is, decided before any skill is
    // loaded. The doing rules — the file, the target, status, dismiss — are claims of the
    // countdown-timers skill.
    public static readonly PromptClaim DurationIsACountdown =
        new("timers.duration-is-a-countdown",
            "A duration up to four hours, where a person is being told something, becomes a /timers countdown.");

    public static readonly PromptClaim AgentActsIsAScheduledTask =
        new("timers.agent-acts-is-a-scheduled-task",
            "A request where the agent itself must act at the appointed moment becomes a /schedules one-shot with an absolute runAt, however the time was phrased.");

    public static readonly PromptClaim ClockTimeIsACalendarAlarm =
        new("timers.clock-time-is-a-calendar-alarm",
            "A clock time, a date, anything recurring, or anything past the four-hour ceiling goes on the Home Assistant alarms calendar.");

    public static readonly PromptClaim SchedulesAreNeverHumanReminders =
        new("timers.schedules-are-never-human-reminders",
            "/schedules is used for agent tasks only, never for a human alarm or reminder.");

    public static readonly PromptClaim DurationCappedAtFourHours =
        new("timers.duration-capped-at-four-hours",
            "durationSeconds is never written above four hours.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        DurationIsACountdown,
        AgentActsIsAScheduledTask,
        ClockTimeIsACalendarAlarm,
        SchedulesAreNeverHumanReminders,
        DurationCappedAtFourHours
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