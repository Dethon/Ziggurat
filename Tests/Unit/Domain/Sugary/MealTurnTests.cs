using Domain.Prompts;
using Domain.Tools.Sugary.Estimation;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// What a meal estimate's turn says to the agent, in the standalone app's words.
public class MealTurnTests
{
    [Fact]
    public void FoodsThePersonAlreadyAdded_AreGivenAsCountedSoTheModelLeavesThemOut()
    {
        var text = MealTurn.Text(new MealInput(0, "breakfast", null, "en", "- Pan de molde: 29 g of carbs"));

        text.ShouldContain("Already added by the person and counted, so leave these out of your list:\n- Pan de molde: 29 g of carbs");
        SugaryPrompt.Prompt.ShouldContain("do not list them again");
    }

    [Fact]
    public void WithNothingAddedYet_NoSuchSectionIsSent()
    {
        MealTurn.Text(new MealInput(0, "breakfast", null, "en")).ShouldNotContain("Already added");
    }

    [Fact]
    public void ADescriptionAlone_SaysThereIsNoPhoto()
    {
        var text = MealTurn.Text(new MealInput(0, " two slices of toast ", null, "en"));

        text.ShouldStartWith("There is no photo of this meal, only the description below.");
        text.ShouldContain("The person's description:\ntwo slices of toast");
    }

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData(" ES ", "Spanish")]
    [InlineData("en", "English")]
    [InlineData("fr", "English")]
    [InlineData(null, "English")]
    public void TheLanguage_IsNamedForEverythingTheEstimateWrites(string? language, string named)
    {
        MealTurn.Text(new MealInput(0, "paella", null, language))
            .ShouldEndWith($"Write every name, portion, note and question in {named}.");
    }
}