using Domain.Tools.Sugary.Estimation;

namespace Domain.Tools.Sugary.Labels;

// What a label's turn says to the agent. The standalone app read labels with a system prompt of
// their own (LabelReader.SystemPrompt); here the agent's served prompt is the meal estimate's and
// reaches every turn, so the reader's instructions ride on the label turn instead, word for word
// under one sentence saying this turn is the other job. Meal turns never carry them, and the served
// prompt stays what the eval measures.
public static class LabelTurn
{
    private const string Lead =
        "This turn is not a meal to estimate: the attached photo is a nutrition label to read.";

    private const string Instructions =
        """
        You read the nutrition table on packaged food for a person with type 1 diabetes who counts carbs to decide their insulin. Your job is to transcribe what is printed, never to estimate it: a value you guessed becomes a wrong insulin dose, while a label you declined to read only costs them a second photo.

        From the photo, return:
        - readable: true only when you can read the carbohydrate value and what amount it refers to. A photo with no nutrition table, a blurred one, or one where the carbohydrate row is cut off is not readable.
        - carbsPer100g: the carbohydrate value per 100 g or per 100 ml (the row "carbohydrates", "hidratos de carbono", "glucides" and similar, not the "of which sugars" row). When the label only gives a value per serving, work it out from the serving size printed on the label and say so in note. null when not readable.
        - unit: "ml" when the label is per 100 ml, otherwise "g".
        - servingGrams: the serving or portion size printed on the label, in grams or millilitres. null when the label does not state one.
        - name: the product name if it is visible, otherwise a few words describing the product, otherwise null.
        - note: one short sentence when not readable saying what is wrong so they can retake the photo, or when there is something they must know to use the value: for example that it is a US-style label where carbohydrates include fibre, or that the values are for the dry or unprepared product. Otherwise null.
        """;

    public static string Text(string? language) => string.Join("\n\n",
        Lead,
        Instructions,
        $"Read the nutrition label in the attached photo. Write name and note in {MealTurn.LanguageName(language)}.");
}