using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Store;

// The mount's and the downloads' reads, a context per call opened for the person the read is for:
// the mount is a singleton answering overlapping calls, and a context is not safe to share between
// them. The context's filters keep each read to that person's log and their household's foods.
public sealed class SqliteSugaryLog(SugaryStore store) : ISugaryLog
{
    public async Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
        return await db.Meals.AsNoTracking().OrderBy(m => m.EatenAt).Select(m => m.EatenAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoggedMeal>> MealsAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = store.For(owner);
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

    public async Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
        return await db.Doses.AsNoTracking().OrderBy(d => d.TakenAt).Select(d => d.TakenAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoggedDose>> DosesAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = store.For(owner);
        var doses = await db.Doses.AsNoTracking()
            .Where(d => d.TakenAt >= from && d.TakenAt < to)
            .OrderBy(d => d.TakenAt)
            .ToListAsync(ct);
        return doses.Select(d => new LoggedDose(d.Id, d.TakenAt, d.Units, d.Kind, d.MealIds, d.Note)).ToList();
    }

    public async Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
        return await db.ExerciseSessions.AsNoTracking().OrderBy(e => e.StartedAt).Select(e => e.StartedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = store.For(owner);
        var sessions = await db.ExerciseSessions.AsNoTracking()
            .Where(e => e.StartedAt >= from && e.StartedAt < to)
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);
        return sessions.Select(e => new LoggedExercise(e.Id, e.Kind, e.Name, e.StartedAt, e.EndedAt, e.Note)).ToList();
    }

    public async Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
        var periods = await db.Periods.AsNoTracking().OrderBy(p => p.Start).ToListAsync(ct);
        return periods.Select(p => new LoggedPeriod(p.Id, p.Start, p.End)).ToList();
    }

    public async Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
        var notes = await db.DayNotes.AsNoTracking().OrderBy(n => n.Day).ToListAsync(ct);
        return notes.Select(n => new LoggedDayNote(n.Day, n.Text)).ToList();
    }

    public async Task<IReadOnlyList<SavedFood>> FoodsAsync(Account owner, CancellationToken ct)
    {
        await using var db = store.For(owner);
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