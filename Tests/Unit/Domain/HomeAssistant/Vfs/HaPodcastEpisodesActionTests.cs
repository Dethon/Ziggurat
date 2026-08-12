using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeMusicAssistantClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The failure these cover: a podcast EPISODE is only playable by its exact MA URI, and no Home
// Assistant call can enumerate a show's episodes. Passing an episode title to
// `music_assistant.play_media` resolves to the SHOW and silently starts its newest episode.
public class HaPodcastEpisodesActionTests
{
    private const string ShowUri = "spotify--w2nq2jMe://podcast/5dbvpKwtqz3X3hcX1BSEzf";
    private const string PalantirUri = "spotify--w2nq2jMe://podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN";
    private const string PlayerDir = "entities/media_player/speaker_(speaker-fran-office)";

    private static HaFileSystem Build(out FakeMusicAssistantClient music, bool musicConfigured = true)
    {
        var ha = new FakeHaClient
        {
            States = { Entity("media_player.speaker", "idle", ("friendly_name", JsonValue.Create("speaker-fran-office"))) },
            Services = { Service("music_assistant", "play_media", DomainTarget("media_player")) }
        };
        var ma = new FakeMusicAssistantClient
        {
            Podcasts = { Item("No es el fin del mundo", ShowUri), Item("El Orden Mundial", "spotify--w2nq2jMe://podcast/1wsNhdPRTo47jppKnKCk3E") },
            EpisodesByPodcastUri =
            {
                [ShowUri] =
                [
                    Item("292. La guerra por el agua: el recurso imprescindible", "spotify--w2nq2jMe://podcast_episode/5V4BfCyA4vFH01rFMr3sRE", 5279),
                    Item("291. La geopolítica de la cerámica", "spotify--w2nq2jMe://podcast_episode/3bjldMCdbZ4xjpEsy27cAk", 4100),
                    Item("280. Palantir: el control tecnológico de la defensa, con Marta Peirano", PalantirUri, 7276)
                ]
            }
        };
        music = ma;

        var provider = new HaCatalogProvider(
            () => ha,
            new FakeTimeProvider(),
            extraServices: musicConfigured ? [HaMusicActions.PodcastEpisodes] : null);
        return new HaFileSystem(provider, () => ha, musicClientFactory: musicConfigured ? () => ma : null);
    }

    private static async Task<FsExecResult> Exec(HaFileSystem fs, string command) =>
        (await fs.ExecAsync(PlayerDir, command, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

    [Fact]
    public async Task Exec_PodcastByName_ResolvesShowThenReturnsEpisodeUris()
    {
        var fs = Build(out var music);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"No es el fin del mundo\"");

        exec.ExitCode.ShouldBe(0);
        music.LastEpisodeLookup!.Value.ToString().ShouldBe(ShowUri);

        var payload = JsonNode.Parse(exec.Stdout)!;
        payload["podcast"]!["uri"]!.GetValue<string>().ShouldBe(ShowUri);
        payload["episodes"]!.AsArray().Count.ShouldBe(3);
        payload["episodes"]![2]!["uri"]!.GetValue<string>().ShouldBe(PalantirUri);
    }

    // The whole point of the action: one call turns "the Palantir episode" into the exact URI that
    // music_assistant.play_media accepts. Voice transcripts drop accents ("tecnologico"), and the
    // user's wording rarely matches case.
    [Fact]
    public async Task Exec_Match_IgnoresCaseAndAccents()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"No es el fin del mundo\" --match \"CONTROL TECNOLOGICO\"");

        var episodes = JsonNode.Parse(exec.Stdout)!["episodes"]!.AsArray();
        episodes.Count.ShouldBe(1);
        episodes[0]!["uri"]!.GetValue<string>().ShouldBe(PalantirUri);
    }

    [Fact]
    public async Task Exec_PodcastByUri_SkipsTheSearch()
    {
        var fs = Build(out var music);

        var exec = await Exec(fs, $"music_assistant.podcast_episodes.sh --podcast \"{ShowUri}\"");

        exec.ExitCode.ShouldBe(0);
        music.LastSearch.ShouldBeNull();
        music.LastEpisodeLookup!.Value.ItemId.ShouldBe("5dbvpKwtqz3X3hcX1BSEzf");
    }

    // 294 episodes is the real-world size; an unbounded dump would swamp the context.
    [Fact]
    public async Task Exec_Limit_CapsAndReportsTruncation()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"No es el fin del mundo\" --limit 2");

        var payload = JsonNode.Parse(exec.Stdout)!;
        payload["episodes"]!.AsArray().Count.ShouldBe(2);
        payload["total"]!.GetValue<int>().ShouldBe(3);
        payload["truncated"]!.GetValue<bool>().ShouldBeTrue();
        payload["suggestion"]!.GetValue<string>().ShouldContain("--match");
    }

    [Fact]
    public async Task Exec_UnknownPodcast_Returns1_WithoutListing()
    {
        var fs = Build(out var music);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"Un podcast que no existe\"");

        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("Un podcast que no existe");
        music.LastEpisodeLookup.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_MatchWithNoHit_Returns0_AndSaysSo()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"No es el fin del mundo\" --match zzz");

        exec.ExitCode.ShouldBe(0);
        var payload = JsonNode.Parse(exec.Stdout)!;
        payload["episodes"]!.AsArray().Count.ShouldBe(0);
        payload["total"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public async Task Exec_MissingRequiredPodcast_Returns2()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --match palantir");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--podcast");
    }

    [Fact]
    public async Task Exec_Help_ListsFields_WithoutCallingMusicAssistant()
    {
        var fs = Build(out var music);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --help");

        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain("--podcast");
        exec.Stdout.ShouldContain("--match");
        music.LastSearch.ShouldBeNull();
        music.LastEpisodeLookup.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_MusicAssistantFails_Returns1_WithReason()
    {
        var fs = Build(out var music);
        music.Fault = new InvalidOperationException("Music Assistant is unreachable");

        var exec = await Exec(fs, $"music_assistant.podcast_episodes.sh --podcast \"{ShowUri}\"");

        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("Music Assistant is unreachable");
    }

    // The action is only listed when a Music Assistant connection is actually configured, so a
    // deployment without one never advertises an action that cannot work.
    [Fact]
    public async Task Exec_MusicAssistantNotConfigured_ActionIsNotListed()
    {
        var fs = Build(out _, musicConfigured: false);

        var exec = await Exec(fs, "music_assistant.podcast_episodes.sh --podcast \"No es el fin del mundo\"");

        exec.ExitCode.ShouldBe(127);
        exec.Stderr[exec.Stderr.IndexOf("Available actions:", StringComparison.Ordinal)..]
            .ShouldNotContain("podcast_episodes");
    }

    [Fact]
    public async Task Glob_ListsThePodcastEpisodesAction_InThePlayerDirectory()
    {
        var fs = Build(out _);

        var result = await fs.GlobAsync(PlayerDir, "*", CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries
            .ShouldContain(e => e.EndsWith("music_assistant.podcast_episodes.sh", StringComparison.Ordinal));
    }
}