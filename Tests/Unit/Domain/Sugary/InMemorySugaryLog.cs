using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;

namespace Tests.Unit.Domain.Sugary;

// One person's log held in lists, answering every read the way the store does: a range is
// [from, to). Whoever a read names, it is this log: a test of the mount is a test of one person's.
public sealed class InMemorySugaryLog : ISugaryLog
{
    // A call on her turn, as the registrar hands the mount one: the mount answers only a call that
    // names a person, so a test reads it through her view (SugaryFileSystem.For).
    public static FileSystemCaller Her { get; } = new(
        new ConversationContext("jonas", "conversation-1", "user-her", new ReplyTarget("signalr", "conversation-1"),
            Principal: new Principal("signalr", "user-her", new Account("household-home", "user-her"))),
        null);

    public List<LoggedMeal> Meals { get; } = [];
    public List<LoggedDose> Doses { get; } = [];
    public List<LoggedExercise> Exercise { get; } = [];
    public List<LoggedPeriod> Periods { get; } = [];
    public List<LoggedDayNote> DayNotes { get; } = [];
    public List<SavedFood> Foods { get; } = [];

    public Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Meals.Select(meal => meal.EatenAt)]);

    public Task<IReadOnlyList<LoggedMeal>> MealsAsync(Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedMeal>>([.. Meals.Where(meal => meal.EatenAt >= from && meal.EatenAt < to)]);

    public Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Doses.Select(dose => dose.TakenAt)]);

    public Task<IReadOnlyList<LoggedDose>> DosesAsync(Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedDose>>([.. Doses.Where(dose => dose.TakenAt >= from && dose.TakenAt < to)]);

    public Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. Exercise.Select(session => session.StartedAt)]);

    public Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedExercise>>(
            [.. Exercise.Where(session => session.StartedAt >= from && session.StartedAt < to)]);

    public Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedPeriod>>([.. Periods]);

    public Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoggedDayNote>>([.. DayNotes]);

    public Task<IReadOnlyList<SavedFood>> FoodsAsync(Account owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SavedFood>>([.. Foods]);
}