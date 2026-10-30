using Domain.DTOs.Sugary;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;

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
        Mcp = McpTestSecret.Gate
    };

    // Hers: the account every eval turn acts for, so the mount reads it on a scenario's turn as it
    // reads a person's log on theirs, and nothing else in the database answers.
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var db = services.GetRequiredService<SugaryStore>().For(EvalRun.Account);
        db.Foods.AddRange(Foods);
        db.Meals.AddRange(Meals);
        db.Doses.AddRange(Doses);
        await db.SaveChangesAsync();
    }

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