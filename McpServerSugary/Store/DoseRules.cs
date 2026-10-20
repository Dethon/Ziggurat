using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;

namespace McpServerSugary.Store;

// What makes a dose one the log keeps, however it arrives: on its own or saved with a meal. Each
// answer is the sentence the refusal says, or null. The bounds catch a slipped digit, not a clinical
// limit, and they refuse rather than round or clamp: the number kept is always the one she typed.
public static class DoseRules
{
    public const double MaxUnits = 100;
    private const int MaxNoteLength = 200;

    public const string UnknownMeal = "There is no such meal to link the dose to.";
    public const string NoTime = "A dose needs the time it was taken.";

    public static string? Problem(double? units, DoseKind? kind, string? note) => units switch
    {
        null or <= 0 or double.NaN => "A dose needs a number of units above 0.",
        > MaxUnits => $"A dose can be at most {MaxUnits:0} units.",
        { } value when Math.Round(value, 2) != value => "Units can have at most two decimals.",
        _ when kind is null => "A dose is rapid or basal.",
        _ when note?.Trim().Length > MaxNoteLength => $"A note can be at most {MaxNoteLength} characters.",
        _ => null
    };

    // The kind as the API spells it ("rapid", "basal"); anything else is no kind at all, so a typo
    // is refused with a sentence rather than failing to bind.
    public static DoseKind? Kind(string? word) =>
        Enum.GetValues<DoseKind>().Cast<DoseKind?>().FirstOrDefault(kind => SugaryTables.Word(kind!.Value) == word);

    public static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}