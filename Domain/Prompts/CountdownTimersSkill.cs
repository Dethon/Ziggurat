namespace Domain.Prompts;

// The doing rules of a countdown, loaded when a request creates, reads, changes, cancels or
// silences one. Which mechanism a request is — a countdown, a calendar alarm, a scheduled task —
// stays in the timers prompt, because the model decides that before it loads anything; everything
// it needs while touching /timers is here (docs/adr/0039).
// Its words and claims are Skills/countdown-timers.md; this is the handle the server and the scenarios hold.
public static class CountdownTimersSkill
{
    public const string Name = "countdown-timers";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForATimerRequest =
        _document.Claim("countdown-timers.loads-for-a-timer-request");

    public static readonly PromptClaim CreatedAtItsOwnPath =
        _document.Claim("countdown-timers.created-at-its-own-path");

    public static readonly PromptClaim IdIsDescriptive =
        _document.Claim("countdown-timers.id-is-descriptive");

    public static readonly PromptClaim VoiceTargetsTheSpeakingRoom =
        _document.Claim("countdown-timers.voice-targets-the-speaking-room");

    public static readonly PromptClaim NoSatelliteAsksWhichRoom =
        _document.Claim("countdown-timers.no-satellite-asks-which-room");

    public static readonly PromptClaim TextIsSpokenNeverAnInstruction =
        _document.Claim("countdown-timers.text-is-spoken-never-an-instruction");

    public static readonly PromptClaim StatusIsReadForTimeLeft =
        _document.Claim("countdown-timers.status-is-read-for-time-left");

    public static readonly PromptClaim SpokenStatusGivesOnlyTheRemainingTime =
        _document.Claim("countdown-timers.spoken-status-gives-only-the-remaining-time");

    public static readonly PromptClaim WrittenStatusIncludesFiresAt =
        _document.Claim("countdown-timers.written-status-includes-fires-at");

    public static readonly PromptClaim ListedByGlob =
        _document.Claim("countdown-timers.listed-by-glob");

    public static readonly PromptClaim CancelledByRemovingIt =
        _document.Claim("countdown-timers.cancelled-by-removing-it");

    public static readonly PromptClaim ChangedByDeleteAndRecreate =
        _document.Claim("countdown-timers.changed-by-delete-and-recreate");

    public static readonly PromptClaim ExtendingADismissedOneIsANewTimer =
        _document.Claim("countdown-timers.extending-a-dismissed-one-is-a-new-timer");

    public static readonly PromptClaim RecreationIsNeverNarrated =
        _document.Claim("countdown-timers.recreation-is-never-narrated");

    public static readonly PromptClaim RingingIsStoppedByDismiss =
        _document.Claim("countdown-timers.ringing-is-stopped-by-dismiss");
}