using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// Long-term statistics outlive the recorder's retention: hourly mean/min/max (or sum and change for
// a total) that Home Assistant compiles for every sensor with a state_class and keeps for good.
// They are WebSocket-only, so the mount serves `statistics.sh` — in the directories of exactly the
// entities that have them.
public class HaStatisticsActionTests
{
    private const string TempDir = "entities/sensor/temperature_(temperature)";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 16, 0, 0, TimeSpan.Zero);

    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States =
            {
                Entity("sensor.temperature", "21",
                    ("friendly_name", JsonValue.Create("Temperature")),
                    ("state_class", JsonValue.Create("measurement"))),
                Entity("sensor.energy", "1200", ("state_class", JsonValue.Create("total_increasing"))),
                Entity("sensor.glucose", "94"),
                Entity("light.kitchen", "off")
            },
            Services = { Service("light", "turn_on", DomainTarget("light")) }
        };
        var local = client;
        var time = new FakeTimeProvider(Now);
        var provider = new HaCatalogProvider(() => local, time,
            extraServices: [HaHistoryActions.History, HaStatisticsActions.Statistics]);
        return new HaFileSystem(provider, () => local, timeProvider: time);
    }

    private static async Task<FsExecResult> Exec(HaFileSystem fs, string command, string dir = TempDir) =>
        (await fs.ExecAsync(dir, command, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

    private static async Task<IReadOnlyList<string>> Glob(HaFileSystem fs, string dir) =>
        (await fs.GlobAsync(dir, "*", CancellationToken.None))
        .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries.Select(e => e.Split('/')[^1]).ToList();

    private static HaStatisticsRow Row(string start, double? mean = null, double? min = null, double? max = null,
        double? sum = null, double? change = null, double? state = null) => new()
        {
            Start = DateTimeOffset.Parse(start),
            End = DateTimeOffset.Parse(start).AddHours(1),
            Mean = mean, Min = min, Max = max, Sum = sum, Change = change, State = state
        };

    [Fact]
    public async Task Glob_OnlyEntitiesWithAStateClass_ServeStatistics_AndEveryoneServesHistory()
    {
        var fs = Build(out _);

        (await Glob(fs, TempDir)).ShouldContain("statistics.sh");
        (await Glob(fs, "entities/sensor/energy")).ShouldContain("statistics.sh");
        (await Glob(fs, "entities/sensor/glucose")).ShouldNotContain("statistics.sh");
        (await Glob(fs, "entities/light/kitchen")).ShouldNotContain("statistics.sh");
        (await Glob(fs, "entities/sensor/glucose")).ShouldContain("history.sh");
    }

    [Fact]
    public async Task Exec_OnAnEntityWithoutStatistics_IsCommandNotFound()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "statistics.sh", "entities/sensor/glucose");

        exec.ExitCode.ShouldBe(127);
        exec.Stderr.ShouldContain("history.sh");
        client.LastStatisticsWindow.ShouldBeNull();
    }

    [Fact]
    public async Task Help_ListsTheWindowAndPeriodArguments()
    {
        var fs = Build(out _);

        var help = await Exec(fs, "statistics.sh --help");

        help.ExitCode.ShouldBe(0);
        help.Stdout.ShouldContain("--days");
        help.Stdout.ShouldContain("--period");
        help.Stdout.ShouldContain("hour");
        help.Stdout.ShouldContain("month");
        help.Stdout.ShouldContain("--start_date_time");
        help.Stdout.ShouldContain("--limit");
    }

    [Fact]
    public async Task NoArguments_AsksForAWeekOfHours_AndRendersMeanMinMax()
    {
        var fs = Build(out var client);
        client.Statistics.AddRange([
            Row("2026-09-04T12:00:00+00:00", mean: 21.5, min: 21, max: 22),
            Row("2026-09-04T13:00:00+00:00", mean: 22.25, min: 22, max: 23)
        ]);

        var exec = await Exec(fs, "statistics.sh");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastStatisticsWindow.ShouldBe(("sensor.temperature", "2026-08-28T16:00:00+00:00", "2026-09-04T16:00:00+00:00", "hour"));
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["ok"]!.GetValue<bool>().ShouldBeTrue();
        payload["entity_id"]!.GetValue<string>().ShouldBe("sensor.temperature");
        payload["period"]!.GetValue<string>().ShouldBe("hour");
        var rows = payload["rows"]!.AsArray();
        rows.Count.ShouldBe(2);
        rows[0]!["start"]!.GetValue<string>().ShouldBe("2026-09-04T12:00:00+00:00");
        rows[0]!["mean"]!.GetValue<double>().ShouldBe(21.5);
        rows[0]!["min"]!.GetValue<double>().ShouldBe(21);
        rows[0]!["max"]!.GetValue<double>().ShouldBe(22);
        rows[0]!.AsObject().ContainsKey("sum").ShouldBeFalse();
        payload["count"]!.GetValue<int>().ShouldBe(2);
        payload["truncated"]!.GetValue<bool>().ShouldBeFalse();
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task ATotal_RendersSumAndChange_NotAMean()
    {
        var fs = Build(out var client);
        client.Statistics.Add(Row("2026-09-04T12:00:00+00:00", sum: 1200.5, change: 0.7, state: 1200.5));

        var exec = await Exec(fs, "statistics.sh", "entities/sensor/energy");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var row = JsonNode.Parse(exec.Stdout)!["rows"]![0]!.AsObject();
        row["sum"]!.GetValue<double>().ShouldBe(1200.5);
        row["change"]!.GetValue<double>().ShouldBe(0.7);
        row["state"]!.GetValue<double>().ShouldBe(1200.5);
        row.ContainsKey("mean").ShouldBeFalse();
    }

    [Fact]
    public async Task DaysAndPeriod_ShapeTheRequest()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "statistics.sh --days 90 --period day");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastStatisticsWindow.ShouldBe(("sensor.temperature", "2026-06-06T16:00:00+00:00", "2026-09-04T16:00:00+00:00", "day"));
    }

    [Fact]
    public async Task ExplicitWindow_PassesTheCallersStringsThrough()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """statistics.sh --start_date_time "2026-08-01 00:00:00" --end_date_time "2026-09-01 00:00:00" --period week""");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        client.LastStatisticsWindow.ShouldBe(("sensor.temperature", "2026-08-01 00:00:00", "2026-09-01 00:00:00", "week"));
    }

    [Fact]
    public async Task DaysAndStart_IsAContradiction()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, """statistics.sh --days 3 --start_date_time "2026-08-01 00:00:00" """);

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("--days");
        client.LastStatisticsWindow.ShouldBeNull();
    }

    [Fact]
    public async Task AnUnknownPeriod_IsABadArgument()
    {
        var fs = Build(out var client);

        var exec = await Exec(fs, "statistics.sh --period fortnight");

        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("hour");
        client.LastStatisticsWindow.ShouldBeNull();
    }

    [Fact]
    public async Task MoreRowsThanTheLimit_KeepsTheLatest_AndSuggestsACoarserPeriod()
    {
        var fs = Build(out var client);
        client.Statistics.AddRange(Enumerable.Range(0, 12).Select(i => Row($"2026-09-04T{i:00}:00:00+00:00", mean: i)));

        var exec = await Exec(fs, "statistics.sh --limit 4");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        var rows = payload["rows"]!.AsArray();
        rows.Count.ShouldBe(4);
        rows[0]!["mean"]!.GetValue<double>().ShouldBe(8);
        payload["count"]!.GetValue<int>().ShouldBe(12);
        payload["truncated"]!.GetValue<bool>().ShouldBeTrue();
        payload["suggestion"]!.GetValue<string>().ShouldContain("--period");
    }

    [Fact]
    public async Task NoRows_SaysWhenTheFirstOneComes()
    {
        var fs = Build(out _);

        var exec = await Exec(fs, "statistics.sh");

        exec.ExitCode.ShouldBe(0, exec.Stderr);
        var payload = JsonNode.Parse(exec.Stdout)!.AsObject();
        payload["rows"]!.AsArray().Count.ShouldBe(0);
        payload["suggestion"]!.GetValue<string>().ShouldContain("hour");
    }

    [Fact]
    public async Task HomeAssistantRefusingTheWindow_IsExitOne_WithTheReason()
    {
        var fs = Build(out var client);
        client.StatisticsFailure = new HomeAssistantException("Home Assistant refused the command (invalid_start_time): Invalid start_time");

        var exec = await Exec(fs, "statistics.sh --start_date_time nonsense");

        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("Invalid start_time");
    }
}