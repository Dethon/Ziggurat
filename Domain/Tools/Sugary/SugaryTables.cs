using System.Globalization;
using System.Text.Json;
using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary;

// The log as tables another tool can lay beside glucose readings: one with a row per meal, one
// with a row per food eaten, one with her saved foods, one with a row per insulin dose, one with a
// row per session of exercise, one with a row per period and one with a row per day she wrote a note on. The first three's columns, order
// and spelling are the standalone app's export, so a reader of either reads both; the rest are
// Sugary's own, the doses joined to a meal by meal_id.
public static class SugaryTables
{
    public static readonly string[] MealColumns =
    [
        "meal_id", "eaten_at", "carbs_g", "carbs_low_g", "carbs_high_g", "first_estimate_g",
        "first_estimate_low_g", "first_estimate_high_g", "outcome", "items", "notes"
    ];

    public static readonly string[] ItemColumns =
    [
        "meal_id", "eaten_at", "position", "name", "portion", "amount", "carbs_per_100", "carbs_g",
        "carbs_low_g", "carbs_high_g", "source", "confidence", "edited", "food_id"
    ];

    public static readonly string[] FoodColumns =
    [
        "food_id", "name", "carbs_per_100", "unit", "portion_amount", "portion_carbs_g", "barcode", "source"
    ];

    public static readonly string[] DoseColumns = ["dose_id", "taken_at", "kind", "units", "meal_id", "note"];

    public static readonly string[] ExerciseColumns =
        ["exercise_id", "started_at", "ended_at", "minutes", "kind", "name", "note"];

    public static readonly string[] PeriodColumns = ["period_id", "start_date", "end_date", "days", "cycle_days"];

    public static readonly string[] DayNoteColumns = ["date", "note"];

    // Oldest first, the order a table of events is read in.
    public static string Meals(IEnumerable<LoggedMeal> meals, TimeZoneInfo zone) =>
        meals.OrderBy(meal => meal.EatenAt)
            .Aggregate(new SugaryCsv(MealColumns), (csv, meal) => csv.Row(
                meal.Id.ToString(), Stamp(meal.EatenAt, zone), meal.Carbs.Best, meal.Carbs.Low, meal.Carbs.High,
                meal.FirstEstimate?.Best, meal.FirstEstimate?.Low, meal.FirstEstimate?.High, Word(meal.Outcome),
                string.Join("; ", meal.Items.OrderBy(item => item.Position).Select(item => item.Name)),
                meal.Notes))
            .ToString();

    public static string Items(IEnumerable<LoggedMeal> meals, TimeZoneInfo zone) =>
        meals.OrderBy(meal => meal.EatenAt)
            .SelectMany(meal => meal.Items.OrderBy(item => item.Position).Select(item => (meal, item)))
            .Aggregate(new SugaryCsv(ItemColumns), (csv, row) => csv.Row(
                row.meal.Id.ToString(), Stamp(row.meal.EatenAt, zone), row.item.Position + 1, row.item.Name,
                row.item.Portion, row.item.Amount, row.item.CarbsPer100, row.item.Carbs.Best, row.item.Carbs.Low,
                row.item.Carbs.High, Word(row.item.Source),
                row.item.Confidence is { } confidence ? Word(confidence) : null, row.item.Edited,
                row.item.FoodId?.ToString()))
            .ToString();

    public static string Doses(IEnumerable<LoggedDose> doses, TimeZoneInfo zone) =>
        doses.OrderBy(dose => dose.TakenAt)
            .Aggregate(new SugaryCsv(DoseColumns), (csv, dose) => csv.Row(
                dose.Id.ToString(), Stamp(dose.TakenAt, zone), Word(dose.Kind), dose.Units, dose.MealId?.ToString(),
                dose.Note))
            .ToString();

    // A session's minutes are worked out from its two times on every read, rounded to the whole
    // minute, so they never disagree with them; a running session has neither an end nor minutes.
    public static string Exercise(IEnumerable<LoggedExercise> sessions, TimeZoneInfo zone) =>
        sessions.OrderBy(session => session.StartedAt)
            .Aggregate(new SugaryCsv(ExerciseColumns), (csv, session) => csv.Row(
                session.Id.ToString(), Stamp(session.StartedAt, zone),
                session.EndedAt is { } end ? Stamp(end, zone) : null,
                session.EndedAt is { } last
                    ? (int)Math.Round((last - session.StartedAt).TotalMinutes, MidpointRounding.AwayFromZero)
                    : null,
                Word(session.Kind), session.Name, session.Note))
            .ToString();

    // Each period's length (both ends counted) and the cycle it began (to the next start) are worked
    // out here, from her days on every read, so a reader never does date arithmetic and the
    // figures can never disagree with the days. An ongoing period has neither.
    public static string Periods(IEnumerable<LoggedPeriod> periods)
    {
        var sorted = periods.OrderBy(period => period.Start).ToList();
        return sorted
            .Select((period, index) => (period, next: index + 1 < sorted.Count ? sorted[index + 1].Start : (DateOnly?)null))
            .Aggregate(new SugaryCsv(PeriodColumns), (csv, row) => csv.Row(
                row.period.Id.ToString(), Day(row.period.Start), row.period.End is { } end ? Day(end) : null,
                row.period.End is { } last ? last.DayNumber - row.period.Start.DayNumber + 1 : null,
                row.next is { } next ? next.DayNumber - row.period.Start.DayNumber : null))
            .ToString();
    }

    public static string DayNotes(IEnumerable<LoggedDayNote> notes) =>
        notes.OrderBy(note => note.Day)
            .Aggregate(new SugaryCsv(DayNoteColumns), (csv, note) => csv.Row(Day(note.Day), note.Text))
            .ToString();

    // A day of her calendar, with no time and no offset: the same day wherever it is read.
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // By name, the order a list of things she keeps is looked through in.
    public static string Foods(IEnumerable<SavedFood> foods) =>
        foods.OrderBy(food => food.Name, StringComparer.Ordinal)
            .Aggregate(new SugaryCsv(FoodColumns), (csv, food) => csv.Row(
                food.Id.ToString(), food.Name, food.CarbsPer100, food.Unit, food.PortionAmount, food.PortionCarbs,
                food.Barcode, Word(food.Source)))
            .ToString();

    // Every time carries its offset, so a reader never has to guess which clock a column is on.
    public static string Stamp(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    // The month a meal is filed under is the month its eaten_at shows: the home's calendar, not UTC's.
    public static string Month(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // Enums are written the way the API spells them.
    public static string Word<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}