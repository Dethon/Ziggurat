using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Security.Sealing;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Sugary reads every live link on its own, once a period: the connection's current reading and the
// curve LibreLinkUp keeps, kept as her readings without counting a moment twice; what LibreLinkUp
// answered is the link's state, and a refused password stops that link alone. The poller and the
// client run on the test's clock, against LibreLinkUp's wire faked; the server's loop is left out, so
// each sweep is the test's.
public class SugaryGlucosePollerTests
{
    private static readonly string _key = Convert.ToBase64String(SealingKey.Generate().Export());
    private static readonly DateTimeOffset _start = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    private sealed class RecordingObserver : IGlucosePollObserver
    {
        private readonly Lock _gate = new();
        private readonly List<GlucosePoll> _polls = [];

        public Account? ThrowsFor { get; set; }

        public IReadOnlyList<GlucosePoll> Polls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _polls];
                }
            }
        }

        public Task PolledAsync(GlucosePoll poll, CancellationToken ct)
        {
            lock (_gate)
            {
                _polls.Add(poll);
            }

            return poll.Person == ThrowsFor
                ? throw new InvalidOperationException("a watch that fails")
                : Task.CompletedTask;
        }
    }

    private sealed class Harness(FakeLibreLinkUp fake, SugaryServer server, FakeTimeProvider clock, RecordingObserver observer)
        : IAsyncDisposable
    {
        public FakeLibreLinkUp Fake => fake;
        public SugaryServer Server => server;
        public FakeTimeProvider Clock => clock;
        public RecordingObserver Observer => observer;

        public static TimeSpan Period => TimeSpan.FromSeconds(60);

        private GlucosePoller Poller => server.Services.GetRequiredService<GlucosePoller>();

        public Task SweepAsync() => Poller.PollDueAsync(CancellationToken.None);

        // One period on, and whatever is due by then polled.
        public async Task NextPeriodAsync(int periods = 1)
        {
            foreach (var _ in Enumerable.Range(0, periods))
            {
                clock.Advance(Period);
                await SweepAsync();
            }
        }

        // Her link as the app would have kept it, then seen by the poller once: a link is first polled
        // at its own moment of the period, so the next period polls it.
        public async Task LinkAsync(Account person, string email, string password, string patientId, string? key = null)
        {
            var credentials = key is null
                ? server.Services.GetRequiredService<GlucoseCredentials>()
                : new GlucoseCredentials(SealingKey.Parse(key, "key"));
            await using (var db = Factory())
            {
                db.GlucoseLinks.Add(new GlucoseLink
                {
                    UserId = person.UserId,
                    TenantId = person.TenantId,
                    Email = email,
                    SealedPassword = credentials.Seal(person, password),
                    Region = "eu",
                    PatientId = patientId,
                    Connections = [new GlucoseConnectionOffered { PatientId = patientId, Name = "Ana García" }],
                    LinkedAt = clock.GetUtcNow()
                });
                await db.SaveChangesAsync();
            }

            await SweepAsync();
        }

        public async Task<IReadOnlyList<GlucoseReading>> ReadingsAsync(Account person)
        {
            await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
            return await db.GlucoseReadings.AsNoTracking().OrderBy(r => r.At).ToListAsync();
        }

        public async Task<IReadOnlyList<GlucoseSensor>> SensorsAsync(Account person)
        {
            await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
            return await db.GlucoseSensors.AsNoTracking().OrderBy(s => s.Serial).ToListAsync();
        }

        public async Task<GlucoseLink> LinkOfAsync(Account person)
        {
            await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
            return await db.GlucoseLinks.AsNoTracking().SingleAsync();
        }

        public SugaryDbContext Factory() =>
            server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>().CreateDbContext();

        public int GraphCalls(string patientId) =>
            fake.Requests.Count(r => r.Path.EndsWith($"/connections/{patientId}/graph", StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync();
            await fake.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(bool withLoop = false)
    {
        var clock = new FakeTimeProvider(_start);
        var fake = await FakeLibreLinkUp.StartAsync(clock);
        var observer = new RecordingObserver();
        var server = await SugaryServer.StartAsync(
            configure: s => s with { SealingKey = _key },
            services: c =>
            {
                c.Replace(ServiceDescriptor.Singleton(new LibreLinkUpClient(
                    LibreLinkUpClient.CreateHttp(), fake.Regions, clock, NullLogger<LibreLinkUpClient>.Instance)));
                c.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<GlucosePoller>(sp, (TimeProvider)clock)));
                c.Replace(ServiceDescriptor.Singleton<IGlucosePollObserver>(observer));
                c.Remove(c.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GlucosePollLoop)));
                if (withLoop)
                {
                    c.AddHostedService(sp => ActivatorUtilities.CreateInstance<GlucosePollLoop>(sp, (TimeProvider)clock));
                }
            });
        return new Harness(fake, server, clock, observer);
    }

    private static double ValueAt(DateTimeOffset at) => Math.Round(120 + 50 * Math.Sin(at.ToUnixTimeSeconds() / 3600.0));

    private static DateTimeOffset ToTheSecond(DateTimeOffset at) => new(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    // A sensor worn up to now: its reading of a moment ago with an arrow, and the curve of the last
    // twelve hours on the five-minute marks before it, as LibreLinkUp keeps it.
    private static void Wear(FakeLibreLinkUp.Connection connection, DateTimeOffset now)
    {
        var latest = ToTheSecond(now - TimeSpan.FromSeconds(20));
        connection.Latest = (new FakeLibreLinkUp.Point(latest, ValueAt(latest)), 3);
        var lastMark = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero) - TimeSpan.FromMinutes(5);
        connection.Graph = [.. Enumerable.Range(0, 144).Reverse()
            .Select(i => lastMark - TimeSpan.FromMinutes(5 * i))
            .Select(at => new FakeLibreLinkUp.Point(at, ValueAt(at)))];
    }

    [Fact]
    public async Task APoll_KeepsTheCurrentReadingWithItsArrow_AndTheCurveWithout()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        var now = _start + TimeSpan.FromMinutes(10);
        connection.Latest = (new FakeLibreLinkUp.Point(now + TimeSpan.FromSeconds(30), 140), 4);
        connection.Graph =
        [
            new(now - TimeSpan.FromMinutes(10), 120),
            new(now - TimeSpan.FromMinutes(5), 130),
            // The curve's point of the current reading's minute: the current reading, with its arrow, wins.
            new(now, 138)
        ];
        connection.Current = new FakeLibreLinkUp.Sensor("SN-1", _start - TimeSpan.FromDays(3), ProductType: 4);
        connection.Sensors = [connection.Current];
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");

        await harness.NextPeriodAsync();

        var readings = await harness.ReadingsAsync(SugaryServer.Her);
        readings.Select(r => (r.At, r.MgPerDl, r.Trend, r.Source)).ShouldBe(
        [
            (now - TimeSpan.FromMinutes(10), 120, GlucoseTrend.Unknown, GlucoseSource.Graph),
            (now - TimeSpan.FromMinutes(5), 130, GlucoseTrend.Unknown, GlucoseSource.Graph),
            (now + TimeSpan.FromSeconds(30), 140, GlucoseTrend.Rising, GlucoseSource.Current)
        ]);
        var sensor = (await harness.SensorsAsync(SugaryServer.Her)).ShouldHaveSingleItem();
        (sensor.Serial, sensor.ActivatedAt, sensor.ProductType).ShouldBe(("SN-1", _start - TimeSpan.FromDays(3), 4));
        sensor.LastSeenAt.ShouldBe(harness.Clock.GetUtcNow());
        var link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastSuccessAt.ShouldBe(harness.Clock.GetUtcNow());
        link.LastFailure.ShouldBeNull();
        link.FailingSince.ShouldBeNull();
    }

    // Each poll sees the last twelve hours again; over a day the log keeps every moment once.
    [Fact]
    public async Task ReadingsAccumulatePastTwelveHours_WithoutAMomentKeptTwice()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");

        foreach (var _ in Enumerable.Range(0, 13 * 60))
        {
            Wear(connection, harness.Clock.GetUtcNow() + Harness.Period);
            await harness.NextPeriodAsync();
        }

        var readings = await harness.ReadingsAsync(SugaryServer.Her);
        var gaps = readings.Zip(readings.Skip(1), (a, b) => b.At - a.At).ToList();
        gaps.Min().ShouldBeGreaterThanOrEqualTo(GlucoseReadingRules.Spacing, "no moment is kept twice");
        var end = harness.Clock.GetUtcNow();
        var curve = Enumerable.Range(0, (int)((end - _start + TimeSpan.FromHours(12)) / TimeSpan.FromMinutes(5)) - 2)
            .Select(i => _start - TimeSpan.FromHours(12) + TimeSpan.FromMinutes(5 * i))
            .Where(at => at >= readings[0].At);
        curve.Where(at => readings.All(r => (r.At - at).Duration() >= GlucoseReadingRules.Spacing))
            .ShouldBeEmpty("every moment the curve showed is kept, or one beside it is");
        readings[0].At.ShouldBeLessThan(_start - TimeSpan.FromHours(11.5), "the first poll kept the curve it was shown");
        (readings[^1].At - readings[0].At).ShouldBeGreaterThan(TimeSpan.FromHours(24));
        readings.ShouldContain(r => r.Source == GlucoseSource.Current);
        readings.ShouldContain(r => r.Source == GlucoseSource.Graph);
        readings.ShouldAllBe(r => r.MgPerDl == ValueAt(r.At));
        harness.Fake.Logins.Count.ShouldBe(1, "the token is kept, never asked for per poll");
        harness.Fake.Unhandled.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALink_IsPolledOncePerPeriod()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        harness.GraphCalls("patient-1").ShouldBe(0, "a link is first polled at its own moment of the period");

        await harness.NextPeriodAsync();
        await harness.SweepAsync();
        await harness.SweepAsync();

        harness.GraphCalls("patient-1").ShouldBe(1);

        await harness.NextPeriodAsync(3);

        harness.GraphCalls("patient-1").ShouldBe(4);
    }

    // Typing the password again is the only thing that fixes a refused one, so the link stops being
    // polled — asking every minute is what gets an account locked — until she links it again.
    [Fact]
    public async Task ARefusedPassword_MarksTheLinkFailing_AndStopsItUntilSheLinksAgain()
    {
        await using var harness = await StartAsync();
        var account = harness.Fake.AddAccount("ana@example.com", "new password");
        Wear(account.Follow("patient-1", "Ana", "García"), harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "old password", "patient-1");

        await harness.NextPeriodAsync();
        var refusedAt = harness.Clock.GetUtcNow();
        await harness.NextPeriodAsync(10);

        harness.Fake.Logins.Count.ShouldBe(1);
        var link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastFailure.ShouldBe(GlucoseLinkFailure.CredentialsRefused);
        link.FailingSince.ShouldBe(refusedAt);
        (await harness.ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();

        var relinked = await harness.Server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link",
            new JsonObject { ["email"] = "ana@example.com", ["password"] = "new password" });
        relinked.StatusCode.ShouldBe(HttpStatusCode.OK);
        await harness.SweepAsync();
        await harness.NextPeriodAsync();

        harness.GraphCalls("patient-1").ShouldBe(1);
        (await harness.ReadingsAsync(SugaryServer.Her)).ShouldNotBeEmpty();
        (await harness.LinkOfAsync(SugaryServer.Her)).LastFailure.ShouldBeNull();
    }

    [Fact]
    public async Task ARateLimit_WaitsItsRetryAfter()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        await harness.NextPeriodAsync();
        var answeredAt = harness.Clock.GetUtcNow();

        harness.Fake.InterruptNext((HttpStatusCode)429, null, ("Retry-After", "600"));
        await harness.NextPeriodAsync();
        var limitedAt = harness.Clock.GetUtcNow();
        var link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastFailure.ShouldBe(GlucoseLinkFailure.RateLimited);
        link.FailingSince.ShouldBe(limitedAt);
        link.LastSuccessAt.ShouldBe(answeredAt);

        await harness.NextPeriodAsync(9);
        harness.GraphCalls("patient-1").ShouldBe(2, "nothing is asked for the ten minutes it said");

        await harness.NextPeriodAsync();
        harness.GraphCalls("patient-1").ShouldBe(3);
        (await harness.LinkOfAsync(SugaryServer.Her)).LastFailure.ShouldBeNull();
    }

    [Fact]
    public async Task ARateLimitThatSaysNoWait_WaitsFiveMinutes()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        await harness.NextPeriodAsync();

        harness.Fake.InterruptNext((HttpStatusCode)429);
        await harness.NextPeriodAsync();
        await harness.NextPeriodAsync(4);
        harness.GraphCalls("patient-1").ShouldBe(2);

        await harness.NextPeriodAsync();
        harness.GraphCalls("patient-1").ShouldBe(3);
    }

    // LibreLinkUp down is recorded from its first failure, which a watch on a failing link reads; it is
    // asked less and less often while it stays down, and the first answer clears it.
    [Fact]
    public async Task AnOutage_IsRecordedFromItsStart_AndAskedLessOften()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        await harness.NextPeriodAsync();
        var answeredAt = harness.Clock.GetUtcNow();

        connection.Down = true;
        await harness.NextPeriodAsync();
        var downAt = harness.Clock.GetUtcNow();
        await harness.NextPeriodAsync(60);

        var link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastFailure.ShouldBe(GlucoseLinkFailure.Unreachable);
        link.FailingSince.ShouldBe(downAt);
        link.LastSuccessAt.ShouldBe(answeredAt);
        link.FailureDetail.ShouldBeNull("the reason is the log's, never the app's");
        harness.GraphCalls("patient-1").ShouldBeInRange(6, 12, "an hour down is asked about every fifteen minutes at most, not every minute");

        connection.Down = false;
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.NextPeriodAsync(15);

        link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastFailure.ShouldBeNull();
        link.FailingSince.ShouldBeNull();
        link.LastSuccessAt.ShouldNotBeNull().ShouldBeGreaterThan(downAt);
    }

    [Fact]
    public async Task OnePersonsFailure_NeverStopsAnothersPolling()
    {
        await using var harness = await StartAsync();
        harness.Fake.AddAccount("her@example.com", "changed").Follow("patient-her", "Ana", "García");
        var his = harness.Fake.AddAccount("him@example.com", "pw").Follow("patient-him", "Leo", "García");
        his.Down = true;
        var neighbours = harness.Fake.AddAccount("next@example.com", "pw").Follow("patient-next", "Eva", "Ruiz");
        Wear(neighbours, harness.Clock.GetUtcNow());
        harness.Observer.ThrowsFor = SugaryServer.Him;
        await harness.LinkAsync(SugaryServer.Her, "her@example.com", "wrong", "patient-her");
        await harness.LinkAsync(SugaryServer.Him, "him@example.com", "pw", "patient-him");
        await harness.LinkAsync(SugaryServer.Neighbour, "next@example.com", "pw", "patient-next");

        foreach (var _ in Enumerable.Range(0, 5))
        {
            Wear(neighbours, harness.Clock.GetUtcNow() + Harness.Period);
            await harness.NextPeriodAsync();
        }

        harness.GraphCalls("patient-next").ShouldBe(5);
        (await harness.ReadingsAsync(SugaryServer.Neighbour)).Count.ShouldBeGreaterThan(144);
        (await harness.LinkOfAsync(SugaryServer.Neighbour)).LastSuccessAt.ShouldBe(harness.Clock.GetUtcNow());
        (await harness.LinkOfAsync(SugaryServer.Her)).LastFailure.ShouldBe(GlucoseLinkFailure.CredentialsRefused);
        (await harness.LinkOfAsync(SugaryServer.Him)).LastFailure.ShouldBe(GlucoseLinkFailure.Unreachable);
        harness.Observer.Polls.Count(p => p.Person == SugaryServer.Neighbour).ShouldBe(5);
    }

    [Fact]
    public async Task Readings_AreTheirLinksOwnersAndNoOneElses()
    {
        await using var harness = await StartAsync();
        var hers = harness.Fake.AddAccount("her@example.com", "pw").Follow("patient-her", "Ana", "García");
        hers.Latest = (new FakeLibreLinkUp.Point(_start, 101), 3);
        hers.Current = new FakeLibreLinkUp.Sensor("SN-HER", _start - TimeSpan.FromDays(1));
        hers.Sensors = [hers.Current];
        var neighbours = harness.Fake.AddAccount("next@example.com", "pw").Follow("patient-next", "Eva", "Ruiz");
        neighbours.Latest = (new FakeLibreLinkUp.Point(_start, 202), 3);
        await harness.LinkAsync(SugaryServer.Her, "her@example.com", "pw", "patient-her");
        await harness.LinkAsync(SugaryServer.Neighbour, "next@example.com", "pw", "patient-next");

        await harness.NextPeriodAsync();

        (await harness.ReadingsAsync(SugaryServer.Her)).Select(r => r.MgPerDl).ShouldBe([101]);
        (await harness.ReadingsAsync(SugaryServer.Neighbour)).Select(r => r.MgPerDl).ShouldBe([202]);
        (await harness.ReadingsAsync(SugaryServer.Him)).ShouldBeEmpty("he shares her household, not her log");
        (await harness.SensorsAsync(SugaryServer.Her)).Select(s => s.Serial).ShouldBe(["SN-HER"]);
        (await harness.SensorsAsync(SugaryServer.Neighbour)).ShouldBeEmpty();
        await using var db = harness.Factory();
        (await db.GlucoseReadings.IgnoreQueryFilters().ToListAsync())
            .Select(r => (r.TenantId, r.UserId, r.MgPerDl))
            .ShouldBe([(SugaryServer.Her.TenantId, SugaryServer.Her.UserId, 101d),
                (SugaryServer.Neighbour.TenantId, SugaryServer.Neighbour.UserId, 202d)], ignoreOrder: true);
    }

    // A poll that was out while she linked again — another password, another person followed — writes
    // nothing: its answer was for a link that is no longer hers, and a refusal it carried would stop a
    // good one.
    [Theory]
    [InlineData("relinked")]
    [InlineData("chose another")]
    public async Task APollThatOutlivesItsLink_WritesNothing(string change)
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        connection.BeforeGraph = async () =>
        {
            await using var db = harness.Factory();
            var hers = db.GlucoseLinks.IgnoreQueryFilters().Where(l => l.UserId == SugaryServer.Her.UserId);
            if (change == "relinked")
            {
                await hers.ExecuteUpdateAsync(set => set.SetProperty(l => l.LinkedAt, _start + TimeSpan.FromSeconds(1)));
            }
            else
            {
                await hers.ExecuteUpdateAsync(set => set.SetProperty(l => l.PatientId, "patient-2"));
            }
        };

        await harness.NextPeriodAsync();

        harness.GraphCalls("patient-1").ShouldBe(1);
        (await harness.ReadingsAsync(SugaryServer.Her)).ShouldBeEmpty();
        (await harness.LinkOfAsync(SugaryServer.Her)).LastSuccessAt.ShouldBeNull();
        harness.Observer.Polls.ShouldBeEmpty();
    }

    // What a watch is told after each of her polls: what was kept, the newest reading LibreLinkUp gave
    // (kept or not), and the link as it now stands — after a failure too.
    [Fact]
    public async Task AfterEachPoll_TheWatchesAreToldWhatItGot()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");

        await harness.NextPeriodAsync();
        var first = harness.Observer.Polls.ShouldHaveSingleItem();
        first.Person.ShouldBe(SugaryServer.Her);
        first.Stored.Count.ShouldBe((await harness.ReadingsAsync(SugaryServer.Her)).Count);
        first.Newest.ShouldBe(new SensorReading(connection.Latest!.Value.Point.At, connection.Latest.Value.Point.MgPerDl, GlucoseTrend.Steady));
        first.Link.LastSuccessAt.ShouldBe(harness.Clock.GetUtcNow());
        first.Link.LastFailure.ShouldBeNull();

        // The same reading again, a minute on: nothing new is kept, and the newest is still said.
        await harness.NextPeriodAsync();
        var second = harness.Observer.Polls[1];
        second.Stored.ShouldBeEmpty();
        second.Newest.ShouldBe(first.Newest);

        connection.Down = true;
        await harness.NextPeriodAsync();
        var failed = harness.Observer.Polls[2];
        failed.Stored.ShouldBeEmpty();
        failed.Newest.ShouldBeNull();
        failed.Link.LastFailure.ShouldBe(GlucoseLinkFailure.Unreachable);
        failed.Link.FailingSince.ShouldBe(harness.Clock.GetUtcNow());
    }

    // A password sealed under another key (the key was changed) cannot be opened: the link is not
    // polled, and nothing is written about it, since LibreLinkUp was never asked.
    [Fact]
    public async Task APasswordThatDoesNotOpen_IsNotPolled()
    {
        await using var harness = await StartAsync();
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1",
            key: Convert.ToBase64String(SealingKey.Generate().Export()));

        await harness.NextPeriodAsync(3);

        harness.Fake.Requests.ShouldBeEmpty();
        var link = await harness.LinkOfAsync(SugaryServer.Her);
        link.LastSuccessAt.ShouldBeNull();
        link.LastFailure.ShouldBeNull();
    }

    // A link with nobody chosen yet has nobody to read.
    [Fact]
    public async Task ALinkWithNobodyChosen_IsNotPolled()
    {
        await using var harness = await StartAsync();
        harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");
        await using (var db = harness.Factory())
        {
            await db.GlucoseLinks.IgnoreQueryFilters().ExecuteUpdateAsync(set => set.SetProperty(l => l.PatientId, (string?)null));
        }

        await harness.NextPeriodAsync(3);

        harness.Fake.Requests.ShouldBeEmpty();
    }

    // The server's own loop polls with nobody sweeping for it.
    [Fact]
    public async Task TheLoop_PollsOnItsOwn()
    {
        await using var harness = await StartAsync(withLoop: true);
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        Wear(connection, harness.Clock.GetUtcNow());
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");

        await Eventually.Until(async () =>
        {
            harness.Clock.Advance(GlucosePollLoop.Tick);
            return (await harness.ReadingsAsync(SugaryServer.Her)).Count > 0;
        }, "the loop polls her link");
    }
}