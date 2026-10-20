using Domain.DTOs.Sugary;

namespace McpServerSugary.Store;

// What makes a meal one the log keeps, whichever way it arrives: saved from the app, or carried over
// from the standalone app's downloads. Each answer is the sentence the refusal says, or null.
public static class MealRules
{
    public const int MaxItems = 50;
    public const double MaxCarbs = 2000;
    private const int MaxNameLength = 200;
    private const int MaxNotesLength = 2000;

    public static string? ItemProblem(MealItem item) =>
        string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > MaxNameLength
            ? $"Every item needs a name of at most {MaxNameLength} characters."
            : !IsOrdered(item.CarbsLow, item.Carbs, item.CarbsHigh)
                ? "Carbs must satisfy 0 ≤ low ≤ best ≤ high."
                : null;

    public static string? MealProblem(
        IReadOnlyList<MealItem>? items, Interval? firstEstimate, string? notes, MealOutcome outcome) => items switch
        {
            null or { Count: 0 } => "A meal needs at least one item.",
            { Count: > MaxItems } => $"A meal can have at most {MaxItems} items.",
            _ when items.Select(ItemProblem).FirstOrDefault(problem => problem is not null) is { } problem => problem,
            _ when firstEstimate is { } first && !IsOrdered(first.Low, first.Best, first.High) =>
                "The first estimate must satisfy 0 ≤ low ≤ best ≤ high.",
            _ when notes?.Length > MaxNotesLength => $"Notes can be at most {MaxNotesLength} characters.",
            _ when !Enum.IsDefined(outcome) => "Unknown outcome.",
            _ => null
        };

    // The meal's own figures are its items' sum, so one row says when and how much without a join.
    public static void Total(Meal meal)
    {
        meal.CarbsLow = Math.Round(meal.Items.Sum(item => item.CarbsLow), 1);
        meal.Carbs = Math.Round(meal.Items.Sum(item => item.Carbs), 1);
        meal.CarbsHigh = Math.Round(meal.Items.Sum(item => item.CarbsHigh), 1);
    }

    private static bool IsOrdered(double low, double best, double high) =>
        low >= 0 && low <= best && best <= high && high <= MaxCarbs;
}