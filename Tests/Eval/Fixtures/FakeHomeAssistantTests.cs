using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Clients.HomeAssistant;
using Infrastructure.Clients.MusicAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Eval.Fixtures;

// The Home Assistant fake, proven against the real client and the real mount. Nothing here needs a
// model: what is being checked is that a scenario running against this fake is running against the
// same surface a deployment serves — the same action files, the same argument parsing, the same
// service call — so that a failure in the eval means the agent got it wrong rather than the fake.
public class FakeHomeAssistantTests
{
    [Fact]
    public async Task TheAlarmsCalendar_OffersCreateEvent_AsAnActionFile()
    {
        var files = await Mount().GlobAsync(Relative("/ha/entities/calendar"), "**", CancellationToken.None);

        Paths(files).ShouldContain(Relative(FakeHomeAssistant.AlarmsDirectory) + "/create_event.sh");
    }

    [Fact]
    public async Task CreatingAnEvent_ReachesTheCalendar_WithTheArgumentsTheAgentWrote()
    {
        var home = new FakeHomeAssistant();

        var result = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.AlarmsDirectory),
            """create_event.sh --summary "Levántate" --start_date_time "2026-08-18 07:00:00" """,
            timeoutSeconds: null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        var call = home.Calls.ShouldHaveSingleItem();
        call.Domain.ShouldBe("calendar");
        call.Service.ShouldBe("create_event");
        call.EntityId.ShouldBe(FakeHomeAssistant.AlarmsEntityId);
        call.Data["summary"]!.GetValue<string>().ShouldBe("Levántate");
        call.Data["start_date_time"]!.GetValue<string>().ShouldBe("2026-08-18 07:00:00");
    }

    [Fact]
    public async Task AnActionThatChangesADevice_MovesTheStateTheFakeReports()
    {
        var home = new FakeHomeAssistant();

        await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.KitchenLightDirectory), "turn_off.sh",
            timeoutSeconds: null, CancellationToken.None);

        home.StateOf(FakeHomeAssistant.KitchenLightEntityId).ShouldBe("off");
    }

    [Fact]
    public async Task SettingATargetTemperature_ShowsUpInTheSnapshot_ThoughTheStateDidNotMove()
    {
        // A thermostat moved to another temperature has changed without its state changing, so a
        // snapshot that only carried states would report "nothing happened" for the one call the
        // user actually made.
        var home = new FakeHomeAssistant();

        await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.AirConditionerDirectory), "set_temperature.sh --temperature 22",
            timeoutSeconds: null, CancellationToken.None);

        var snapshot = home.Snapshot();
        snapshot[FakeHomeAssistant.AirConditionerEntityId].ShouldBe("cool");
        snapshot[FakeHomeAssistant.AirConditionerEntityId + "#temperature"].ShouldBe("22");
    }

    [Fact]
    public async Task ReadingAnEntity_ReturnsTheLiveState()
    {
        var home = new FakeHomeAssistant();

        var read = await Mount(home).ReadAsync(
            Relative(FakeHomeAssistant.KitchenLightDirectory) + "/state.json", offset: null, limit: null,
            CancellationToken.None);

        read.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"on\"");
    }

    [Fact]
    public async Task ATurnThatOnlyRead_LeavesNothingForTheDiffToReport()
    {
        // The other half of the state diff's contract: a read is not a change. Taken around real
        // calls through the real mount rather than around two dictionaries somebody typed, because
        // what is being checked is that reading through this fake has no side effects.
        var home = new FakeHomeAssistant();
        var mount = Mount(home);
        var before = home.Snapshot();

        await mount.ReadAsync(Relative(FakeHomeAssistant.KitchenLightDirectory) + "/state.json",
            offset: null, limit: null, CancellationToken.None);
        await mount.GlobAsync(Relative("/ha/entities"), "**", CancellationToken.None);
        await mount.ExecAsync(Relative(FakeHomeAssistant.KitchenLightDirectory), "turn_off.sh --help",
            timeoutSeconds: null, CancellationToken.None);

        home.Snapshot().ShouldBe(before);
        home.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task BrowsingTheLibrary_ReturnsTheTitlesTheUserActuallyHas()
    {
        // The whole point of the browse-before-you-play rule: what the user calls the list and
        // what the list is called are different strings, and only this call knows the second one.
        var home = new FakeHomeAssistant();

        var result = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.KitchenSpeakerDirectory),
            "browse_media.sh --media_content_id playlists --media_content_type music_assistant",
            timeoutSeconds: null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain(FakeHomeAssistant.FavouritesPlaylist);
    }

    [Fact]
    public async Task PlayingAPlaylistTheLibraryDoesNotHave_FailsTheWayHomeAssistantFails()
    {
        // A 500 with nothing useful in it, which is exactly what a real home answers when
        // play_media cannot resolve a name. A fake that accepted an invented title would make the
        // browse-first rule unfalsifiable.
        var home = new FakeHomeAssistant();

        var result = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.KitchenSpeakerDirectory),
            """music_assistant.play_media.sh --media_id "Mi música favorita" --media_type playlist""",
            timeoutSeconds: null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("browse_media.sh");
    }

    [Fact]
    public async Task PlayingAPlaylistThatIsInTheLibrary_ReachesMusicAssistant()
    {
        var home = new FakeHomeAssistant();

        var result = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.KitchenSpeakerDirectory),
            $"""music_assistant.play_media.sh --media_id "{FakeHomeAssistant.FavouritesPlaylist}" --media_type playlist""",
            timeoutSeconds: null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        var call = home.Calls.ShouldHaveSingleItem();
        call.Domain.ShouldBe("music_assistant");
        call.Service.ShouldBe("play_media");
        call.EntityId.ShouldBe(FakeHomeAssistant.KitchenSpeakerEntityId);
        call.Data["media_id"]!.GetValue<string>().ShouldBe(FakeHomeAssistant.FavouritesPlaylist);
    }

    [Fact]
    public async Task ListingAPodcastsEpisodes_ReturnsTheUriEachOnePlaysBy()
    {
        // The action Home Assistant has no service for. It is served by the mount against Music
        // Assistant's own websocket, so an eval that skipped this fake would leave the one rule
        // about episodes untestable.
        await using var music = await FakeMusicAssistantServer.StartAsync();
        var home = new FakeHomeAssistant();

        var result = await Mount(home, music).ExecAsync(
            Relative(FakeHomeAssistant.KitchenSpeakerDirectory),
            """music_assistant.podcast_episodes.sh --podcast "No es el fin del mundo" --match "Palantir" """,
            timeoutSeconds: null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain("podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN");
    }

    [Fact]
    public async Task TheStudy_KeepsItsFrozenSlugUnderItsNewName()
    {
        // The area was created as "Despacho" and later renamed to "Estudio": HA freezes the slug
        // at creation, so the directory keeps the old word and the display name carries the new
        // one. This gap is the whole subject of the area-slug rule.
        var files = await Mount().GlobAsync(Relative("/ha/areas/despacho"), "**", CancellationToken.None);

        Paths(files).ShouldContain(path => path.Contains("aspiradora"));
    }

    [Fact]
    public async Task TheCleanZoneHelp_SaysTheArgumentIsAnAreaSlug()
    {
        var result = await Mount().ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "clean_zone.sh --help",
            timeoutSeconds: null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.Stdout
            .ShouldContain("AREA_ID (slug)");
    }

    [Fact]
    public async Task CleaningAnArea_AnswersToTheSlugAndNotToTheDisplayName()
    {
        var home = new FakeHomeAssistant();

        // The display name, lowercased the way a model derives it, cleans nothing…
        await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "clean_zone.sh --cleaning_area_id estudio",
            timeoutSeconds: null, CancellationToken.None);
        home.StateOf(FakeHomeAssistant.VacuumEntityId).ShouldBe("docked");

        // …and the frozen slug from the registry does.
        await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "clean_zone.sh --cleaning_area_id despacho",
            timeoutSeconds: null, CancellationToken.None);
        home.StateOf(FakeHomeAssistant.VacuumEntityId).ShouldBe("cleaning");
    }

    [Fact]
    public async Task TheFanSpeedHelp_ListsItsExactOptions()
    {
        // The option set is the only place the exact casing exists: the user says "turbo" and the
        // service wants "Turbo", so an argument that works was read rather than derived.
        var result = await Mount().ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "set_fan_speed.sh --help",
            timeoutSeconds: null, CancellationToken.None);

        var stdout = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.Stdout;
        stdout.ShouldContain("Silencioso");
        stdout.ShouldContain("Turbo");
    }

    [Fact]
    public async Task SettingTheFanSpeed_AnswersToTheListedOptionAndNotToAGuess()
    {
        var home = new FakeHomeAssistant();

        // The user's word, passed as heard: a bad argument, answered with the real options —
        // which is the same information --help prints, so the fix is a re-read either way.
        var guessed = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "set_fan_speed.sh --fan_speed turbo",
            timeoutSeconds: null, CancellationToken.None);

        var exec = guessed.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldNotBe(0);
        exec.Stderr.ShouldContain("Turbo");
        home.Snapshot().ShouldNotContainKey(FakeHomeAssistant.VacuumEntityId + "#fan_speed");

        var listed = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.VacuumDirectory), "set_fan_speed.sh --fan_speed Turbo",
            timeoutSeconds: null, CancellationToken.None);

        listed.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        home.Snapshot()[FakeHomeAssistant.VacuumEntityId + "#fan_speed"].ShouldBe("Turbo");
    }

    private static HaFileSystem Mount(
        FakeHomeAssistant? home = null, FakeMusicAssistantServer? music = null)
    {
        var fake = home ?? new FakeHomeAssistant();
        IHomeAssistantClient client() => new HomeAssistantClient(
            new HttpClient(fake) { BaseAddress = new Uri("http://home-assistant.eval/") },
            FakeHomeAssistant.Token);

        // The podcast action exists only where Music Assistant does, in the mount as in the
        // deployment: no music server, no extra service, no action file.
        IMusicAssistantClient musicClient() =>
            new MusicAssistantClient(music!.BaseUrl, FakeMusicAssistantServer.ValidToken);

        return new HaFileSystem(
            new HaCatalogProvider(client, extraServices: music is null ? null : [HaMusicActions.PodcastEpisodes]),
            client,
            musicClientFactory: music is null ? null : musicClient);
    }

    // The backend is handed mount-relative paths; the `/ha` prefix a scenario asserts on is the
    // router's, and it is stripped before the backend ever sees it.
    private static string Relative(string path) => path["/ha/".Length..];

    private static IReadOnlyList<string> Paths(FsResult<FsGlobResult> result) =>
        result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
}