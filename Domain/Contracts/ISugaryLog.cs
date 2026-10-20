using Domain.DTOs.Sugary;

namespace Domain.Contracts;

// Her carb log as the /sugary mount reads it, straight from the store on every call: nothing here
// is cached, so what a reader sees is what the database holds at that moment.
public interface ISugaryLog
{
    // When every meal was eaten, oldest first: enough to say which months have a file without
    // loading a single item.
    Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(CancellationToken ct);

    // The meals eaten in [from, to), oldest first, each with its items in the order of the meal.
    Task<IReadOnlyList<LoggedMeal>> MealsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // When every dose was taken, oldest first, for the same reason as the meals' times.
    Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(CancellationToken ct);

    // The doses taken in [from, to), oldest first, each with the meal it belongs to, if any.
    Task<IReadOnlyList<LoggedDose>> DosesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // When every session of exercise started, oldest first, for the same reason as the meals' times.
    Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(CancellationToken ct);

    // The sessions started in [from, to), oldest first, a running one with no end.
    Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // Her periods and her day notes, each whole and oldest first: a few a year, and a few a week.
    Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(CancellationToken ct);

    Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(CancellationToken ct);

    // Her saved foods, by name.
    Task<IReadOnlyList<SavedFood>> FoodsAsync(CancellationToken ct);
}