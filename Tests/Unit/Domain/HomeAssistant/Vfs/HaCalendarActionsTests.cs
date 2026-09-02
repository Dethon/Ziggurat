using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The failure these cover: Home Assistant's service catalog has no way to delete a calendar event
// (deletion is a WebSocket command) and its get_events answers without the uid deletion needs, so
// an agent asked to cancel an alarm found `delete_event.sh` missing and created a duplicate
// instead. The calendar's action files are served by the mount, the way the podcast listing is.
public class HaCalendarActionsTests
{
    private const string CalendarDir = "entities/calendar/alarms_(alarms)";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States =
            {
                Entity("calendar.alarms", "off", ("friendly_name", JsonValue.Create("Alarms"))),
                Entity("light.kitchen", "off")
            },
            // What a real home publishes for the calendar domain: no rrule, no delete, no update.
            Services =
            {
                Service("calendar", "create_event", DomainTarget("calendar"),
                    ("summary", new HaServiceField { Required = true }),
                    ("start_date_time", new HaServiceField()), ("end_date_time", new HaServiceField()),
                    ("in", new HaServiceField())),
                Service("calendar", "get_events", DomainTarget("calendar"),
                    ("start_date_time", new HaServiceField()), ("end_date_time", new HaServiceField()),
                    ("duration", new HaServiceField())),
                Service("light", "turn_on", DomainTarget("light"))
            }
        };
        var local = client;
        var time = new FakeTimeProvider(Now);
        var provider = new HaCatalogProvider(() => local, time, extraServices: HaCalendarActions.All);
        return new HaFileSystem(provider, () => local, timeProvider: time);
    }

    private static async Task<FsExecResult> Exec(HaFileSystem fs, string command, string dir = CalendarDir) =>
        (await fs.ExecAsync(dir, command, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

    [Fact]
    public async Task Glob_TheCalendarDirectory_ServesCreateDeleteAndListButNoUpdate()
    {
        var fs = Build(out _);

        var entries = (await fs.GlobAsync(CalendarDir, "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;

        entries.ShouldContain(e => e.EndsWith("/create_event.sh"));
        entries.ShouldContain(e => e.EndsWith("/delete_event.sh"));
        entries.ShouldContain(e => e.EndsWith("/get_events.sh"));
        entries.ShouldNotContain(e => e.EndsWith("/update_event.sh"));
        entries.Count(e => e.EndsWith("/create_event.sh")).ShouldBe(1);
    }

    [Fact]
    public async Task Glob_ALightDirectory_HasNoCalendarActions()
    {
        var fs = Build(out _);

        var entries = (await fs.GlobAsync("entities/light/kitchen", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;

        entries.ShouldNotContain(e => e.EndsWith("/delete_event.sh"));
        entries.ShouldNotContain(e => e.EndsWith("/get_events.sh"));
    }

    [Fact]
    public async Task Help_CreateEvent_ListsTheRecurrenceRuleAndNotTheRelativeDay()
    {
        var fs = Build(out _);

        var help = await Exec(fs, "create_event.sh --help");

        help.ExitCode.ShouldBe(0);
        help.Stdout.ShouldContain("--rrule");
        help.Stdout.ShouldContain("--description");
        help.Stdout.ShouldNotContain("--in ");
    }

    [Fact]
    public async Task CreateEvent_StartOnly_EndsOneMinuteLater_AndPassesTheTextThrough()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs,
            """create_event.sh --summary "Take out the trash" --start_date_time "2026-09-02 21:30:00" --description '{"target":{"room":"Kitchen"},"insistent":{}}' """);

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var (entityId, draft) = client.CreatedEvents.ShouldHaveSingleItem();
        entityId.ShouldBe("calendar.alarms");
        draft.Summary.ShouldBe("Take out the trash");
        draft.Start.ShouldBe("2026-09-02 21:30:00");
        draft.End.ShouldBe("2026-09-02 21:31:00");
        draft.Description.ShouldBe("""{"target":{"room":"Kitchen"},"insistent":{}}""");
        draft.Rrule.ShouldBeNull();
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task CreateEvent_WithAnOffset_KeepsTheOffsetOnTheDerivedEnd()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """create_event.sh --summary Wake --start_date_time 2026-09-03T07:00:00+02:00""");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.CreatedEvents.Single().Draft.End.ShouldBe("2026-09-03T07:01:00+02:00");
    }

    [Fact]
    public async Task CreateEvent_ExplicitEnd_AndRecurrence_ReachTheDraft()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs,
            """create_event.sh --summary Wake --start_date_time "2026-09-03 07:00:00" --end_date_time "2026-09-03 07:05:00" --rrule "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR" """);

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var draft = client.CreatedEvents.Single().Draft;
        draft.End.ShouldBe("2026-09-03 07:05:00");
        draft.Rrule.ShouldBe("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
    }

    [Fact]
    public async Task CreateEvent_AllDay_EndsTheNextDay()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """create_event.sh --summary Holiday --start_date 2026-12-25""");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var draft = client.CreatedEvents.Single().Draft;
        draft.Start.ShouldBe("2026-12-25");
        draft.End.ShouldBe("2026-12-26");
    }

    [Fact]
    public async Task CreateEvent_NoStart_IsABadArgument()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """create_event.sh --summary Wake""");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("start_date_time");
        client.CreatedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateEvent_UnparseableStart_IsABadArgument()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """create_event.sh --summary Wake --start_date_time "tomorrow at seven" """);

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("start_date_time");
        client.CreatedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetEvents_ListsEachEventWithItsUid()
    {
        var fs = Build(out var client);
        client.CalendarEvents.Add(new HaCalendarEvent
        {
            Uid = "fd96f9b2", Summary = "Llama al administrador",
            Start = "2026-09-02T10:30:00+02:00", End = "2026-09-02T10:31:00+02:00",
            Description = """{"target":{"room":"office"},"insistent":{}}"""
        });
        client.CalendarEvents.Add(new HaCalendarEvent
        {
            Uid = "365f3228", Summary = "Gym", Start = "2026-09-03", End = "2026-09-04", AllDay = true,
            Rrule = "FREQ=WEEKLY"
        });

        var exec = await Exec(fs, """get_events.sh --start_date_time "2026-09-02 00:00:00" --end_date_time "2026-09-09 00:00:00" """);

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastCalendarWindow.ShouldBe(("calendar.alarms", "2026-09-02 00:00:00", "2026-09-09 00:00:00"));
        var events = JsonNode.Parse(exec.Stdout)!["events"]!.AsArray();
        events.Count.ShouldBe(2);
        events[0]!["uid"]!.GetValue<string>().ShouldBe("fd96f9b2");
        events[0]!["summary"]!.GetValue<string>().ShouldBe("Llama al administrador");
        events[0]!["start"]!.GetValue<string>().ShouldBe("2026-09-02T10:30:00+02:00");
        events[0]!["description"]!.GetValue<string>().ShouldContain("insistent");
        events[0]!["rrule"].ShouldBeNull();
        events[1]!["rrule"]!.GetValue<string>().ShouldBe("FREQ=WEEKLY");
        events[1]!["allDay"]!.GetValue<bool>().ShouldBeTrue();
    }

    // Without a window the listing covers the week ahead from now — the question "what alarms do
    // I have" needs no arguments to be answerable.
    [Fact]
    public async Task GetEvents_NoArguments_CoversTheWeekAheadFromNow()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "get_events.sh");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastCalendarWindow!.Value.Start.ShouldBe("2026-09-02T10:00:00+00:00");
        client.LastCalendarWindow.Value.End.ShouldBe("2026-09-09T10:00:00+00:00");
    }

    [Fact]
    public async Task GetEvents_Days_DerivesTheEndFromTheStart()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """get_events.sh --start_date_time "2026-09-02 00:00:00" --days 1""");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastCalendarWindow!.Value.End.ShouldBe("2026-09-03 00:00:00");
    }

    [Fact]
    public async Task DeleteEvent_ByUid_DeletesThatEvent()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "delete_event.sh --uid fd96f9b2");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.DeletedEvents.ShouldHaveSingleItem().ShouldBe(("calendar.alarms", "fd96f9b2", null, null));
        JsonNode.Parse(exec.Stdout)!["ok"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteEvent_OneInstanceOfARecurrence_PassesTheRecurrenceThrough()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs,
            "delete_event.sh --uid 365f3228 --recurrence_id 20260903 --recurrence_range THISANDFUTURE");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.DeletedEvents.Single().ShouldBe(("calendar.alarms", "365f3228", "20260903", "THISANDFUTURE"));
    }

    [Fact]
    public async Task DeleteEvent_NoUid_IsABadArgument()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "delete_event.sh");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--uid");
        client.DeletedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteEvent_HomeAssistantRefuses_IsExitOneWithTheReason()
    {
        var fs = Build(out var client);
        client.CalendarFailure = new HomeAssistantNotFoundException("Home Assistant returned 404: Event not found");

        var exec = await Exec(fs, "delete_event.sh --uid nope");

        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("Event not found");
    }

    [Fact]
    public async Task Read_DeleteEventFile_ReturnsItsUsage()
    {
        var fs = Build(out _);

        var read = (await fs.ReadAsync($"{CalendarDir}/delete_event.sh", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("--uid");
    }
}