using McpServerSugary.App;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// Open Food Facts is crowd-sourced: the same field arrives as a number or a string, and a product
// can be listed with no carbohydrate value at all.
public class OpenFoodFactsClientTests
{
    [Fact]
    public void Parse_ReadsCarbsServingAndName()
    {
        var food = OpenFoodFactsClient.Parse(
            """
            {"status":1,"product":{"product_name":"Corn Flakes","brands":"Kellogg's",
            "nutriments":{"carbohydrates_100g":84,"sugars_100g":8},
            "serving_quantity":30,"serving_quantity_unit":"g"}}
            """);

        food.ShouldBe(new PackagedFood("Corn Flakes · Kellogg's", 84, 30, "g", null));
    }

    [Fact]
    public void Parse_AcceptsNumbersServedAsStrings()
    {
        var food = OpenFoodFactsClient.Parse(
            """
            {"product":{"product_name":"Zumo","nutriments":{"carbohydrates_100g":"10.5"},
            "serving_quantity":"200","serving_quantity_unit":"ml"}}
            """);

        food.ShouldBe(new PackagedFood("Zumo", 10.5, 200, "ml", null));
    }

    [Theory]
    [InlineData("""{"status":0,"status_verbose":"product not found"}""")]
    [InlineData("""{"product":{"product_name":"Mystery","nutriments":{}}}""")]
    [InlineData("""{"product":{"product_name":"Heavy","nutriments":{"carbohydrates_100g":100.5}}}""")]
    [InlineData("""{"product":{"product_name":"Negative","nutriments":{"carbohydrates_100g":"-1"}}}""")]
    public void Parse_TreatsAProductWithoutCarbsAsUnknown(string json)
    {
        OpenFoodFactsClient.Parse(json).ShouldBeNull();
    }
}