namespace Domain.Prompts;

// That a schedule is a deferred action of the agent's own (Sections/scheduling_prompt.md), served by
// mcp-scheduling. It names no agent: it is fetched for nobody and cached for every household, and a
// household's own agents are the directories its /schedules lists.
public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Prompt = _document.Text;

    public static readonly PromptClaim NeverAHumanReminder =
        _document.Claim("scheduling.never-a-human-reminder");

    public static readonly PromptClaim DeferredActionIsASchedule =
        _document.Claim("scheduling.deferred-action-is-a-schedule");
}