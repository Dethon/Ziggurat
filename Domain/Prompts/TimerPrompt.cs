using Domain.DTOs.Voice;

namespace Domain.Prompts;

// Which mechanism a moment is (Sections/timers_prompt.md), served by mcp-timers without a roster: the
// prompt is cached once for every household, so a household's satellites are read per call from
// /timers/satellites.json (TimersSystemPrompt).
public static class TimerPrompt
{
    public const string Name = "timers_prompt";
    public const string Description =
        "Explains how to manage short countdown timers via the /timers filesystem";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    private static readonly PromptTemplate _prompt = _document.Template("satellites");

    private static readonly PromptTemplate _roster = _document.Part("satellites", "roster");

    // The words with no roster: what a server serves when the hub cannot be asked.
    public static readonly string Prompt = _prompt.Fill(("satellites", ""));

    public static readonly PromptClaim DurationIsACountdown =
        _document.Claim("timers.duration-is-a-countdown");

    public static readonly PromptClaim AgentActsIsAScheduledTask =
        _document.Claim("timers.agent-acts-is-a-scheduled-task");

    public static readonly PromptClaim NoHomeReminderIsASchedule =
        _document.Claim("timers.no-home-reminder-is-a-schedule");

    public static readonly PromptClaim ClockTimeIsACalendarAlarm =
        _document.Claim("timers.clock-time-is-a-calendar-alarm");

    public static readonly PromptClaim SchedulesAreNeverHumanReminders =
        _document.Claim("timers.schedules-are-never-human-reminders");

    public static readonly PromptClaim DurationCappedAtFourHours =
        _document.Claim("timers.duration-capped-at-four-hours");

    // Only tests fill a roster; mcp-timers always serves Build([]), since its prompt is cached for
    // every household (TimersSystemPrompt).
    public static string Build(IReadOnlyList<SatelliteDescriptor> satellites) =>
        satellites.Count == 0
            ? Prompt
            : _prompt.Fill(("satellites", "\n\n" + _roster.Fill(
                ("roster", string.Join("\n", satellites.Select(s => $"- {s.Id} — {s.Room}"))))));
}