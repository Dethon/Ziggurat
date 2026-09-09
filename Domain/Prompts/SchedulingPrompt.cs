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

        A schedule is a **deferred action of your own** — "turn the air conditioning off in an hour", "start the washing machine at three", "check tomorrow whether the import finished" — with the absolute time worked out into `runAt` and the action in `prompt`, however the time was phrased. It is **not an alarm clock**: a human alarm, wake-up or reminder never goes here, because a schedule speaks once at most and skips offline satellites. Which of the alarms calendar, `/timers` and `/schedules` a request is, is decided once, under **Which mechanism** in the Timers section.
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