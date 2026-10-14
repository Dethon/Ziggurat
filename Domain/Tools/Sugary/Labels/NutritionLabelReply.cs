using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Tools.Sugary.Estimation;

namespace Domain.Tools.Sugary.Labels;

// What a label says, as printed: carbs per 100 g (or ml) and the serving it states, if any.
public record PrintedLabel(string? Name, double CarbsPer100g, double? ServingGrams, string Unit, string? Note);

public abstract record LabelReading
{
    private LabelReading()
    {
    }

    public sealed record Read(PrintedLabel Label) : LabelReading;

    // The model declined, or answered with no value that could be printed; the reason is hers to read.
    public sealed record Unreadable(string Reason) : LabelReading;
}

// Reads a label's answer against the schema its turn asked for, whole or not at all, then applies
// the standalone reader's rule: only a readable label with carbs between 0 and 100 per 100 is a
// value. Anything else is refused with the model's reason, never shown as a number she might dose
// against. An answer outside the schema is not a refusal but a failure, said the way an estimate's is.
public static class NutritionLabelReply
{
    public const string CouldNotBeRead = "The label could not be read.";

    private static readonly string[] _units = ["g", "ml"];

    private static readonly JsonSerializerOptions _strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static LabelReading Read(string reply)
    {
        ModelLabel? label;
        try
        {
            label = JsonSerializer.Deserialize<ModelLabel>(reply, _strict);
        }
        catch (JsonException e)
        {
            throw Unreadable(e);
        }

        if (label is null || !_units.Contains(label.Unit))
        {
            throw Unreadable();
        }

        return label is { Readable: true, CarbsPer100g: >= 0 and <= 100 }
            ? new LabelReading.Read(new PrintedLabel(
                Blank(label.Name),
                label.CarbsPer100g.Value,
                label.ServingGrams is > 0 ? label.ServingGrams : null,
                label.Unit,
                Blank(label.Note)))
            : new LabelReading.Unreadable(Blank(label.Note) ?? CouldNotBeRead);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static UnreadableEstimateException Unreadable(Exception? inner = null) =>
        new("The model returned an answer that could not be read.", inner);

    private sealed record ModelLabel(
        bool Readable,
        double? CarbsPer100g,
        string Unit,
        double? ServingGrams,
        string? Name,
        string? Note);
}