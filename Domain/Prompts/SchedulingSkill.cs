namespace Domain.Prompts;

// The doing rules of a schedule, loaded when a request creates, changes, runs or removes one.
// That a schedule is a deferred action and never a human reminder stays in the scheduling
// prompt, because the model chooses the mechanism before it loads anything; everything it
// needs while writing the file is here (docs/adr/0039). It names no zone: it is served before any
// call names a household, and a schedule is read in the calling household's zone — local time.
// Its words and claims are Skills/scheduling.md; this is the handle the server and the scenarios hold.
public static class SchedulingSkill
{
    public const string Name = "scheduling";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly SkillText Text = new(Name, Description, _document.Text);

    public static readonly PromptClaim LoadsForAScheduleRequest =
        _document.Claim("scheduling.loads-for-a-schedule-request");
}