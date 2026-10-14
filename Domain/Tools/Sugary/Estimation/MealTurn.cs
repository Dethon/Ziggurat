namespace Domain.Tools.Sugary.Estimation;

public record MealInput(
    int PhotoCount,
    string? Description,
    string? Clarifications,
    string? Language,
    // Foods the person added themselves, one per line with their carbs.
    string? AlreadyCounted = null);

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
            input.PhotoCount switch
            {
                0 => "There is no photo of this meal, only the description below.",
                1 => "One photo of the meal is attached.",
                var n => $"{n} photos of the same meal are attached."
            },
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

    // The app is in English or Spanish; anything else it might send is read as English.
    public static string LanguageName(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "es" => "Spanish",
        _ => "English"
    };

    private static string? Section(string title, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : $"{title}:\n{text.Trim()}";
}