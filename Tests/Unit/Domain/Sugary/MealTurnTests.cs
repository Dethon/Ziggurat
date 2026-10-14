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

    // A filmed meal is said in the standalone benchmark's words for its video. The clip may reach a
    // model that only sees pictures as stills taken from it, and the text is written before the
    // send knows which, so it says that too.
    [Fact]
    public void AClipAlone_IsAVideoOfTheMeal_NeverAMealWithNoPhoto()
    {
        var text = MealTurn.Text(new MealInput(0, null, null, "en", Filmed: true));

        text.ShouldStartWith(
            "A video of the meal is attached, filmed while moving around the plate. " +
            "If you are shown still frames instead, they were taken from that video.");
        text.ShouldNotContain("There is no photo");
    }

    [Fact]
    public void PhotosAndAClip_CountHerPhotos_AndAddTheVideo()
    {
        MealTurn.Text(new MealInput(2, "paella", null, "en", Filmed: true)).ShouldStartWith(
            "2 photos of the same meal are attached. " +
            "A video of the same meal is attached too, filmed while moving around the plate. " +
            "If you are shown still frames instead, they were taken from that video.");
    }

    // Her answers are the next turn of the conversation whose first message holds the photos and her
    // words, so the follow-up says nothing about either: "there is no photo" would be false there.
    [Fact]
    public void AFollowUp_CarriesHerAnswersAndWhatSheCounted_AndNothingAboutPhotos()
    {
        var text = MealTurn.FollowUp(new MealFollowUp(
            " Q: Sugar-free?\nA: Yes ", "- Pan de molde: 29 g of carbs", "es"));

        text.ShouldContain("Their answers to your earlier questions:\nQ: Sugar-free?\nA: Yes");
        text.ShouldContain("Already added by the person and counted, so leave these out of your list:\n- Pan de molde: 29 g of carbs");
        text.ShouldEndWith("Write every name, portion, note and question in Spanish.");
        text.ShouldNotContain("photo");
        text.ShouldNotContain("description");
    }

    [Fact]
    public void AFollowUpWithNothingCounted_SendsNoSuchSection()
    {
        MealTurn.FollowUp(new MealFollowUp("Q: Shared?\nA: No", null, "en")).ShouldNotContain("Already added");
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