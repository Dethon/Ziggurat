namespace Domain.Prompts;

// That a schedule is a deferred action of the agent's own, and the live agent list the server
// appends (Sections/scheduling_prompt.md), served by mcp-scheduling.
public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    private static readonly PromptTemplate _prompt = _document.Template("setup");

    // The words a setup summary is made of; ScheduleSetupSummary fills them from the agent catalog.
    internal static readonly PromptTemplate Setup = _document.Part("setup", "directories", "agents");

    // The words with no setup: what a deployment with no agents serves.
    public static readonly string Prompt = _prompt.Fill(("setup", ""));

    public static readonly PromptClaim NeverAHumanReminder =
        _document.Claim("scheduling.never-a-human-reminder");

    public static readonly PromptClaim DeferredActionIsASchedule =
        _document.Claim("scheduling.deferred-action-is-a-schedule");

    public static string Build(string setup) =>
        string.IsNullOrEmpty(setup) ? Prompt : _prompt.Fill(("setup", "\n\n" + setup));
}