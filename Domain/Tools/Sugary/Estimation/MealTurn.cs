namespace Domain.Tools.Sugary.Estimation;

public record MealInput(
    int PhotoCount,
    string? Description,
    string? Clarifications,
    string? Language,
    // Foods the person added themselves, one per line with their carbs.
    string? AlreadyCounted = null,
    // Whether a clip around the plate came with the meal. PhotoCount stays the photos she took: the
    // stills taken from the clip are the send's to show or not, never photos of hers.
    bool Filmed = false);

// Her answers and corrections to an estimate already made, and the foods she added herself.
public record MealFollowUp(string? Clarifications, string? AlreadyCounted, string? Language);

// What a meal estimate's turn says to the agent: the standalone app's user message
// (MealEstimator.UserText), word for word, so moving the estimate changed where it runs and not
// what the model is told.
public static class MealTurn
{
    public static string Text(MealInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var parts = new[]
        {
            Pictures(input),
            Section("The person's description", input.Description),
            Section("Their answers to your earlier questions", input.Clarifications),
            Section("Already added by the person and counted, so leave these out of your list", input.AlreadyCounted),
            $"Write every name, portion, note and question in {LanguageName(input.Language)}."
        };
        return string.Join("\n\n", parts.Where(p => p is not null));
    }

    // Her answers, as the next turn of the conversation the estimate opened. The standalone app
    // resent the whole meal each time; here the first message — photos and words — stays in the
    // agent's history, so this turn says only what is new, under the standalone's own headings, and
    // what she counted herself again because she may have added it since.
    public static string FollowUp(MealFollowUp input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var parts = new[]
        {
            "The person has answered about the same meal. Estimate the whole meal again: keep what was " +
            "already established and change only what their answers change.",
            Section("Their answers to your earlier questions", input.Clarifications),
            Section("Already added by the person and counted, so leave these out of your list", input.AlreadyCounted),
            $"Write every name, portion, note and question in {LanguageName(input.Language)}."
        };
        return string.Join("\n\n", parts.Where(p => p is not null));
    }

    // The video sentences are the standalone benchmark's, which measured video on Gemini. The last
    // one is new: the send gives a model that only sees pictures the clip's stills instead (ADR-0045),
    // and this text is written before anyone knows which model that will be.
    private static string Pictures(MealInput input)
    {
        var photos = input.PhotoCount switch
        {
            0 when input.Filmed => null,
            0 => "There is no photo of this meal, only the description below.",
            1 => "One photo of the meal is attached.",
            var n => $"{n} photos of the same meal are attached."
        };
        var video = !input.Filmed
            ? null
            : (photos is null
                  ? "A video of the meal is attached, filmed while moving around the plate."
                  : "A video of the same meal is attached too, filmed while moving around the plate.")
              + " If you are shown still frames instead, they were taken from that video.";
        return string.Join(' ', new[] { photos, video }.OfType<string>());
    }

    // The app is in English or Spanish; anything else it might send is read as English.
    public static string LanguageName(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "es" => "Spanish",
        _ => "English"
    };

    private static string? Section(string title, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : $"{title}:\n{text.Trim()}";
}