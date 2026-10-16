using Domain.Contracts;
using Domain.DTOs.Sugary;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Store;

// The mount's reads, a context per call: the mount is a singleton answering overlapping calls, and
// a context is not safe to share between them.
public sealed class SqliteSugaryLog(IDbContextFactory<SugaryDbContext> contexts) : ISugaryLog
{
    public async Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Meals.AsNoTracking().OrderBy(m => m.EatenAt).Select(m => m.EatenAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoggedMeal>> MealsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Projected so the photo stays in the database.
        var meals = await db.Meals.AsNoTracking()
            .Where(m => m.EatenAt >= from && m.EatenAt < to)
            .OrderBy(m => m.EatenAt)
            .Select(m => new Meal
            {
                Id = m.Id,
                EatenAt = m.EatenAt,
                Notes = m.Notes,
                Outcome = m.Outcome,
                FirstEstimate = m.FirstEstimate,
                Items = m.Items,
                CarbsLow = m.CarbsLow,
                Carbs = m.Carbs,
                CarbsHigh = m.CarbsHigh
            })
            .ToListAsync(ct);
        return meals.Select(ToLogged).ToList();
    }

    public async Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Doses.AsNoTracking().OrderBy(d => d.TakenAt).Select(d => d.TakenAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoggedDose>> DosesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var doses = await db.Doses.AsNoTracking()
            .Where(d => d.TakenAt >= from && d.TakenAt < to)
            .OrderBy(d => d.TakenAt)
            .ToListAsync(ct);
        return doses.Select(d => new LoggedDose(d.Id, d.TakenAt, d.Units, d.Kind, d.MealId, d.Note)).ToList();
    }

    public async Task<IReadOnlyList<SavedFood>> FoodsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var foods = await db.Foods.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        return foods
            .Select(f => new SavedFood(
                f.Id, f.Name, f.CarbsPer100g, f.Unit, f.PortionGrams, f.PortionCarbs, f.Barcode, f.Source))
            .ToList();
    }

    private static LoggedMeal ToLogged(Meal meal) => new(
        meal.Id,
        meal.EatenAt,
        new CarbRange(meal.CarbsLow, meal.Carbs, meal.CarbsHigh),
        meal.FirstEstimate is { } first ? new CarbRange(first.Low, first.Best, first.High) : null,
        meal.Outcome,
        meal.Notes,
        meal.Items
            .OrderBy(i => i.Position)
            .Select(i => new LoggedItem(
                i.Position, i.Name, i.Portion, i.Grams, i.CarbsPer100g, new CarbRange(i.CarbsLow, i.Carbs, i.CarbsHigh),
                i.Source, i.Confidence, i.Edited, i.FoodId))
            .ToList());
}