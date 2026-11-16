using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// A Sugary estimate in a review copy is read as the app reads it (MealEstimateReply), so the total the
// chat shows is the one the app showed; anything it cannot read falls back to the outline.
public class StructuredAnswersTests
{
    private const string Estimate =
        """{"items":[{"name":"cooked macaroni","portion":"about 200 g","gramsLow":180,"grams":200,"gramsHigh":220,"carbsPer100g":28,"confidence":"high","note":null}],"questions":[],"notes":[]}""";

    [Fact]
    public void AMealEstimateAnswer_IsReadAsTheAppReadsIt()
    {
        var estimate = StructuredAnswers.MealEstimateOf(
            new ChatMessageModel { Role = "assistant", Content = Estimate, ResponseFormat = "meal_estimate" });

        estimate.ShouldNotBeNull();
        estimate.Total.Best.ShouldBe(56);
        estimate.Items.ShouldHaveSingleItem().Name.ShouldBe("cooked macaroni");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("something_else")]
    public void AnotherFormatsAnswer_IsNoEstimate(string? format) =>
        StructuredAnswers.MealEstimateOf(new ChatMessageModel { Role = "assistant", Content = Estimate, ResponseFormat = format })
            .ShouldBeNull();

    [Fact]
    public void AnEstimateItCannotRead_IsNoEstimate() =>
        StructuredAnswers.MealEstimateOf(new ChatMessageModel { Role = "assistant", Content = """{"items":[]}""", ResponseFormat = "meal_estimate" })
            .ShouldBeNull();
}