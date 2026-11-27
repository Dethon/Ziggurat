namespace Domain.Exceptions;

// A provider refusing a request that asked for its answer in a strict schema (OpenRouter's 400): most often
// the schema itself, which strict mode or that provider would not take. The same request fails the same way
// every time, so whoever sent the schema changes it rather than asking again; the provider's own words ride
// in the message, since they say what to change.
public sealed class ResponseFormatRefusedException(string providerSaid, Exception? inner = null)
    : Exception(Opening + providerSaid, inner)
{
    private const string Opening = "The model's provider refused a request asking for its answer in a schema: ";

    // Whether a failed turn's answer (TurnFailure.Answer) is this refusal: a channel's reply carries words alone.
    public static bool Answers(string answer) =>
        answer.TrimStart().StartsWith(TurnFailure.ErrorOpening + Opening, StringComparison.Ordinal);
}