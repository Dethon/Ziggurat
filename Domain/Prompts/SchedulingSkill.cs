namespace Domain.Prompts;

// The doing rules of a schedule, loaded when a request creates, changes, runs or removes one.
// That a schedule is a deferred action and never a human reminder stays in the scheduling
// prompt, because the model chooses the mechanism before it loads anything; everything it
// needs while writing the file is here (docs/adr/0039). Built per deployment, like the prompt,
// because the time zone the cron and runAt are read in is the server's.
public static class SchedulingSkill
{
    public const string Name = "scheduling";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Creating, changing, running now or removing a scheduled task under `/schedules` — an action the agent itself performs later, once or on a cron (\"turn the air off in an hour\", \"every morning at nine send me the news\", \"cancel the nightly check\"). Not for a human alarm, reminder or timer. The layout, which agent's directory, the schedule.json fields, cron and runAt in the local zone, delivery targets, and the managing actions.";

    public static string Body(string zoneId) =>
        $$"""
        ### Layout

        - `/schedules` — the root. Each immediate child directory is an **agent** you can schedule work for.
        - `/schedules/<agentId>/agent_info.json` — read this to learn what *another* agent does before scheduling against it.
        - `/schedules/<agentId>/<scheduleId>/schedule.json` — one schedule. `<scheduleId>` is a descriptive, unique id you choose (e.g. `morning-news`).
        - `/schedules/<agentId>/<scheduleId>/status.json` — read-only timing: `createdAt`, `lastRunAt`, `nextRunAt`, shown in the **{{zoneId}}** time zone.

        ### Which agent to schedule against

        **Schedule against yourself** — the agent directory whose `agent_info.json` `name` is your own — **unless the user names another agent**. The directory you write to decides who runs the prompt later and where the result is delivered; nothing routes it back to you afterwards. So another agent's directory means someone else does the work and answers on their own channel, not the one you are talking on.

        Use another agent's directory only when the user asked for that agent by name, or when the task needs a tool you genuinely do not have. Your own description says how you talk, not what you can do — never read it as a reason to hand work away. The `agent_info.json` blurbs exist for that narrow choice, not for delegating tasks you can run yourself.

        ### Creating a schedule

        `domain__filesystem__text_create` a `schedule.json` whose content is a JSON object:

        - `prompt` (required) — the instruction delivered to the agent when the schedule fires.
        - `cron` **or** `runAt` — exactly one is required, and they are mutually exclusive.
          - `cron` — a standard 5-field cron expression for a **recurring** schedule. Times are interpreted in the **{{zoneId}}** time zone and adjust automatically across daylight-saving changes. Examples:
            - `"0 9 * * *"` — every day at 09:00 {{zoneId}} time
            - `"0 */2 * * *"` — every 2 hours
            - `"30 14 * * 1-5"` — weekdays at 14:30 {{zoneId}} time
          - `runAt` — an ISO-8601 datetime for a **one-shot** schedule. You may include a time zone — `Z` for UTC (e.g. `2026-06-01T14:30:00Z`) or an explicit offset (e.g. `2026-06-01T16:30:00+02:00`) — or omit it, in which case it is read as **{{zoneId}}** local time (e.g. `2026-06-01T18:00:00`). It is stored as UTC and deleted automatically once it fires.
        - `userId` (optional) — the user the fired prompt should be attributed to.
        - `deliverTo` (optional) — a list of channel ids that should receive the result (e.g. `["signalr", "telegram"]`). Omit to use the configured default.

          **Voice delivery (speak the result aloud).** A `deliverTo` entry may target the voice channel:
          - `"voice"` or `"voice:all"` — speak on every voice satellite.
          - `"voice:<satelliteId>"` — speak on one specific satellite (e.g. `"voice:office-01"`).
          - Repeat `"voice:<satelliteId>"` for several specific satellites — each is spoken once, e.g. `["signalr", "voice:office-01", "voice:kitchen-01"]`.

          Add a voice target **only when the user explicitly asked to be notified by voice** (spoken aloud / announced). Otherwise omit voice — **silence is the default**. For example, a schedule that starts the air conditioning at night must NOT announce. Offline satellites are skipped silently. To keep tool-approval prompts answerable, list a non-voice channel first, e.g. `["signalr", "voice:fran-office-01"]`.

        A recurring schedule — every day at 09:00 {{zoneId}} time:

        ```json
        {
          "prompt": "Summarize today's tech news and send me the highlights",
          "cron": "0 9 * * *",
          "deliverTo": ["signalr"]
        }
        ```

        A one-shot schedule — fires once, then deletes itself:

        ```json
        {
          "prompt": "Check whether the media library import finished and report the result",
          "runAt": "2026-06-01T14:30:00"
        }
        ```

        ### Managing schedules

        - **Discover** — `domain__filesystem__glob` on `/schedules` to list agents, then the same on
          `/schedules/<agentId>` to list their schedules.
        - **Change** — `domain__filesystem__text_edit` the `schedule.json` to adjust the prompt, timing, or delivery.
        - **Reassign / rename** — `domain__filesystem__move` a schedule directory to a different `<agentId>` or `<scheduleId>`.
        - **Remove** — `domain__filesystem__remove` the schedule directory.
        - **Run now** — `domain__filesystem__exec` of `run_now.sh` on a schedule directory to fire it immediately without waiting for its next scheduled time.
        """;

    public static SkillText For(string zoneId) => new(Name, Description, Body(zoneId));

    // The trigger claim: a request of this kind loads the skill. Cited by the scenario that
    // writes a schedule, so a skill nobody loads shows as a red description rather than a red
    // body. The prose declares no other claim yet, as the section declared none.
    public static readonly PromptClaim LoadsForAScheduleRequest =
        new("scheduling.loads-for-a-schedule-request",
            "A request to create, change, run or remove a scheduled task loads the scheduling skill before /schedules is written to.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [LoadsForAScheduleRequest];
}