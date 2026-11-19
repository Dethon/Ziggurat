namespace Domain.Prompts;

// The doing rules of a glucose watch, loaded when a request is about being warned of a person's
// glucose. Sugary serves it beside its log, so a household with no home still has it; the home's own
// watches keep theirs (home-watches), and the two descriptions say which is which (docs/adr/0039).
// Its words and claims are Skills/glucose-watches.md; this is the handle the server and the scenarios hold.
public static class GlucoseWatchesSkill
{
    public const string Name = "glucose-watches";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static SkillText Text { get; } = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAGlucoseWatchRequest =
        _document.Claim("glucose-watches.loads-for-a-glucose-watch-request");

    public static readonly PromptClaim PlainWarningIsAPrompt =
        _document.Claim("glucose-watches.plain-warning-is-a-prompt");

    public static readonly PromptClaim UrgencyIsAnInsistentAlarmInTheRoom =
        _document.Claim("glucose-watches.urgency-is-an-insistent-alarm-in-the-room");

    public static readonly PromptClaim FallingFastIsATrendTrigger =
        _document.Claim("glucose-watches.falling-fast-is-a-trend-trigger");

    public static readonly PromptClaim SilentSensorIsNoReadings =
        _document.Claim("glucose-watches.silent-sensor-is-no-readings");

    public static readonly PromptClaim AnotherPersonsGlucoseNamesTheirFolder =
        _document.Claim("glucose-watches.another-persons-glucose-names-their-folder");

    public static readonly PromptClaim LevelsAreMgDl =
        _document.Claim("glucose-watches.levels-are-mg-dl");

    public static readonly PromptClaim CrossingOnlyIsSaid =
        _document.Claim("glucose-watches.crossing-only-is-said");
}