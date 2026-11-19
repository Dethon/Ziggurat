using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;

namespace McpServerSugary.Store;

// What makes a profile and a measurement ones the app keeps. Each problem is the sentence the refusal
// says, or null. The bounds catch a slipped digit or a height typed in metres, not a clinical limit.
public static class ProfileRules
{
    // Her phone can be a day ahead of the home's calendar, travelling east.
    public const int FutureSlackDays = 1;

    public const double MinHeightCm = 30;
    public const double MaxHeightCm = 250;
    public const double MinWeightKg = 1;
    public const double MaxWeightKg = 400;

    public const string UnknownSex = "Sex is female or male.";
    public const string UnknownDiabetesType = "A diabetes type is type1, type2, lada, mody, gestational or other.";
    public const string DiagnosedInFuture = "A diagnosis can't be after today.";
    public const string UnknownMeasure = "A measurement is height or weight.";
    public const string NoDay = "A measurement needs the day it was taken.";
    public const string MeasuredInFuture = "A measurement can't be taken after today.";
    public const string HeightOutOfRange = "A height is between 30 and 250 cm.";
    public const string WeightOutOfRange = "A weight is between 1 and 400 kg.";

    // A word the API spells, or null for a word it does not; an absent word is no answer, not a wrong one.
    public static T? Word<T>(string? word) where T : struct, Enum =>
        Enum.GetValues<T>().Cast<T?>().FirstOrDefault(value => SugaryTables.Word(value!.Value) == word);

    public static string? ProfileProblem(string? sex, string? diabetesType, DateOnly? diagnosedOn, DateOnly today) =>
        (sex, diabetesType, diagnosedOn) switch
        {
            ({ } word, _, _) when Word<Sex>(word) is null => UnknownSex,
            (_, { } word, _) when Word<DiabetesType>(word) is null => UnknownDiabetesType,
            (_, _, { } day) when day > today.AddDays(FutureSlackDays) => DiagnosedInFuture,
            _ => null
        };

    public static string? MeasurementProblem(string? kind, DateOnly? on, double? value, DateOnly today) =>
        (Word<BodyMeasure>(kind), on, value) switch
        {
            (null, _, _) => UnknownMeasure,
            (_, null, _) => NoDay,
            (_, { } day, _) when day > today.AddDays(FutureSlackDays) => MeasuredInFuture,
            (BodyMeasure.Height, _, not (>= MinHeightCm and <= MaxHeightCm)) => HeightOutOfRange,
            (BodyMeasure.Weight, _, not (>= MinWeightKg and <= MaxWeightKg)) => WeightOutOfRange,
            _ => null
        };

    public static string DayTaken(BodyMeasure kind) => $"That day already has a {SugaryTables.Word(kind)}: change that one.";
}