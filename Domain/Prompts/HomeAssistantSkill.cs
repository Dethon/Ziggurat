namespace Domain.Prompts;

// The doing rules of the home, loaded when a request is about a device, its past, an alarm on the
// calendar or music on a player. Which mechanism a request is — a timer, an alarm, a watch, a
// schedule — and that an area id is read rather than derived stay in the home guide, because the
// model decides those before it loads anything; everything it needs while acting is here
// (docs/adr/0039).
// Its words and claims are Skills/home-assistant.md; this is the handle the server and the scenarios hold.
public static class HomeAssistantSkill
{
    public const string Name = "home-assistant";

    // The one file the body says to read in the same turn: the mount root and the index's name,
    // spelled where the document's preload-reads and the eval's expectation both read it.
    public const string SetupIndexPath = "/ha/" + Domain.Tools.HomeAssistant.Vfs.HaVfsPath.SetupIndexFileName;

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAHomeRequest =
        _document.Claim("home-assistant.loads-for-a-home-request");

    public static readonly PromptClaim EntityNamedAsListed =
        _document.Claim("home-assistant.entity-named-as-listed");

    public static readonly PromptClaim ArgumentsComeFromHelp =
        _document.Claim("home-assistant.arguments-come-from-help");

    public static readonly PromptClaim ExitCodeIsTheConfirmation =
        _document.Claim("home-assistant.exit-code-is-the-confirmation");

    public static readonly PromptClaim ExitCodesAreNeverVoiced =
        _document.Claim("home-assistant.exit-codes-are-never-voiced");

    public static readonly PromptClaim AlarmCarriesTargetAndInsistent =
        _document.Claim("home-assistant.alarm-carries-target-and-insistent");

    public static readonly PromptClaim SnoozeIsANewEvent =
        _document.Claim("home-assistant.snooze-is-a-new-event");

    public static readonly PromptClaim AlarmIsCancelledByUid =
        _document.Claim("home-assistant.alarm-is-cancelled-by-uid");

    public static readonly PromptClaim AlarmIsChangedByDeleteAndCreate =
        _document.Claim("home-assistant.alarm-is-changed-by-delete-and-create");

    public static readonly PromptClaim MusicPlaysOnTheMusicAssistantPlayer =
        _document.Claim("home-assistant.music-plays-on-the-music-assistant-player");

    public static readonly PromptClaim PlaylistIsBrowsedBeforeItIsPlayed =
        _document.Claim("home-assistant.playlist-is-browsed-before-it-is-played");

    public static readonly PromptClaim EpisodePlaysOnlyByItsUri =
        _document.Claim("home-assistant.episode-plays-only-by-its-uri");

    public static readonly PromptClaim TheWebIsNoMediaFallback =
        _document.Claim("home-assistant.web-is-no-media-fallback");

    public static readonly PromptClaim RestartIsASeek =
        _document.Claim("home-assistant.restart-is-a-seek");

    public static readonly PromptClaim RelativeSeekReadsThePositionFirst =
        _document.Claim("home-assistant.relative-seek-reads-the-position-first");

    public static readonly PromptClaim IndexLineChoicesAreActedOnDirectly =
        _document.Claim("home-assistant.index-line-choices-are-acted-on-directly");

    public static readonly PromptClaim SpokenNameTakesTheNearestChoice =
        _document.Claim("home-assistant.spoken-name-takes-the-nearest-choice");

    public static readonly PromptClaim EmptyChangedIsASuccess =
        _document.Claim("home-assistant.empty-changed-is-a-success");

    public static readonly PromptClaim ThePastIsReadFromHistory =
        _document.Claim("home-assistant.past-is-read-from-history");
}