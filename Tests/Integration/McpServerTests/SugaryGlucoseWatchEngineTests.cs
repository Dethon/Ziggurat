using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Security.Sealing;
using Domain.Tools.Sugary.Watches;
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

// Her glucose watches judged by the server: after each poll of her link on what it kept and the link's
// state, and on the clock for the sensor's triggers, what was judged kept in her store so a restart
// neither fires again nor forgets, and every fire handed to the watch's effects. The engine, the poller
// and LibreLinkUp's client run on the test's clock; the server's loops are left out, so each poll and
// each pass is the test's.
public class SugaryGlucoseWatchEngineTests
{
    private static readonly string _key = Convert.ToBase64String(SealingKey.Generate().Export());
    private static readonly DateTimeOffset _start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingEffects : IGlucoseWatchEffects
    {
        private readonly Lock _gate = new();
        private readonly List<(FiredGlucoseWatch Watch, GlucoseTriggerFire Report)> _fires = [];

        public int Delivered { get; set; } = 1;
        public string? ThrowsFor { get; set; }
        public Func<FiredGlucoseWatch, Task>? During { get; set; }

        public IReadOnlyList<(FiredGlucoseWatch Watch, GlucoseTriggerFire Report)> Fires
        {
            get
            {
                lock (_gate)
                {
                    return [.. _fires];
                }
            }
        }

        public IReadOnlyList<string> FiredIds => [.. Fires.Select(f => f.Watch.Watch.Id)];

        public async Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct)
        {
            lock (_gate)
            {
                _fires.Add((watch, report));
            }

            if (During is { } during)
            {
                await during(watch);
            }

            return watch.Watch.Id == ThrowsFor
                ? throw new InvalidOperationException("an effect that fails")
                : new GlucoseEffectsOutcome(Delivered);
        }
    }

    private sealed class Harness(FakeLibreLinkUp fake, SugaryServer server, FakeTimeProvider clock, RecordingEffects effects)
        : IAsyncDisposable
    {
        public FakeLibreLinkUp Fake => fake;
        public SugaryServer Server => server;
        public FakeTimeProvider Clock => clock;
        public RecordingEffects Effects => effects;
        public DateTimeOffset Now => clock.GetUtcNow();

        public GlucoseWatchEngine Engine { get; private set; } = server.Services.GetRequiredService<GlucoseWatchEngine>();

        // A process started again: nothing of the last one's but what it kept in the store.
        public void Restart() =>
            Engine = ActivatorUtilities.CreateInstance<GlucoseWatchEngine>(server.Services, (TimeProvider)clock);

        public IGlucoseWatches Watches => server.Services.GetRequiredService<IGlucoseWatches>();

        public async Task WatchAsync(
            string id, string triggers, string extra = "", Account? subject = null, bool enabled = true, bool spent = false)
        {
            var spec = GlucoseWatchSpec.Parse(
                $$"""{"name":"{{id}}","triggers":[{{triggers}}],"effects":[{"kind":"prompt","prompt":"Tell her"}]{{extra}}}""");
            var person = subject ?? SugaryServer.Her;
            (await Watches.AddAsync(person, new StoredGlucoseWatch
            {
                Id = id,
                SubjectUserId = person.UserId,
                WriterUserId = person.UserId,
                AgentId = "jonas",
                Spec = spec.ToJson(),
                Enabled = enabled,
                Once = spec.Once,
                CreatedAt = Now,
                Spent = spent
            }, CancellationToken.None)).ShouldBeTrue();
        }

        public async Task KeepAsync(Account person, DateTimeOffset at, double mgPerDl, GlucoseSource source = GlucoseSource.Current)
        {
            await using var db = Factory();
            db.GlucoseReadings.Add(new GlucoseReading
            {
                UserId = person.UserId, TenantId = person.TenantId, At = at, MgPerDl = mgPerDl, Source = source
            });
            await db.SaveChangesAsync();
        }

        // A poll that answered with a reading measured now, kept as the poller keeps one.
        public async Task PollAsync(double mgPerDl, Account? person = null, GlucoseWatchEngine? engine = null)
        {
            var who = person ?? SugaryServer.Her;
            var reading = new SensorReading(Now, mgPerDl, GlucoseTrend.Steady);
            await KeepAsync(who, Now, mgPerDl);
            await (engine ?? Engine).PolledAsync(
                new GlucosePoll(who, [reading], new GlucoseLinkState(_start - TimeSpan.FromDays(1), Now, null, null, null), reading),
                CancellationToken.None);
        }

        public async Task<IReadOnlyList<GlucoseTriggerCondition>> StatesAsync(string id) =>
            await Watches.ConditionsAsync(SugaryServer.Her, id, CancellationToken.None);

        public async Task<GlucoseWatch?> RowAsync(string id)
        {
            await using var db = Factory();
            return await db.GlucoseWatches.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(w => w.TenantId == SugaryServer.Her.TenantId && w.Id == id);
        }

        public SugaryDbContext Factory() =>
            server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>().CreateDbContext();

        public async Task LinkAsync(Account person, string email, string password, string patientId)
        {
            await using (var db = Factory())
            {
                db.GlucoseLinks.Add(new GlucoseLink
                {
                    UserId = person.UserId,
                    TenantId = person.TenantId,
                    Email = email,
                    SealedPassword = server.Services.GetRequiredService<GlucoseCredentials>().Seal(person, password),
                    Region = "eu",
                    PatientId = patientId,
                    Connections = [new GlucoseConnectionOffered { PatientId = patientId, Name = "Ana García" }],
                    LinkedAt = Now
                });
                await db.SaveChangesAsync();
            }

            await SweepAsync();
        }

        public Task SweepAsync() => server.Services.GetRequiredService<GlucosePoller>().PollDueAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync();
            await fake.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync()
    {
        var clock = new FakeTimeProvider(_start);
        var fake = await FakeLibreLinkUp.StartAsync(clock);
        var effects = new RecordingEffects();
        var server = await SugaryServer.StartAsync(
            configure: s => s with { SealingKey = _key },
            services: c =>
            {
                c.Replace(ServiceDescriptor.Singleton(new LibreLinkUpClient(
                    LibreLinkUpClient.CreateHttp(), fake.Regions, clock, NullLogger<LibreLinkUpClient>.Instance)));
                c.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<GlucosePoller>(sp, (TimeProvider)clock)));
                c.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<GlucoseWatchEngine>(sp, (TimeProvider)clock)));
                c.Replace(ServiceDescriptor.Singleton<IGlucoseWatchEffects>(effects));
                foreach (var loop in c.Where(d => d.ServiceType == typeof(IHostedService)
                                                  && (d.ImplementationType == typeof(GlucosePollLoop) || d.ImplementationType == typeof(GlucoseWatchLoop))).ToList())
                {
                    c.Remove(loop);
                }
            });
        return new Harness(fake, server, clock, effects);
    }

    [Fact]
    public void TheServer_JudgesHerWatchesAfterEveryPoll_AndOnTheClock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        McpServerSugary.Modules.ConfigModule.ConfigureSugary(services, new McpServerSugary.Settings.SugarySettings
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "never-opened.db"),
            SealingKey = _key,
            Mcp = McpTestSecret.Gate
        });

        services.Single(d => d.ServiceType == typeof(IGlucosePollObserver)).ImplementationFactory.ShouldNotBeNull();
        services.ShouldContain(d => d.ServiceType == typeof(GlucoseWatchEngine));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GlucoseWatchLoop));
    }

    [Fact]
    public async Task AFallBelow_FiresItsEffects_OncePerEpisode()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70}""");

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(65);
        var firedAt = harness.Now;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);

        var (watch, report) = harness.Effects.Fires.ShouldHaveSingleItem();
        watch.Subject.ShouldBe(SugaryServer.Her);
        watch.Watch.Id.ShouldBe("low");
        watch.Spec.Triggers.ShouldHaveSingleItem().ShouldBe(new GlucoseLevelTrigger(GlucoseBound.Below, 70));
        report.Newest.ShouldNotBeNull().MgPerDl.ShouldBe(65);
        report.At.ShouldBe(firedAt);
        (await harness.RowAsync("low"))!.LastFiredAt.ShouldBe(firedAt);
        (await harness.StatesAsync("low")).ShouldBe([new GlucoseTriggerCondition(0, true, firedAt, true)]);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(80);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(66);
        harness.Effects.Fires.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AWatchWrittenWhileItHolds_FiresOnlyAtTheNextCrossing()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70}""");

        await harness.PollAsync(60);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(62);
        harness.Effects.Fires.ShouldBeEmpty();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(80);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(65);
        harness.Effects.Fires.ShouldHaveSingleItem();
    }

    // Kept in the store, not the process: a pending `for` survives a restart and fires once.
    [Fact]
    public async Task ARestart_NeitherFiresAgainNorForgetsAPendingFor()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70,"for":{"minutes":10}}""");
        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(65);

        harness.Restart();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.PollAsync(64);
        harness.Effects.Fires.ShouldBeEmpty();

        harness.Restart();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.PollAsync(63);
        harness.Effects.Fires.ShouldHaveSingleItem();

        harness.Restart();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(62);
        harness.Effects.Fires.ShouldHaveSingleItem("the episode fired before the restart");
    }

    // Imported values are history brought in: they make no trend and are no reading of hers now.
    [Fact]
    public async Task ImportedReadings_AreNeitherATrendNorANewestReading()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("falling", """{"trigger":"glucose_trend","falling":1}""");
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(10), 100);
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(5), 100);
        await harness.PollAsync(100);
        (await harness.StatesAsync("falling")).ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(14), 300, GlucoseSource.Import);
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(9), 250, GlucoseSource.ImportHourly);
        await harness.PollAsync(100);

        harness.Effects.Fires.ShouldBeEmpty();
        (await harness.StatesAsync("falling")).ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);
    }

    // A curve point filling a gap is judged on nothing: only the newest reading is, though the point does
    // inform a rate's fit.
    [Fact]
    public async Task ABackFilledCurvePoint_IsNotJudged()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70}""");
        await harness.PollAsync(100);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(8), 50, GlucoseSource.Graph);
        await harness.PollAsync(100);

        harness.Effects.Fires.ShouldBeEmpty();
        (await harness.StatesAsync("low")).ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);
    }

    [Fact]
    public async Task NoReadings_CountsHerLastOwnReading_NotAnImportedOne()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("silent", """{"trigger":"sensor","state":"no_readings","for":{"minutes":20}}""");
        var last = harness.Now;
        await harness.PollAsync(100);

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.KeepAsync(SugaryServer.Her, harness.Now - TimeSpan.FromMinutes(1), 120, GlucoseSource.Import);
        var poll = new GlucosePoll(SugaryServer.Her, [],
            new GlucoseLinkState(_start - TimeSpan.FromDays(1), harness.Now, null, null, null), new SensorReading(last, 100, GlucoseTrend.Steady));
        await harness.Engine.PolledAsync(poll, CancellationToken.None);

        harness.Effects.Fires.ShouldHaveSingleItem().Report.Since.ShouldBe(last);
    }

    [Fact]
    public async Task DisabledAndSpentWatches_AreNotJudged_AndKeepTheirStates()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("off", """{"trigger":"glucose","below":70}""", enabled: false);
        await harness.WatchAsync("spent", """{"trigger":"glucose","below":70}""", extra: ""","once":true""", spent: true);
        await using (var db = harness.Factory())
        {
            db.GlucoseWatchStates.AddRange(
                new GlucoseWatchState { TenantId = SugaryServer.Her.TenantId, WatchId = "off", TriggerIndex = 0 },
                new GlucoseWatchState { TenantId = SugaryServer.Her.TenantId, WatchId = "spent", TriggerIndex = 0 });
            await db.SaveChangesAsync();
        }

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);
        await harness.Engine.TickAsync(CancellationToken.None);

        harness.Effects.Fires.ShouldBeEmpty();
        (await harness.StatesAsync("off")).ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);
        (await harness.StatesAsync("spent")).ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);
    }

    [Fact]
    public async Task AOnceWatch_IsSpentByAFireThatReachedSomebody()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("once", """{"trigger":"glucose","below":70},{"trigger":"glucose","below":80}""", extra: ""","once":true""");

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);

        harness.Effects.Fires.ShouldHaveSingleItem("both triggers fired together, and a one-shot acts once");
        var row = (await harness.RowAsync("once")).ShouldNotBeNull();
        (row.Spent, row.Enabled, row.LastFiredAt).ShouldBe((true, false, harness.Now));
        (await harness.Watches.FindAsync(SugaryServer.Her, "once", CancellationToken.None))!.Enabled.ShouldBeFalse();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);
        harness.Effects.Fires.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AOnceWatch_WhoseFireReachedNobody_StaysArmed()
    {
        await using var harness = await StartAsync();
        harness.Effects.Delivered = 0;
        await harness.WatchAsync("once", """{"trigger":"glucose","below":70}""", extra: ""","once":true""");

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);
        var row = (await harness.RowAsync("once")).ShouldNotBeNull();
        (row.Spent, row.Enabled, row.LastFiredAt).ShouldBe((false, true, harness.Now));

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(59);
        harness.Effects.Fires.Count.ShouldBe(1, "the episode is fired, delivered or not");

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);
        harness.Effects.Fires.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FailingEffects_AreContained_AndTheEpisodeIsFired()
    {
        await using var harness = await StartAsync();
        harness.Effects.ThrowsFor = "a";
        await harness.WatchAsync("a", """{"trigger":"glucose","below":70}""");
        await harness.WatchAsync("b", """{"trigger":"glucose","below":70}""");

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);

        harness.Effects.FiredIds.ShouldBe(["a", "b"]);
        (await harness.StatesAsync("a")).ShouldHaveSingleItem().FiredThisEpisode.ShouldBeTrue();
    }

    // A watch rewritten or removed while its fire was out is not given what was judged of the old one.
    [Theory]
    [InlineData("rewritten")]
    [InlineData("rewritten the same")]
    [InlineData("removed")]
    public async Task AWatchChangedWhileItFires_IsNotGivenTheOldJudgement(string change)
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70}""");
        await harness.WatchAsync("other", """{"trigger":"glucose","below":70}""");
        await harness.PollAsync(100);
        harness.Effects.During = async watch =>
        {
            if (watch.Watch.Id != "low")
            {
                return;
            }

            var spec = change == "rewritten"
                ? GlucoseWatchSpec.Parse("""{"name":"high","triggers":[{"trigger":"glucose","above":250}],"effects":[{"kind":"prompt","prompt":"x"}]}""").ToJson()
                : watch.Watch.Spec;
            if (change == "removed")
            {
                await harness.Watches.RemoveAsync(SugaryServer.Her, "low", CancellationToken.None);
            }
            else
            {
                await harness.Watches.ReplaceAsync(SugaryServer.Her, watch.Watch with { Spec = spec }, CancellationToken.None);
            }
        };

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);

        harness.Effects.FiredIds.ShouldBe(["low", "other"]);
        (await harness.StatesAsync("low")).ShouldBeEmpty();
        (await harness.RowAsync("low"))?.LastFiredAt.ShouldBeNull();
        (await harness.StatesAsync("other")).ShouldHaveSingleItem().FiredThisEpisode.ShouldBeTrue();
    }

    // A poll and the clock's pass judging her at once fire an episode once.
    [Fact]
    public async Task TwoJudgementsAtOnce_FireOnce()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("silent", """{"trigger":"sensor","state":"link_failing","for":{"minutes":5}}""");
        await using (var db = harness.Factory())
        {
            db.GlucoseLinks.Add(new GlucoseLink
            {
                UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, PatientId = "p", LinkedAt = _start,
                LastFailure = GlucoseLinkFailure.CredentialsRefused, FailingSince = _start
            });
            await db.SaveChangesAsync();
        }

        await harness.Engine.TickAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var release = new TaskCompletionSource();
        harness.Effects.During = _ => release.Task;

        var first = harness.Engine.TickAsync(CancellationToken.None);
        await Eventually.Until(() => harness.Effects.Fires.Count == 1, "the first judgement fired");
        var second = harness.Engine.TickAsync(CancellationToken.None);
        await Eventually.Settle();
        release.SetResult();
        await Task.WhenAll(first, second);

        harness.Effects.Fires.ShouldHaveSingleItem();
    }

    // A refused password is never polled again, so its `for` runs out on the clock.
    [Fact]
    public async Task LinkFailing_FiresOnTheClock_ForALinkNoPollAsksAgain()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("failing", """{"trigger":"sensor","state":"link_failing","for":{"minutes":30}}""");
        await using (var db = harness.Factory())
        {
            db.GlucoseLinks.Add(new GlucoseLink
            {
                UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, PatientId = "p", LinkedAt = _start - TimeSpan.FromDays(1),
                LastSuccessAt = _start - TimeSpan.FromMinutes(1), LastFailure = GlucoseLinkFailure.CredentialsRefused, FailingSince = _start
            });
            await db.SaveChangesAsync();
        }

        await harness.Engine.TickAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(29));
        await harness.Engine.TickAsync(CancellationToken.None);
        harness.Effects.Fires.ShouldBeEmpty();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Engine.TickAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Engine.TickAsync(CancellationToken.None);

        harness.Effects.Fires.ShouldHaveSingleItem().Report.Since.ShouldBe(_start);
    }

    // The sensor on her is the latest started among those LibreLinkUp last listed.
    [Fact]
    public async Task Ending_FiresOnTheClock_ForTheSensorOnHer()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("ending", """{"trigger":"sensor","state":"ending","within":{"hours":24}}""");
        var lastSeen = _start - TimeSpan.FromMinutes(1);
        var activated = _start - TimeSpan.FromDays(14) + TimeSpan.FromHours(30);
        await using (var db = harness.Factory())
        {
            db.GlucoseSensors.AddRange(
                // Started after the one on her, but not listed by the last poll.
                Sensor("gone", activated + TimeSpan.FromDays(1), lastSeen - TimeSpan.FromDays(1)),
                Sensor("listed-too", activated - TimeSpan.FromDays(1), lastSeen),
                Sensor("worn", activated, lastSeen));
            await db.SaveChangesAsync();
        }

        await harness.Engine.TickAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(6));
        await harness.Engine.TickAsync(CancellationToken.None);

        harness.Effects.Fires.ShouldHaveSingleItem().Report.SensorEndsAt.ShouldBe(activated + TimeSpan.FromDays(14));

        static GlucoseSensor Sensor(string serial, DateTimeOffset activatedAt, DateTimeOffset seen) => new()
        {
            UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, Serial = serial, ActivatedAt = activatedAt, LastSeenAt = seen
        };
    }

    // A watch's hours are her household's, as her phone last saved its zone: 12:00 UTC on Wednesday the
    // 18th is Thursday 01:00 in Auckland and Wednesday 13:00 on the server's (Madrid).
    [Fact]
    public async Task TimeConditions_AreReadOnTheZoneHerPhoneSaved()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("thursday", """{"trigger":"glucose","below":70}""",
            extra: ""","conditions":[{"condition":"time","weekday":["thu"]}]""");
        await using (var db = harness.Factory())
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, Endpoint = "https://push.example/her",
                TimeZone = "Pacific/Auckland"
            });
            await db.SaveChangesAsync();
        }

        await harness.PollAsync(100);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync(60);

        harness.Effects.Fires.ShouldHaveSingleItem();
    }

    // Through the poller against LibreLinkUp's wire: a fall below fires; an outage is "link failing"
    // and never "no readings".
    [Fact]
    public async Task ThePoller_HandsEachPollToHerWatches()
    {
        await using var harness = await StartAsync();
        await harness.WatchAsync("low", """{"trigger":"glucose","below":70}""");
        await harness.WatchAsync("failing", """{"trigger":"sensor","state":"link_failing","for":{"minutes":5}}""");
        await harness.WatchAsync("silent", """{"trigger":"sensor","state":"no_readings","for":{"minutes":5}}""");
        var connection = harness.Fake.AddAccount("ana@example.com", "pw").Follow("patient-1", "Ana", "García");
        connection.Latest = (new FakeLibreLinkUp.Point(harness.Now, 100), 3);
        await harness.LinkAsync(SugaryServer.Her, "ana@example.com", "pw", "patient-1");

        await NextMinuteAsync();
        connection.Latest = (new FakeLibreLinkUp.Point(harness.Now, 65), 2);
        await NextMinuteAsync();

        var (_, report) = harness.Effects.Fires.ShouldHaveSingleItem();
        report.Newest.ShouldBe(new SensorReading(harness.Now - TimeSpan.FromMinutes(1), 65, GlucoseTrend.Falling));

        connection.Down = true;
        foreach (var _ in Enumerable.Range(0, 20))
        {
            await NextMinuteAsync();
        }

        harness.Effects.FiredIds.ShouldBe(["low", "failing"]);
        harness.Fake.Unhandled.ShouldBeEmpty();

        async Task NextMinuteAsync()
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.SweepAsync();
            await harness.Engine.TickAsync(CancellationToken.None);
        }
    }
}