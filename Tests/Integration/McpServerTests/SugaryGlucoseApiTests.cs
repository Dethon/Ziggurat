using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;
using GlucoseSource = Domain.DTOs.Sugary.GlucoseSource;
using GlucoseTrend = Domain.DTOs.Sugary.GlucoseTrend;
using GlucoseUnit = Domain.DTOs.Sugary.GlucoseUnit;

namespace Tests.Integration.McpServerTests;

// What the Glucose tab reads: her newest reading with its arrow and age beside her link's state, her
// curve over a window (kept small for a phone), and a clinic's figures over a day, a week, two weeks
// or ninety days. Her readings are hers alone, and outlive the link: an unlinked person with an
// imported history still has a curve, read in mg/dL against 70–180.
public class SugaryGlucoseApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset _morning = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(SugaryServer server, Account person, params GlucoseReading[] readings)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.GlucoseReadings.AddRange(readings);
        await db.SaveChangesAsync();
    }

    private static GlucoseReading Reading(DateTimeOffset at, double mgPerDl, GlucoseTrend trend = GlucoseTrend.Unknown) =>
        new() { At = at, MgPerDl = mgPerDl, Trend = trend, Source = GlucoseSource.Graph };

    private static GlucoseReading MinutesAgo(double minutes, double mgPerDl, GlucoseTrend trend = GlucoseTrend.Unknown) =>
        Reading(DateTimeOffset.UtcNow.AddMinutes(-minutes), mgPerDl, trend);

    private static async Task LinkAsync(SugaryServer server, Account person, Action<GlucoseLink>? shape = null)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        var link = new GlucoseLink
        {
            Email = "ana@example.com",
            SealedPassword = [1, 2, 3],
            PatientId = "patient-1",
            LinkedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastSuccessAt = DateTimeOffset.UtcNow
        };
        link.Connections.Add(new GlucoseConnectionOffered { PatientId = "patient-1", Name = "Ana García" });
        shape?.Invoke(link);
        db.GlucoseLinks.Add(link);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonNode> GetAsync(SugaryServer server, string path, Account? person = null)
    {
        var response = await server.As(person ?? SugaryServer.Her).GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(SugaryServer server, string path)
    {
        var response = await server.As(SugaryServer.Her).GetAsync(path);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>(_json);
        return (response.StatusCode, body?["code"]?.GetValue<string>());
    }

    private static string Q(DateTimeOffset instant) => Uri.EscapeDataString(instant.ToString("O"));

    private static double Number(JsonNode? node, string field) => node![field]!.GetValue<double>();

    [Fact]
    public async Task NothingRead_HasNoCurrentReading_AndHerDefaults()
    {
        await using var server = await SugaryServer.StartAsync();

        var now = await GetAsync(server, "/api/glucose/current");

        now["reading"].ShouldBeNull();
        now["link"].ShouldBeNull();
        now["canLink"]!.GetValue<bool>().ShouldBeFalse();
        now["unit"]!.GetValue<string>().ShouldBe("mgdl");
        now["targetLow"]!.GetValue<int>().ShouldBe(70);
        now["targetHigh"]!.GetValue<int>().ShouldBe(180);
    }

    [Fact]
    public async Task TheCurrentReading_IsHerNewest_WithItsArrowAndAge()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(10, 120), MinutesAgo(3, 141.5, GlucoseTrend.RisingFast));
        await SeedAsync(server, SugaryServer.Him, MinutesAgo(1, 60, GlucoseTrend.Falling));

        var reading = (await GetAsync(server, "/api/glucose/current"))["reading"];

        Number(reading, "mgPerDl").ShouldBe(141.5);
        reading!["trend"]!.GetValue<string>().ShouldBe("risingFast");
        reading["ageSeconds"]!.GetValue<int>().ShouldBeInRange(175, 240);
    }

    [Fact]
    public async Task TheCurrentReading_CarriesHerLinkAndHowSheReadsIt()
    {
        await using var server = await SugaryServer.StartAsync();
        var failingSince = DateTimeOffset.UtcNow.AddMinutes(-40);
        await LinkAsync(server, SugaryServer.Her, link =>
        {
            link.Unit = GlucoseUnit.Mmol;
            link.TargetLow = 80;
            link.TargetHigh = 160;
            link.LastFailure = GlucoseLinkFailure.Unreachable;
            link.FailingSince = failingSince;
        });

        var now = await GetAsync(server, "/api/glucose/current");

        now["unit"]!.GetValue<string>().ShouldBe("mmol");
        now["targetLow"]!.GetValue<int>().ShouldBe(80);
        now["targetHigh"]!.GetValue<int>().ShouldBe(160);
        now["link"]!["name"]!.GetValue<string>().ShouldBe("Ana García");
        now["link"]!["lastFailure"]!.GetValue<string>().ShouldBe("unavailable");
        now["link"]!["failingSince"]!.GetValue<DateTimeOffset>().ShouldBe(failingSince, TimeSpan.FromSeconds(1));
        now["link"]!.AsObject().ContainsKey("sealedPassword").ShouldBeFalse();
    }

    // A password the server could not open is said as its own failure, for the app to ask her to link again.
    [Fact]
    public async Task ALinkWhosePasswordDoesNotOpen_SaysSo()
    {
        await using var server = await SugaryServer.StartAsync();
        await LinkAsync(server, SugaryServer.Her, link =>
        {
            link.LastFailure = GlucoseLinkFailure.CredentialsUnreadable;
            link.FailingSince = DateTimeOffset.UtcNow.AddMinutes(-5);
        });

        var now = await GetAsync(server, "/api/glucose/current");

        now["link"]!["lastFailure"]!.GetValue<string>().ShouldBe("credentials-unreadable");
    }

    [Fact]
    public async Task ADay_IsAsRead_AndOnlyHers()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her,
            Reading(_morning.AddMinutes(-30), 99), Reading(_morning, 100), Reading(_morning.AddMinutes(5), 110),
            Reading(_morning.AddHours(25), 300));
        await SeedAsync(server, SugaryServer.Him, Reading(_morning.AddMinutes(10), 250));

        var curve = await GetAsync(server,
            $"/api/glucose/readings?from={Q(_morning.AddMinutes(-1))}&to={Q(_morning.AddHours(23))}");

        curve["bucketMinutes"].ShouldBeNull();
        curve["points"]!.AsArray().Select(p => Number(p, "mgPerDl")).ShouldBe([100, 110]);
        curve["points"]![0]!["at"]!.GetValue<DateTimeOffset>().ShouldBe(_morning);
    }

    [Fact]
    public async Task TwoWeeks_AreQuarterHourMeans()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her,
            Reading(_morning, 100), Reading(_morning.AddMinutes(5), 110), Reading(_morning.AddMinutes(10), 121),
            Reading(_morning.AddHours(3), 180));

        var curve = await GetAsync(server,
            $"/api/glucose/readings?from={Q(_morning.AddDays(-7))}&to={Q(_morning.AddDays(7))}");

        curve["bucketMinutes"]!.GetValue<int>().ShouldBe(15);
        var points = curve["points"]!.AsArray();
        points.Select(p => Number(p, "mgPerDl")).ShouldBe([110.3, 180]);
        points[0]!["at"]!.GetValue<DateTimeOffset>().ShouldBe(_morning.AddMinutes(7.5));
    }

    [Fact]
    public async Task NinetyDays_AreHourlyMeans()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, Reading(_morning, 100), Reading(_morning.AddMinutes(40), 140));

        var curve = await GetAsync(server,
            $"/api/glucose/readings?from={Q(_morning.AddDays(-89))}&to={Q(_morning.AddDays(1))}");

        curve["bucketMinutes"]!.GetValue<int>().ShouldBe(60);
        curve["points"]!.AsArray().Select(p => Number(p, "mgPerDl")).ShouldBe([120]);
    }

    [Theory]
    [InlineData(-91, 0, "window-too-long")]
    [InlineData(0, -1, "bad-window")]
    [InlineData(0, 0, "bad-window")]
    public async Task AWindowOutOfBounds_IsRefused(int fromDays, int toDays, string code)
    {
        await using var server = await SugaryServer.StartAsync();

        (await RefusalAsync(server,
                $"/api/glucose/readings?from={Q(_morning.AddDays(fromDays))}&to={Q(_morning.AddDays(toDays))}"))
            .ShouldBe((HttpStatusCode.BadRequest, code));
    }

    // With no window named, the curve is the last day's.
    [Fact]
    public async Task NoWindow_IsTheLastDay()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(25 * 60, 90), MinutesAgo(60, 130));

        var curve = await GetAsync(server, "/api/glucose/readings");

        curve["points"]!.AsArray().Select(p => Number(p, "mgPerDl")).ShouldBe([130]);
    }

    // 60, 100, 200 and 260 five minutes apart: a quarter each below 70 and over 250, half over 180.
    [Fact]
    public async Task ADaysFigures_AreHers_WeighedByTheMinute()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her,
            MinutesAgo(20, 60), MinutesAgo(15, 100), MinutesAgo(10, 200), MinutesAgo(5, 260), MinutesAgo(26 * 60, 40));
        await SeedAsync(server, SugaryServer.Him, MinutesAgo(30, 45));

        var figures = await GetAsync(server, "/api/glucose/statistics?days=1");

        figures["days"]!.GetValue<int>().ShouldBe(1);
        figures["count"]!.GetValue<int>().ShouldBe(4);
        Number(figures, "coverage").ShouldBe(20.0 / 1440, 0.001);
        Number(figures, "veryLow").ShouldBe(0, 0.01);
        Number(figures, "low").ShouldBe(0.25, 0.01);
        Number(figures, "inRange").ShouldBe(0.25, 0.01);
        Number(figures, "high").ShouldBe(0.5, 0.01);
        Number(figures, "veryHigh").ShouldBe(0.25, 0.01);
        Number(figures, "mean").ShouldBe(155, 0.5);
        Number(figures, "gmi").ShouldBe(7.02, 0.02);
        Number(figures, "cv").ShouldBe(Math.Sqrt(6275) / 155, 0.01);
    }

    [Fact]
    public async Task Figures_AreAgainstHerRange()
    {
        await using var server = await SugaryServer.StartAsync();
        await LinkAsync(server, SugaryServer.Her, link => (link.TargetLow, link.TargetHigh) = (80, 120));
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(10, 100), MinutesAgo(5, 150));

        var figures = await GetAsync(server, "/api/glucose/statistics?days=7");

        figures["targetLow"]!.GetValue<int>().ShouldBe(80);
        figures["targetHigh"]!.GetValue<int>().ShouldBe(120);
        Number(figures, "inRange").ShouldBe(0.5, 0.01);
    }

    // Her glucose in each part of her finished cycles, against her range: the luteal days (the fourteen
    // before a start) read apart from her period and the days after it. Hers alone.
    [Fact]
    public async Task HerCycle_IsFiguredByPhase_FromHerOwnPeriodsAndReadings()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            new Period { Id = Guid.NewGuid(), Start = new DateOnly(2026, 9, 1), End = new DateOnly(2026, 9, 5) },
            new Period { Id = Guid.NewGuid(), Start = new DateOnly(2026, 9, 29), End = null });
        DateTimeOffset day(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);
        GlucoseReading[] every15(int from, int to, double value) =>
        [
            .. Enumerable.Range(0, (to - from + 1) * 96).Select(n => Reading(day(from).AddMinutes(15 * n), value))
        ];
        await SeedAsync(server, SugaryServer.Her, [.. every15(6, 13, 110), .. every15(16, 27, 200)]);
        await SeedAsync(server, SugaryServer.Him, every15(16, 27, 60));

        var phases = await GetAsync(server, "/api/glucose/cycle");

        phases["cycles"]!.GetValue<int>().ShouldBe(1);
        phases["targetLow"]!.GetValue<int>().ShouldBe(70);
        phases["unit"]!.GetValue<string>().ShouldBe("mgdl");
        phases["hasReadings"]!.GetValue<bool>().ShouldBeTrue();
        Number(phases["follicular"]!, "mean").ShouldBe(110, 10);
        Number(phases["luteal"]!, "mean").ShouldBe(200, 10);
        Number(phases["luteal"]!, "inRange").ShouldBe(0, 0.15);
        phases["luteal"]!["days"]!.GetValue<int>().ShouldBe(14);
        phases["menstrual"]!["mean"].ShouldBeNull();
    }

    // A new link has nothing yet: the figures say so rather than fail on an average of nothing.
    [Fact]
    public async Task NoReadings_HaveNoFigures()
    {
        await using var server = await SugaryServer.StartAsync();

        var figures = await GetAsync(server, "/api/glucose/statistics?days=90");

        figures["count"]!.GetValue<int>().ShouldBe(0);
        Number(figures, "coverage").ShouldBe(0);
        figures["mean"].ShouldBeNull();
        figures["gmi"].ShouldBeNull();
        figures["inRange"].ShouldBeNull();
    }

    private static GlucoseReading[] Minutes(DateTimeOffset start, params double[] values) =>
        [.. values.Select((value, minute) => Reading(start.AddMinutes(minute), value))];

    private static double[] CurveValues(JsonNode curve) =>
        [.. curve["points"]!.AsArray().Select(p => Number(p, "mgPerDl"))];

    [Fact]
    public async Task TheCurve_IsSmoothed_UnlessAskedForTheRawReadings()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, Minutes(_morning, 85, 85, 90, 60, 80, 85, 85));
        var window = $"from={Q(_morning)}&to={Q(_morning.AddHours(1))}";

        var unnamed = await GetAsync(server, $"/api/glucose/readings?{window}");
        var smoothed = await GetAsync(server, $"/api/glucose/readings?{window}&view=smoothed");
        var raw = await GetAsync(server, $"/api/glucose/readings?{window}&view=raw");

        CurveValues(unnamed).ShouldNotContain(60);
        CurveValues(smoothed).ShouldBe(CurveValues(unnamed));
        CurveValues(raw).ShouldBe([85, 85, 90, 60, 80, 85, 85]);
    }

    [Fact]
    public async Task AReadingAtTheWindowsEdge_IsSmoothedWithTheReadingsBeyondIt()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, Minutes(_morning, 85, 85, 90, 60, 80, 85, 85));

        var curve = await GetAsync(server,
            $"/api/glucose/readings?from={Q(_morning.AddMinutes(3))}&to={Q(_morning.AddMinutes(4))}");

        CurveValues(curve).ShouldBe([85]);
    }

    [Fact]
    public async Task AnInstant_ReadsTheSame_InAnyWindow()
    {
        await using var server = await SugaryServer.StartAsync();
        var values = Enumerable.Range(0, 150)
            .Select(minute => Math.Round(120 + 30 * Math.Sin(minute / 40.0) + 4 * Math.Sin(minute * 2 * Math.PI / 8)))
            .ToArray();
        await SeedAsync(server, SugaryServer.Her, Minutes(_morning, values));
        var instant = _morning.AddMinutes(75);

        var whole = await GetAsync(server,
            $"/api/glucose/readings?from={Q(_morning)}&to={Q(_morning.AddMinutes(150))}");
        var alone = await GetAsync(server,
            $"/api/glucose/readings?from={Q(instant)}&to={Q(instant.AddMinutes(1))}");

        CurveValues(alone).ShouldBe([CurveValues(whole)[75]]);
    }

    [Fact]
    public async Task ALongWindow_AveragesTheSmoothedReadings()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her, Minutes(_morning, 100, 100, 100, 40, 100, 100, 100));
        var window = $"from={Q(_morning.AddDays(-7))}&to={Q(_morning.AddDays(7))}";

        CurveValues(await GetAsync(server, $"/api/glucose/readings?{window}")).ShouldBe([100]);
        CurveValues(await GetAsync(server, $"/api/glucose/readings?{window}&view=raw")).ShouldBe([91.4]);
    }

    [Fact]
    public async Task TheFigures_AreSmoothed_UnlessAskedForTheRawReadings()
    {
        await using var server = await SugaryServer.StartAsync();
        await SeedAsync(server, SugaryServer.Her,
            MinutesAgo(8, 85), MinutesAgo(7, 85), MinutesAgo(6, 90), MinutesAgo(5, 60), MinutesAgo(4, 80),
            MinutesAgo(3, 85), MinutesAgo(2, 85));

        var smoothed = await GetAsync(server, "/api/glucose/statistics?days=1");
        var raw = await GetAsync(server, "/api/glucose/statistics?days=1&view=raw");

        Number(smoothed, "low").ShouldBe(0);
        Number(raw, "low").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task HerCyclePhases_DoNotCountAMisread()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            new Period { Id = Guid.NewGuid(), Start = new DateOnly(2026, 9, 1), End = new DateOnly(2026, 9, 5) },
            new Period { Id = Guid.NewGuid(), Start = new DateOnly(2026, 9, 29), End = null });
        var start = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var misreadAt = start.AddDays(5);
        var sparse = Enumerable.Range(0, 12 * 96)
            .Select(n => start.AddMinutes(15 * n))
            .Where(at => at != misreadAt)
            .Select(at => Reading(at, 100));
        await SeedAsync(server, SugaryServer.Her,
        [
            .. sparse,
            .. Minutes(misreadAt.AddMinutes(-2), 100, 100, 40, 100, 100)
        ]);

        var phases = await GetAsync(server, "/api/glucose/cycle");

        Number(phases["luteal"]!, "low").ShouldBe(0);
    }

    [Theory]
    [InlineData("readings")]
    [InlineData("statistics?days=1")]
    public async Task AViewNotOffered_IsRefused(string route)
    {
        await using var server = await SugaryServer.StartAsync();
        var separator = route.Contains('?') ? '&' : '?';

        (await RefusalAsync(server, $"/api/glucose/{route}{separator}view=wobbly"))
            .ShouldBe((HttpStatusCode.BadRequest, "bad-view"));
    }

    [Theory]
    [InlineData("days=3")]
    [InlineData("days=0")]
    [InlineData("days=-7")]
    public async Task APeriodNotOffered_IsRefused(string query)
    {
        await using var server = await SugaryServer.StartAsync();

        (await RefusalAsync(server, $"/api/glucose/statistics?{query}"))
            .ShouldBe((HttpStatusCode.BadRequest, "bad-period"));
    }
}