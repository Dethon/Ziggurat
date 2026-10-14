using System.Text.Json;
using Domain.Tools.Sugary.Estimation;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// A reply is read against the shape the turn asked for, or not at all. A missing field read as zero
// grams, or an unknown answer read as an empty meal, would be a number she counts insulin against.
public class MealEstimateReplyTests
{
    private const string Item =
        """{"name":"rice","portion":"a cup","gramsLow":140,"grams":150,"gramsHigh":160,"carbsPer100g":28,"confidence":"medium","note":null}""";

    private static string Reply(string item = Item, string questions = "[]", string notes = "[]") =>
        $$"""{"items":[{{item}}],"questions":{{questions}},"notes":{{notes}}}""";

    [Fact]
    public void AReplyInTheSchema_IsReadIntoAnEstimate()
    {
        var estimate = MealEstimateReply.Read(Reply(
            questions: """[{"text":"How much rice?","options":["a cupped handful (about 60 g)","about one cup (160 g)"]}]""",
            notes: """["High in fat"]"""));

        var item = estimate.Items.ShouldHaveSingleItem();
        item.Name.ShouldBe("rice");
        item.Confidence.ShouldBe(Confidence.Medium);
        item.Grams.ShouldBe(new Interval(90, 150, 240));
        estimate.Questions.ShouldHaveSingleItem().Options.Count.ShouldBe(2);
        estimate.Notes.ShouldBe(["High in fat"]);
    }

    public static TheoryData<string, string> Unreadable => new()
    {
        { "prose", "Your meal has about 40 g of carbs." },
        { "empty object", "{}" },
        { "a field missing", Reply(Item.Replace(""","gramsHigh":160""", "")) },
        { "the note missing", Reply(Item.Replace(""","note":null""", "")) },
        { "a field it never asked for", Reply(Item.Replace("}", ""","insulin":"4 units"}""")) },
        { "a number as text", Reply(Item.Replace("\"grams\":150", "\"grams\":\"150\"")) },
        { "a null where a number goes", Reply(Item.Replace("\"grams\":150", "\"grams\":null")) },
        { "a confidence outside the list", Reply(Item.Replace("medium", "certain")) },
        { "a confidence as a number", Reply(Item.Replace("\"medium\"", "1")) },
        { "the questions missing", """{"items":[],"notes":[]}""" },
        { "a list that is null", """{"items":null,"questions":[],"notes":[]}""" },
        { "two answers", Reply() + Reply() },
        { "truncated", Reply()[..^5] }
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void AReplyOutsideTheSchema_IsRefusedWhole(string what, string reply)
    {
        Should.Throw<UnreadableEstimateException>(() => MealEstimateReply.Read(reply), what);
    }

    // The schema the turn sends is the one the reply is read against: strict mode wants every
    // property required and nothing else allowed, on every object, or the provider refuses it.
    [Fact]
    public void TheSchema_IsOneStrictModeAccepts()
    {
        AssertStrict(MealEstimateSchema.Format.Schema);
        MealEstimateSchema.Format.Name.ShouldBe("meal_estimate");
    }

    private static void AssertStrict(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
            var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();
            properties.EnumerateObject().Select(p => p.Name).ShouldBe(required, ignoreOrder: true);
        }

        foreach (var child in schema.EnumerateObject())
        {
            if (child.Value.ValueKind == JsonValueKind.Object)
            {
                AssertStrict(child.Value);
            }
            else if (child.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in child.Value.EnumerateArray())
                {
                    AssertStrict(element);
                }
            }
        }
    }
}