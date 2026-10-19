using Domain.Contracts;
using Domain.DTOs.Sugary;

namespace Tests.Unit.Domain.Sugary;

// Her log held in lists, answering every read the way the store does: a range is [from, to).
public sealed class InMemorySugaryLog : ISugaryLog
{
    public List<LoggedMeal> Meals { get; } = [];
    public List<LoggedDose> Doses { get; } = [];
    public List<LoggedExercise> Exercise { get; } = [];
    public List<LoggedPeriod> Periods { get; } = [];
    public List<LoggedDayNote> DayNotes { get; } = [];
    public List<SavedFood> Foods { get; } = [];

    public Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Meals.Select(meal => meal.EatenAt)]);

    public Task<IReadOnlyList<LoggedMeal>> MealsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedMeal>>([.. Meals.Where(meal => meal.EatenAt >= from && meal.EatenAt < to)]);

    public Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Doses.Select(dose => dose.TakenAt)]);

    public Task<IReadOnlyList<LoggedDose>> DosesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedDose>>([.. Doses.Where(dose => dose.TakenAt >= from && dose.TakenAt < to)]);

    public Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Exercise.Select(session => session.StartedAt)]);

    public Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedExercise>>(
            [.. Exercise.Where(session => session.StartedAt >= from && session.StartedAt < to)]);

    public Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedPeriod>>([.. Periods]);

    public Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedDayNote>>([.. DayNotes]);

    public Task<IReadOnlyList<SavedFood>> FoodsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SavedFood>>([.. Foods]);
}