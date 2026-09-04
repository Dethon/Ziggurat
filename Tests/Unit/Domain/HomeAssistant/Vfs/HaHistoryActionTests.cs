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
        // An end given alone: the default length is counted back from it, and the help says so.
        help.Stdout.ShouldContain("counted back");
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

    // The recorder stamps every change in UTC whatever the home's zone (Home Assistant's history
    // endpoint formats `last_changed` from a UTC timestamp), so the buckets cannot follow the
    // stamps' offset: they follow the home's clock, read from its configuration, and a day bucket
    // runs from the home's midnight. The stamp of a bucket carries the home's offset at that instant.
    [Fact]
    public async Task Every_AlignsBucketsToTheHomesClock_NotTheUtcStampsTheRecorderSends()
    {
        var fs = Build(out var client);
        client.TimeZone = "Europe/Madrid";
        client.History.AddRange([
            Change("100", "2026-09-03T21:30:00+00:00"),
            Change("110", "2026-09-03T23:00:00+00:00"),
            Change("120", "2026-09-04T11:00:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 1440");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["bucket_zone"]!.GetValue<string>().ShouldBe("Europe/Madrid");
        var buckets = payload["buckets"]!.AsArray();
        buckets.Count.ShouldBe(2);
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-09-03T00:00:00+02:00");
        buckets[0]!["samples"]!.GetValue<int>().ShouldBe(1);
        buckets[1]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T00:00:00+02:00");
        buckets[1]!["samples"]!.GetValue<int>().ShouldBe(2);
    }

    // A home whose zone could not be read (or is unknown here) buckets on UTC and says so, rather
    // than guessing this process's zone.
    [Fact]
    public async Task Every_WithoutAKnownHomeZone_BucketsOnUtc_AndSaysSo()
    {
        var fs = Build(out var client);
        client.TimeZone = null;
        client.History.AddRange([
            Change("100", "2026-09-03T21:30:00+00:00"),
            Change("110", "2026-09-03T23:00:00+00:00"),
            Change("120", "2026-09-04T11:00:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 1440");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["bucket_zone"]!.GetValue<string>().ShouldBe("UTC");
        var buckets = payload["buckets"]!.AsArray();
        buckets.Count.ShouldBe(2);
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-09-03T00:00:00+00:00");
        buckets[0]!["samples"]!.GetValue<int>().ShouldBe(2);
        buckets[1]!["at"]!.GetValue<string>().ShouldBe("2026-09-04T00:00:00+00:00");
    }

    // The help's range is read as a hard limit, and the parser enforces none: a ceiling of ten days
    // would stop the model asking for the month a home with raised retention can answer.
    [Fact]
    public async Task Help_PutsNoCeilingOnHours()
    {
        var fs = Build(out _);

        var help = await Exec(fs, "history.sh --help");

        help.Stdout.ShouldNotContain("1-240");
        var month = await Exec(fs, "history.sh --hours 720");
        month.ExitCode.ShouldBe(0, month.Stderr);
    }

    // A mean is rounded to one place past the samples' own precision, so integer readings give a
    // tenth and a kilowatt sensor's thousandths are not flattened to 0.0.
    [Fact]
    public async Task Every_RoundsTheMean_ToOnePlacePastTheSamplesPrecision()
    {
        var fs = Build(out var client);
        client.History.AddRange([
            Change("0.012", "2026-09-04T12:01:00+00:00"),
            Change("0.015", "2026-09-04T12:07:00+00:00"),
            Change("0.014", "2026-09-04T12:14:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 60");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var buckets = JsonNode.Parse(exec.Stdout)!["buckets"]!.AsArray();
        buckets[0]!["mean"]!.GetValue<double>().ShouldBe(0.0137);
    }

    [Fact]
    public async Task Every_IntegerSamples_GiveAMeanToOneDecimal()
    {
        var fs = Build(out var client);
        client.History.AddRange([
            Change("100", "2026-09-04T12:01:00+00:00"),
            Change("101", "2026-09-04T12:07:00+00:00"),
            Change("101", "2026-09-04T12:14:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 60");

        var buckets = JsonNode.Parse(exec.Stdout)!["buckets"]!.AsArray();
        buckets[0]!["mean"]!.GetValue<double>().ShouldBe(100.7);
    }

    // The hour the clocks fall back happens twice on the wall; both belong to the same wall-clock
    // bucket, which is stamped at its first occurrence, the DST offset, not the second.
    [Fact]
    public async Task Every_TheRepeatedHourOfAFallBack_IsOneBucket_StampedAtItsFirstOccurrence()
    {
        var fs = Build(out var client);
        client.TimeZone = "Europe/Madrid";
        client.History.AddRange([
            Change("100", "2026-10-25T00:30:00+00:00"),
            Change("110", "2026-10-25T01:30:00+00:00"),
            Change("120", "2026-10-25T02:30:00+00:00")
        ]);

        var exec = await Exec(fs, "history.sh --every 60");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var buckets = JsonNode.Parse(exec.Stdout)!["buckets"]!.AsArray();
        buckets.Count.ShouldBe(2);
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-10-25T02:00:00+02:00");
        buckets[0]!["samples"]!.GetValue<int>().ShouldBe(2);
        buckets[1]!["at"]!.GetValue<string>().ShouldBe("2026-10-25T03:00:00+01:00");
    }

    // A bucket whose wall-clock start the spring-forward skips opens at the instant the clock
    // jumps to, never at a time that did not exist.
    [Fact]
    public async Task Every_ABucketOpeningInTheSkippedHour_IsStampedWhereTheClockLanded()
    {
        var fs = Build(out var client);
        client.TimeZone = "Europe/Madrid";
        client.History.AddRange([Change("100", "2026-03-29T01:30:00+00:00")]);

        var exec = await Exec(fs, "history.sh --every 120");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var buckets = JsonNode.Parse(exec.Stdout)!["buckets"]!.AsArray();
        buckets.ShouldHaveSingleItem();
        buckets[0]!["at"]!.GetValue<string>().ShouldBe("2026-03-29T03:00:00+02:00");
    }

    // --limit caps a listing; a summary has buckets, not changes, so the pair is a mistake to name,
    // not one to ignore.
    [Fact]
    public async Task Every_WithLimit_IsABadArgument()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "history.sh --every 60 --limit 5");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--every");
        exec.Stderr.ShouldContain("--limit");
        client.LastHistoryWindow.ShouldBeNull();
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

    // An out-of-retention day is exactly when the model reaches for --every, so the empty window
    // must answer with the retention note there too, not fall over on an empty summary.
    [Fact]
    public async Task NoChanges_UnderEvery_IsAnEmptySummary_WithTheRetentionNote()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "history.sh --every 60 --hours 240");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["buckets"]!.AsArray().Count.ShouldBe(0);
        payload["samples"]!.GetValue<int>().ShouldBe(0);
        payload["skipped"]!.GetValue<int>().ShouldBe(0);
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