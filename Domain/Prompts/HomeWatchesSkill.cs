namespace Domain.Prompts;

// The doing rules of a watch, loaded when a request is about reacting to the home. What a watch
// is and when it is the right mechanism stay in the home guide, because the model decides that
// before it loads anything; everything it needs while writing one is here (docs/adr/0039).
// Its words and claims are Skills/home-watches.md; this is the handle the server and the scenarios hold.
public static class HomeWatchesSkill
{
    public const string Name = "home-watches";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAWatchRequest =
        _document.Claim("home-watches.loads-for-a-watch-request");

    public static readonly PromptClaim DefaultsToAPromptDeliveredWhereAsked =
        _document.Claim("home-watches.defaults-to-a-prompt-delivered-where-asked");

    public static readonly PromptClaim RangeIsTwoTriggers =
        _document.Claim("home-watches.range-is-two-triggers");

    public static readonly PromptClaim UrgencyIsAnInsistentAnnouncement =
        _document.Claim("home-watches.urgency-is-an-insistent-announcement");

    public static readonly PromptClaim HomeActionIsAnActionsEffect =
        _document.Claim("home-watches.home-action-is-an-actions-effect");

    public static readonly PromptClaim OneShotUsesOnce =
        _document.Claim("home-watches.one-shot-uses-once");

    public static readonly PromptClaim PauseIsEnabledFalse =
        _document.Claim("home-watches.pause-is-enabled-false");

    public static readonly PromptClaim ChangeReplacesInPlace =
        _document.Claim("home-watches.change-replaces-in-place");

    public static readonly PromptClaim IsRemovedByDelete =
        _document.Claim("home-watches.is-removed-by-delete");

    public static readonly PromptClaim IsReadBackOnCreation =
        _document.Claim("home-watches.is-read-back-on-creation");

    public static readonly PromptClaim NeedsNoApproval =
        _document.Claim("home-watches.needs-no-approval");

    public static readonly PromptClaim CrossingOnlyIsSaid =
        _document.Claim("home-watches.crossing-only-is-said");

    public static readonly PromptClaim NoisySensorWatchUsesFor =
        _document.Claim("home-watches.noisy-sensor-uses-for");

    public static readonly PromptClaim SpentWatchIsCleanedUp =
        _document.Claim("home-watches.spent-is-cleaned-up");

    public static readonly PromptClaim NabuNeverDeliversToTelegram =
        _document.Claim("home-watches.nabu-never-delivers-to-telegram");
}