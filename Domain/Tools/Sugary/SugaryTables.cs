using System.Globalization;
using System.Text.Json;
using Domain.DTOs.Sugary;
using Domain.DTOs.Tables;

namespace Domain.Tools.Sugary;

// The log as tables another tool can lay beside glucose readings: one with a row per meal, one
// with a row per food eaten, one with her saved foods, one with a row per insulin dose, one with a
// row per session of exercise, one with a row per period and one with a row per day she wrote a note on. The first three's columns, order
// and spelling are the standalone app's export, so a reader of either reads both; the rest are
// Sugary's own. A dose is joined to the meals it was for through dose_meals, a row per link, so
// doses stays a row per dose and a sum of its units counts a bolus for two meals once.
public static class SugaryTables
{
    private static readonly TableColumn[] _mealColumns =
    [
        new("meal_id", ColumnType.Text), new("eaten_at", ColumnType.Instant), new("carbs_g", ColumnType.Decimal),
        new("carbs_low_g", ColumnType.Decimal), new("carbs_high_g", ColumnType.Decimal),
        new("first_estimate_g", ColumnType.Decimal), new("first_estimate_low_g", ColumnType.Decimal),
        new("first_estimate_high_g", ColumnType.Decimal), new("outcome", ColumnType.Text),
        new("items", ColumnType.Text), new("notes", ColumnType.Text)
    ];

    private static readonly TableColumn[] _itemColumns =
    [
        new("meal_id", ColumnType.Text), new("eaten_at", ColumnType.Instant), new("position", ColumnType.Integer),
        new("name", ColumnType.Text), new("portion", ColumnType.Text), new("amount", ColumnType.Decimal),
        new("carbs_per_100", ColumnType.Decimal), new("carbs_g", ColumnType.Decimal),
        new("carbs_low_g", ColumnType.Decimal), new("carbs_high_g", ColumnType.Decimal),
        new("source", ColumnType.Text), new("confidence", ColumnType.Text), new("edited", ColumnType.Boolean),
        new("food_id", ColumnType.Text)
    ];

    private static readonly TableColumn[] _foodColumns =
    [
        new("food_id", ColumnType.Text), new("name", ColumnType.Text), new("carbs_per_100", ColumnType.Decimal),
        new("unit", ColumnType.Text), new("portion_amount", ColumnType.Decimal),
        new("portion_carbs_g", ColumnType.Decimal), new("barcode", ColumnType.Text), new("source", ColumnType.Text)
    ];

    private static readonly TableColumn[] _doseColumns =
    [
        new("dose_id", ColumnType.Text), new("taken_at", ColumnType.Instant), new("kind", ColumnType.Text),
        new("units", ColumnType.Decimal), new("note", ColumnType.Text)
    ];

    private static readonly TableColumn[] _doseMealColumns = [new("dose_id", ColumnType.Text), new("meal_id", ColumnType.Text)];

    private static readonly TableColumn[] _exerciseColumns =
    [
        new("exercise_id", ColumnType.Text), new("started_at", ColumnType.Instant),
        new("ended_at", ColumnType.Instant), new("minutes", ColumnType.Integer), new("kind", ColumnType.Text),
        new("name", ColumnType.Text), new("note", ColumnType.Text)
    ];

    private static readonly TableColumn[] _periodColumns =
    [
        new("period_id", ColumnType.Text), new("start_date", ColumnType.Date), new("end_date", ColumnType.Date),
        new("days", ColumnType.Integer), new("cycle_days", ColumnType.Integer)
    ];

    private static readonly TableColumn[] _dayNoteColumns = [new("date", ColumnType.Date), new("note", ColumnType.Text)];

    public static readonly string[] MealColumns = Names(_mealColumns);
    public static readonly string[] ItemColumns = Names(_itemColumns);
    public static readonly string[] FoodColumns = Names(_foodColumns);

    // Oldest first, the order a table of events is read in.
    public static Table Meals(IEnumerable<LoggedMeal> meals, TimeZoneInfo zone) =>
        new("meals", _mealColumns, [.. meals.OrderBy(meal => meal.EatenAt).Select(meal => Row(
            meal.Id.ToString(), On(meal.EatenAt, zone), meal.Carbs.Best, meal.Carbs.Low, meal.Carbs.High,
            meal.FirstEstimate?.Best, meal.FirstEstimate?.Low, meal.FirstEstimate?.High, Word(meal.Outcome),
            string.Join("; ", meal.Items.OrderBy(item => item.Position).Select(item => item.Name)),
            meal.Notes))]);

    public static Table Items(IEnumerable<LoggedMeal> meals, TimeZoneInfo zone) =>
        new("items", _itemColumns, [.. meals.OrderBy(meal => meal.EatenAt)
            .SelectMany(meal => meal.Items.OrderBy(item => item.Position).Select(item => (meal, item)))
            .Select(row => Row(
                row.meal.Id.ToString(), On(row.meal.EatenAt, zone), row.item.Position + 1, row.item.Name,
                row.item.Portion, row.item.Amount, row.item.CarbsPer100, row.item.Carbs.Best, row.item.Carbs.Low,
                row.item.Carbs.High, Word(row.item.Source),
                row.item.Confidence is { } confidence ? Word(confidence) : null, row.item.Edited,
                row.item.FoodId?.ToString()))]);

    public static Table Doses(IEnumerable<LoggedDose> doses, TimeZoneInfo zone) =>
        new("doses", _doseColumns, [.. doses.OrderBy(dose => dose.TakenAt).Select(dose => Row(
            dose.Id.ToString(), On(dose.TakenAt, zone), Word(dose.Kind), dose.Units, dose.Note))]);

    // In the doses' order, each dose's meals in the order she picked them.
    public static Table DoseMeals(IEnumerable<LoggedDose> doses) =>
        new("dose_meals", _doseMealColumns, [.. doses.OrderBy(dose => dose.TakenAt)
            .SelectMany(dose => dose.MealIds.Select(meal => Row(dose.Id.ToString(), meal.ToString())))]);

    // A session's minutes are worked out from its two times on every read, rounded to the whole
    // minute, so they never disagree with them; a running session has neither an end nor minutes.
    public static Table Exercise(IEnumerable<LoggedExercise> sessions, TimeZoneInfo zone) =>
        new("exercise", _exerciseColumns, [.. sessions.OrderBy(session => session.StartedAt).Select(session => Row(
            session.Id.ToString(), On(session.StartedAt, zone),
            session.EndedAt is { } end ? On(end, zone) : null,
            session.EndedAt is { } last
                ? (int)Math.Round((last - session.StartedAt).TotalMinutes, MidpointRounding.AwayFromZero)
                : null,
            Word(session.Kind), session.Name, session.Note))]);

    // Each period's length (both ends counted) and the cycle it began (to the next start) are worked
    // out here, from her days on every read, so a reader never does date arithmetic and the
    // figures can never disagree with the days. An ongoing period has neither.
    public static Table Periods(IEnumerable<LoggedPeriod> periods)
    {
        var sorted = periods.OrderBy(period => period.Start).ToList();
        return new("periods", _periodColumns, [.. sorted
            .Select((period, index) => (period, next: index + 1 < sorted.Count ? sorted[index + 1].Start : (DateOnly?)null))
            .Select(row => Row(
                row.period.Id.ToString(), row.period.Start, row.period.End,
                row.period.End is { } last ? last.DayNumber - row.period.Start.DayNumber + 1 : null,
                row.next is { } next ? next.DayNumber - row.period.Start.DayNumber : null))]);
    }

    public static Table DayNotes(IEnumerable<LoggedDayNote> notes) =>
        new("day_notes", _dayNoteColumns, [.. notes.OrderBy(note => note.Day).Select(note => Row(note.Day, note.Text))]);

    // By name, the order a list of things she keeps is looked through in.
    public static Table Foods(IEnumerable<SavedFood> foods) =>
        new("foods", _foodColumns, [.. foods.OrderBy(food => food.Name, StringComparer.Ordinal).Select(food => Row(
            food.Id.ToString(), food.Name, food.CarbsPer100, food.Unit, food.PortionAmount, food.PortionCarbs,
            food.Barcode, Word(food.Source)))]);

    // The month a meal is filed under is the month its eaten_at shows: the home's calendar, not UTC's.
    public static string Month(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // Enums are written the way the API spells them.
    public static string Word<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    // Every time is held on the clock the table is read on, so whichever encoding writes it carries
    // that offset and a reader never has to guess which clock a column is on.
    private static DateTimeOffset On(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone);

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string[] Names(TableColumn[] columns) => [.. columns.Select(column => column.Name)];
}