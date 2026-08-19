using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The media rules exist because guessing a name fails in a particular way: Home Assistant answers
// a name it cannot resolve with a bare 500, and an episode title resolves to its show and starts
// the newest episode while reporting success. Both are silent to a model that does not look first,
// which is why every scenario here is about the call that comes *before* the playback one.
public static class MusicScenarios
{
    public static IReadOnlyList<Scenario> All => [FavouriteMusic, ThePalantirEpisode, FromTheBeginning];

    // The user's words for the list and the list's stored title share nothing. The only place the
    // real title exists is the library listing, so a play that did not browse first is a play of
    // something the user does not have.
    public static Scenario FavouriteMusic => new()
    {
        Name = "a playlist is played by the title the library has",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon mi música favorita",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "browse",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^browse_media\.sh\b")
                ]
            },
            new CallExpectation
            {
                Label = "play",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    // On the Music Assistant player, not on the television standing beside it in
                    // the same room and listing the same actions.
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^music_assistant\.play_media\.sh\b"),
                    Arg.Matches("command", FakeHomeAssistant.FavouritesPlaylist),
                    Arg.Matches("command", @"--media_type\s+""?playlist")
                ]
            }
        ],
        Ordering = [new OrderingConstraint("browse", "play")],
        Permitted = [.. CallPermission.LookingAndManuals("/ha*")],
        // Tight on purpose: a model that guesses a title first gets a 500, and the browse and the
        // retry that follow put it over. That is the failure this scenario is named after.
        CallCeiling = 5,
        // The playlist rule alone. Demonstrated red by deleting it: the model played "mis
        // favoritos", took the 500, and spent the rest of the turn on an empty browse and a
        // --help. Which player it targets is asserted above as a side condition — deleting the
        // paragraph that teaches it changed nothing, because the speaker and the television in
        // that kitchen are named after what they are.
        Claims = [HomeAssistantPrompt.PlaylistIsBrowsedBeforeItIsPlayed.Id],
        Policy = new RunPolicy(2, 3),
        // This family's canary: the browse-then-play pair is the rule the other two are variations
        // of, and the one whose absence is invisible until somebody notices the wrong list playing.
        Tier = EvalTier.Smoke
    };

    // A show plays by name; an episode never does. The title the user says resolves to the show
    // and starts its newest episode, reporting success — so the only honest way through is the
    // episode listing, and the value played has to be the uri it returned.
    public static Scenario ThePalantirEpisode => new()
    {
        Name = "an episode plays only by the uri the listing gave",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon el episodio sobre Palantir de No es el fin del mundo",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "episodes",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^music_assistant\.podcast_episodes\.sh\b"),
                    // The show, not just the flag: --podcast with anything after it would pass a
                    // check on the flag's name alone.
                    Arg.Matches("command", "(?i)--podcast +\"?[^\"]*fin del mundo")
                ]
            },
            new CallExpectation
            {
                Label = "play",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^music_assistant\.play_media\.sh\b"),
                    // The exact uri the listing returned. A play carrying the episode's title, or
                    // the show's name, does not match — which is the point.
                    Arg.Matches("command", "podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN")
                ]
            }
        ],
        Ordering = [new OrderingConstraint("episodes", "play")],
        Permitted = [.. CallPermission.LookingAndManuals("/ha*")],
        CallCeiling = 5,
        // Demonstrated red by deleting the podcast bullet: with it gone the model read the
        // player's state and answered without playing anything at all.
        Claims = [HomeAssistantPrompt.EpisodePlaysOnlyByItsUri.Id],
        // Two of four rather than two of three: on about a third of runs the model hands the
        // episode lookup to a worker instead of doing it, which is the reflex the delegation
        // exemptions record. The threshold says the behaviour has to hold at least half the time
        // rather than retrying until it does.
        Policy = new RunPolicy(2, 4)
    };

    // Playing the same uri again does nothing visible: Music Assistant keeps a resume point per
    // episode and starts there, reporting success. The seek is the only call that restarts it, and
    // the player already has the item loaded — which is what makes a seek possible at all.
    public static Scenario FromTheBeginning => new()
    {
        Name = "starting it over is a seek, not another play",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "ponlo otra vez desde el principio",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "seek",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^media_seek\.sh\b"),
                    Arg.Matches("command", @"--seek_position\s+""?[01]\b")
                ]
            }
        ],
        // Nothing else may be run: a play of any kind is an unnecessary call here, and that is
        // exactly the mistake the rule names. Reading an action's manual is not running it.
        Permitted = [.. CallPermission.LookingAndManuals("/ha*")],
        CallCeiling = 4,
        // No citation: with the restart bullet deleted the seek still happened. Working out that
        // "from the beginning" is a seek rather than a second play is this model's default.
        Policy = new RunPolicy(2, 3)
    };
}