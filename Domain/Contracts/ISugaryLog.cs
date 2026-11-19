using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;

namespace Domain.Contracts;

// One person's carb log as the /sugary mount and the app's downloads read it, straight from the store
// on every call: nothing here is cached, so what a reader sees is what the database holds at that
// moment. Every read names whose log it is: the person's own meals, doses, exercise, periods,
// notes and glucose, and their household's foods — never anyone else's, and never a row nobody owns yet.
public interface ISugaryLog
{
    // When every meal was eaten, oldest first: enough to say which months have a file without
    // loading a single item.
    Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(Account owner, CancellationToken ct);

    // The meals eaten in [from, to), oldest first, each with its items in the order of the meal.
    Task<IReadOnlyList<LoggedMeal>> MealsAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // When every dose was taken, oldest first, for the same reason as the meals' times.
    Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(Account owner, CancellationToken ct);

    // The doses taken in [from, to), oldest first, each with the meal it belongs to, if any.
    Task<IReadOnlyList<LoggedDose>> DosesAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // When every session of exercise started, oldest first, for the same reason as the meals' times.
    Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(Account owner, CancellationToken ct);

    // The sessions started in [from, to), oldest first, a running one with no end.
    Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // The person's periods and day notes, each whole and oldest first: a few a year, and a few a week.
    Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(Account owner, CancellationToken ct);

    Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(Account owner, CancellationToken ct);

    // The months on this clock holding at least one of the person's glucose readings, "yyyy-MM", oldest
    // first. A reading a minute is half a million a year, too many to load for a listing of a few
    // dozen months, so the store finds each month's first reading instead.
    Task<IReadOnlyList<string>> GlucoseMonthsAsync(Account owner, TimeZoneInfo zone, CancellationToken ct);

    // The readings measured in [from, to), oldest first.
    Task<IReadOnlyList<LoggedGlucose>> GlucoseAsync(
        Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // Every sensor the person has worn as LibreLinkUp listed it, a few a month.
    Task<IReadOnlyList<LoggedSensor>> GlucoseSensorsAsync(Account owner, CancellationToken ct);

    // The person's unit and target range as their LibreLinkUp link keeps them; null while there is no
    // link (never linked, or unlinked: the readings stay).
    Task<GlucoseTargets?> GlucoseTargetsAsync(Account owner, CancellationToken ct);

    // The foods the person's household keeps, by name.
    Task<IReadOnlyList<SavedFood>> FoodsAsync(Account owner, CancellationToken ct);
}