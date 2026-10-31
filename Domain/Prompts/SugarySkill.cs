namespace Domain.Prompts;

// How her carb log is laid out, loaded when a request is about her foods, what she ate, the insulin
// she recorded taking, her exercise or her periods. The
// columns themselves are explained by the README the mount serves, which is rendered beside the
// tables and so can never describe a column that is not there; this says what the tables are and how
// to ask them (docs/adr/0039).
// Its words and claims are Skills/sugary-log.md; this is the handle the server and the scenarios hold.
public static class SugarySkill
{
    public const string Name = "sugary-log";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static SkillText Text { get; } = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAQuestionAboutHerLog =
        _document.Claim("sugary-log.loads-for-a-question-about-her-log");

    public static readonly PromptClaim WorksADoseFromHerNumbers =
        _document.Claim("sugary-log.works-a-dose-from-her-numbers");

    public static readonly PromptClaim AsksForAMissingNumber =
        _document.Claim("sugary-log.asks-for-a-missing-number");
}