using Domain.Prompts;
using Domain.Tools.Sugary.Labels;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// What a label's turn says to the agent: the standalone label reader's instructions word for word,
// on the turn itself, since the agent's served prompt is about estimating meals.
public class LabelTurnTests
{
    [Fact]
    public void ALabelTurn_SaysItIsALabelToRead_NotAMealToEstimate()
    {
        LabelTurn.Text("en").ShouldStartWith(
            "This turn is not a meal to estimate: the attached photo is a nutrition label to read.");
    }

    // The standalone reader's own words: transcribe, never estimate; when it is unreadable; which row.
    [Fact]
    public void ALabelTurn_CarriesTheStandaloneReadersInstructions()
    {
        var text = LabelTurn.Text("en");

        text.ShouldContain("Your job is to transcribe what is printed, never to estimate it");
        text.ShouldContain("- readable: true only when you can read the carbohydrate value and what amount it refers to.");
        text.ShouldContain("not the \"of which sugars\" row");
        text.ShouldContain("When the label only gives a value per serving, work it out from the serving size printed on the label and say so in note.");
        text.ShouldContain("- servingGrams: the serving or portion size printed on the label");
        SugaryPrompt.Prompt.ShouldNotContain("readable");
    }

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData("en", "English")]
    [InlineData(null, "English")]
    [InlineData("fr", "English")]
    public void ALabelTurn_EndsInTheAppsLanguage(string? language, string named)
    {
        LabelTurn.Text(language).ShouldEndWith(
            $"Read the nutrition label in the attached photo. Write name and note in {named}.");
    }
}