namespace Domain.Prompts;

// The doing rules of the web, loaded when a request reaches for the browser. That a url comes from
// a search and that no call is a probe stay in the web prompt, because both are decided before
// the first call — and a probe is the call that would precede any load (docs/adr/0039).
// Its words and claims are Skills/web-browsing.md; this is the handle the server and the scenarios hold.
public static class WebBrowsingSkill
{
    public const string Name = "web-browsing";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAWebTask =
        _document.Claim("web-browsing.loads-for-a-web-task");

    public static readonly PromptClaim AnswerComesFromWhatWasRead =
        _document.Claim("web-browsing.answer-comes-from-what-was-read");

    public static readonly PromptClaim RawContentIsNeverDumped =
        _document.Claim("web-browsing.raw-content-is-never-dumped");

    public static readonly PromptClaim RefsComeFromASnapshot =
        _document.Claim("web-browsing.refs-come-from-a-snapshot");

    public static readonly PromptClaim ActionsChainFromTheDiff =
        _document.Claim("web-browsing.actions-chain-from-the-diff");

    public static readonly PromptClaim BrowseReadsAndSnapshotStructures =
        _document.Claim("web-browsing.browse-reads-and-snapshot-structures");

    public static readonly PromptClaim TypeReactsAndFillSets =
        _document.Claim("web-browsing.type-reacts-and-fill-sets");

    public static readonly PromptClaim UrlsAreCitedOnlyInWriting =
        _document.Claim("web-browsing.urls-are-cited-only-in-writing");

    public static readonly PromptClaim StepsAreNotReported =
        _document.Claim("web-browsing.steps-are-not-reported");

    public static readonly PromptClaim PartialContentIsFetchedOnce =
        _document.Claim("web-browsing.partial-content-is-fetched-once");

    public static readonly PromptClaim AsMeOnlyForThePersonsAccount =
        _document.Claim("web-browsing.as-me-only-for-the-persons-account");

    public static readonly PromptClaim ALoginPageIsHandedOff =
        _document.Claim("web-browsing.a-login-page-is-handed-off");

    public static readonly PromptClaim AFireReportsALoginWall =
        _document.Claim("web-browsing.a-fire-reports-a-login-wall");

    public static readonly PromptClaim AWorkerReportsALoginWall =
        _document.Claim("web-browsing.a-worker-reports-a-login-wall");

    public static readonly PromptClaim BackIsAnAction =
        _document.Claim("web-browsing.back-is-an-action");
}