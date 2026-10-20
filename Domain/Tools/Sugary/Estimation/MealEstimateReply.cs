using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Tools.Sugary.Estimation;

public sealed class UnreadableEstimateException(string message, Exception? inner = null) : Exception(message, inner);

// Reads the agent's answer against the schema its turn asked for, whole or not at all. The schema is
// strict on the wire, but a provider can still answer in prose or drop a field, and the standalone
// app's lenient read would then show a missing weight as zero grams or an unknown answer as an empty
// meal. Here every field must be present, typed as asked, and nothing else may be there.
public static class MealEstimateReply
{
    private static readonly JsonSerializerOptions _strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static MealEstimate Read(string reply)
    {
        ModelEstimate? raw;
        try
        {
            raw = JsonSerializer.Deserialize<ModelEstimate>(reply, _strict);
        }
        catch (JsonException e)
        {
            throw Unreadable(e);
        }

        // Nullable annotations are checked on members, not on what a list holds.
        if (raw is null
            || raw.Items.Any(item => item is null)
            || raw.Questions.Any(q => q is null || q.Options.Any(o => o is null))
            || raw.Notes.Any(n => n is null))
        {
            throw Unreadable();
        }

        return MealEstimate.From(raw);
    }

    internal static UnreadableEstimateException Unreadable(Exception? inner = null) =>
        new("The model returned an answer that could not be read.", inner);
}