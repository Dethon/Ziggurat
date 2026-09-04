using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The need these cover: a glucose sensor updates every minute, and "how was it overnight" cannot be
// answered from state.json, which holds one number. Home Assistant's recorder keeps the changes, but
// the service catalog has no way to read them — the history endpoint is REST only — so the mount
// serves `history.sh` in every entity directory, read-only classes included, the way the calendar's
// listing is served.
public class HaHistoryActionTests
{
    private const string GlucoseDir = "entities/sensor/glucose_(glucose)";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 16, 0, 0, TimeSpan.Zero);

    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States =
            {
                Entity("sensor.glucose", "94", ("friendly_name", JsonValue.Create("Glucose"))),
                Entity("light.kitchen", "off"),
                Entity("binary_sensor.door", "off")
            },
            Services = { Service("light", "turn_on", DomainTarget("light")) }
        };
        var local = client;
        var time = new FakeTimeProvider(Now);
        var provider = new HaCatalogProvider(() => local, time, extraServices: [HaHistoryActions.History]);
        return new HaFileSystem(provider, () => local, timeProvider: time);
    }

    private static async Task<FsExecResult> Exec(HaFileSystem fs, string command, string dir = GlucoseDir) =>
        (await fs.ExecAsync(dir, command, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

    private static HaStateChange Change(string state, string at) =>
        new() { State = state, At = DateTimeOffset.Parse(at) };

    [Fact]
    public async Task Glob_EveryEntityDirectory_ServesHistory_ReadOnlyClassesIncluded()
    {
        var fs = Build(out _);

        foreach (var dir in new[] { GlucoseDir, "entities/light/kitchen", "entities/binary_sensor/door" })
        {
            var entries = (await fs.GlobAsync(dir, "*", CancellationToken.None))
                .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
            entries.ShouldContain(e => e.EndsWith("/history.sh"), dir);
            entries.Count(e => e.EndsWith("/history.sh")).ShouldBe(1, dir);
        }

        // Bare name, never domain-qualified: it is nobody's service.
        var light = (await fs.GlobAsync("entities/light/kitchen", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
        light.ShouldContain(e => e.EndsWith("/turn_on.sh"));
        light.ShouldNotContain(e => e.Contains("homeassistant.history"));
    }

    [Fact]
    public async Task Help_ListsTheWindowAndSummaryArguments()
    {
        var fs = Build(out _);

        var help = await Exec(fs, "history.sh --help");

        help.ExitCode.ShouldBe(0);
        help.Stdout.ShouldContain("--hours");
        help.Stdout.ShouldContain("--start_date_time");
        help.Stdout.ShouldContain("--end_date_time");
        help.Stdout.ShouldContain("--every");
        help.Stdout.ShouldContain("--limit");
    }

    [Fact]
    public async Task NoArguments_AsksForTheLastDayEndingNow_AndListsEveryChange()
    {
        var fs = Build(out var client);
        client.History.AddRange([
            Change("104", "2026-09-04T12:35:31+00:00"),
            Change("102", "2026-09-04T12:36:30+00:00"),
            Change("100", "2026-09-04T12:41:22+00:00")
        ]);

        var exec = await Exec(fs, "history.sh");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastHistoryWindow.ShouldBe(("sensor.glucose", "2026-09-03T16:00:00+00:00", "2026-09-04T16:00:00+00:00"));
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["ok"]!.GetValue<bool>().ShouldBeTrue();
        payload["entity_id"]!.GetValue<string>().ShouldBe("sensor.glucose");
        payload["window"]!["start"]!.GetValue<string>().ShouldBe("2026-09-03T16:00:00+00:00");
        payload["window"]!["end"]!.GetValue<string>().ShouldBe("2026-09-04T16:00:00+00:00");
        var changes = payload["changes"]!.AsArray();
        changes.Count.ShouldBe(3);
        changes[0]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T12:35:31+00:00");
        changes[0]!["state"]!.GetValue<string>().ShouldBe("104");
        changes[2]!["state"]!.GetValue<string>().ShouldBe("100");
        payload["count"]!.GetValue<int>().ShouldBe(3);
        payload["truncated"]!.GetValue<bool>().ShouldBeFalse();
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task Hours_SetsTheWindowStart()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "history.sh --hours 6");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastHistoryWindow!.Value.Start.ShouldBe("2026-09-04T10:00:00+00:00");
        client.LastHistoryWindow!.Value.End.ShouldBe("2026-09-04T16:00:00+00:00");
    }

    [Fact]
    public async Task ExplicitWindow_PassesTheCallersStringsThrough_AndEndDefaultsToNow()
    {
        var fs = Build(out var client);

        var both = await Exec(fs, """history.sh --start_date_time "2026-09-03 22:00:00" --end_date_time "2026-09-04 08:00:00" """);
        both.ExitCode.ShouldBe(0, both.Stderr);
        client.LastHistoryWindow.ShouldBe(("sensor.glucose", "2026-09-03 22:00:00", "2026-09-04 08:00:00"));

        var startOnly = await Exec(fs, """history.sh --start_date_time "2026-09-03 22:00:00" """);
        startOnly.ExitCode.ShouldBe(0, startOnly.Stderr);
        client.LastHistoryWindow.ShouldBe(("sensor.glucose", "2026-09-03 22:00:00", "2026-09-04T16:00:00+00:00"));
    }

    [Fact]
    public async Task EndWithHours_CountsBackFromTheEnd_InTheEndsOwnShape()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """history.sh --end_date_time "2026-09-04 08:00:00" --hours 10""");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastHistoryWindow.ShouldBe(("sensor.glucose", "2026-09-03 22:00:00", "2026-09-04 08:00:00"));
    }

    [Fact]
    public async Task HoursAndStart_IsAContradiction()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """history.sh --hours 6 --start_date_time "2026-09-03 22:00:00" """);

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--hours");
        exec.Stderr.ShouldContain("--start_date_time");
        client.LastHistoryWindow.ShouldBeNull();
    }

    [Fact]
    public async Task MoreChangesThanTheLimit_KeepsTheLatest_AndSaysHowToSummarise()
    {
        var fs = Build(out var client);
        client.History.AddRange(Enumerable.Range(0, 30).Select(i =>
            Change((100 + i).ToString(), $"2026-09-04T12:{i:00}:00+00:00")));

        var exec = await Exec(fs, "history.sh --limit 5");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        var changes = payload["changes"]!.AsArray();
        changes.Count.ShouldBe(5);
        changes[0]!["state"]!.GetValue<string>().ShouldBe("125");
        changes[4]!["state"]!.GetValue<string>().ShouldBe("129");
        payload["count"]!.GetValue<int>().ShouldBe(30);
        payload["truncated"]!.GetValue<bool>().ShouldBeTrue();
        payload["suggestion"]!.GetValue<string>().ShouldContain("--every");
    }

    [Fact]
    public async Task Every_SummarisesNumericStatesPerBucket()
    {
        var fs = Build(out var client);
        client.History.AddRange([
            Change("100", "2026-09-04T12:01:00+00:00"),
            Change("110", "2026-09-04T12:07:00+00:00"),
            Change("90", "2026-09-04T12:14:00+00:00"),
            Change("unavailable", "2026-09-04T12:20:00+00:00"),
            Change("120", "2026-09-04T12:31:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 15");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["every_minutes"]!.GetValue<int>().ShouldBe(15);
        payload["samples"]!.GetValue<int>().ShouldBe(4);
        payload["skipped"]!.GetValue<int>().ShouldBe(1);
        var buckets = payload["buckets"]!.AsArray();
        buckets.Count.ShouldBe(2);
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T12:00:00+00:00");
        buckets[0]!["min"]!.GetValue<double>().ShouldBe(90);
        buckets[0]!["max"]!.GetValue<double>().ShouldBe(110);
        buckets[0]!["mean"]!.GetValue<double>().ShouldBe(100);
        buckets[0]!["last"]!.GetValue<double>().ShouldBe(90);
        buckets[0]!["samples"]!.GetValue<int>().ShouldBe(3);
        buckets[1]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T12:30:00+00:00");
        buckets[1]!["samples"]!.GetValue<int>().ShouldBe(1);
        payload.ContainsKey("changes").ShouldBeFalse();
    }

    // The instants carry the home's offset, and the buckets follow it: a day bucket runs from the
    // home's midnight, not UTC's, and its stamp keeps the same offset the changes have.
    [Fact]
    public async Task Every_AlignsBucketsToTheChangesOwnOffset()
    {
        var fs = Build(out var client);
        client.History.AddRange([
            Change("100", "2026-09-03T23:30:00+02:00"),
            Change("110", "2026-09-04T01:00:00+02:00"),
            Change("120", "2026-09-04T13:00:00+02:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 1440");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var buckets = JsonNode.Parse(exec.Stdout)!["buckets"]!.AsArray();
        buckets.Count.ShouldBe(2);
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-09-03T00:00:00+02:00");
        buckets[0]!["samples"]!.GetValue<int>().ShouldBe(1);
        buckets[1]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T00:00:00+02:00");
        buckets[1]!["samples"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Every_OnAnEntityWithNoNumericStates_IsABadArgument()
    {
        var fs = Build(out var client);
        client.History.AddRange([Change("on", "2026-09-04T12:01:00+00:00"), Change("off", "2026-09-04T12:07:00+00:00")]);

        var exec = await Exec(fs, "history.sh --every 15", "entities/light/kitchen");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--every");
        exec.Stderr.ShouldContain("numeric");
    }

    [Fact]
    public async Task NoChanges_SaysSo_AndNamesTheRetention()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "history.sh --hours 240");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["changes"]!.AsArray().Count.ShouldBe(0);
        payload["suggestion"]!.GetValue<string>().ShouldContain("retention");
    }

    [Fact]
    public async Task HomeAssistantRefusingTheWindow_IsExitOne_WithTheReason()
    {
        var fs = Build(out var client);
        client.HistoryFailure = new HomeAssistantException("Home Assistant returned 400: Invalid datetime", 400);

        var exec = await Exec(fs, "history.sh --start_date_time nonsense");

        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("Invalid datetime");
    }

    [Fact]
    public async Task Read_TheActionFile_RendersItsHelp()
    {
        var fs = Build(out _);

        var read = (await fs.ReadAsync($"{GlucoseDir}/history.sh", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("--hours");
    }
}