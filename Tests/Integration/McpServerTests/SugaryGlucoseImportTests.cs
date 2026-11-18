using System.Globalization;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using McpServerSugary.Import;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her glucose from before Sugary read LibreLinkUp, carried over once from Home Assistant by the
// operator's `import-glucose` verb: the recorder's history where it reaches, the long-term statistics'
// hourly means before it, each kept as hers and never beside a reading she already has. Home Assistant
// is faked on the wire, REST and WebSocket on one address, as the command is given one.
public sealed class SugaryGlucoseImportTests : IAsyncLifetime
{
    private const string Entity = "sensor.glucosa_ana";
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _secrets = Path.Combine(Path.GetTempPath(), "sugary-glucose-import", Guid.NewGuid().ToString("N"));
    private SugaryServer _server = null!;
    private FakeHomeAssistantSocket _home = null!;

    private string TokenFile => Path.Combine(_secrets, "ha-token");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_secrets);
        await File.WriteAllTextAsync(TokenFile, FakeHomeAssistantSocket.ValidToken + "\n");
        _server = await SugaryServer.StartAsync();
        _home = await FakeHomeAssistantSocket.StartAsync();
        Measures("mg/dL");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _home.DisposeAsync();
        Directory.Delete(_secrets, recursive: true);
    }

    private void Measures(string? unit) =>
        _home.States[Entity] = ("118", unit is null
            ? new JsonObject { ["state_class"] = "measurement" }
            : new JsonObject { ["unit_of_measurement"] = unit, ["state_class"] = "measurement" });

    private void Recorded(params (DateTimeOffset At, string State)[] changes) => _home.History[Entity] = [.. changes];

    private void Compiled(params (DateTimeOffset Start, double? Mean)[] hours) =>
        _home.Statistics[Entity] = [.. hours.Select(h => (JsonNode)new JsonObject
        {
            ["start"] = h.Start.ToUnixTimeMilliseconds(),
            ["end"] = (h.Start + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
            ["mean"] = h.Mean,
            ["min"] = h.Mean,
            ["max"] = h.Mean
        })];

    private string[] Arguments(Account person, params string[] extra) =>
    [
        "import-glucose", "--ha", _home.BaseUrl, "--token-file", TokenFile, "--entity", Entity,
        "--tenant", person.TenantId, "--user", person.UserId, .. extra
    ];

    private async Task<(int Exit, string Output)> ImportAsync(params string[] arguments)
    {
        await using var output = new StringWriter();
        var exit = await SugaryCommand.RunAsync(
            arguments, _server.Settings, output, CancellationToken.None, new FakeTimeProvider(_now));
        return (exit, output.ToString());
    }

    private Task<(int Exit, string Output)> ImportHersAsync(params string[] extra) =>
        ImportAsync(Arguments(SugaryServer.Her, extra));

    private async Task<IReadOnlyList<GlucoseReading>> ReadingsAsync(Account person)
    {
        await using var db = _server.Services.GetRequiredService<SugaryStore>().For(person);
        return await db.GlucoseReadings.AsNoTracking().OrderBy(r => r.At).ToListAsync();
    }

    private async Task SheHasAsync(params (DateTimeOffset At, double MgPerDl)[] readings)
    {
        await using var db = _server.Services.GetRequiredService<SugaryStore>().For(SugaryServer.Her);
        db.GlucoseReadings.AddRange(readings.Select(r => new GlucoseReading
        {
            At = r.At, MgPerDl = r.MgPerDl, Trend = GlucoseTrend.Steady, Source = GlucoseSource.Current
        }));
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Ago(double hours) => _now - TimeSpan.FromHours(hours);

    [Fact]
    public async Task ARun_ImportsTheHistoryAsHers_AndASecondRunImportsNothing()
    {
        Recorded((Ago(3), "110"), (Ago(2.9), "115"), (Ago(2.8), "121.5"), (Ago(0.5), "140"));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(0, output);
        output.ShouldContain("imported 4 readings and 0 hourly means");
        var readings = await ReadingsAsync(SugaryServer.Her);
        readings.Select(r => (r.At, r.MgPerDl, r.Source, r.Trend)).ShouldBe(
        [
            (Ago(3), 110, GlucoseSource.Import, GlucoseTrend.Unknown),
            (Ago(2.9), 115, GlucoseSource.Import, GlucoseTrend.Unknown),
            (Ago(2.8), 121.5, GlucoseSource.Import, GlucoseTrend.Unknown),
            (Ago(0.5), 140, GlucoseSource.Import, GlucoseTrend.Unknown)
        ]);
        readings.ShouldAllBe(r => r.At.Offset == TimeSpan.Zero);
        (await ReadingsAsync(SugaryServer.Him)).ShouldBeEmpty();

        var (again, secondOutput) = await ImportHersAsync();

        again.ShouldBe(0, secondOutput);
        secondOutput.ShouldContain("imported 0 readings and 0 hourly means");
        secondOutput.ShouldContain("skipped 4");
        (await ReadingsAsync(SugaryServer.Her)).Count.ShouldBe(4);
    }

    // Home Assistant's recorder keeps a sensor's outage as states of their own; none is a reading.
    [Fact]
    public async Task StatesThatAreNotNumbers_AreSkipped()
    {
        Recorded((Ago(5), "unavailable"), (Ago(4.9), "100"), (Ago(4.8), "unknown"), (Ago(4.7), ""),
            (Ago(4.6), "NaN"), (Ago(4.5), "-3"), (Ago(4.4), "101"));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(0, output);
        (await ReadingsAsync(SugaryServer.Her)).Select(r => (r.At, r.MgPerDl)).ShouldBe([(Ago(4.9), 100), (Ago(4.4), 101)]);
        output.ShouldContain("5 that were not readings");
    }

    // A sensor Home Assistant reads in mmol/L is kept in mg/dL, as every reading of hers is.
    [Fact]
    public async Task AnMmolSensor_IsKeptInMgPerDl()
    {
        Measures("mmol/L");
        Recorded((Ago(30), "5.5"));
        Compiled((Ago(100), 7.0));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(0, output);
        var readings = await ReadingsAsync(SugaryServer.Her);
        readings.Select(r => r.MgPerDl).ShouldBe([7.0 * 18.016, 5.5 * 18.016], tolerance: 1e-9);
    }

    // A unit that is neither, or none at all, would be a guess about a health record.
    [Theory]
    [InlineData("%")]
    [InlineData(null)]
    public async Task ASensorOfAnotherUnit_IsRefused_AndNothingWritten(string? unit)
    {
        Measures(unit);
        Recorded((Ago(1), "6"));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(1);
        output.ShouldContain("nothing was imported");
        (await ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEntityHomeAssistantDoesNotKnow_IsRefused()
    {
        _home.States.Clear();

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(1);
        output.ShouldContain(Entity);
        (await ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
    }

    // Ten days a window, from --days back to now, in UTC; each window opens with the state the sensor
    // had at its start, stamped there, which is no measurement and is not kept — a sensor silent across
    // a window's start does not grow a reading on it.
    [Fact]
    public async Task TheHistory_IsReadTenDaysAtATime_AndAWindowsOpeningStateIsNoReading()
    {
        var silentAcrossTheBoundary = _now - TimeSpan.FromDays(15) - TimeSpan.FromHours(2);
        Recorded((_now - TimeSpan.FromDays(30), "90"), (silentAcrossTheBoundary, "150"), (Ago(1), "130"));

        var (exit, output) = await ImportHersAsync("--days", "25");

        exit.ShouldBe(0, output);
        _home.HistoryRequests.ShouldBe(
        [
            ("2026-10-24T12:00:00Z", "2026-11-03T12:00:00Z"),
            ("2026-11-03T12:00:00Z", "2026-11-13T12:00:00Z"),
            ("2026-11-13T12:00:00Z", "2026-11-18T12:00:00Z")
        ]);
        (await ReadingsAsync(SugaryServer.Her)).Select(r => (r.At, r.MgPerDl))
            .ShouldBe([(silentAcrossTheBoundary, 150), (Ago(1), 130)]);
    }

    // The history's points are the curve's kind: one within 2.5 minutes of a reading she has, or of one
    // just imported, is the same moment seen twice.
    [Fact]
    public async Task APointBesideAReadingSheHas_IsSkipped_AndTheHistoryIsThinnedAsTheCurveIs()
    {
        await SheHasAsync((Ago(2), 120));
        Recorded(
            (Ago(2) - TimeSpan.FromMinutes(2), "119"),
            (Ago(2) + TimeSpan.FromMinutes(2.5), "121"),
            (Ago(1), "100"), (Ago(1) + TimeSpan.FromMinutes(1), "101"), (Ago(1) + TimeSpan.FromMinutes(3), "103"));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(0, output);
        (await ReadingsAsync(SugaryServer.Her)).Select(r => (r.At, r.MgPerDl, r.Source)).ShouldBe(
        [
            (Ago(2), 120, GlucoseSource.Current),
            (Ago(2) + TimeSpan.FromMinutes(2.5), 121, GlucoseSource.Import),
            (Ago(1), 100, GlucoseSource.Import),
            (Ago(1) + TimeSpan.FromMinutes(3), 103, GlucoseSource.Import)
        ]);
        output.ShouldContain("imported 3 readings");
        output.ShouldContain("skipped 2");
    }

    // Before the recorder's first point, the hourly means of the long-term statistics, stamped at the
    // hour's start and marked a lower resolution; never one for an hour she has readings in, nor one
    // the statistics left without a mean, nor any from where the history reaches.
    [Fact]
    public async Task HourlyMeans_FillTheDaysBeforeTheHistory()
    {
        var historyStarts = _now - TimeSpan.FromDays(3);
        Recorded((historyStarts, "100"), (Ago(1), "110"));
        var hourSheHas = historyStarts - TimeSpan.FromHours(10);
        await SheHasAsync((hourSheHas + TimeSpan.FromMinutes(20), 95));
        Compiled(
            (historyStarts - TimeSpan.FromHours(12), 140),
            (historyStarts - TimeSpan.FromHours(11), null),
            (hourSheHas, 90),
            (historyStarts - TimeSpan.FromHours(1), 120),
            (historyStarts, 101),
            (Ago(5), 111));

        var (exit, output) = await ImportHersAsync("--days", "5");

        exit.ShouldBe(0, output);
        var hourly = (await ReadingsAsync(SugaryServer.Her)).Where(r => r.Source == GlucoseSource.ImportHourly);
        hourly.Select(r => (r.At, r.MgPerDl)).ShouldBe(
        [
            (historyStarts - TimeSpan.FromHours(12), 140),
            (historyStarts - TimeSpan.FromHours(1), 120)
        ]);
        var asked = _home.LastStatisticsRequest.ShouldNotBeNull();
        asked.Period.ShouldBe("hour");
        asked.Start.ShouldBe("2026-11-13T12:00:00Z");
        output.ShouldContain("imported 2 readings and 2 hourly means");

        var (_, again) = await ImportHersAsync("--days", "5");

        again.ShouldContain("imported 0 readings and 0 hourly means");
    }

    [Fact]
    public async Task WithNoHistory_TheHourlyMeansCoverTheWholeSpan()
    {
        Compiled((Ago(48), 130), (Ago(47), 131));

        var (exit, output) = await ImportHersAsync("--days", "3");

        exit.ShouldBe(0, output);
        (await ReadingsAsync(SugaryServer.Her)).Select(r => (r.At, r.MgPerDl, r.Source))
            .ShouldBe([(Ago(48), 130, GlucoseSource.ImportHourly), (Ago(47), 131, GlucoseSource.ImportHourly)]);
    }

    // The token is a key to the whole home: read from a file, never taken as an argument (a shell's
    // history and the process list keep those), and never printed.
    [Fact]
    public async Task ATokenOnTheCommandLine_IsRefused_AndNeverEchoed()
    {
        Recorded((Ago(1), "100"));
        string[] arguments = [.. Arguments(SugaryServer.Her).Where(a => a != "--token-file" && a != TokenFile), "--token", "s3cr3t-value"];

        var (exit, output) = await ImportAsync(arguments);

        exit.ShouldBe(2);
        output.ShouldContain("--token-file");
        output.ShouldNotContain("s3cr3t-value");
        (await ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenHomeAssistantRefuses_ImportsNothing_AndIsNotPrinted()
    {
        await File.WriteAllTextAsync(TokenFile, "wrong-token");
        Recorded((Ago(1), "100"));

        var (exit, output) = await ImportHersAsync();

        exit.ShouldBe(1);
        output.ShouldNotContain("wrong-token");
        (await ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--user", "")]
    [InlineData("--user", "legacy")]
    [InlineData("--days", "0")]
    [InlineData("--days", "many")]
    public async Task ArgumentsThatNameNobodyOrNoSpan_AreRefused(string flag, string value)
    {
        Recorded((Ago(1), "100"));
        var arguments = Arguments(SugaryServer.Her).ToList();
        var at = arguments.IndexOf(flag);
        if (at < 0)
        {
            arguments.AddRange([flag, value]);
        }
        else
        {
            arguments[at + 1] = value;
        }

        var (exit, _) = await ImportAsync([.. arguments]);

        exit.ShouldBe(2);
        (await ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
    }

    [Fact]
    public void TheVerb_IsACommand_SoTheServerDoesNotStart() =>
        SugaryCommand.IsCommand(["import-glucose", "--ha", "http://ha:8123"]).ShouldBeTrue();

    // Stamps cross as UTC instants however the home's zone stamps its wall clock.
    [Fact]
    public async Task Instants_AreStoredAsUtc()
    {
        Recorded((new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(1)), "100"));

        await ImportHersAsync();

        var reading = (await ReadingsAsync(SugaryServer.Her)).ShouldHaveSingleItem();
        reading.At.ShouldBe(DateTimeOffset.Parse("2026-11-18T09:00:00Z", CultureInfo.InvariantCulture));
        reading.At.Offset.ShouldBe(TimeSpan.Zero);
    }
}