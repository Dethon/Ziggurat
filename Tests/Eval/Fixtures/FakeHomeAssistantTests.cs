using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;

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

    private static HaFileSystem Mount(FakeHomeAssistant? home = null)
    {
        var fake = home ?? new FakeHomeAssistant();
        IHomeAssistantClient Client() => new HomeAssistantClient(
            new HttpClient(fake) { BaseAddress = new Uri("http://home-assistant.eval/") },
            FakeHomeAssistant.Token);

        return new HaFileSystem(new HaCatalogProvider(Client), Client);
    }

    // The backend is handed mount-relative paths; the `/ha` prefix a scenario asserts on is the
    // router's, and it is stripped before the backend ever sees it.
    private static string Relative(string path) => path["/ha/".Length..];

    private static IReadOnlyList<string> Paths(FsResult<FsGlobResult> result) =>
        result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
}