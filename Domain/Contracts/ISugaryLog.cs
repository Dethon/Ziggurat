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

    // Her saved foods, by name.
    Task<IReadOnlyList<SavedFood>> FoodsAsync(CancellationToken ct);
}