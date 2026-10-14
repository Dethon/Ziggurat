namespace Domain.DTOs.Sugary;

// Where an item's numbers came from. Only an estimate is the model's; the other three are ground
// truth she supplied (a label read, a barcode looked up, a number typed).
public enum ItemSource
{
    Estimate,
    Label,
    Barcode,
    Manual
}

// Whether her glucose ended in range after the meal, recorded afterwards.
public enum MealOutcome
{
    Unknown,
    Low,
    InRange,
    High
}

// How sure the estimate was of one item. Only an estimated item has one.
public enum ItemConfidence
{
    Low,
    Medium,
    High
}

// A range of carbohydrate in grams: the lowest plausible, the best guess, the highest plausible.
public sealed record CarbRange(double Low, double Best, double High);

// One food in a saved meal, as it was finally logged. Position is zero-based within the meal.
public sealed record LoggedItem(
    int Position,
    string Name,
    string? Portion,
    double? Amount,
    double? CarbsPer100,
    CarbRange Carbs,
    ItemSource Source,
    ItemConfidence? Confidence,
    bool Edited,
    Guid? FoodId);

// A saved meal. Carbs is the items' total; FirstEstimate is what the model first said for the items
// it estimated, before any answer or correction.
public sealed record LoggedMeal(
    Guid Id,
    DateTimeOffset EatenAt,
    CarbRange Carbs,
    CarbRange? FirstEstimate,
    MealOutcome Outcome,
    string? Notes,
    IReadOnlyList<LoggedItem> Items);

// A food from her own list. Amounts are in its unit (g or ml), carbs per 100 of that unit.
public sealed record SavedFood(
    Guid Id,
    string Name,
    double? CarbsPer100,
    string Unit,
    double? PortionAmount,
    double? PortionCarbs,
    string? Barcode,
    ItemSource Source);