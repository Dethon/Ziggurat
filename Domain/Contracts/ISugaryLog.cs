using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;

namespace Domain.Contracts;

// One person's carb log as the /sugary mount and the app's downloads read it, straight from the store
// on every call: nothing here is cached, so what a reader sees is what the database holds at that
// moment. Every read names whose log it is: the person's own meals, doses, exercise, periods and
// notes, and their household's foods — never anyone else's, and never a row nobody owns yet.
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

    // The foods the person's household keeps, by name.
    Task<IReadOnlyList<SavedFood>> FoodsAsync(Account owner, CancellationToken ct);
}