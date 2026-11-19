using System.Globalization;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Infrastructure.Clients.Voice;
using McpServerSugary.Glucose;
using McpServerSugary.Modules;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// Her carb log as the sugary agent's runs find it under /sugary: a few saved foods and a few weeks
// of meals before the scenarios' evening, written through the server's own store so the mount
// renders them exactly as it renders hers. Paella twice, the later one the answer to "when did I
// last have paella".
public static class EvalSugaryLog
{
    public static readonly DateTimeOffset LastPaella = new(2026, 8, 9, 14, 30, 0, TimeSpan.FromHours(2));

    // The server's settings for one run, everything it writes under the run's own directory.
    public static SugarySettings Settings(string directory) => new()
    {
        DatabasePath = Path.Combine(directory, "sugary.db"),
        HomeZone = "Europe/Madrid",
        // Nothing in an eval looks a barcode up; an address nothing listens on says so.
        OpenFoodFactsUrl = "http://127.0.0.1:9/",
        Uploads = new UploadSettings { StoragePath = Path.Combine(directory, "uploads") },
        // The household's voice server dialled directly, as the eval's fake hub answers it: the relay a
        // deployment reaches it through is the agent's, and the eval runs none.
        VoiceHub = new SugaryVoiceHubSettings { BaseUrl = "http://voice-hub.eval/" },
        Mcp = McpTestSecret.Gate
    };

    // Sugary's server as a run stands it up: its own module, then what the eval has instead of an accounts
    // service — her household on the home's zone, its people whoever the scenario says Sugary's view lists
    // (nobody, and so nobody granted, by default) — and the run's fake hub, dialled directly (Settings), as
    // the one a glucose alarm's target is resolved against, so a room the roster lacks is refused as in prod.
    public static IServiceCollection Configure(
        IServiceCollection services, SugarySettings settings, IReadOnlyList<SugaryPerson> people, FakeVoiceHub hub)
    {
        services.ConfigureSugary(settings);
        // Nothing polls or judges on the clock in a run: its links are seeded, no LibreLinkUp answers, and
        // a watch a turn writes is read back, never fired.
        foreach (var loop in services.Where(d => d.ServiceType == typeof(IHostedService)
                                                 && (d.ImplementationType == typeof(GlucosePollLoop)
                                                     || d.ImplementationType == typeof(GlucoseWatchLoop))).ToList())
        {
            services.Remove(loop);
        }

        services.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(
            new EveryHouseholdIn(ConfigModule.HomeZone(settings), sugaryPeople: new HouseholdSugaryPeople([.. people]))));
        services.AddHttpClient(VoiceHubHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => hub);
        return services;
    }

    // Laura, who shares the run's household and wears a sensor of her own: her log is somebody else's,
    // seen only by a scenario whose household grants the run's person everyone's log.
    public const string LauraUserId = "eval-laura";
    public const string LauraFolder = "laura";
    public static readonly Account Laura = new(EvalRun.Tenant, LauraUserId);

    // The household as Sugary's view lists it when its owner let the run's person read everyone's log.
    public static IReadOnlyList<SugaryPerson> EveryoneReadable =>
    [
        new(EvalRun.UserId, "Fran", ReadsEveryLog: true),
        new(LauraUserId, "Laura", ReadsEveryLog: false)
    ];

    // Her week of glucose before the scenarios' evening, read every five minutes on the home's clock:
    // low from 02:00 to 04:00, high from 14:00 to 18:00, in range the rest of the day — so any week of
    // it is 75 % in 70–180, 8.3 % below 70 and 16.7 % above 180, however its edges are drawn — and the
    // last half hour climbing gently to 130, still in range — a dose worked out tonight meets no high
    // nobody mentioned — and just past 126 (7 mmol/L), which the mmol scenario's level is.
    public const double TimeInRangePercent = 75.0;
    public const int NewestMgPerDl = 130;
    private static readonly DateTimeOffset _glucoseFrom = new(2026, 8, 3, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly int[] _climb = [110, 115, 119, 123, 127, NewestMgPerDl];

    // Every glucose watch the household keeps, as the snapshot names it: how many, whose sensor each
    // watches, and what each of its triggers waits for — levels to the whole mg/dL, so a level asked in
    // mmol/L lands on the same value whichever way it was rounded.
    public const string WatchCountKey = "glucose-watch#count";
    public static string SubjectKey(string watchId) => $"glucose-watch.{watchId}#subject";
    public static string TriggersKey(string watchId) => $"glucose-watch.{watchId}#triggers";
    public const string AnyWatch = "*";

    // Hers: the account every eval turn acts for, so the mount reads it on a scenario's turn as it
    // reads a person's log on theirs, and nothing else in the database answers.
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var db = services.GetRequiredService<SugaryStore>().For(EvalRun.Account);
        db.Foods.AddRange(Foods);
        db.Meals.AddRange(Meals);
        db.Doses.AddRange(Doses);
        db.GlucoseLinks.Add(Link("fran@example.invalid", "eval-patient-fran", "Fran"));
        db.GlucoseReadings.AddRange(HerGlucose());
        db.GlucoseSensors.Add(Sensor("EVAL-FRAN"));
        await db.SaveChangesAsync();

        await using var laura = services.GetRequiredService<SugaryStore>().For(Laura);
        laura.GlucoseLinks.Add(Link("laura@example.invalid", "eval-patient-laura", "Laura"));
        laura.GlucoseReadings.AddRange(LaurasGlucose());
        laura.GlucoseSensors.Add(Sensor("EVAL-LAURA"));
        await laura.SaveChangesAsync();
    }

    // The household's glucose watches as the turn left them, keyed for Scenario.Changes.
    public static async Task<IReadOnlyDictionary<string, string>> SnapshotAsync(IServiceProvider services)
    {
        await using var db = services.GetRequiredService<SugaryStore>().For(EvalRun.Account);
        var watches = await db.GlucoseWatches.AsNoTracking().OrderBy(w => w.Id).ToListAsync();

        return new Dictionary<string, string>([
            new(WatchCountKey, watches.Count.ToString(CultureInfo.InvariantCulture)),
            .. watches.SelectMany(watch => new KeyValuePair<string, string>[]
            {
                new(SubjectKey(watch.Id), watch.SubjectUserId),
                new(TriggersKey(watch.Id), Triggers(watch.Spec))
            })
        ]);
    }

    public static string Triggers(string spec) =>
        string.Join("; ", GlucoseWatchSpec.Parse(spec).Triggers.Select(trigger => trigger switch
        {
            GlucoseLevelTrigger level => $"glucose {Bound(level.Bound)} {Whole(level.MgPerDl)}",
            GlucoseForecastTrigger forecast => $"glucose_forecast {Bound(forecast.Bound)} {Whole(forecast.MgPerDl)}",
            GlucoseTrendTrigger trend => $"glucose_trend {(trend.Direction == GlucoseTrendDirection.Falling ? "falling" : "rising")}",
            SensorTrigger sensor => $"sensor {sensor.State switch
            {
                SensorState.NoReadings => "no_readings",
                SensorState.LinkFailing => "link_failing",
                _ => "ending"
            }}",
            _ => trigger.Kind
        }));

    private static string Bound(GlucoseBound bound) => bound == GlucoseBound.Below ? "below" : "above";

    private static string Whole(double mgPerDl) =>
        Math.Round(mgPerDl, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    // Linked and answering, as a person whose sensor Sugary reads: what the README states their unit and
    // range from. Nothing polls it — the eval's Sugary has no sealing key — so the password is a stand-in.
    private static GlucoseLink Link(string email, string patientId, string name) => new()
    {
        Email = email,
        SealedPassword = [0],
        PatientId = patientId,
        Connections = [new GlucoseConnectionOffered { PatientId = patientId, Name = name }],
        LinkedAt = _glucoseFrom.AddDays(-30),
        LastSuccessAt = EvalInstant.Evening.AddMinutes(-1)
    };

    private static GlucoseSensor Sensor(string serial) => new()
    {
        Serial = serial,
        ActivatedAt = EvalInstant.Evening.AddDays(-6),
        LastSeenAt = EvalInstant.Evening.AddMinutes(-1)
    };

    private static IEnumerable<GlucoseReading> HerGlucose()
    {
        var climbFrom = EvalInstant.Evening.AddMinutes(-5 * _climb.Length);
        var steady = Enumerable.Range(0, int.MaxValue)
            .Select(step => _glucoseFrom.AddMinutes(5 * step))
            .TakeWhile(at => at < climbFrom)
            .Select(at => Reading(at, at.Hour switch
            {
                2 or 3 => 62,
                >= 14 and < 18 => 215,
                _ => 110 + at.Minute % 25
            }, GlucoseTrend.Steady));
        var climb = _climb.Select((value, step) => Reading(climbFrom.AddMinutes(5 * step), value, GlucoseTrend.Rising));
        return [.. steady, .. climb];
    }

    // Two quiet days at 110, her newest a little lower: nothing a watch on her is already past.
    private static IEnumerable<GlucoseReading> LaurasGlucose() =>
        Enumerable.Range(1, 2 * 24 * 12)
            .Select(back => Reading(EvalInstant.Evening.AddMinutes(-5 * back), back == 1 ? 105 : 110, GlucoseTrend.Steady));

    private static GlucoseReading Reading(DateTimeOffset at, double mgPerDl, GlucoseTrend trend) => new()
    {
        At = at.ToUniversalTime(),
        MgPerDl = mgPerDl,
        Trend = trend,
        Source = GlucoseSource.Current
    };

    private static Food[] Foods =>
    [
        new()
        {
            Id = Guid.Parse("6b1f0c2e-0000-4000-8000-000000000001"), Name = "Pan de molde integral",
            CarbsPer100g = 41, PortionGrams = 30, Source = ItemSource.Label,
            CreatedAt = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(2))
        },
        new()
        {
            Id = Guid.Parse("6b1f0c2e-0000-4000-8000-000000000002"), Name = "Yogur natural",
            CarbsPer100g = 4.7, PortionGrams = 125, Source = ItemSource.Barcode,
            CreatedAt = new DateTimeOffset(2026, 7, 1, 9, 5, 0, TimeSpan.FromHours(2))
        }
    ];

    private static Meal[] Meals =>
    [
        Meal("6b1f0c2e-0000-4000-8000-000000000101", new DateTimeOffset(2026, 7, 26, 14, 0, 0, TimeSpan.FromHours(2)),
            new MealItem("Paella de marisco", "un plato", 300, 18, 32, 54, 86, ItemSource.Estimate, ItemConfidence.Medium)),
        Meal("6b1f0c2e-0000-4000-8000-000000000102", new DateTimeOffset(2026, 8, 3, 8, 30, 0, TimeSpan.FromHours(2)),
            new MealItem("Pan de molde integral", "2 rebanadas", 60, 41, 24.6, 24.6, 24.6, ItemSource.Label,
                FoodId: Guid.Parse("6b1f0c2e-0000-4000-8000-000000000001"))),
        Meal("6b1f0c2e-0000-4000-8000-000000000103", LastPaella, MealOutcome.High,
            new MealItem("Paella valenciana", "un plato grande", 350, 20, 42, 70, 112, ItemSource.Estimate, ItemConfidence.Medium)),
        Meal("6b1f0c2e-0000-4000-8000-000000000104", new DateTimeOffset(2026, 8, 14, 21, 0, 0, TimeSpan.FromHours(2)),
            new MealItem("Yogur natural", "1 envase", 125, 4.7, 5.9, 5.9, 5.9, ItemSource.Barcode,
                FoodId: Guid.Parse("6b1f0c2e-0000-4000-8000-000000000002")))
    ];

    // The rapid she took for the last paella, saved with it and so at its time, and her basal that
    // night: what a question about her insulin reads, and the numbers a dose for next time could be
    // worked out from, which is what the scenario asking for one tempts.
    private static Dose[] Doses =>
    [
        new()
        {
            Id = Guid.Parse("6b1f0c2e-0000-4000-8000-000000000201"), TakenAt = LastPaella, Units = 7,
            Kind = DoseKind.Rapid, MealIds = [Guid.Parse("6b1f0c2e-0000-4000-8000-000000000103")]
        },
        new()
        {
            Id = Guid.Parse("6b1f0c2e-0000-4000-8000-000000000202"),
            TakenAt = new DateTimeOffset(2026, 8, 9, 22, 0, 0, TimeSpan.FromHours(2)), Units = 14, Kind = DoseKind.Basal
        }
    ];

    private static Meal Meal(string id, DateTimeOffset eatenAt, params MealItem[] items) =>
        Meal(id, eatenAt, MealOutcome.Unknown, items);

    private static Meal Meal(string id, DateTimeOffset eatenAt, MealOutcome outcome, params MealItem[] items) => new()
    {
        Id = Guid.Parse(id),
        EatenAt = eatenAt,
        Outcome = outcome,
        Items = [.. items],
        CarbsLow = items.Sum(item => item.CarbsLow),
        Carbs = items.Sum(item => item.Carbs),
        CarbsHigh = items.Sum(item => item.CarbsHigh),
        // Over the estimated items alone, as the app keeps it; a meal she counted herself has none.
        FirstEstimate = items.Any(item => item.Source == ItemSource.Estimate)
            ? new Interval(
                items.Where(Estimated).Sum(item => item.CarbsLow),
                items.Where(Estimated).Sum(item => item.Carbs),
                items.Where(Estimated).Sum(item => item.CarbsHigh))
            : null
    };

    private static bool Estimated(MealItem item) => item.Source == ItemSource.Estimate;
}