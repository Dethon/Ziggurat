namespace McpServerSugary.Store;

// What a day of her calendar can say besides her words. The codes are the wire's and the mount's; the
// app words them in her language and draws each its own icon. Kept in this order, each once.
public static class DayEntries
{
    public static readonly IReadOnlyList<string> Flows = ["spotting", "light", "medium", "heavy"];

    public static readonly IReadOnlyList<string> Symptoms =
    [
        "cramps", "headache", "back_pain", "tender_breasts", "bloating", "nausea", "tiredness", "mood_swings",
        "cravings", "acne"
    ];

    public const string UnknownFlow = "A flow is spotting, light, medium or heavy.";

    public static string UnknownSymptom(string code) => $"Not a symptom Sugary knows: {code}.";

    public static IReadOnlyList<string> Ordered(IEnumerable<string> codes)
    {
        var said = codes.ToHashSet(StringComparer.Ordinal);
        return [.. Symptoms.Where(said.Contains)];
    }

    public static IReadOnlyList<string> Split(string stored) =>
        stored == "" ? [] : stored.Split(',');

    public static string Join(IEnumerable<string> codes) => string.Join(',', Ordered(codes));
}