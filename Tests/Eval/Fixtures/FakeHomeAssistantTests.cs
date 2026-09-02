using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    public async Task TheAlarmsCalendar_OffersCreateListAndDelete_AsActionFiles()
    {
        var files = await Mount().GlobAsync(Relative("/ha/entities/calendar"), "**", CancellationToken.None);

        var directory = Relative(FakeHomeAssistant.AlarmsDirectory);
        Paths(files).ShouldContain(directory + "/create_event.sh");
        Paths(files).ShouldContain(directory + "/get_events.sh");
        Paths(files).ShouldContain(directory + "/delete_event.sh");
        Paths(files).ShouldNotContain(directory + "/update_event.sh");
    }

    [Fact]
    public async Task TheAlarmsCalendar_IsServedUnderItsAreaToo_AndThePatternAcceptsBothViews()
    {
        // The mount serves the calendar again under its area (unassigned — it belongs to none),
        // and both are the deployment's own paths: the snooze scenario pinned the entities view
        // and an armed run that wrote the correct event through the area view went red on it.
        var files = await Mount().GlobAsync(Relative("/ha/areas"), "**", CancellationToken.None);
        var areaView = Paths(files).Single(p => p.EndsWith("/create_event.sh"));
        var areaDirectory = "/ha/" + areaView[..areaView.LastIndexOf('/')];

        Regex.IsMatch(areaDirectory, FakeHomeAssistant.AlarmsPathPattern, RegexOptions.IgnoreCase)
            .ShouldBeTrue(areaDirectory);
        Regex.IsMatch(
                FakeHomeAssistant.AlarmsDirectory, FakeHomeAssistant.AlarmsPathPattern,
                RegexOptions.IgnoreCase)
            .ShouldBeTrue(FakeHomeAssistant.AlarmsDirectory);
    }

    [Fact]
    public async Task CreatingAnEvent_ReachesTheCalendar_WithTheArgumentsTheAgentWrote()
    {
        var home = new FakeHomeAssistant();
        await using var socket = await Socket(home);

        var result = await Mount(home, socket: socket).ExecAsync(
            Relative(FakeHomeAssistant.AlarmsDirectory),
            """create_event.sh --summary "Levántate" --start_date_time "2026-08-18 07:00:00" """,
            timeoutSeconds: null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        var call = home.Calls.ShouldHaveSingleItem();
        call.Domain.ShouldBe("calendar");
        call.Service.ShouldBe("create_event");
        call.EntityId.ShouldBe(FakeHomeAssistant.AlarmsEntityId);
        call.Data["summary"]!.GetValue<string>().ShouldBe("Levántate");
        call.Data["dtstart"]!.GetValue<string>().ShouldBe("2026-08-18 07:00:00");
        home.Snapshot()[FakeHomeAssistant.AlarmsEventCountKey].ShouldBe("2");
    }

    // The seeded alarm is listed with the uid the cancel scenario expects back — a uid is only
    // knowable from this listing, which is what makes "list, then delete" falsifiable.
    [Fact]
    public async Task ListingTheCalendar_ReturnsTheSeededAlarm_WithItsUid()
    {
        var home = new FakeHomeAssistant();

        var result = await Mount(home).ExecAsync(
            Relative(FakeHomeAssistant.AlarmsDirectory), "get_events.sh --days 30",
            timeoutSeconds: null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var alarm = JsonNode.Parse(exec.Stdout)!["events"]!.AsArray().ShouldHaveSingleItem()!;
        alarm["uid"]!.GetValue<string>().ShouldBe(FakeHomeAssistant.TrashAlarmUid);
        alarm["summary"]!.GetValue<string>().ShouldBe(FakeHomeAssistant.TrashAlarmSummary);
        home.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingByUid_RemovesTheAlarm_AndTheSnapshotShowsIt()
    {
        var home = new FakeHomeAssistant();
        await using var socket = await Socket(home);

        var result = await Mount(home, socket: socket).ExecAsync(
            Relative(FakeHomeAssistant.AlarmsDirectory),
            $"delete_event.sh --uid {FakeHomeAssistant.TrashAlarmUid}",
            timeoutSeconds: null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        var call = home.Calls.ShouldHaveSingleItem();
        call.Service.ShouldBe("delete_event");
        call.Data["uid"]!.GetValue<string>().ShouldBe(FakeHomeAssistant.TrashAlarmUid);
        home.Snapshot()[FakeHomeAssistant.AlarmsEventCountKey].ShouldBe("0");
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
            .ShouldContain("AREA_ID (slug: read it verbatim from the setup index heading");
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

    // The websocket side of the home, wired the way the stack wires it: the same calendar store,
    // and every mutation recorded beside the REST calls.
    private static async Task<FakeHomeAssistantSocket> Socket(FakeHomeAssistant home)
    {
        var socket = await FakeHomeAssistantSocket.StartAsync(home.Calendar, FakeHomeAssistant.Token);
        socket.Recorder = home.Record;
        return socket;
    }

    private static HaFileSystem Mount(
        FakeHomeAssistant? home = null, FakeMusicAssistantServer? music = null, FakeHomeAssistantSocket? socket = null)
    {
        var fake = home ?? new FakeHomeAssistant();
        IHomeAssistantClient client() => new HomeAssistantClient(
            new HttpClient(fake) { BaseAddress = new Uri(socket?.BaseUrl ?? "http://home-assistant.eval") },
            FakeHomeAssistant.Token);

        // The podcast action exists only where Music Assistant does, in the mount as in the
        // deployment: no music server, no extra service, no action file. The calendar's actions
        // are always there, as they are in the deployment.
        IMusicAssistantClient musicClient() =>
            new MusicAssistantClient(music!.BaseUrl, FakeMusicAssistantServer.ValidToken);
        IReadOnlyList<HaServiceDefinition> served = music is null
            ? HaCalendarActions.All
            : [.. HaCalendarActions.All, HaMusicActions.PodcastEpisodes];

        return new HaFileSystem(
            new HaCatalogProvider(client, extraServices: served),
            client,
            musicClientFactory: music is null ? null : musicClient);
    }

    // The backend is handed mount-relative paths; the `/ha` prefix a scenario asserts on is the
    // router's, and it is stripped before the backend ever sees it.
    private static string Relative(string path) => path["/ha/".Length..];

    private static IReadOnlyList<string> Paths(FsResult<FsGlobResult> result) =>
        result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
}