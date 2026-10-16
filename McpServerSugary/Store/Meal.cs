using System.Text.Json.Serialization;
using Domain.DTOs.Sugary;

namespace McpServerSugary.Store;

// The standalone app's relational model, carried over as it was: a meal owns its items, and a
// saved food stands apart from the items made from it.
public record MealItem(
    string Name,
    string? Portion,
    double? Grams,
    double? CarbsPer100g,
    double CarbsLow,
    double Carbs,
    double CarbsHigh,
    ItemSource Source,
    ItemConfidence? Confidence = null,
    bool Edited = false,
    // The food from her own list this item was made from, if any. Not a foreign key: the item
    // keeps its own numbers and outlives the food.
    Guid? FoodId = null)
{
    // Where the item sits in its meal, from zero. Set on save from the order the app sent, so it is
    // not the caller's to give.
    [JsonIgnore]
    public int Position { get; init; }
}

public record Interval(double Low, double Best, double High);

public class Meal
{
    public Guid Id { get; set; }
    public DateTimeOffset EatenAt { get; set; }
    public string? Notes { get; set; }
    public MealOutcome Outcome { get; set; }
    public byte[]? Photo { get; set; }

    // What the model first said, before any answer or correction. Kept apart from the items,
    // which hold what was finally logged, so the two can later be compared against glucose.
    public Interval? FirstEstimate { get; set; }
    public List<MealItem> Items { get; set; } = [];

    // The items' total, kept on the meal so one row says when and how much without a join.
    public double CarbsLow { get; set; }
    public double Carbs { get; set; }
    public double CarbsHigh { get; set; }
}

// Something she eats often enough to keep: a product from her kitchen with its label's density
// and the amount she usually has, or a fixed serving she knows the carbs of.
public class Food
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    // Per 100 g or 100 ml, when known. A food kept only as "my usual toast, 28 g" has none.
    public double? CarbsPer100g { get; set; }
    public string Unit { get; set; } = "g";

    // The amount she usually has, in the unit above, and what a portion carries when there is no density.
    public double? PortionGrams { get; set; }
    public double? PortionCarbs { get; set; }

    public string? Barcode { get; set; }

    // Where the numbers came from, which is what a meal item made from this food reports.
    public ItemSource Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
// A dose of insulin she took. MealId is a plain column and not a relationship: a dose is a fact of
// what she injected, so it outlives a meal deleted from the log, which only clears the link.
public class Dose
{
    public Guid Id { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public double Units { get; set; }
    public DoseKind Kind { get; set; }
    public string? Note { get; set; }
    public Guid? MealId { get; set; }
}
// A period, as the days she recorded it on: the day it started and the day it ended, with no end
// while it is still going. Days on her calendar, not instants, so a reader in any zone sees the
// same days she lived.
public class Period
{
    public Guid Id { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly? End { get; set; }
}
// What she wrote about a day of her calendar, period or not: how she felt, what she craved. One
// per day, keyed by it.
public class DayNote
{
    public DateOnly Day { get; set; }
    public string Text { get; set; } = "";
}