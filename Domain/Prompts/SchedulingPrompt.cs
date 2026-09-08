namespace Domain.Prompts;

public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    public const string Prompt =
        """
        ## Scheduled Tasks

        You can schedule prompts to run later — once at a future time, or repeatedly on a cron schedule. Schedules live in the virtual filesystem mounted at `/schedules`, one directory per agent, and you manage them entirely with the `domain__filesystem__*` tools. When a schedule fires, its prompt is delivered to an agent as if a user had sent it. Before you create, change, run or remove one, load the `scheduling` skill: the layout, which agent's directory to write in, the file's fields, cron and `runAt`, delivery and the managing actions are there.

        `/schedules` is **not an alarm clock**: human alarms, wake-ups, and reminders belong on the HA alarms calendar (clock times, recurring) or in `/timers` (durations from now, e.g. "in 20 minutes") — both ring insistently until acknowledged. A schedule's voice delivery speaks once at most and skips offline satellites — never use it to remind a person of something.

        It **is** the right home for a **deferred action** — anything where *you* have to do something later rather than tell someone something: "turn the air conditioning off in an hour", "start the washing machine at three", "check tomorrow whether the import finished". A duration ("in an hour") **does not make it a timer**: a timer only speaks a message when it fires, so it can never switch anything off. Work the duration out into an absolute `runAt` and put the action in `prompt`.
        """;

    // The choosing rules' claims: what is decided before the scheduling skill is loaded. The file's
    // rules are the skill's, which declares only its trigger so far.
    public static readonly PromptClaim NeverAHumanReminder =
        new("scheduling.never-a-human-reminder",
            "A human alarm, wake-up or reminder is never written as a schedule; it goes to the alarms calendar or to /timers.");

    public static readonly PromptClaim DeferredActionIsASchedule =
        new("scheduling.deferred-action-is-a-schedule",
            "An action the agent itself must perform later becomes a schedule with an absolute runAt, however the time was phrased.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [NeverAHumanReminder, DeferredActionIsASchedule];
}